using SplitTrack.Encode.Video;

namespace SplitTrack.Export;

/// <param name="Volume">Gain applied before mixing; 1 is as recorded.</param>
public sealed record TrackMix(SourceTrack Track, double Volume);

/// <summary>What to export: which tracks at what volume, which part, and limits on the result.</summary>
/// <param name="Tracks">Mixed into the one audio track of the MP4; empty for no audio.</param>
public sealed record ExportRequest(ExportSource Source, IReadOnlyList<TrackMix> Tracks, string OutputPath)
{
    /// <summary>Start in seconds; null is the beginning.</summary>
    public double? From { get; init; }

    /// <summary>End in seconds; null is the end.</summary>
    public double? To { get; init; }

    /// <summary>Start on exactly <see cref="From"/> even if that means re-encoding the video.</summary>
    public bool Precise { get; init; }

    /// <summary>The MP4 must not be larger (e.g. Discord's upload limit).</summary>
    public long? MaxBytes { get; init; }

    /// <summary>Scale the video down to this height (re-encodes).</summary>
    public int? MaxHeight { get; init; }

    /// <summary>Video bitrate when re-encoding (re-encodes).</summary>
    public int? VideoBitrateKbps { get; init; }

    /// <summary>Re-encode the video even when a copy would do.</summary>
    public bool Reencode { get; init; }

    /// <summary>Encoder for when the video is re-encoded; null picks the first that works.</summary>
    public VideoEncoderKind? Encoder { get; init; }

    public int AudioBitrateKbps { get; init; } = 160;
}

public enum VideoMode
{
    None,

    /// <summary>The recorded H.264 packets as they are: fast, no quality loss, starts on a keyframe.</summary>
    Copy,
    Encode,
}

/// <summary>How a request will be carried out, decided before anything is written.</summary>
/// <param name="Start">Start in the source, seconds. A copy starts on the keyframe at or before the requested start.</param>
/// <param name="Notes">Things worth telling the user, e.g. that the start moved to a keyframe.</param>
public sealed record ExportPlan(
    ExportRequest Request,
    double Start,
    double End,
    VideoMode Video,
    VideoEncoderKind? Encoder,
    int? VideoBitrateKbps,
    int? OutputHeight,
    IReadOnlyList<string> Notes)
{
    public double Duration => End - Start;
}

/// <param name="Attempt">1, or higher when an export was redone to fit the size limit.</param>
public readonly record struct ExportProgress(ExportPlan Plan, int Attempt, double DoneSeconds, double? Speed, long Bytes)
{
    public double Fraction => Plan.Duration > 0 ? Math.Clamp(DoneSeconds / Plan.Duration, 0, 1) : 0;
}

/// <param name="Plan">The plan of the attempt that was kept.</param>
public sealed record ExportResult(string Path, long Bytes, ExportPlan Plan, int Attempts, TimeSpan Elapsed);
