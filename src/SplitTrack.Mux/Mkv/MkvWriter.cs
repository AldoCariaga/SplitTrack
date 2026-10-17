using System.Threading.Channels;
using FFmpeg.AutoGen;
using SplitTrack.Common.Diagnostics;
using SplitTrack.FFmpeg;

namespace SplitTrack.Mux.Mkv;

/// <summary>
/// Matroska file through libavformat. Packets are queued by encoder threads and written on a
/// dedicated thread, so a slow disk stalls this queue rather than capture or encoding.
/// </summary>
public sealed unsafe class MkvWriter : IDisposable
{
    // About 10 s of 60 fps video; beyond that the encoder thread waits for the disk.
    private const int QueueCapacity = 600;

    private readonly Channel<(int Stream, MediaPacket Packet)> _queue =
        Channel.CreateBounded<(int, MediaPacket)>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true });

    private readonly List<AVRational> _streamTimeBases = [];
    private AVFormatContext* _format;
    private Thread? _writer;
    private Exception? _writeError;
    private long _bytes;
    private long _packets;
    private bool _closed;

    public MkvWriter(string path)
    {
        Path = path;
        AVFormatContext* format = null;
        FFmpegException.Check(ffmpeg.avformat_alloc_output_context2(&format, null, "matroska", path), "avformat_alloc_output_context2");
        _format = format;
    }

    public string Path { get; }

    public long BytesWritten => Interlocked.Read(ref _bytes);

    public long PacketsWritten => Interlocked.Read(ref _packets);

    /// <summary>Set when writing failed; later packets are discarded.</summary>
    public Exception? WriteError => Volatile.Read(ref _writeError);

    /// <summary>Declares a stream before <see cref="Start"/>; returns its index for <see cref="Write"/>.</summary>
    public int AddStream(MediaStreamInfo info)
    {
        if (_writer is not null)
        {
            throw new InvalidOperationException("Streams must be added before Start.");
        }

        AVStream* stream = ffmpeg.avformat_new_stream(_format, null);
        if (stream == null)
        {
            throw new OutOfMemoryException("avformat_new_stream failed.");
        }

        FFmpegException.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, info.Parameters), "avcodec_parameters_copy");
        stream->time_base = info.TimeBase;
        stream->avg_frame_rate = info.FrameRate;
        ffmpeg.av_dict_set(&stream->metadata, "title", info.Title, 0);
        return stream->index;
    }

    /// <summary>Opens the file, writes the header and starts the writer thread.</summary>
    public void Start()
    {
        FFmpegException.Check(ffmpeg.avio_open(&_format->pb, Path, ffmpeg.AVIO_FLAG_WRITE), $"Opening {Path}");
        FFmpegException.Check(ffmpeg.avformat_write_header(_format, null), "avformat_write_header");

        // The muxer may change each stream's time base (Matroska uses 1/1000).
        for (int i = 0; i < _format->nb_streams; i++)
        {
            _streamTimeBases.Add(_format->streams[i]->time_base);
        }

        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "SplitTrack MKV writer" };
        _writer.Start();
        Log.Info($"MKV opened: {Path}");
    }

    /// <summary>Queues a packet and takes ownership of it.</summary>
    public void Write(int stream, MediaPacket packet)
    {
        if (!_queue.Writer.TryWrite((stream, packet)))
        {
            if (!_queue.Writer.WriteAsync((stream, packet)).AsTask().Wait(TimeSpan.FromSeconds(30)))
            {
                packet.Dispose();
                throw new IOException($"The disk has not accepted data for 30 s ({Path}).");
            }
        }
    }

    /// <summary>Writes everything still queued, then the index, and closes the file.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _queue.Writer.TryComplete();
        _writer?.Join();

        if (_writer is not null && WriteError is null)
        {
            FFmpegException.Check(ffmpeg.av_write_trailer(_format), "av_write_trailer");
        }

        if (_format->pb != null)
        {
            _bytes = ffmpeg.avio_size(_format->pb);
            ffmpeg.avio_closep(&_format->pb);
        }

        Log.Info($"MKV closed: {Path}, {_packets} packets, {_bytes / 1024.0 / 1024.0:0.0} MB");
    }

    public void Dispose()
    {
        try
        {
            Close();
        }
        finally
        {
            while (_queue.Reader.TryRead(out var item))
            {
                item.Packet.Dispose();
            }

            if (_format != null)
            {
                ffmpeg.avformat_free_context(_format);
                _format = null;
            }
        }
    }

    private void WriteLoop()
    {
        AVPacket* scratch = ffmpeg.av_packet_alloc();
        try
        {
            while (_queue.Reader.WaitToReadAsync().AsTask().Result)
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    using MediaPacket packet = item.Packet;
                    if (WriteError is not null)
                    {
                        continue;
                    }

                    try
                    {
                        ffmpeg.av_packet_ref(scratch, packet.Native);
                        scratch->stream_index = item.Stream;
                        ffmpeg.av_packet_rescale_ts(scratch, packet.TimeBase, _streamTimeBases[item.Stream]);
                        int size = scratch->size;

                        // Takes over the packet's reference and blanks it, even on failure.
                        FFmpegException.Check(ffmpeg.av_interleaved_write_frame(_format, scratch), "av_interleaved_write_frame");
                        Interlocked.Add(ref _bytes, size);
                        Interlocked.Increment(ref _packets);
                    }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref _writeError, ex);
                        Log.Error($"Writing {Path} failed", ex);
                    }
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&scratch);
        }
    }
}
