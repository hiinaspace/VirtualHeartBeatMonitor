using System.Globalization;
using System.Text.Json;

namespace HeartBeat.Core.Recordings;

/// <summary>Which recorded stream becomes the engine's input.</summary>
public enum RecordingSource
{
    /// <summary>Raw IMU samples (imu_*.csv): accel and gyro at the device's IMU rate.</summary>
    Imu,
    /// <summary>
    /// Tracked poses (poses_*.csv), turned into IMU-like samples: gyro from the pose's angular
    /// velocity, accel from the change in linear velocity plus gravity, both in the device frame.
    /// This is what an app gets from a runtime that exposes no raw IMU (OpenXR, xrLocateSpace).
    /// </summary>
    Poses,
}

public sealed record RecordingLoadOptions
{
    public RecordingSource Source { get; init; } = RecordingSource.Imu;

    /// <summary>
    /// When above zero, resample every device to this fixed rate by taking the latest sample at
    /// or before each tick (no anti-alias filter), the way an app polling once per rendered frame
    /// would see it. 0 keeps the recorded samples.
    /// </summary>
    public double ResampleHz { get; init; }
}

public sealed class RecordedDevice
{
    public required string Kind { get; init; }
    public required uint Index { get; init; }
    public required DeviceRole DefaultRole { get; init; }
    public required List<ImuSample> Imu { get; init; }
    /// <summary>Raw IMU samples dropped at the start because they predate the recording.</summary>
    public int StaleDropped { get; init; }
}

/// <summary>
/// A Probe recording folder: session.json plus imu_&lt;index&gt;_&lt;kind&gt;.csv and
/// poses_&lt;index&gt;_&lt;kind&gt;.csv. Sample times are on the host clock, so devices line up.
/// </summary>
public sealed class Recording
{
    public string Folder { get; init; } = "";
    public string Label { get; init; } = "";
    public string Note { get; init; } = "";
    /// <summary>Pulse counted by hand before and after recording (Probe asks), if given.</summary>
    public double? PulseBeforeBpm { get; init; }
    public double? PulseAfterBpm { get; init; }
    public List<RecordedDevice> Devices { get; init; } = new();

    private const double StaleGapSeconds = 1.0;
    private const int StartupSamples = 4000;
    private const double ClockLeakPerSample = 1e-6;
    private const double Gravity = 9.80665;
    private const double MaxHoldSeconds = 0.1;

    public static Recording Load(string folder, RecordingLoadOptions? options = null)
    {
        options ??= new RecordingLoadOptions();
        string label = "", note = "";
        double? pulseBefore = null, pulseAfter = null;
        string sessionPath = Path.Combine(folder, "session.json");
        if (File.Exists(sessionPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sessionPath));
            if (doc.RootElement.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String) label = l.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String) note = n.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("pulseBeforeBpm", out var pb) && pb.ValueKind == JsonValueKind.Number) pulseBefore = pb.GetDouble();
            if (doc.RootElement.TryGetProperty("pulseAfterBpm", out var pa) && pa.ValueKind == JsonValueKind.Number) pulseAfter = pa.GetDouble();
        }

        string prefix = options.Source == RecordingSource.Imu ? "imu_" : "poses_";
        var files = Directory.GetFiles(folder, prefix + "*.csv")
            .Select(f => (file: f, parsed: ParseName(Path.GetFileNameWithoutExtension(f), prefix)))
            .Where(x => x.parsed != null)
            .Select(x => (x.file, index: x.parsed!.Value.index, kind: x.parsed!.Value.kind))
            .OrderBy(x => x.index)
            .ToList();
        if (files.Count == 0)
            throw new FileNotFoundException($"No {prefix}*.csv files in {folder}");

        var devices = new List<RecordedDevice>();
        var usedKinds = new HashSet<string>();
        bool chestAssigned = false;
        foreach (var (file, index, kind) in files)
        {
            // Several devices of one kind (e.g. three trackers): the first keeps the plain name,
            // which Replay uses for the reference; the others get their index appended.
            string name = usedKinds.Add(kind) ? kind : $"{kind}#{index}";
            usedKinds.Add(name);
            DeviceRole role = kind switch
            {
                "HMD" => DeviceRole.Head,
                _ when kind.StartsWith("Controller", StringComparison.Ordinal) => DeviceRole.Hand,
                "Tracker" when !chestAssigned => DeviceRole.Chest,
                _ => DeviceRole.Other,
            };
            if (role == DeviceRole.Chest) chestAssigned = true;

            int stale = 0;
            var samples = options.Source == RecordingSource.Imu ? LoadImu(file, out stale) : LoadPoses(file);
            if (options.ResampleHz > 0) samples = Resample(samples, options.ResampleHz);
            if (samples.Count == 0) continue;
            devices.Add(new RecordedDevice { Kind = name, Index = index, DefaultRole = role, Imu = samples, StaleDropped = stale });
        }
        return new Recording
        {
            Folder = folder, Label = label, Note = note, Devices = devices,
            PulseBeforeBpm = pulseBefore, PulseAfterBpm = pulseAfter,
        };
    }

    private static (uint index, string kind)? ParseName(string stem, string prefix)
    {
        // imu_3_Controller-L -> (3, "Controller-L")
        string rest = stem[prefix.Length..];
        int us = rest.IndexOf('_');
        if (us <= 0 || !uint.TryParse(rest[..us], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint index)) return null;
        return (index, rest[(us + 1)..]);
    }

    private static IEnumerable<string[]> Rows(string file)
    {
        using var reader = new StreamReader(file);
        reader.ReadLine(); // header
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            yield return line.Split(',');
        }
    }

    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// host_t,sample_t,ax,ay,az,gx,gy,gz,offscale. Device time is mapped onto the host clock the
    /// same way the live app does it (smallest host − device difference, slow upward leak).
    /// </summary>
    internal static List<ImuSample> LoadImu(string file, out int staleDropped)
    {
        var raw = new List<(double host, double dev, Vec3 a, Vec3 g)>();
        foreach (var c in Rows(file))
        {
            if (c.Length < 8) continue;
            raw.Add((D(c[0]), D(c[1]), new Vec3(D(c[2]), D(c[3]), D(c[4])), new Vec3(D(c[5]), D(c[6]), D(c[7]))));
        }

        // Leftovers from before the recording started end in a forward jump of over a second.
        staleDropped = 0;
        for (int i = 1; i < Math.Min(raw.Count, StartupSamples); i++)
            if (raw[i].dev - raw[i - 1].dev > StaleGapSeconds) staleDropped = i;
        if (staleDropped > 0) raw.RemoveRange(0, staleDropped);

        var result = new List<ImuSample>(raw.Count);
        double offset = double.NaN;
        foreach (var (host, dev, a, g) in raw)
        {
            double o = host - dev;
            offset = double.IsNaN(offset) ? o : Math.Min(offset + ClockLeakPerSample, o);
            result.Add(new ImuSample(dev + offset, a, g));
        }
        return result;
    }

    /// <summary>
    /// host_t,valid,result,px,py,pz,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz (one row per new driver update).
    /// Velocities are in the tracking universe (y up); they are rotated into the device frame.
    /// </summary>
    internal static List<ImuSample> LoadPoses(string file)
    {
        var rows = new List<(double t, double qw, double qx, double qy, double qz, Vec3 v, Vec3 w)>();
        foreach (var c in Rows(file))
        {
            if (c.Length < 16 || c[1] != "1") continue;
            rows.Add((D(c[0]), D(c[6]), D(c[7]), D(c[8]), D(c[9]),
                new Vec3(D(c[10]), D(c[11]), D(c[12])), new Vec3(D(c[13]), D(c[14]), D(c[15]))));
        }

        var result = new List<ImuSample>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            // Central difference of velocity for acceleration (one-sided at the ends).
            int i0 = Math.Max(0, i - 1), i1 = Math.Min(rows.Count - 1, i + 1);
            double dt = rows[i1].t - rows[i0].t;
            Vec3 accelWorld = dt > 0 ? (rows[i1].v - rows[i0].v) / dt : Vec3.Zero;
            // An accelerometer measures specific force: acceleration minus gravity (gravity is −y).
            Vec3 specificForce = accelWorld + new Vec3(0, Gravity, 0);
            result.Add(new ImuSample(r.t,
                RotateInverse(r.qw, r.qx, r.qy, r.qz, specificForce),
                RotateInverse(r.qw, r.qx, r.qy, r.qz, r.w)));
        }
        return result;
    }

    /// <summary>Rotates a world vector into the device frame: q⁻¹ · v · q for a unit quaternion.</summary>
    private static Vec3 RotateInverse(double qw, double qx, double qy, double qz, in Vec3 v)
    {
        // Conjugate quaternion (w, −x, −y, −z) applied with the standard formula v' = v + 2w(u×v) + 2u×(u×v).
        double ux = -qx, uy = -qy, uz = -qz;
        double cx = uy * v.Z - uz * v.Y, cy = uz * v.X - ux * v.Z, cz = ux * v.Y - uy * v.X;
        double ccx = uy * cz - uz * cy, ccy = uz * cx - ux * cz, ccz = ux * cy - uy * cx;
        return new Vec3(v.X + 2 * (qw * cx + ccx), v.Y + 2 * (qw * cy + ccy), v.Z + 2 * (qw * cz + ccz));
    }

    /// <summary>Latest sample at or before each tick of a fixed-rate clock (sample and hold).</summary>
    internal static List<ImuSample> Resample(List<ImuSample> samples, double hz)
    {
        var result = new List<ImuSample>();
        if (samples.Count == 0) return result;
        double step = 1.0 / hz;
        int j = 0;
        for (double t = samples[0].Time; t <= samples[^1].Time; t += step)
        {
            while (j + 1 < samples.Count && samples[j + 1].Time <= t) j++;
            if (t - samples[j].Time > MaxHoldSeconds) continue; // a dropout stays a gap
            result.Add(samples[j] with { Time = t });
        }
        return result;
    }
}
