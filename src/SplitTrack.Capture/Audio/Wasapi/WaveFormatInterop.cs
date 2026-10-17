using SplitTrack.Common.Audio;
using Windows.Win32.Media.Audio;

namespace SplitTrack.Capture.Audio.Wasapi;

internal static class WaveFormatInterop
{
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;

    public static WAVEFORMATEXTENSIBLE ToNative(AudioFormat format)
    {
        var native = new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = WaveFormatExtensible,
                nChannels = (ushort)format.Channels,
                nSamplesPerSec = (uint)format.SampleRate,
                nAvgBytesPerSec = (uint)format.BytesPerSecond,
                nBlockAlign = (ushort)format.BlockAlign,
                wBitsPerSample = (ushort)format.BitsPerSample,
                cbSize = 22,
            },
            dwChannelMask = format.EffectiveChannelMask,
            SubFormat = format.SubType,
        };
        native.Samples.wValidBitsPerSample = (ushort)format.BitsPerSample;
        return native;
    }

    public static unsafe AudioFormat FromNative(WAVEFORMATEX* native)
    {
        Guid subType;
        uint channelMask = 0;

        if (native->wFormatTag == WaveFormatExtensible && native->cbSize >= 22)
        {
            var extensible = (WAVEFORMATEXTENSIBLE*)native;
            subType = extensible->SubFormat;
            channelMask = extensible->dwChannelMask;
        }
        else if (native->wFormatTag == WaveFormatIeeeFloat)
        {
            subType = AudioFormat.SubTypeIeeeFloat;
        }
        else if (native->wFormatTag == WaveFormatPcm)
        {
            subType = AudioFormat.SubTypePcm;
        }
        else
        {
            throw new NotSupportedException($"Unsupported wave format tag 0x{native->wFormatTag:X4}.");
        }

        int bits = native->wBitsPerSample;
        SampleType sampleType = (subType == AudioFormat.SubTypeIeeeFloat, bits) switch
        {
            (true, 32) => SampleType.Float32,
            (false, 16) => SampleType.Pcm16,
            (false, 24) => SampleType.Pcm24,
            (false, 32) => SampleType.Pcm32,
            _ => throw new NotSupportedException($"Unsupported sample layout: {bits}-bit, subtype {subType}."),
        };

        return new AudioFormat((int)native->nSamplesPerSec, native->nChannels, sampleType, channelMask);
    }
}
