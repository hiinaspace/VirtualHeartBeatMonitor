using System.Globalization;
using HeartBeat.Core.Recordings;

namespace HeartBeat.Core.Tests;

public sealed class RecordingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vhbm-recording-" + Guid.NewGuid().ToString("N"));
    private const double Bpm = 72, Seconds = 40;
    // Device clock runs 1000 s ahead of the host clock; samples reach the host 2–3 ms late.
    private const double DeviceOffset = 1000;
    // The headset is turned 30° about the vertical axis in the tracking universe.
    private static readonly double Half = 15 * Math.PI / 180;
    private static readonly (double w, double x, double y, double z) Q = (Math.Cos(Half), 0, Math.Sin(Half), 0);

    public RecordingTests()
    {
        Directory.CreateDirectory(_folder);
        var inv = CultureInfo.InvariantCulture;
        var samples = Synthetic.Headset(Seconds, Bpm).ToList();
        var rng = new Random(5);

        using (var imu = new StreamWriter(Path.Combine(_folder, "imu_0_HMD.csv")))
        {
            imu.WriteLine("host_t,sample_t,ax,ay,az,gx,gy,gz,offscale");
            // Leftovers from a previous session, seconds older than the real stream.
            for (int i = 0; i < 50; i++)
                imu.WriteLine(string.Create(inv, $"{0.0:F6},{DeviceOffset - 30 + i * 0.001:F7},0,9.81,0,0,0,0,0"));
            foreach (var s in samples)
                imu.WriteLine(string.Create(inv,
                    $"{s.Time + 0.002 + 0.001 * rng.NextDouble():F6},{s.Time + DeviceOffset:F7},{s.Accel.X:G10},{s.Accel.Y:G10},{s.Accel.Z:G10},{s.Gyro.X:G10},{s.Gyro.Y:G10},{s.Gyro.Z:G10},0"));
        }

        using (var poses = new StreamWriter(Path.Combine(_folder, "poses_0_HMD.csv")))
        {
            poses.WriteLine("host_t,valid,result,px,py,pz,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz");
            // Driver pose updates at about 500 Hz; angular velocity reported in the universe frame.
            for (int i = 0; i < samples.Count; i += 2)
            {
                var s = samples[i];
                var w = Rotate(s.Gyro);
                poses.WriteLine(string.Create(inv,
                    $"{s.Time:F6},1,200,0,1.2,0,{Q.w:G9},{Q.x:G9},{Q.y:G9},{Q.z:G9},0,0,0,{w.X:G9},{w.Y:G9},{w.Z:G9}"));
            }
        }
        File.WriteAllText(Path.Combine(_folder, "session.json"), """{ "label": "synthetic", "pulseBeforeBpm": 72, "pulseAfterBpm": null }""");
    }

    private static Vec3 Rotate(Vec3 v)
    {
        // q · v · q⁻¹
        double cx = Q.y * v.Z - Q.z * v.Y, cy = Q.z * v.X - Q.x * v.Z, cz = Q.x * v.Y - Q.y * v.X;
        double ccx = Q.y * cz - Q.z * cy, ccy = Q.z * cx - Q.x * cz, ccz = Q.x * cy - Q.y * cx;
        return new Vec3(v.X + 2 * (Q.w * cx + ccx), v.Y + 2 * (Q.w * cy + ccy), v.Z + 2 * (Q.w * cz + ccz));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Imu_is_mapped_to_the_host_clock_and_stale_samples_dropped()
    {
        var rec = Recording.Load(_folder);
        Assert.Equal("synthetic", rec.Label);
        Assert.Equal(72, rec.PulseBeforeBpm);
        Assert.Null(rec.PulseAfterBpm);
        var hmd = Assert.Single(rec.Devices);
        Assert.Equal("HMD", hmd.Kind);
        Assert.Equal(DeviceRole.Head, hmd.DefaultRole);
        Assert.Equal(50, hmd.StaleDropped);
        Assert.Equal((int)(Seconds * 1000), hmd.Imu.Count);
        // Host time = device time + the smallest delivery delay (2 ms here, within 1 ms).
        Assert.InRange(hmd.Imu[^1].Time - (Seconds - 0.001), 0.001, 0.004);
    }

    [Fact]
    public void Pose_angular_velocity_is_rotated_into_the_device_frame()
    {
        var imu = Recording.Load(_folder).Devices[0].Imu;
        var poses = Recording.Load(_folder, new RecordingLoadOptions { Source = RecordingSource.Poses }).Devices[0].Imu;
        Assert.Equal(imu.Count / 2, poses.Count);
        for (int i = 100; i < poses.Count; i += 997)
        {
            var expected = imu[2 * i].Gyro;
            Assert.InRange((poses[i].Gyro - expected).Length, 0, 1e-6);
            // Still device: specific force is gravity, i.e. "up" at 9.81 m/s².
            Assert.InRange(poses[i].Accel.Length, 9.80, 9.82);
        }
    }

    [Fact]
    public void Resampling_gives_a_fixed_rate()
    {
        var d = Recording.Load(_folder, new RecordingLoadOptions { ResampleHz = 90 }).Devices[0].Imu;
        Assert.InRange(d.Count, 90 * Seconds - 2, 90 * Seconds + 2);
        Assert.InRange(d[1].Time - d[0].Time, 1 / 90.0 - 1e-9, 1 / 90.0 + 1e-9);
    }

    // Raw IMU picked at 90 Hz with no anti-alias filter folds the 1 kHz noise into the heart band,
    // so it is shown less often (here: rarely; the synthetic poses carry the same noise at 500 Hz).
    // Whatever is shown must still be right. The synthetic
    // poses copy the raw gyro exactly, so they say nothing about a real driver's smoothing.
    [Theory]
    [InlineData(RecordingSource.Imu, 0, 0.8)]
    [InlineData(RecordingSource.Poses, 0, 0.8)]
    [InlineData(RecordingSource.Imu, 90, 0)]
    [InlineData(RecordingSource.Poses, 90, 0)]
    public void Heart_rate_is_found_from_each_input(RecordingSource source, double hz, double minShown)
    {
        var device = Recording.Load(_folder, new RecordingLoadOptions { Source = source, ResampleHz = hz }).Devices[0];
        var engine = new MonitorEngine();
        engine.AddDevice(device.Kind, device.DefaultRole);
        var settled = new List<MonitorSnapshot>();
        foreach (var s in device.Imu)
        {
            engine.Push(device.Kind, s);
            if (engine.TryUpdate(s.Time, out var snap) && snap.Time > 20) settled.Add(snap);
        }
        Assert.NotEmpty(settled);
        var shown = settled.Where(s => s.Heart.Displayed).ToList();
        Assert.All(shown, s => Assert.InRange(s.Heart.Bpm, Bpm - 2.5, Bpm + 2.5));
        Assert.True(shown.Count >= minShown * settled.Count, $"shown {shown.Count} of {settled.Count}");
    }
}
