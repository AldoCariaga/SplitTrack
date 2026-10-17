namespace SplitTrack.Capture.Audio;

/// <summary>
/// One WASAPI packet, valid only for the duration of <see cref="IAudioSink.OnPacket"/>.
/// <see cref="Data"/> is empty when the frames are all zero; WASAPI packets flagged <see cref="Silent"/> are.
/// </summary>
public readonly ref struct AudioPacket
{
    public AudioPacket(ReadOnlySpan<byte> data, int frames, long qpcTicks, bool silent, bool discontinuity, bool timestampError)
    {
        Data = data;
        Frames = frames;
        QpcTicks = qpcTicks;
        Silent = silent;
        Discontinuity = discontinuity;
        TimestampError = timestampError;
    }

    public ReadOnlySpan<byte> Data { get; }

    public int Frames { get; }

    /// <summary>Capture time of the first frame, in <see cref="Common.Time.QpcClock"/> ticks.</summary>
    public long QpcTicks { get; }

    public bool Silent { get; }

    public bool Discontinuity { get; }

    /// <summary>The device had no timestamp; <see cref="QpcTicks"/> was estimated from the clock.</summary>
    public bool TimestampError { get; }
}
