using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SplitTrack.FFmpeg;

namespace SplitTrack.Export;

/// <param name="Index">Stream index in the file.</param>
public sealed record VideoStreamInfo(int Index, int Width, int Height, double Fps, string Codec);

/// <param name="Index">Stream index in the file.</param>
/// <param name="Number">1 for the first audio stream, 2 for the second, ...</param>
/// <param name="Title">The stream's title tag, if any.</param>
/// <param name="BitrateKbps">As declared by the file; 0 when unknown (Matroska does not store it).</param>
public sealed record AudioStreamInfo(int Index, int Number, string? Title, int Channels, int SampleRate, int BitrateKbps);

/// <summary>What a media file holds, read with libavformat.</summary>
public sealed unsafe record MediaInfo(
    string Path, double DurationSeconds, long SizeBytes, VideoStreamInfo? Video, IReadOnlyList<AudioStreamInfo> Audio)
{
    // Enough to cover a keyframe interval of several seconds after a seek.
    private const int MaxScanPackets = 20_000;

    public static MediaInfo Probe(string path)
    {
        AVFormatContext* format = Open(path);
        try
        {
            VideoStreamInfo? video = null;
            var audio = new List<AudioStreamInfo>();
            for (int i = 0; i < format->nb_streams; i++)
            {
                AVStream* stream = format->streams[i];
                AVCodecParameters* codec = stream->codecpar;
                if (codec->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && video is null
                    && (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    AVRational rate = stream->avg_frame_rate.num > 0 ? stream->avg_frame_rate : stream->r_frame_rate;
                    double fps = rate.den > 0 ? ffmpeg.av_q2d(rate) : 0;
                    video = new VideoStreamInfo(i, codec->width, codec->height, fps, ffmpeg.avcodec_get_name(codec->codec_id));
                }
                else if (codec->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audio.Add(new AudioStreamInfo(
                        i, audio.Count + 1, Tag(stream->metadata, "title"), codec->ch_layout.nb_channels, codec->sample_rate,
                        (int)(codec->bit_rate / 1000)));
                }
            }

            double duration = format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            return new MediaInfo(path, duration, new FileInfo(path).Length, video, audio);
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }

    /// <summary>
    /// Time of the last video keyframe at or before <paramref name="seconds"/>, or null if none is.
    /// Seeks back, then scans forward, so it is right whether or not the file has a complete index.
    /// </summary>
    public double? KeyframeAtOrBefore(double seconds)
    {
        if (Video is null)
        {
            return null;
        }

        AVFormatContext* format = Open(Path);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            AVRational timeBase = format->streams[Video.Index]->time_base;
            long target = (long)Math.Floor(seconds / ffmpeg.av_q2d(timeBase));
            if (ffmpeg.av_seek_frame(format, Video.Index, target, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            {
                ffmpeg.av_seek_frame(format, Video.Index, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
            }

            long? found = null;
            for (int read = 0; read < MaxScanPackets && ffmpeg.av_read_frame(format, packet) >= 0; read++)
            {
                long pts = packet->pts;
                bool key = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                bool video = packet->stream_index == Video.Index;
                ffmpeg.av_packet_unref(packet);
                if (!video || pts == ffmpeg.AV_NOPTS_VALUE)
                {
                    continue;
                }

                if (pts > target)
                {
                    break;
                }

                if (key)
                {
                    found = pts;
                }
            }

            return found is { } ts ? ts * ffmpeg.av_q2d(timeBase) : null;
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static AVFormatContext* Open(string path)
    {
        AVFormatContext* format = null;
        FFmpegException.Check(ffmpeg.avformat_open_input(&format, path, null, null), $"Opening {path}");
        int result = ffmpeg.avformat_find_stream_info(format, null);
        if (result < 0)
        {
            ffmpeg.avformat_close_input(&format);
            FFmpegException.Check(result, "avformat_find_stream_info");
        }

        return format;
    }

    private static string? Tag(AVDictionary* metadata, string key)
    {
        AVDictionaryEntry* entry = ffmpeg.av_dict_get(metadata, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((nint)entry->value);
    }
}
