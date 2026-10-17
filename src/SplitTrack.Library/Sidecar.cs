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
/// <param name="Kind">process, system or microphone.</param>
/// <param name="Target">Exe name or PID for a process track, name filter for a microphone.</param>
public sealed record SidecarTrack(int Stream, string Title, string Kind, string? Target);

/// <summary>
/// What the library knows about one recording or clip, stored next to it as JSON. Written after the
/// video file is complete, so a video without a sidecar is an unfinished save.
/// </summary>
/// <param name="File">The video's file name, relative to the sidecar.</param>
/// <param name="Thumbnail">JPEG file name relative to the sidecar, or null if none could be made.</param>
/// <param name="Game">The game's exe name or PID as configured, or null without a game track.</param>
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
    public const int CurrentVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
