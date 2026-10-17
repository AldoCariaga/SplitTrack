namespace SplitTrack.Capture.Audio;

/// <summary>Receives packets on the source's capture thread.</summary>
public interface IAudioSink
{
    void OnPacket(in AudioPacket packet);

    /// <summary>Called when the source delivered nothing for a whole wait period.</summary>
    void OnIdle(long nowQpc);
}
