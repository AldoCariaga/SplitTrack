namespace SplitTrack.Capture.Audio;

/// <summary>Downstream of an aligner: receives a gap-free run of frames.</summary>
public interface IAudioFrameWriter
{
    void WriteFrames(ReadOnlySpan<byte> data, int frames);

    void WriteSilence(long frames);
}
