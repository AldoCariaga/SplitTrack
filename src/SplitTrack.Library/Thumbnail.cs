using FFmpeg.AutoGen;
using SplitTrack.FFmpeg;

namespace SplitTrack.Library;

/// <summary>
/// A small JPEG of a video's middle keyframe: software H.264 decode, box downscale in C# (swscale is
/// not shipped), MJPEG encode.
/// </summary>
public static unsafe class Thumbnail
{
    // FFmpeg's FF_QP2LAMBDA; JPEG quality scale 2 (best) to 31.
    private const int QpToLambda = 118;
    private const int Quality = 4;

    // A keyframe decodes from one packet; a few more cover decoder delay.
    private const int MaxPackets = 120;

    public static void Write(string videoPath, string jpegPath, int width = 320)
    {
        AVFormatContext* format = null;
        AVCodecContext* decoder = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            FFmpegException.Check(ffmpeg.avformat_open_input(&format, videoPath, null, null), $"Opening {videoPath}");
            FFmpegException.Check(ffmpeg.avformat_find_stream_info(format, null), "avformat_find_stream_info");

            AVCodec* codec = null;
            int stream = FFmpegException.Check(
                ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0), "Finding the video stream");
            decoder = ffmpeg.avcodec_alloc_context3(codec);
            FFmpegException.Check(ffmpeg.avcodec_parameters_to_context(decoder, format->streams[stream]->codecpar), "avcodec_parameters_to_context");

            // One thread: frame threading would hold the first frame back until several packets are in.
            decoder->thread_count = 1;
            FFmpegException.Check(ffmpeg.avcodec_open2(decoder, codec, null), "Opening the decoder");

            if (format->duration > 0)
            {
                ffmpeg.av_seek_frame(format, -1, format->duration / 2, ffmpeg.AVSEEK_FLAG_BACKWARD);
            }

            DecodeFirstFrame(format, decoder, stream, packet, frame);
            WriteJpeg(frame, jpegPath, width);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&decoder);
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static void DecodeFirstFrame(AVFormatContext* format, AVCodecContext* decoder, int stream, AVPacket* packet, AVFrame* frame)
    {
        for (int read = 0; read < MaxPackets; read++)
        {
            int result = ffmpeg.av_read_frame(format, packet);
            if (result == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            FFmpegException.Check(result, "av_read_frame");
            if (packet->stream_index == stream)
            {
                FFmpegException.Check(ffmpeg.avcodec_send_packet(decoder, packet), "avcodec_send_packet");
            }

            ffmpeg.av_packet_unref(packet);
            if (ffmpeg.avcodec_receive_frame(decoder, frame) == 0)
            {
                return;
            }
        }

        ffmpeg.avcodec_send_packet(decoder, null);
        FFmpegException.Check(ffmpeg.avcodec_receive_frame(decoder, frame), "Decoding a frame for the thumbnail");
    }

    private static void WriteJpeg(AVFrame* source, string jpegPath, int width)
    {
        var sourceFormat = (AVPixelFormat)source->format;
        if (sourceFormat is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P))
        {
            throw new NotSupportedException($"Thumbnail from {sourceFormat} is not supported.");
        }

        width = Math.Min(width, source->width) & ~1;
        int height = Math.Max(2, (int)Math.Round((double)width * source->height / source->width) & ~1);
        bool limited = sourceFormat == AVPixelFormat.AV_PIX_FMT_YUV420P && source->color_range != AVColorRange.AVCOL_RANGE_JPEG;

        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
        if (codec == null)
        {
            throw new FFmpegException("This FFmpeg build has no MJPEG encoder.", ffmpeg.AVERROR_ENCODER_NOT_FOUND);
        }

        AVCodecContext* encoder = ffmpeg.avcodec_alloc_context3(codec);
        AVFrame* scaled = ffmpeg.av_frame_alloc();
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            encoder->width = width;
            encoder->height = height;
            encoder->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            encoder->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            encoder->time_base = new AVRational { num = 1, den = 1 };
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            encoder->global_quality = Quality * QpToLambda;
            FFmpegException.Check(ffmpeg.avcodec_open2(encoder, codec, null), "Opening the MJPEG encoder");

            scaled->format = (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            scaled->width = width;
            scaled->height = height;
            scaled->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            FFmpegException.Check(ffmpeg.av_frame_get_buffer(scaled, 0), "av_frame_get_buffer");
            for (uint plane = 0; plane < 3; plane++)
            {
                int shift = plane == 0 ? 0 : 1;
                Downscale(
                    source->data[plane], source->linesize[plane], source->width >> shift, source->height >> shift,
                    scaled->data[plane], scaled->linesize[plane], width >> shift, height >> shift,
                    limited ? (plane == 0 ? RangeMap.Luma : RangeMap.Chroma) : RangeMap.None);
            }

            scaled->quality = encoder->global_quality;
            scaled->pts = 0;
            FFmpegException.Check(ffmpeg.avcodec_send_frame(encoder, scaled), "avcodec_send_frame (mjpeg)");
            FFmpegException.Check(ffmpeg.avcodec_receive_packet(encoder, packet), "avcodec_receive_packet (mjpeg)");

            using var file = new FileStream(jpegPath, FileMode.Create, FileAccess.Write);
            file.Write(new ReadOnlySpan<byte>(packet->data, packet->size));
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&scaled);
            ffmpeg.avcodec_free_context(&encoder);
        }
    }

    private enum RangeMap
    {
        None,
        Luma,
        Chroma,
    }

    /// <summary>Averages each destination pixel's source rectangle; optionally expands limited range to full.</summary>
    private static void Downscale(
        byte* src, int srcStride, int srcWidth, int srcHeight,
        byte* dst, int dstStride, int dstWidth, int dstHeight,
        RangeMap range)
    {
        for (int y = 0; y < dstHeight; y++)
        {
            int y0 = y * srcHeight / dstHeight;
            int y1 = Math.Max(y0 + 1, (y + 1) * srcHeight / dstHeight);
            for (int x = 0; x < dstWidth; x++)
            {
                int x0 = x * srcWidth / dstWidth;
                int x1 = Math.Max(x0 + 1, (x + 1) * srcWidth / dstWidth);
                int sum = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    byte* row = src + (sy * srcStride);
                    for (int sx = x0; sx < x1; sx++)
                    {
                        sum += row[sx];
                    }
                }

                double value = (double)sum / ((y1 - y0) * (x1 - x0));
                value = range switch
                {
                    RangeMap.Luma => (value - 16) * 255 / 219,
                    RangeMap.Chroma => ((value - 128) * 255 / 224) + 128,
                    _ => value,
                };
                dst[(y * dstStride) + x] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
            }
        }
    }
}
