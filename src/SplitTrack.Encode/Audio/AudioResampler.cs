using System.Numerics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Time;
using SplitTrack.FFmpeg;

namespace SplitTrack.Encode.Audio;

/// <summary>
/// Any capture format → 48 kHz float stereo (interleaved) through libswresample, with soft
/// compensation for a source clock that runs slightly fast or slow.
/// </summary>
public sealed unsafe class AudioResampler : IDisposable
{
    public static readonly AudioFormat OutputFormat = AudioFormat.Float48Stereo;

    private SwrContext* _swr;
    private byte[] _output = [];
    private byte[] _input = [];

    public AudioResampler(AudioFormat input)
    {
        Input = input;

        AVChannelLayout inLayout;
        uint mask = input.EffectiveChannelMask;
        if (mask != 0 && BitOperations.PopCount(mask) == input.Channels)
        {
            ffmpeg.av_channel_layout_from_mask(&inLayout, mask);
        }
        else
        {
            ffmpeg.av_channel_layout_default(&inLayout, input.Channels);
        }

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_from_mask(&outLayout, OutputFormat.EffectiveChannelMask);

        SwrContext* swr = null;
        try
        {
            FFmpegException.Check(
                ffmpeg.swr_alloc_set_opts2(
                    &swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, OutputFormat.SampleRate,
                    &inLayout, ToSampleFormat(input.SampleType), input.SampleRate, 0, null),
                "swr_alloc_set_opts2");

            // Resample even at equal rates, so turning on compensation later never re-initialises.
            FFmpegException.Check(ffmpeg.av_opt_set_int(swr, "flags", ffmpeg.SWR_FLAG_RESAMPLE, 0), "swr flags");
            FFmpegException.Check(ffmpeg.swr_init(swr), "swr_init");
            _swr = swr;
        }
        catch
        {
            ffmpeg.swr_free(&swr);
            throw;
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&inLayout);
            ffmpeg.av_channel_layout_uninit(&outLayout);
        }
    }

    public AudioFormat Input { get; }

    /// <summary>Input still inside the resampler, as time: output lags input by this much.</summary>
    public long DelayTicks => ffmpeg.swr_get_delay(Native, QpcClock.TicksPerSecond);

    /// <summary>
    /// Converts <paramref name="frames"/> input frames (zeros when <paramref name="input"/> is empty).
    /// The result is 48 kHz float stereo and is valid until the next call.
    /// </summary>
    public ReadOnlySpan<byte> Convert(ReadOnlySpan<byte> input, int frames)
    {
        int inBytes = frames * Input.BlockAlign;
        if (input.IsEmpty)
        {
            input = Zeroed(ref _input, inBytes);
        }
        else if (Input.SampleType == SampleType.Pcm24)
        {
            input = ExpandPcm24(input, frames * Input.Channels);
        }

        int capacity = ffmpeg.swr_get_out_samples(Native, frames);
        EnsureCapacity(ref _output, capacity * OutputFormat.BlockAlign);

        fixed (byte* inPtr = input)
        fixed (byte* outPtr = _output)
        {
            byte* inPlane = inPtr;
            byte* outPlane = outPtr;
            int produced = FFmpegException.Check(ffmpeg.swr_convert(Native, &outPlane, capacity, &inPlane, frames), "swr_convert");
            return new ReadOnlySpan<byte>(_output, 0, produced * OutputFormat.BlockAlign);
        }
    }

    /// <summary>Adds (or with a negative delta removes) <paramref name="sampleDelta"/> frames spread over the next <paramref name="distance"/> output frames.</summary>
    public void SetCompensation(int sampleDelta, int distance) =>
        FFmpegException.Check(ffmpeg.swr_set_compensation(Native, sampleDelta, distance), "swr_set_compensation");

    public void Dispose()
    {
        SwrContext* swr = _swr;
        _swr = null;
        ffmpeg.swr_free(&swr);
    }

    private SwrContext* Native => _swr != null ? _swr : throw new ObjectDisposedException(nameof(AudioResampler));

    // FFmpeg has no packed 24-bit format; widen to 32-bit.
    private ReadOnlySpan<byte> ExpandPcm24(ReadOnlySpan<byte> input, int samples)
    {
        EnsureCapacity(ref _input, samples * 4);
        Span<int> wide = MemoryMarshal.Cast<byte, int>(_input.AsSpan(0, samples * 4));
        for (int i = 0; i < samples; i++)
        {
            wide[i] = (input[3 * i] << 8) | (input[3 * i + 1] << 16) | (input[3 * i + 2] << 24);
        }

        return _input.AsSpan(0, samples * 4);
    }

    private static AVSampleFormat ToSampleFormat(SampleType type) => type switch
    {
        SampleType.Float32 => AVSampleFormat.AV_SAMPLE_FMT_FLT,
        SampleType.Pcm16 => AVSampleFormat.AV_SAMPLE_FMT_S16,
        _ => AVSampleFormat.AV_SAMPLE_FMT_S32,
    };

    private static ReadOnlySpan<byte> Zeroed(ref byte[] buffer, int bytes)
    {
        EnsureCapacity(ref buffer, bytes);
        Span<byte> span = buffer.AsSpan(0, bytes);
        span.Clear();
        return span;
    }

    private static void EnsureCapacity(ref byte[] buffer, int bytes)
    {
        if (buffer.Length < bytes)
        {
            buffer = new byte[Math.Max(bytes, buffer.Length * 2)];
        }
    }
}
