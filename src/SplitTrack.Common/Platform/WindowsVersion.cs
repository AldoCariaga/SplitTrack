using System.Runtime.InteropServices;

namespace SplitTrack.Common.Platform;

public static class WindowsVersion
{
    /// <summary>First build with WASAPI process loopback (Windows Server 2022 / Windows 11).</summary>
    public const int ProcessLoopbackMinBuild = 20348;

    // .NET reads this through RtlGetVersion, so a compatibility manifest cannot lie about it.
    public static Version Current => Environment.OSVersion.Version;

    public static bool SupportsProcessLoopback =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, ProcessLoopbackMinBuild);

    /// <summary>Null when this machine is supported; otherwise a message to show before exiting.</summary>
    public static string? GetUnsupportedReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "SplitTrack only runs on Windows.";
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            return "SplitTrack needs 64-bit Windows on an x64 processor.";
        }

        if (!SupportsProcessLoopback)
        {
            return $"SplitTrack needs Windows 11 (build {ProcessLoopbackMinBuild} or newer) for per-app audio capture. " +
                   $"This PC runs build {Current.Build}. Please update Windows and try again.";
        }

        return null;
    }
}
