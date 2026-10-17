using SplitTrack.Capture.Audio.Timeline;
using SplitTrack.Capture.Video;
using SplitTrack.Engine.Video;

namespace SplitTrack.Engine.Recording;

/// <param name="Source">What the track records right now, or null while it has no source.</param>
/// <param name="PeakDbfs">Loudest sample since the previous status (-120 for silence).</param>
/// <param name="CorrectionPpm">Rate correction applied to the source's clock.</param>
/// <param name="Frames">Track length so far at 48 kHz.</param>
public sealed record AudioTrackStatus(
    string Title,
    string? Source,
    AlignerStats Stats,
    double PeakDbfs,
    double CorrectionPpm,
    long Frames,
    Exception? Fault);

public sealed record VideoStatus(string Description, PipelineStats Pipeline, CaptureStats Capture, int Fps, Exception? Fault);

/// <param name="ElapsedTicks">Since the recording's first frame, in QPC ticks; 0 before it starts.</param>
public sealed record SessionStatus(
    long ElapsedTicks,
    VideoStatus? Video,
    IReadOnlyList<AudioTrackStatus> Audio,
    long BytesWritten,
    Exception? WriteError);
