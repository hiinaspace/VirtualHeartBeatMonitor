using System.Reflection;
using System.Runtime.InteropServices;

namespace HeartBeat.Tracking;

/// <summary>
/// Finds the native OpenVR client library. On Windows it is openvr_api.dll next to the exe.
/// On Linux, libopenvr_api.so is looked up in this order: $OPENVR_API_LIB (a file),
/// next to the exe, $VR_OVERRIDE/bin/linux64, then Steam's SteamVR install.
/// </summary>
public static class OpenVrNative
{
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1 || OperatingSystem.IsWindows()) return;
        NativeLibrary.SetDllImportResolver(typeof(Valve.VR.OpenVR).Assembly, Resolve);
    }

    /// <summary>Candidate paths for libopenvr_api.so, in the order they are tried.</summary>
    public static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("OPENVR_API_LIB") is { Length: > 0 } explicitPath)
            yield return explicitPath;
        yield return Path.Combine(AppContext.BaseDirectory, "libopenvr_api.so");
        if (Environment.GetEnvironmentVariable("VR_OVERRIDE") is { Length: > 0 } overrideDir)
            yield return Path.Combine(overrideDir, "bin", "linux64", "libopenvr_api.so");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var steam in new[] { ".local/share/Steam", ".steam/steam", ".steam/root" })
            yield return Path.Combine(home, steam, "steamapps/common/SteamVR/bin/linux64/libopenvr_api.so");
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "openvr_api") return IntPtr.Zero;
        foreach (var path in Candidates())
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        return IntPtr.Zero; // fall back to the default probing (LD_LIBRARY_PATH)
    }
}
