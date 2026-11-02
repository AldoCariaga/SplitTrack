using SplitTrack.Capture.Audio;
using SplitTrack.Capture.Audio.Timeline;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Time;
using SplitTrack.Encode.Audio;

namespace SplitTrack.Engine.Audio;

/// <summary>
/// Between one source and its track's aligner: converts each packet to 48 kHz float stereo and
/// steers the resampler so the source's clock follows QPC. A device's sample clock drifts slightly
/// from the system clock, so without this its track would slowly fall out of sync over a long recording.
/// </summary>
internal sealed class ResamplingSink : IAudioSink, IDisposable
{
    // Correct half the smoothed drift per second, never faster than 1000 ppm (inaudible).
    private const double CorrectionGain = 0.5;
    private const double MaxCorrectionPpm = 1000;

    private static readonly int ControlInterval = AudioResampler.OutputFormat.SampleRate;

    private readonly AudioResampler _resampler;
    private readonly AudioTrackAligner _aligner;
    private readonly int _inputRate;
    private int _sinceControl;
    private int _correctionFrames;

    public ResamplingSink(AudioFormat input, AudioTrackAligner aligner)
    {
        _resampler = new AudioResampler(input);
        _aligner = aligner;
        _inputRate = input.SampleRate;
    }

    /// <summary>Current rate correction: positive stretches the source (its clock is slow).</summary>
    public double CorrectionPpm => Volatile.Read(ref _correctionFrames) * 1e6 / ControlInterval;

    public void OnPacket(in AudioPacket packet)
    {
        ReadOnlySpan<byte> output = _resampler.Convert(packet.Silent ? ReadOnlySpan<byte>.Empty : packet.Data, packet.Frames);
        int frames = output.Length / AudioResampler.OutputFormat.BlockAlign;
        if (frames == 0)
        {
            return;
        }

        // What leaves the resampler now ends where its input ended, minus what it still holds.
        long inputEnd = packet.QpcTicks + QpcClock.FramesToTicks(packet.Frames, _inputRate);
        long outputStart = inputEnd - _resampler.DelayTicks - QpcClock.FramesToTicks(frames, AudioResampler.OutputFormat.SampleRate);
        _aligner.OnPacket(new AudioPacket(output, frames, outputStart, packet.Silent, packet.Discontinuity, packet.TimestampError));

        _sinceControl += frames;
        if (_sinceControl >= ControlInterval)
        {
            _sinceControl = 0;
            UpdateCorrection();
        }
    }

    public void OnIdle(long nowQpc) => _aligner.OnIdle(nowQpc);

    public void Dispose() => _resampler.Dispose();

    private void UpdateCorrection()
    {
        double limit = MaxCorrectionPpm * ControlInterval / 1e6;
        int delta = (int)Math.Round(Math.Clamp(_aligner.SmoothedDriftFrames * CorrectionGain, -limit, limit));

        // Compensation lasts one interval, so it is renewed every time.
        _resampler.SetCompensation(delta, ControlInterval);
        Volatile.Write(ref _correctionFrames, delta);
    }
}
