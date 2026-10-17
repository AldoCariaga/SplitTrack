using SplitTrack.Capture.Audio;
using SplitTrack.Common.Audio;
using SplitTrack.Encode.Audio;
using SplitTrack.FFmpeg;

namespace SplitTrack.Engine.Audio;

/// <summary>End of a track's chain: meters the aligned 48 kHz stereo and encodes it to AAC.</summary>
internal sealed class AacTrackWriter(AacEncoder encoder, Action<MediaPacket> output) : IAudioFrameWriter
{
    public PeakMeter Meter { get; } = new(SampleType.Float32);

    public long Frames => encoder.FramesIn;

    public void WriteFrames(ReadOnlySpan<byte> data, int frames)
    {
        Meter.Process(data);
        encoder.Write(data, output);
    }

    public void WriteSilence(long frames) => encoder.WriteSilence(frames, output);

    public void Flush() => encoder.Flush(output);
}
