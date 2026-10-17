using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitTrack.Library;

public enum EntryKind
{
    /// <summary>Saved from the replay buffer.</summary>
    Clip,

    /// <summary>A whole session recorded to disk.</summary>
    Recording,
}

/// <param name="Encoder">Short encoder name, e.g. amf, qsv, x264.</param>
public sealed record SidecarVideo(int Width, int Height, int Fps, int BitrateKbps, string Encoder);

/// <param name="Stream">Stream index in the MKV.</param>
/// <param name="Kind">game, app, microphone or system.</param>
/// <param name="Targets">As configured: exe names or PIDs for game / app, device name filters for a microphone.</param>
/// <param name="Source">What was last captured: exe name or device name; null if nothing was.</param>
public sealed record SidecarTrack(int Stream, string Title, string Kind, IReadOnlyList<string> Targets, string? Source);

/// <summary>
/// What the library knows about one recording or clip, stored next to it as JSON. Written after the
/// video file is complete, so a video without a sidecar is an unfinished save.
/// </summary>
/// <param name="File">The video's file name, relative to the sidecar.</param>
/// <param name="Thumbnail">JPEG file name relative to the sidecar, or null if none could be made.</param>
/// <param name="Game">The exe the Game track captured last, or null without a game track or game.</param>
/// <param name="ClipLengthSeconds">For a clip: the replay buffer length asked for.</param>
public sealed record Sidecar(
    int Version,
    EntryKind Kind,
    string File,
    string? Thumbnail,
    DateTimeOffset Created,
    double DurationSeconds,
    string? Game,
    SidecarVideo? Video,
    IReadOnlyList<SidecarTrack> AudioTracks,
    double? ClipLengthSeconds = null)
{
    // 2: kinds game/app (was process), track targets as a list, track source.
    public const int CurrentVersion = 2;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
