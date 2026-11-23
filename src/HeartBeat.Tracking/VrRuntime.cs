using System.Runtime.InteropServices;
using Valve.VR;

namespace HeartBeat.Tracking;

public sealed class VrInitException(EVRInitError error)
    : Exception($"OpenVR init failed: {error} ({Valve.VR.OpenVR.GetStringForHmdError(error)})")
{
    public EVRInitError Error { get; } = error;
}

/// <summary>Connection to a running SteamVR instance as a background app (never launches SteamVR).</summary>
public sealed class VrRuntime : IDisposable
{
    private static readonly uint EventSize = (uint)Marshal.SizeOf<VREvent_t>();
    private bool _disposed;

    static VrRuntime() => OpenVrNative.Install();

    public CVRSystem System { get; }
    public CVRIOBuffer? IOBuffer { get; }

    private VrRuntime(CVRSystem system)
    {
        System = system;
        IOBuffer = Valve.VR.OpenVR.IOBuffer;
    }

    public static VrRuntime Connect(EVRApplicationType type = EVRApplicationType.VRApplication_Background)
    {
        var error = EVRInitError.None;
        var system = Valve.VR.OpenVR.Init(ref error, type);
        if (error != EVRInitError.None || system == null)
            throw new VrInitException(error);
        return new VrRuntime(system);
    }

    /// <summary>
    /// Connects as an overlay app (needed to draw overlays and register auto-start), but only if
    /// SteamVR is already running: an overlay-type init would otherwise launch SteamVR itself.
    /// Throws <see cref="VrInitException"/> with Init_NoServerForBackgroundApp when it isn't running.
    /// </summary>
    public static VrRuntime ConnectOverlayIfRunning()
    {
        using (Connect(EVRApplicationType.VRApplication_Background)) { }
        return Connect(EVRApplicationType.VRApplication_Overlay);
    }

    public CVROverlay? Overlay => Valve.VR.OpenVR.Overlay;
    public CVRApplications? Applications => Valve.VR.OpenVR.Applications;

    public string RuntimeVersion => System.GetRuntimeVersion();

    /// <summary>Drains pending events. Returns false when SteamVR asked us to quit.</summary>
    public bool PumpEvents()
    {
        var ev = new VREvent_t();
        while (System.PollNextEvent(ref ev, EventSize))
        {
            if (ev.eventType == (uint)EVREventType.VREvent_Quit)
            {
                System.AcknowledgeQuit_Exiting();
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Valve.VR.OpenVR.Shutdown();
    }
}
