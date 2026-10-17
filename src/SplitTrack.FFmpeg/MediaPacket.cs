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

    /// <summary>A second packet sharing this one's data (reference counted, no copy) with its own timestamps.</summary>
    public MediaPacket Clone()
    {
        AVPacket* packet = ffmpeg.av_packet_clone(Native);
        if (packet == null)
        {
            throw new OutOfMemoryException("av_packet_clone failed.");
        }

        return new MediaPacket(packet, TimeBase);
    }

    /// <summary>
    /// Moves the data into a buffer of its own size when the current one is much larger. Some
    /// encoders (QSV) hand out packets in a worst-case-sized buffer, megabytes per frame; harmless
    /// when written at once, but held in a replay buffer it commits gigabytes.
    /// </summary>
    /// <returns>True if the data was copied.</returns>
    public bool ShrinkToFit()
    {
        AVPacket* packet = Native;
        if (packet->buf == null || (ulong)packet->buf->size < (2 * (ulong)packet->size) + 4096)
        {
            return false;
        }

        // FFmpeg requires zeroed padding after packet data.
        AVBufferRef* fitted = ffmpeg.av_buffer_alloc((ulong)(packet->size + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        if (fitted == null)
        {
            throw new OutOfMemoryException("av_buffer_alloc failed.");
        }

        Buffer.MemoryCopy(packet->data, fitted->data, packet->size, packet->size);
        new Span<byte>(fitted->data + packet->size, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();
        ffmpeg.av_buffer_unref(&packet->buf);
        packet->buf = fitted;
        packet->data = fitted->data;
        return true;
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
