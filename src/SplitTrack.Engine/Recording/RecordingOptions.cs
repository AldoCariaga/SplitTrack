using SplitTrack.Encode.Video;

namespace SplitTrack.Engine.Recording;

public enum AudioTrackKind
{
    /// <summary>The game's process tree, which the System track leaves out. At most one per session.</summary>
    Game,

    /// <summary>Another process tree, e.g. Discord, which the System track also leaves out.</summary>
    App,

    /// <summary>A capture device: the default one, or the first present that matches <see cref="AudioTrackOptions.Targets"/>.</summary>
    Microphone,

    /// <summary>Everything except the game's and app tracks' process trees (everything while none runs). At most one per session.</summary>
    System,
}

/// <param name="Targets">
/// Game / App: exe names or PIDs; the first one running is captured, and followed across restarts.
/// Game only: <see cref="FullScreen"/> stands for whatever program is full screen in front.
/// Microphone: device name filters; the first one present wins, none means the default device.
/// </param>
public sealed record AudioTrackOptions(string Title, AudioTrackKind Kind, IReadOnlyList<string>? Targets = null)
{
    /// <summary>The game target that detects the full-screen program instead of naming one.</summary>
    public const string FullScreen = "auto";

    public IReadOnlyList<string> Targets { get; init; } = Targets ?? [];

    public static bool IsFullScreen(string target) => string.Equals(target, FullScreen, StringComparison.OrdinalIgnoreCase);
}

/// <param name="Monitor">Monitor index from <c>CaptureTargets.ListMonitors</c>; used when <paramref name="Window"/> is null.</param>
/// <param name="Window">Window handle, exe name or title text.</param>
/// <param name="MaxHeight">Scale down to this height; null keeps the capture size.</param>
/// <param name="Encoder">Null picks automatically.</param>
public sealed record VideoOptions(
    int Monitor = 0,
    string? Window = null,
    int Fps = 60,
    int BitrateKbps = 20_000,
    int? MaxHeight = null,
    VideoEncoderKind? Encoder = null,
    bool Cursor = true,
    int Adapter = 0);

/// <param name="BufferSeconds">How much is kept in memory, and so the length of a saved clip.</param>
/// <param name="LibraryDirectory">Where clips are saved.</param>
public sealed record ReplayOptions(double BufferSeconds, string LibraryDirectory);

/// <param name="OutputPath">The whole session as one MKV (+ sidecar and thumbnail at the end), or null for none.</param>
/// <param name="Video">Null records audio only.</param>
/// <param name="AudioTracks">In the order they appear in the file.</param>
/// <param name="Replay">Keep a replay buffer that clips can be saved from, or null for none.</param>
public sealed record RecordingOptions(
    string? OutputPath,
    VideoOptions? Video,
    IReadOnlyList<AudioTrackOptions> AudioTracks,
    int AudioBitrateKbps = 160,
    ReplayOptions? Replay = null);

/// <summary>A recording cannot start because of something the user asked for (no such window, nothing to record...).</summary>
public sealed class RecordingSetupException(string message) : Exception(message);
