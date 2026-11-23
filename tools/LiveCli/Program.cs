using System.Globalization;
using HeartBeat.App;
using HeartBeat.Core;
using HeartBeat.Core.Heart;
using HeartBeat.Osc;
using HeartBeat.Tracking;
using Valve.VR;

const string Usage = """
    heartbeat-live — headless live heart rate from SteamVR raw IMU, printed once a second and
    sent to VRChat over OSC. Same pipeline and settings file as the Windows app.

    Usage:
      heartbeat-live [--settings FILE] [--no-osc] [--beats] [--seconds N] [--chest SERIAL]

      --settings  settings.json to use (default: $XDG_CONFIG_HOME/virtual-heartbeat-monitor/settings.json)
      --no-osc    print only; send nothing to VRChat
      --beats     print a line for every beat event
      --seconds   stop after N seconds
      --chest     treat the tracker with this serial as the chest tracker (also saved to settings)
      --chatbox   also show the heart rate in the VRChat chatbox (visible to others)
      --poses-stdin  read tracked poses from stdin instead of SteamVR (Monado: pipe in
                  tools/linux/tap --stream); line format in PoseStdinLive.cs

    SteamVR must already be running. On Linux, libopenvr_api.so is found through $OPENVR_API_LIB,
    $VR_OVERRIDE/bin/linux64, or Steam's SteamVR install.
    """;

string configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
    ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
string settingsPath = Path.Combine(configDir, "virtual-heartbeat-monitor", "settings.json");
bool osc = true, printBeats = false, chatbox = false, posesStdin = false;
double? seconds = null;
string? chest = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--settings": settingsPath = args[++i]; break;
        case "--no-osc": osc = false; break;
        case "--beats": printBeats = true; break;
        case "--seconds": seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--chest": chest = args[++i]; break;
        case "--chatbox": chatbox = true; break;
        case "--poses-stdin": posesStdin = true; break;
        case "-h" or "--help": Console.WriteLine(Usage); return 0;
        default: Console.Error.WriteLine($"Unknown option '{args[i]}'\n\n{Usage}"); return 1;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
var settings = AppSettings.LoadOrCreate(settingsPath);
if (chest != null && !settings.Devices.ChestTrackerSerials.Contains(chest))
{
    settings.Devices.ChestTrackerSerials.Add(chest);
    settings.Save(settingsPath);
}

if (chatbox) settings.Osc.Chatbox = true;

if (posesStdin)
{
    UdpOscSink? psink = osc && settings.Osc.Enabled ? new UdpOscSink(settings.Osc.Host, settings.Osc.Port) : null;
    VRChatOutput? pout = psink != null ? new VRChatOutput(psink, settings.Osc) : null;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.In.Close(); };
    int rc;
    try { rc = PoseStdinLive.Run(settings, pout, printBeats, seconds, pout != null ? $"{settings.Osc.Host}:{settings.Osc.Port}{(settings.Osc.Chatbox ? " + chatbox" : "")}" : "off"); }
    finally { pout?.Disconnect(); pout?.Dispose(); psink?.Dispose(); }
    return rc;
}

VrRuntime vr;
try { vr = VrRuntime.Connect(); }
catch (VrInitException e) when (e.Error == EVRInitError.Init_NoServerForBackgroundApp)
{
    Console.Error.WriteLine("SteamVR is not running. Start it first (on sayu: start-steamvr).");
    return 2;
}
catch (VrInitException e) { Console.Error.WriteLine(e.Message); return 2; }
catch (DllNotFoundException)
{
    Console.Error.WriteLine("OpenVR client library not found. Tried: " + string.Join(", ", OpenVrNative.Candidates()));
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

using (vr)
using (var session = new LiveSession(vr, settings))
{
    UdpOscSink? sink = null;
    VRChatOutput? output = null;
    if (osc && settings.Osc.Enabled)
    {
        sink = new UdpOscSink(settings.Osc.Host, settings.Osc.Port);
        output = new VRChatOutput(sink, settings.Osc);
        session.SnapshotReady += snap => output.OnSnapshot(snap, HostClock.Now);
        session.Beat += b => output.OnBeat(b);
        session.Breathing += b => output.OnBreathing(b, HostClock.Now);
    }
    if (printBeats)
        session.Beat += b => Console.WriteLine($"    beat {(b.Measured ? "measured " : "predicted")} {b.Bpm:0} BPM  interval {b.IntervalSeconds * 1000:0} ms  strength {b.Strength:0.00}");

    Console.WriteLine($"SteamVR {vr.RuntimeVersion}; settings {settingsPath}; OSC {(output != null ? $"{settings.Osc.Host}:{settings.Osc.Port}" : "off")}");
    foreach (var d in session.GetDevices())
        Console.WriteLine($"  {d.Id,-32} {d.Role,-5} {(d.Stream != null ? "raw IMU " + d.Stream.Path : d.Status)}");
    Console.WriteLine("Sit still for about 10 s. Ctrl+C to stop.");

    double start = HostClock.Now, nextPrint = start + 1;
    int knownDevices = session.GetDevices().Count;
    while (!stop.IsCancellationRequested && (seconds is not { } limit || HostClock.Now - start < limit))
    {
        Thread.Sleep(50);
        if (!session.Poll()) { Console.WriteLine("SteamVR is quitting."); break; }
        var devices = session.GetDevices();
        if (devices.Count != knownDevices)
        {
            knownDevices = devices.Count;
            foreach (var d in devices)
                Console.WriteLine($"  {d.Id,-32} {d.Role,-5} {(d.Stream != null ? "raw IMU " + d.Stream.Path : d.Status)}");
        }
        if (HostClock.Now < nextPrint) continue;
        nextPrint += 1;
        if (session.LatestSnapshot is not { } snap) { Console.WriteLine($"{HostClock.Now - start,6:0} s  waiting for data"); continue; }
        var h = snap.Heart;
        string bpm = h.DisplayBpm is { } v ? $"{v,5:0}" : "   --";
        string per = string.Join("  ", snap.Devices.Select(d =>
            $"{d.Id.Split(':')[0]}={(double.IsNaN(d.PeakBpm) ? "-" : d.PeakBpm.ToString("0"))}/{(double.IsNaN(d.Prominence) ? "-" : d.Prominence.ToString("0.0"))}{(d.Still ? "" : "*")}"));
        Console.WriteLine($"{HostClock.Now - start,6:0} s  HR {bpm}  conf {h.Confidence:0.00}  beats {session.BeatCount} ({session.MeasuredBeatCount} measured)  {per}{(snap.UserMoving ? "  moving" : "")}");
    }

    output?.Disconnect();
    output?.Dispose();
    sink?.Dispose();
}
return 0;
