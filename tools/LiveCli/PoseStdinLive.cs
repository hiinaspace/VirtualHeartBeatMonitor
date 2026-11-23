using System.Globalization;
using HeartBeat.App;
using HeartBeat.Core;
using HeartBeat.Core.Heart;
using HeartBeat.Osc;
using HeartBeat.Tracking;

/// <summary>
/// Live heart rate from tracked poses streamed on stdin, for runtimes without raw IMU (Monado
/// over OpenXR). Each line: serial,kind,t,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz with t in seconds,
/// velocities in a y-up world frame. tools/xr_pose_tap.py --stream writes this format.
/// Poses become IMU-like samples the same way Replay --source poses does it: angular velocity
/// rotated into the device frame, acceleration from the change in velocity plus gravity.
/// </summary>
static class PoseStdinLive
{
    private const double Gravity = 9.80665;

    private sealed class Dev
    {
        public required string Id;
        public required DeviceRole Role;
        public double LastT = double.NaN;
        public Vec3 LastV;
        public long Samples;
    }

    public static int Run(AppSettings settings, VRChatOutput? output, bool printBeats, double? seconds, string oscLabel)
    {
        var engine = new MonitorEngine(new EngineSettings { MotionThresholdDps = settings.Detection.MotionThresholdDps });
        engine.Fusion.ConfidentThreshold = settings.Detection.DisplayThreshold;
        long beats = 0, measured = 0;
        engine.Beat += b =>
        {
            if (!b.Measured && !settings.Beats.SendPredicted) return;
            beats++;
            if (b.Measured) measured++;
            output?.OnBeat(b);
            if (printBeats)
                Console.WriteLine($"    beat {(b.Measured ? "measured " : "predicted")} {b.Bpm:0} BPM  interval {b.IntervalSeconds * 1000:0} ms  strength {b.Strength:0.00}");
        };
        if (output != null) engine.BreathingUpdated += (_, state) => output.OnBreathing(state, HostClock.Now);

        Console.WriteLine($"Poses from stdin; OSC {oscLabel}. Sit still for about 10 s. Ctrl+C to stop.");
        var devices = new Dictionary<string, Dev>();
        double first = double.NaN, latest = double.NegativeInfinity, nextTick = double.NaN, nextPrint = double.NaN;
        MonitorSnapshot? snap = null;
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            var c = line.Split(',');
            if (c.Length < 13) continue;
            string serial = c[0], kind = c[1];
            double D(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
            double t = D(2);
            if (!devices.TryGetValue(serial, out var dev))
            {
                var role = kind switch
                {
                    "HMD" => DeviceRole.Head,
                    _ when kind.StartsWith("Controller", StringComparison.Ordinal) => DeviceRole.Hand,
                    "Tracker" when settings.Devices.ChestTrackerSerials.Contains(serial) => DeviceRole.Chest,
                    _ => DeviceRole.Other,
                };
                bool use = settings.Devices.UseDevice.TryGetValue(serial, out bool chosen) ? chosen : LiveSession.DefaultUse(role);
                dev = new Dev { Id = $"{kind}:{serial}", Role = role };
                devices[serial] = dev;
                engine.AddDevice(dev.Id, role, useForHeartRate: use, useForBreathing: role == DeviceRole.Chest);
                Console.WriteLine($"  {dev.Id,-32} {role,-5} {(use ? "heart rate" : "not used")}");
            }
            var v = new Vec3(D(7), D(8), D(9));
            var w = new Vec3(D(10), D(11), D(12));
            double dt = t - dev.LastT;
            if (!(dt > 0)) { dev.LastT = t; dev.LastV = v; continue; } // first sample or duplicate time
            Vec3 specificForce = (v - dev.LastV) / dt + new Vec3(0, Gravity, 0);
            dev.LastT = t;
            dev.LastV = v;
            double qw = D(3), qx = D(4), qy = D(5), qz = D(6);
            engine.Push(dev.Id, new ImuSample(t, RotateInverse(qw, qx, qy, qz, specificForce), RotateInverse(qw, qx, qy, qz, w)));
            dev.Samples++;
            latest = Math.Max(latest, t);

            if (double.IsNaN(first)) { first = t; nextTick = t + 0.05; nextPrint = t + 1; }
            if (latest >= nextTick)
            {
                nextTick = latest + 0.05;
                if (engine.TryUpdate(latest, out var s))
                {
                    snap = s;
                    output?.OnSnapshot(s, HostClock.Now);
                }
            }
            if (latest >= nextPrint)
            {
                nextPrint += 1;
                if (snap is null) { Console.WriteLine($"{latest - first,6:0} s  waiting for data"); continue; }
                var h = snap.Heart;
                string bpm = h.DisplayBpm is { } b ? $"{b,5:0}" : "   --";
                string per = string.Join("  ", snap.Devices.Select(d =>
                    $"{d.Id.Split(':')[0]}={(double.IsNaN(d.PeakBpm) ? "-" : d.PeakBpm.ToString("0"))}/{(double.IsNaN(d.Prominence) ? "-" : d.Prominence.ToString("0.0"))}{(d.Still ? "" : "*")}"));
                Console.WriteLine($"{latest - first,6:0} s  HR {bpm}  conf {h.Confidence:0.00}  beats {beats} ({measured} measured)  {per}{(snap.UserMoving ? "  moving" : "")}");
            }
            if (seconds is { } limit && latest - first >= limit) break;
        }
        return 0;
    }

    /// <summary>Rotates a world vector into the device frame (q⁻¹ · v · q for a unit quaternion).</summary>
    private static Vec3 RotateInverse(double qw, double qx, double qy, double qz, Vec3 v)
    {
        double ux = -qx, uy = -qy, uz = -qz;
        double cx = uy * v.Z - uz * v.Y, cy = uz * v.X - ux * v.Z, cz = ux * v.Y - uy * v.X;
        double c2x = uy * cz - uz * cy, c2y = uz * cx - ux * cz, c2z = ux * cy - uy * cx;
        return new Vec3(v.X + 2 * (qw * cx + c2x), v.Y + 2 * (qw * cy + c2y), v.Z + 2 * (qw * cz + c2z));
    }
}
