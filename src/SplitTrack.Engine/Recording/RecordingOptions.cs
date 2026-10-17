using SplitTrack.Encode.Video;

namespace SplitTrack.Engine.Recording;

public enum AudioTrackKind
{
    /// <summary>One process tree, followed across restarts when given by exe name.</summary>
    Process,

    /// <summary>Everything except the game's process tree (everything while no game runs).</summary>
    System,

    /// <summary>A capture device: the default one, or the first whose name contains <see cref="AudioTrackOptions.Target"/>.</summary>
    Microphone,
}

/// <param name="Target">Process: exe name or PID. Microphone: name filter, or null for the default device.</param>
/// <param name="IsGame">The process the System track leaves out.</param>
public sealed record AudioTrackOptions(string Title, AudioTrackKind Kind, string? Target = null, bool IsGame = false);

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
