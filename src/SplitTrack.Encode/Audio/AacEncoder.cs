using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.FFmpeg;

namespace SplitTrack.Encode.Audio;

/// <summary>
/// AAC-LC (FFmpeg's native encoder) for 48 kHz float stereo. Takes any number of interleaved
/// frames, cuts them into the encoder's 1024-frame frames and hands out packets as they are ready;
/// pts is the sample index on the recording's timeline.
/// </summary>
public sealed unsafe class AacEncoder : IDisposable
{
    public static readonly AudioFormat InputFormat = AudioFormat.Float48Stereo;

    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private int _filled;
    private long _nextPts;

    public AacEncoder(int bitrateKbps)
    {
        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null)
        {
            throw new FFmpegException("This FFmpeg build has no AAC encoder.", ffmpeg.AVERROR_ENCODER_NOT_FOUND);
        }

        try
        {
            _codec = ffmpeg.avcodec_alloc_context3(codec);
            _codec->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            _codec->sample_rate = InputFormat.SampleRate;
            ffmpeg.av_channel_layout_from_mask(&_codec->ch_layout, InputFormat.EffectiveChannelMask);
            _codec->bit_rate = bitrateKbps * 1000L;
            _codec->time_base = TimeBase;
            _codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            FFmpegException.Check(ffmpeg.avcodec_open2(_codec, codec, null), "Opening the AAC encoder");

            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            _frame->sample_rate = InputFormat.SampleRate;
            _frame->nb_samples = _codec->frame_size;
            FFmpegException.Check(ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_codec->ch_layout), "av_channel_layout_copy");
            FFmpegException.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer");
            _packet = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }

        Log.Debug($"Opened aac: {bitrateKbps} kbps, frame size {_codec->frame_size}, priming {_codec->initial_padding}");
    }

    public AVRational TimeBase { get; } = new() { num = 1, den = InputFormat.SampleRate };

    /// <summary>Frames given to <see cref="Write"/> and <see cref="WriteSilence"/> so far.</summary>
    public long FramesIn { get; private set; }

    /// <summary>Encodes interleaved stereo float frames.</summary>
    public void Write(ReadOnlySpan<byte> interleaved, Action<MediaPacket> output)
    {
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(interleaved);
        int frames = samples.Length / 2;
        FramesIn += frames;
        int offset = 0;
        while (offset < frames)
        {
            int count = Math.Min(frames - offset, _frame->nb_samples - _filled);
            PrepareFrame();
            float* left = (float*)_frame->data[0] + _filled;
            float* right = (float*)_frame->data[1] + _filled;
            for (int i = 0; i < count; i++)
            {
                left[i] = samples[2 * (offset + i)];
                right[i] = samples[2 * (offset + i) + 1];
            }

            _filled += count;
            offset += count;
            SendIfFull(output);
        }
    }

    public void WriteSilence(long frames, Action<MediaPacket> output)
    {
        FramesIn += frames;
        while (frames > 0)
        {
            int count = (int)Math.Min(frames, _frame->nb_samples - _filled);
            PrepareFrame();
            new Span<float>((float*)_frame->data[0] + _filled, count).Clear();
            new Span<float>((float*)_frame->data[1] + _filled, count).Clear();
            _filled += count;
            frames -= count;
            SendIfFull(output);
        }
    }

    /// <summary>Encodes the last, partial frame and drains the encoder.</summary>
    public void Flush(Action<MediaPacket> output)
    {
        if (_filled > 0)
        {
            // The AAC encoder accepts a short last frame.
            _frame->nb_samples = _filled;
            Send(_frame, output);
        }

        Send(null, output);
    }

    public MediaStreamInfo CreateStreamInfo(string title) => new(_codec, title);

    public void Dispose()
    {
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        AVCodecContext* codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        _packet = null;
        _frame = null;
        _codec = null;
    }

    private void PrepareFrame()
    {
        // The encoder may still hold the previous frame's buffer; get a fresh one if so.
        if (_filled == 0)
        {
            FFmpegException.Check(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable");
        }
    }

    private void SendIfFull(Action<MediaPacket> output)
    {
        if (_filled == _frame->nb_samples)
        {
            Send(_frame, output);
        }
    }

    private void Send(AVFrame* frame, Action<MediaPacket> output)
    {
        if (frame != null)
        {
            frame->pts = _nextPts;
            _nextPts += _filled;
            _filled = 0;
        }

        FFmpegException.Check(ffmpeg.avcodec_send_frame(_codec, frame), "avcodec_send_frame (aac)");
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(_codec, _packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            FFmpegException.Check(result, "avcodec_receive_packet (aac)");
            output(MediaPacket.MoveFrom(_packet, TimeBase));
        }
    }
}
