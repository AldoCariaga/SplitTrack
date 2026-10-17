namespace SplitTrack.Capture.Audio.Sessions;

/// <summary>A root process and every process in its tree that owns a render session.</summary>
public sealed record AudioProcessInfo(
    int RootPid,
    string ExeName,
    IReadOnlyList<int> SessionPids,
    bool IsActive,
    IReadOnlyList<string> DeviceNames);
