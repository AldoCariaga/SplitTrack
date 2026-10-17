namespace SplitTrack.Common.Platform;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\SplitTrack: settings and logs. Local, not roaming: monitors, GPUs and devices are per machine.</summary>
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SplitTrack");

    public static string Logs => Path.Combine(DataRoot, "logs");

    public static string Settings => Path.Combine(DataRoot, "settings.json");
}
