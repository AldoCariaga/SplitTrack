using FFmpeg.AutoGen;

namespace SplitTrack.FFmpeg;

/// <summary>An encoded packet that owns its AVPacket; timestamps are in <see cref="TimeBase"/>.</summary>
public sealed unsafe class MediaPacket : IDisposable
{
    private AVPacket* _packet;

    private MediaPacket(AVPacket* packet, AVRational timeBase)
    {
        _packet = packet;
        TimeBase = timeBase;
    }

    ~MediaPacket() => Free();

    public AVRational TimeBase { get; }

    public AVPacket* Native => _packet != null ? _packet : throw new ObjectDisposedException(nameof(MediaPacket));

    public long Pts => Native->pts;

    public int Size => Native->size;

    public bool IsKeyFrame => (Native->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

    /// <summary>Moves the contents of <paramref name="source"/> into a new packet, leaving it blank for reuse.</summary>
    public static MediaPacket MoveFrom(AVPacket* source, AVRational timeBase)
    {
        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
        {
            throw new OutOfMemoryException("av_packet_alloc failed.");
        }

        ffmpeg.av_packet_move_ref(packet, source);
        return new MediaPacket(packet, timeBase);
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        AVPacket* packet = _packet;
        _packet = null;
        if (packet != null)
        {
            ffmpeg.av_packet_free(&packet);
        }
    }
}
