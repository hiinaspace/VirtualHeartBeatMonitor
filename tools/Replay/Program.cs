using System.Globalization;
using System.Text;
using HeartBeat.Core;
using HeartBeat.Core.Breathing;
using HeartBeat.Core.Heart;
using HeartBeat.Core.Recordings;

const string Usage = """
    Replay — runs the HeartBeat.Core pipeline over a Probe recording and scores it.

    Usage:
      Replay <recording folder> [--hr-devices HMD,Controller-L,...] [--motion 5] [--timeline 5] [--csv]

      --hr-devices  devices fused into the heart rate (default: everything except the tracker).
                    The tracker is always used as the reference and for breathing.
      --motion      motion threshold in deg/s for the stillness gate (default 8)
      --min-window  shortest analysis window in seconds after settling (default 6)
      --max-masked  largest share of a window that may be cut out for movement (default 0.25)
      --timeline    print one timeline row every N seconds (default 5, 0 = off)
      --csv         write replay.csv into the recording folder
      --beat K=V    override a BeatDetector setting, e.g. --beat ProximityFloor=0.6 (repeatable)
      --source S    imu (default) or poses: feed the fused devices from tracked poses instead of
                    raw IMU, as a runtime without raw IMU access (OpenXR) would. The reference
                    tracker always comes from raw IMU.
      --resample HZ sample every fused device at a fixed rate (latest sample at each tick, no
                    anti-alias filter), e.g. 90 for once per rendered frame. Default: as recorded.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

string folder = args[0];
string[]? hrDevices = null;
double motion = 8;
double minWindow = 6, maxMasked = 0.25;
double headWeight = 2, handWeight = 1, chestWeight = 3;
double timelineEvery = 5;
bool writeCsv = false;
var beatOptions = new Dictionary<string, double>();
var source = RecordingSource.Imu;
double resampleHz = 0;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--hr-devices": hrDevices = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--motion": motion = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-window": minWindow = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--max-masked": maxMasked = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--head-weight": headWeight = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--hand-weight": handWeight = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--chest-weight": chestWeight = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--timeline": timelineEvery = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--csv": writeCsv = true; break;
        case "--source": source = Enum.Parse<RecordingSource>(args[++i], ignoreCase: true); break;
        case "--resample": resampleHz = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--beat": { var kv = args[++i].Split('='); beatOptions[kv[0]] = double.Parse(kv[1], CultureInfo.InvariantCulture); break; }
        default: Console.Error.WriteLine($"Unknown option '{args[i]}'\n\n{Usage}"); return 1;
    }
}

var recording = Recording.Load(folder, new RecordingLoadOptions { Source = source, ResampleHz = resampleHz });
const string ReferenceKind = "Tracker";
if (source != RecordingSource.Imu || resampleHz > 0)
{
    // Score against the same reference as a raw-IMU replay: the chest tracker's own raw IMU.
    // No raw IMU (e.g. a Monado/OpenXR pose recording): fall back to the tracker's own poses at full rate.
    RecordedDevice? rawReference;
    try { rawReference = Recording.Load(folder).Devices.FirstOrDefault(d => d.Kind == ReferenceKind); }
    catch (FileNotFoundException)
    {
        rawReference = Recording.Load(folder, new RecordingLoadOptions { Source = RecordingSource.Poses })
            .Devices.FirstOrDefault(d => d.Kind == ReferenceKind);
        Console.WriteLine("Reference: tracker poses (no raw IMU in this recording)");
    }
    recording.Devices.RemoveAll(d => d.Kind == ReferenceKind);
    if (rawReference != null) recording.Devices.Add(rawReference);
}
var engine = new MonitorEngine(new EngineSettings { MotionThresholdDps = motion, MinWindowSeconds = minWindow, MaxMaskedFraction = maxMasked,
    HeadWeight = headWeight, HandWeight = handWeight, ChestWeight = chestWeight });
foreach (var d in recording.Devices)
{
    bool useHr = hrDevices?.Contains(d.Kind) ?? d.Kind != ReferenceKind;
    engine.AddDevice(d.Kind, d.DefaultRole, useForHeartRate: useHr);
}

// Merge all devices' samples in time order, as they would arrive live.
var events = new List<(double t, int dev, int idx)>();
for (int di = 0; di < recording.Devices.Count; di++)
    for (int si = 0; si < recording.Devices[di].Imu.Count; si++)
        events.Add((recording.Devices[di].Imu[si].Time, di, si));
events.Sort((a, b) => a.t.CompareTo(b.t));
double t0 = events[0].t;

var breathTrace = new List<BreathingState>();
engine.BreathingUpdated += (_, state) => breathTrace.Add(state);
void ApplyBeatOptions(BeatDetector det)
{
    foreach (var (k, v) in beatOptions)
        (typeof(BeatDetector).GetProperty(k) ?? throw new ArgumentException($"Unknown beat option {k}")).SetValue(det, v);
}
ApplyBeatOptions(engine.BeatDetector);
var beatEvents = new List<BeatEvent>();
engine.Beat += beatEvents.Add;

// A second engine fed only by the chest tracker gives reference beats (its measured ones).
bool hasReference = recording.Devices.Any(d => d.Kind == ReferenceKind);
var refEngine = new MonitorEngine(new EngineSettings { MotionThresholdDps = motion });
var refBeats = new List<BeatEvent>();
if (hasReference)
{
    refEngine.AddDevice(ReferenceKind, DeviceRole.Chest, useForHeartRate: true, useForBreathing: false);
    refEngine.Beat += b => { if (b.Measured) refBeats.Add(b); };
    refEngine.BeatDetector.LockRatio = 1.8; // the chest signal is strong; lock on only to unmistakable beats
    refEngine.BeatDetector.ProximityFloor = 1.0; // reference: strongest peak, no pull toward the prediction
}

var snapshots = new List<MonitorSnapshot>();
foreach (var (t, dev, idx) in events)
{
    var device = recording.Devices[dev];
    engine.Push(device.Kind, device.Imu[idx]);
    if (engine.TryUpdate(t, out var snap)) snapshots.Add(snap);
    if (hasReference && device.Kind == ReferenceKind)
    {
        refEngine.Push(ReferenceKind, device.Imu[idx]);
        refEngine.TryUpdate(t, out _);
    }
}

// ---- Report ------------------------------------------------------------------------------
var inv = CultureInfo.InvariantCulture;
string F(double v, string fmt = "0.0") => double.IsNaN(v) ? "-" : v.ToString(fmt, inv);
double Rel(double t) => t - t0;
double warmup = engine.Settings.WindowSeconds + 1;
var scored = snapshots.Where(s => Rel(s.Time) >= warmup).ToList();
double Reference(MonitorSnapshot s)
{
    var r = s.Devices.FirstOrDefault(d => d.Id == ReferenceKind);
    return r != null && r.Prominence >= 10 ? r.PeakBpm : double.NaN;
}
double Median(IEnumerable<double> xs)
{
    var a = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
    return a.Length == 0 ? double.NaN : a[a.Length / 2];
}
double Pct(int n, int d) => d == 0 ? double.NaN : 100.0 * n / d;

Console.WriteLine($"Recording: {Path.GetFileName(folder.TrimEnd('/', '\\'))}  label='{recording.Label}'  duration {F(Rel(events[^1].t), "0")} s");
Console.WriteLine($"Input: {source.ToString().ToLowerInvariant()}{(resampleHz > 0 ? $" resampled to {resampleHz.ToString(inv)} Hz" : "")}");
Console.WriteLine($"Pulse notes: before {F(recording.PulseBeforeBpm ?? double.NaN, "0")}, after {F(recording.PulseAfterBpm ?? double.NaN, "0")}");
Console.WriteLine($"Fused devices: {string.Join(", ", recording.Devices.Where(d => hrDevices?.Contains(d.Kind) ?? d.Kind != ReferenceKind).Select(d => d.Kind))}; reference: {ReferenceKind} (when its prominence >= 10)");

if (timelineEvery > 0)
{
    Console.WriteLine();
    var header = new StringBuilder($"{"t",5} {"HR",6} {"conf",5} {"ref",6} |");
    foreach (var d in recording.Devices) header.Append($" {Short(d.Kind),-17}|");
    header.Append(" breathing");
    Console.WriteLine(header);
    double next = 0;
    foreach (var s in snapshots)
    {
        if (Rel(s.Time) < next) continue;
        next += timelineEvery;
        var row = new StringBuilder($"{F(Rel(s.Time), "0"),5} {s.Heart.DisplayText,6} {F(s.Heart.Confidence, "0.00"),5} {F(Reference(s)),6} |");
        foreach (var d in s.Devices)
            row.Append($" {F(d.PeakBpm),5} x{F(d.Prominence, "0"),-3} {(d.Still ? "  " : "M ")}{F(d.MotionDps, "0.0"),4} |");
        if (s.Breathing is { } b)
            row.Append(b.Valid ? $" {F(b.RatePerMinute, "0.0")}/min{(b.IsHold ? $" HOLD {b.HoldSeconds:0}s" : "")}" : " (no signal)");
        Console.WriteLine(row);
    }
    Console.WriteLine("  columns per device: peak BPM, prominence, M = moving, motion deg/s");
}

Console.WriteLine();
Console.WriteLine($"== Summary (after {warmup:0} s warm-up, {scored.Count} updates) ==");
// Score what a user would see: the number when displayed, "--" otherwise.
var shown = scored.Where(s => s.Heart.Displayed).ToList();
var withRef = shown.Where(s => !double.IsNaN(Reference(s))).ToList();
var errors = withRef.Select(s => Math.Abs(s.Heart.Bpm - Reference(s))).ToList();
Console.WriteLine($"Fused HR:  median {F(Median(shown.Select(s => s.Heart.Bpm)))} BPM, shown {F(Pct(shown.Count, scored.Count), "0")}% of time (\"--\" otherwise)" +
    (errors.Count > 0 ? $", vs reference MAE {F(errors.Average(), "0.0")} BPM, within 5 BPM {F(Pct(errors.Count(e => e <= 5), errors.Count), "0")}% ({errors.Count} updates)" : ", no reference"));
if (recording.PulseBeforeBpm is { } pb || recording.PulseAfterBpm is not null)
{
    string Near(double fromT, double toT) => F(Median(shown.Where(s => Rel(s.Time) >= fromT && Rel(s.Time) <= toT).Select(s => s.Heart.Bpm)), "0");
    double end = Rel(scored[^1].Time);
    Console.WriteLine($"Counted pulse {F(recording.PulseBeforeBpm ?? double.NaN, "0")} -> {F(recording.PulseAfterBpm ?? double.NaN, "0")}; " +
        $"shown near start {Near(warmup, warmup + 15)}, near end {Near(end - 15, end)}");
}

// Beat events: count, how many were measured vs predicted, delay, and agreement with chest beats.
double shownSeconds = shown.Count * engine.Settings.UpdateIntervalSeconds;
Console.Write($"Beat events: {beatEvents.Count} ({F(beatEvents.Count / Math.Max(shownSeconds, 1) * 60, "0")}/min while shown), " +
    $"measured {F(Pct(beatEvents.Count(b => b.Measured), beatEvents.Count), "0")}%, median delay {F(Median(beatEvents.Select(b => b.DelaySeconds * 1000)), "0")} ms");
if (refBeats.Count > 10 && beatEvents.Count > 10)
{
    // Different devices can see the beat at slightly different times; remove the typical offset first.
    var offsets = beatEvents.Select(b => b.Time - refBeats.MinBy(r => Math.Abs(r.Time - b.Time)).Time).ToList();
    double typical = Median(offsets.Where(o => Math.Abs(o) < 0.3));
    double onBeat = Pct(offsets.Count(o => Math.Abs(o - typical) <= 0.12), offsets.Count);
    double measuredOnBeat = Pct(beatEvents.Where((b, i) => b.Measured && Math.Abs(offsets[i] - typical) <= 0.12).Count(), beatEvents.Count(b => b.Measured));
    double predictedOnBeat = Pct(beatEvents.Where((b, i) => !b.Measured && Math.Abs(offsets[i] - typical) <= 0.12).Count(), beatEvents.Count(b => !b.Measured));
    Console.WriteLine();
    Console.Write($"  vs chest beats: {F(onBeat, "0")}% of events within 120 ms (measured {F(measuredOnBeat, "0")}%, predicted {F(predictedOnBeat, "0")}%), typical offset {F(typical * 1000, "0")} ms");
}
Console.WriteLine();

Console.WriteLine();
Console.WriteLine($"  {"Device",-14}{"median",8}{"within6",9}{"prom",7}{"MAE ref",9}{"still",7}{"motion p50/p95",16}");
foreach (var d in recording.Devices)
{
    var rows = scored.Select(s => s.Devices.First(x => x.Id == d.Kind)).ToList();
    double med = Median(rows.Select(r => r.PeakBpm));
    double within = Pct(rows.Count(r => Math.Abs(r.PeakBpm - med) < 6), rows.Count);
    var errs = scored.Select(s => (s, r: s.Devices.First(x => x.Id == d.Kind)))
        .Where(p => !double.IsNaN(Reference(p.s)) && !double.IsNaN(p.r.PeakBpm) && d.Kind != ReferenceKind)
        .Select(p => Math.Abs(p.r.PeakBpm - Reference(p.s))).ToList();
    var motions = rows.Select(r => r.MotionDps).OrderBy(x => x).ToArray();
    Console.WriteLine($"  {d.Kind,-14}{F(med),8}{F(within, "0") + "%",9}{F(Median(rows.Select(r => r.Prominence))),7}{(errs.Count > 0 ? F(errs.Average()) : "-"),9}" +
        $"{F(Pct(rows.Count(r => r.Still), rows.Count), "0") + "%",7}{F(motions[motions.Length / 2], "0.0") + " / " + F(motions[(int)(motions.Length * 0.95)], "0.0"),16}");
}

var breathing = scored.Where(s => s.Breathing is { Valid: true }).Select(s => s.Breathing!.Value).ToList();
Console.WriteLine();
if (breathing.Count == 0)
    Console.WriteLine("Breathing: no valid signal (no chest device, or it was not moving with breathing).");
else
{
    Console.WriteLine($"Breathing: valid {F(Pct(breathing.Count, scored.Count), "0")}% of time, median rate {F(Median(breathing.Select(b => b.RatePerMinute)))} /min");
    var holds = new List<(double start, double len)>();
    BreathingState? prev = null;
    foreach (var s in scored)
    {
        if (s.Breathing is not { } b) continue;
        if (b.IsHold && !(prev?.IsHold ?? false)) holds.Add((Rel(b.Time) - b.HoldSeconds, b.HoldSeconds));
        else if (b.IsHold) holds[^1] = (holds[^1].start, b.HoldSeconds);
        prev = b;
    }
    Console.WriteLine(holds.Count == 0 ? "  no breath holds detected"
        : "  holds: " + string.Join(", ", holds.Select(h => $"{h.start:0}s for {h.len:0} s")));
}

if (writeCsv)
{
    string path = Path.Combine(folder, "replay.csv");
    using var w = new StreamWriter(path);
    var cols = new List<string> { "t", "display", "hr", "confidence", "held", "raw", "prominence", "reference" };
    foreach (var d in recording.Devices) cols.AddRange([$"{d.Kind}_peak", $"{d.Kind}_prom", $"{d.Kind}_still", $"{d.Kind}_motion"]);
    cols.AddRange(["breath_valid", "breath_rate", "breath_phase", "breath_hold", "breath_signal"]);
    w.WriteLine(string.Join(",", cols));
    foreach (var s in snapshots)
    {
        var v = new List<string> { F(Rel(s.Time), "0.00"), s.Heart.DisplayText, F(s.Heart.Bpm, "0.00"), F(s.Heart.Confidence, "0.000"), s.Heart.Held ? "1" : "0",
            F(s.Heart.RawBpm, "0.00"), F(s.Heart.Prominence, "0.00"), F(Reference(s), "0.00") };
        foreach (var d in s.Devices) v.AddRange([F(d.PeakBpm, "0.00"), F(d.Prominence, "0.00"), d.Still ? "1" : "0", F(d.MotionDps, "0.00")]);
        var b = s.Breathing;
        v.AddRange([b is { Valid: true } ? "1" : "0", F(b?.RatePerMinute ?? double.NaN, "0.0"), F(b?.Phase ?? double.NaN, "0.000"),
            b is { IsHold: true } ? "1" : "0", F(b?.SignalDegrees ?? double.NaN, "0.000")]);
        w.WriteLine(string.Join(",", v));
    }
    Console.WriteLine($"\nWrote {path}");

    string breathPath = Path.Combine(folder, "replay_breathing.csv");
    using var bw = new StreamWriter(breathPath);
    bw.WriteLine("t,valid,rate,phase,depth,hold,hold_s,signal,tilt");
    foreach (var b in breathTrace)
        bw.WriteLine(string.Join(",", F(Rel(b.Time), "0.00"), b.Valid ? "1" : "0", F(b.RatePerMinute, "0.0"), F(b.Phase, "0.000"),
            F(b.DepthDegrees, "0.00"), b.IsHold ? "1" : "0", F(b.HoldSeconds, "0.0"), F(b.SignalDegrees, "0.0000"), F(b.TiltDegrees, "0.0000")));
    Console.WriteLine($"Wrote {breathPath}");

    string beatPath = Path.Combine(folder, "replay_beats.csv");
    using var btw = new StreamWriter(beatPath);
    btw.WriteLine("t,measured,bpm,interval,strength,delay");
    foreach (var b in beatEvents)
        btw.WriteLine(string.Join(",", F(Rel(b.Time), "0.000"), b.Measured ? "1" : "0", F(b.Bpm, "0.0"), F(b.IntervalSeconds, "0.000"),
            F(b.Strength, "0.00"), F(b.DelaySeconds, "0.000")));
    Console.WriteLine($"Wrote {beatPath}");
}
return 0;

static string Short(string kind) => kind.Replace("Controller-", "Ctrl-");
