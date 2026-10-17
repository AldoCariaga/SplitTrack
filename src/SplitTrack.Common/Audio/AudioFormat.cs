namespace SplitTrack.Common.Audio;

public enum SampleType
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>Interleaved PCM layout of one audio stream.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, SampleType SampleType, uint ChannelMask = 0)
{
    public static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private const uint SpeakerStereo = 0x3;

    public static AudioFormat Float48Stereo => new(48_000, 2, SampleType.Float32, SpeakerStereo);

    public static AudioFormat Pcm16Stereo44 => new(44_100, 2, SampleType.Pcm16, SpeakerStereo);

    public int BitsPerSample => SampleType switch
    {
        SampleType.Pcm16 => 16,
        SampleType.Pcm24 => 24,
        _ => 32,
    };

    public int BytesPerSample => BitsPerSample / 8;

    public int BlockAlign => BytesPerSample * Channels;

    public int BytesPerSecond => BlockAlign * SampleRate;

    public Guid SubType => SampleType == SampleType.Float32 ? SubTypeIeeeFloat : SubTypePcm;

    /// <summary>The device's mask, or the standard one for common channel counts when it gave none.</summary>
    public uint EffectiveChannelMask => ChannelMask != 0 ? ChannelMask : Channels switch
    {
        1 => 0x4,
        2 => 0x3,
        4 => 0x33,
        6 => 0x3F,
        8 => 0x63F,
        _ => 0,
    };

    public override string ToString() =>
        $"{SampleRate / 1000.0:0.#} kHz {Channels}ch {(SampleType == SampleType.Float32 ? "float32" : $"pcm{BitsPerSample}")}";
}
