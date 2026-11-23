using HeartBeat.Probe;
using HeartBeat.Tracking;
using Valve.VR;

Options options;
try
{
    options = Options.Parse(args);
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 1;
}

if (options.ShowHelp)
{
    Console.WriteLine(Options.Usage);
    return 0;
}

using var timerResolution = new TimerResolutionScope();

VrRuntime vr;
try
{
    vr = VrRuntime.Connect();
}
catch (VrInitException e) when (e.Error == EVRInitError.Init_NoServerForBackgroundApp)
{
    Console.Error.WriteLine("SteamVR is not running. Start SteamVR with your headset connected, then run Probe again.");
    return 2;
}
catch (VrInitException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
catch (DllNotFoundException)
{
    Console.Error.WriteLine(OperatingSystem.IsWindows()
        ? "openvr_api.dll was not found next to Probe.exe."
        : "libopenvr_api.so was not found. Tried: " + string.Join(", ", OpenVrNative.Candidates()) + " (set OPENVR_API_LIB to its path).");
    return 2;
}

using (vr)
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };

    return options.Command switch
    {
        "check" => CheckCommand.Run(vr, options, stop.Token),
        "record" => RecordCommand.Run(vr, options, stop.Token),
        "live" => LiveCommand.Run(vr, options, stop.Token),
        _ => Unknown(options.Command),
    };
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    Console.Error.WriteLine(Options.Usage);
    return 1;
}
