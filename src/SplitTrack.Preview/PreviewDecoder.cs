using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SplitTrack.Common.Diagnostics;
using SplitTrack.FFmpeg;

namespace SplitTrack.Preview;

/// <summary>
/// The preview's decode thread: demuxes the file, decodes video to BGRA frames (swscale, scaled to
/// the preview size) into a short queue, and each audio stream into its own <see cref="TrackRing"/>.
/// Runs ahead of the play position by the queue / <see cref="AudioLeadFrames"/> and then waits.
/// </summary>
internal sealed unsafe class PreviewDecoder : IDisposable
{
    public const int SampleRate = 48_000;

    // How far audio is decoded ahead of the mix position; rings hold four times that.
    public const int AudioLeadFrames = SampleRate;

    private const int VideoQueueSize = 6;
    private const int IdleWaitMs = 5;

    // Frames this far behind the clock are dropped when the queue is full (e.g. the window is minimised).
    private const double StaleFrameSeconds = 0.1;

    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Queue<VideoFrame> _frames = new();
    private readonly Stack<VideoFrame> _pool = new();
    private readonly Func<long> _mixPosition;
    private readonly Func<double> _clock;
    private readonly AudioDecoder[] _audio;
    private readonly Thread _thread;
    private AVFormatContext* _format;
    private AVCodecContext* _video;
    private SwsContext* _sws;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private int _videoStream = -1;
    private double _frameSeconds;
    private long _startTime;
    private volatile bool _disposed;

    // Seek handshake: the requester bumps _requested; the thread applies it and sets _applied.
    private int _requested;
    private int _applied;
    private double _seekTarget;
    private readonly ManualResetEventSlim _seekApplied = new(true);

    private int _readyGeneration = -1;
    private double _dropBefore;
    private bool _eof;

    public PreviewDecoder(string path, int maxWidth, int maxHeight, Func<long> mixPosition, Func<double> clock)
    {
        _mixPosition = mixPosition;
        _clock = clock;
        try
        {
            AVFormatContext* format = null;
            FFmpegException.Check(ffmpeg.avformat_open_input(&format, path, null, null), $"Opening {path}");
            _format = format;
            FFmpegException.Check(ffmpeg.avformat_find_stream_info(_format, null), "avformat_find_stream_info");
            _startTime = _format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : _format->start_time;
            DurationSeconds = _format->duration > 0 ? _format->duration / (double)ffmpeg.AV_TIME_BASE : 0;

            var audio = new List<AudioDecoder>();
            for (int i = 0; i < _format->nb_streams; i++)
            {
                AVStream* stream = _format->streams[i];
                AVMediaType type = stream->codecpar->codec_type;
                if (type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStream < 0 && (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    OpenVideo(i, maxWidth, maxHeight);
                }
                else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audio.Add(new AudioDecoder(stream, i, audio.Count + 1));
                }
                else
                {
                    stream->discard = AVDiscard.AVDISCARD_ALL;
                }
            }

            _audio = audio.ToArray();
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
        }
        catch
        {
            _audio ??= [];
            FreeNative();
            throw;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Preview decode", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Raised on the decode thread when, after a seek, the first picture and enough audio are ready (or the file ended).</summary>
    public event Action<int>? Ready;

    public double DurationSeconds { get; }

    public bool HasVideo => _videoStream >= 0;

    public int FrameWidth { get; private set; }

    public int FrameHeight { get; private set; }

    public int SourceWidth { get; private set; }

    public int SourceHeight { get; private set; }

    public IReadOnlyList<AudioDecoder> Audio => _audio;

    public void Start() => _thread.Start();

    /// <summary>Restarts decoding at <paramref name="seconds"/>; <see cref="WaitSeekApplied"/> waits for it to take effect.</summary>
    /// <returns>The seek generation, as later passed to <see cref="Ready"/>.</returns>
    public int RequestSeek(double seconds)
    {
        int generation;
        lock (_gate)
        {
            _seekTarget = seconds;
            generation = ++_requested;
            _seekApplied.Reset();
        }

        _wake.Set();
        return generation;
    }

    /// <summary>Returns once the old output is gone and the rings expect the new position.</summary>
    public void WaitSeekApplied()
    {
        if (!_seekApplied.Wait(TimeSpan.FromSeconds(2)))
        {
            Log.Warn("Preview: the decoder did not take a seek within 2 s");
        }
    }

    /// <summary>The newest frame not after <paramref name="seconds"/>, or null; older ones are recycled.</summary>
    public VideoFrame? TakeFrame(double seconds)
    {
        lock (_gate)
        {
            if (_applied != _requested)
            {
                return null;
            }

            VideoFrame? found = null;
            while (_frames.TryPeek(out VideoFrame? next) && next.Time <= seconds)
            {
                if (found is not null)
                {
                    _pool.Push(found);
                }

                found = _frames.Dequeue();
            }

            if (found is not null)
            {
                _wake.Set();
            }

            return found;
        }
    }

    public void Release(VideoFrame frame)
    {
        lock (_gate)
        {
            if (frame.Width == FrameWidth && frame.Height == FrameHeight)
            {
                _pool.Push(frame);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wake.Set();
        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        FreeNative();
        _wake.Dispose();
        _seekApplied.Dispose();
    }

    private void OpenVideo(int index, int maxWidth, int maxHeight)
    {
        AVStream* stream = _format->streams[index];
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
        {
            Log.Warn($"Preview: no decoder for video codec {stream->codecpar->codec_id}; audio only");
            stream->discard = AVDiscard.AVDISCARD_ALL;
            return;
        }

        _video = ffmpeg.avcodec_alloc_context3(codec);
        FFmpegException.Check(ffmpeg.avcodec_parameters_to_context(_video, stream->codecpar), "avcodec_parameters_to_context");
        _video->thread_count = 0;
        _video->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        FFmpegException.Check(ffmpeg.avcodec_open2(_video, codec, null), "Opening the video decoder");

        _videoStream = index;
        SourceWidth = _video->width;
        SourceHeight = _video->height;
        double scale = Math.Min(1, Math.Min((double)maxWidth / SourceWidth, (double)maxHeight / SourceHeight));
        FrameWidth = Math.Max(2, (int)Math.Round(SourceWidth * scale) & ~1);
        FrameHeight = Math.Max(2, (int)Math.Round(SourceHeight * scale) & ~1);
        AVRational rate = stream->avg_frame_rate.num > 0 ? stream->avg_frame_rate : stream->r_frame_rate;
        _frameSeconds = rate.num > 0 ? ffmpeg.av_q2d(ffmpeg.av_inv_q(rate)) : 1 / 60.0;
    }

    private void Run()
    {
        try
        {
            while (!_disposed)
            {
                ApplySeek();
                CheckReady();
                if (_eof || !NeedsData())
                {
                    _wake.WaitOne(IdleWaitMs);
                    continue;
                }

                ReadOne();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Preview: decoding stopped", ex);
            lock (_gate)
            {
                _eof = true;
                _applied = _requested;
            }

            _seekApplied.Set();
            Ready?.Invoke(_applied);
        }
    }

    private bool NeedsData()
    {
        lock (_gate)
        {
            if (HasVideo && _frames.Count < VideoQueueSize)
            {
                return true;
            }
        }

        long position = _mixPosition();
        return _audio.Any(a => a.Ring.End - position < AudioLeadFrames);
    }

    private void ApplySeek()
    {
        double target;
        int generation;
        lock (_gate)
        {
            if (_applied == _requested)
            {
                return;
            }

            target = _seekTarget;
            generation = _requested;
            while (_frames.TryDequeue(out VideoFrame? frame))
            {
                _pool.Push(frame);
            }
        }

        long timestamp = _startTime + (long)(target * ffmpeg.AV_TIME_BASE);
        int result = ffmpeg.avformat_seek_file(_format, -1, long.MinValue, timestamp, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (result < 0)
        {
            ffmpeg.av_seek_frame(_format, -1, _startTime, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }

        if (_video != null)
        {
            ffmpeg.avcodec_flush_buffers(_video);
        }

        long sample = (long)Math.Round(target * SampleRate);
        foreach (AudioDecoder audio in _audio)
        {
            audio.Flush();
            audio.Ring.Reset(sample);
        }

        _dropBefore = target - (_frameSeconds / 2);
        _eof = false;
        lock (_gate)
        {
            _applied = generation;
            _readyGeneration = -1;
        }

        _seekApplied.Set();
    }

    private void CheckReady()
    {
        int generation;
        lock (_gate)
        {
            generation = _applied;
            if (_readyGeneration == generation || _applied != _requested)
            {
                return;
            }

            bool videoReady = !HasVideo || _frames.Count > 0 || _eof;
            if (!videoReady)
            {
                return;
            }
        }

        long position = _mixPosition();
        bool audioReady = _eof || _audio.All(a => a.Ring.End - position >= AudioLeadFrames / 5);
        if (!audioReady)
        {
            return;
        }

        lock (_gate)
        {
            _readyGeneration = generation;
        }

        Ready?.Invoke(generation);
    }

    private void ReadOne()
    {
        int result = ffmpeg.av_read_frame(_format, _packet);
        if (result == ffmpeg.AVERROR_EOF)
        {
            DrainAll();
            _eof = true;
            return;
        }

        if (result < 0)
        {
            Log.Warn($"Preview: read error {FFmpegException.Describe(result)}; treating as the end");
            DrainAll();
            _eof = true;
            return;
        }

        try
        {
            if (_packet->stream_index == _videoStream)
            {
                if (ffmpeg.avcodec_send_packet(_video, _packet) >= 0)
                {
                    ReceiveVideo();
                }
            }
            else if (_audio.FirstOrDefault(a => a.Stream == _packet->stream_index) is { } audio)
            {
                audio.Decode(_packet, _frame, _startTime);
            }
        }
        finally
        {
            ffmpeg.av_packet_unref(_packet);
        }
    }

    private void DrainAll()
    {
        if (_video != null)
        {
            ffmpeg.avcodec_send_packet(_video, null);
            ReceiveVideo();
        }

        foreach (AudioDecoder audio in _audio)
        {
            audio.Decode(null, _frame, _startTime);
        }
    }

    private void ReceiveVideo()
    {
        while (ffmpeg.avcodec_receive_frame(_video, _frame) == 0)
        {
            try
            {
                long pts = _frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE)
                {
                    continue;
                }

                AVRational timeBase = _format->streams[_videoStream]->time_base;
                double seconds = (pts - ffmpeg.av_rescale_q(_startTime, ffmpeg.av_get_time_base_q(), timeBase)) * ffmpeg.av_q2d(timeBase);
                if (seconds < _dropBefore)
                {
                    continue;
                }

                if (TakeFreeFrame() is not { } target)
                {
                    return;
                }

                Convert(_frame, target);
                target.Time = seconds;
                lock (_gate)
                {
                    _frames.Enqueue(target);
                }
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>A pooled frame once the queue has room; null when a seek or dispose interrupts the wait.</summary>
    private VideoFrame? TakeFreeFrame()
    {
        while (!_disposed)
        {
            lock (_gate)
            {
                if (_applied != _requested)
                {
                    return null;
                }

                if (_frames.Count >= VideoQueueSize && _frames.Peek().Time < _clock() - StaleFrameSeconds)
                {
                    _pool.Push(_frames.Dequeue());
                }

                if (_frames.Count < VideoQueueSize)
                {
                    return _pool.TryPop(out VideoFrame? pooled) ? pooled : new VideoFrame(FrameWidth, FrameHeight);
                }
            }

            _wake.WaitOne(IdleWaitMs);
        }

        return null;
    }

    private void Convert(AVFrame* source, VideoFrame target)
    {
        var format = (AVPixelFormat)source->format;
        _sws = ffmpeg.sws_getCachedContext(
            _sws, source->width, source->height, format, target.Width, target.Height, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws == null)
        {
            throw new FFmpegException($"swscale cannot convert {format} to BGRA.", -22);
        }

        // The recorder writes BT.709 limited range; swscale assumes BT.601 unless told.
        int colorspace = source->colorspace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
            AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
            AVColorSpace.AVCOL_SPC_UNSPECIFIED when source->height >= 720 => ffmpeg.SWS_CS_ITU709,
            _ => ffmpeg.SWS_CS_ITU601,
        };
        bool fullRange = source->color_range == AVColorRange.AVCOL_RANGE_JPEG || format == AVPixelFormat.AV_PIX_FMT_YUVJ420P;
        int_array4 sourceTable = Table(ffmpeg.sws_getCoefficients(colorspace));
        int_array4 targetTable = Table(ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_DEFAULT));
        ffmpeg.sws_setColorspaceDetails(_sws, sourceTable, fullRange ? 1 : 0, targetTable, 1, 0, 1 << 16, 1 << 16);

        fixed (byte* pixels = target.Pixels)
        {
            byte*[] dst = [pixels];
            int[] dstStride = [target.Stride];
            ffmpeg.sws_scale(_sws, source->data.ToArray(), source->linesize.ToArray(), 0, source->height, dst, dstStride);
        }
    }

    private static int_array4 Table(int* coefficients)
    {
        var table = new int_array4();
        for (uint i = 0; i < 4; i++)
        {
            table[i] = coefficients[i];
        }

        return table;
    }

    private void FreeNative()
    {
        foreach (AudioDecoder audio in _audio ?? [])
        {
            audio.Dispose();
        }

        AVCodecContext* video = _video;
        ffmpeg.avcodec_free_context(&video);
        _video = null;
        ffmpeg.sws_freeContext(_sws);
        _sws = null;
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
        AVFormatContext* format = _format;
        ffmpeg.avformat_close_input(&format);
        _format = null;
    }
}

/// <summary>One audio stream: its decoder, a converter to 48 kHz float stereo, and its ring.</summary>
internal sealed unsafe class AudioDecoder : IDisposable
{
    private readonly AVRational _timeBase;
    private AVCodecContext* _codec;
    private SwrContext* _swr;
    private float[] _converted = new float[8192];

    public AudioDecoder(AVStream* stream, int index, int number)
    {
        Stream = index;
        Number = number;
        _timeBase = stream->time_base;
        Title = Tag(stream->metadata, "title");
        Ring = new TrackRing(PreviewDecoder.AudioLeadFrames * 4);

        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
        {
            Log.Warn($"Preview: no decoder for audio stream {index} ({stream->codecpar->codec_id}); it stays silent");
            return;
        }

        _codec = ffmpeg.avcodec_alloc_context3(codec);
        FFmpegException.Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), "avcodec_parameters_to_context");
        FFmpegException.Check(ffmpeg.avcodec_open2(_codec, codec, null), "Opening an audio decoder");
    }

    public int Stream { get; }

    public int Number { get; }

    public string? Title { get; }

    public TrackRing Ring { get; }

    public void Flush()
    {
        if (_codec != null)
        {
            ffmpeg.avcodec_flush_buffers(_codec);
        }

        // Its buffered input belongs to the old position.
        SwrContext* swr = _swr;
        ffmpeg.swr_free(&swr);
        _swr = null;
    }

    /// <param name="packet">Null drains the decoder at the end of the file.</param>
    public void Decode(AVPacket* packet, AVFrame* frame, long startTime)
    {
        if (_codec == null || ffmpeg.avcodec_send_packet(_codec, packet) < 0)
        {
            return;
        }

        while (ffmpeg.avcodec_receive_frame(_codec, frame) == 0)
        {
            try
            {
                long pts = frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE)
                {
                    continue;
                }

                long position = ffmpeg.av_rescale_q(pts - ffmpeg.av_rescale_q(startTime, ffmpeg.av_get_time_base_q(), _timeBase), _timeBase,
                    new AVRational { num = 1, den = PreviewDecoder.SampleRate });
                int frames = Convert(frame);
                if (frames > 0)
                {
                    Ring.Write(position, _converted.AsSpan(0, frames * 2));
                }
            }
            finally
            {
                ffmpeg.av_frame_unref(frame);
            }
        }
    }

    public void Dispose()
    {
        AVCodecContext* codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        _codec = null;
        SwrContext* swr = _swr;
        ffmpeg.swr_free(&swr);
        _swr = null;
    }

    private int Convert(AVFrame* frame)
    {
        if (_swr == null)
        {
            AVChannelLayout stereo;
            ffmpeg.av_channel_layout_default(&stereo, 2);
            SwrContext* swr = null;
            FFmpegException.Check(
                ffmpeg.swr_alloc_set_opts2(
                    &swr, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, PreviewDecoder.SampleRate,
                    &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null),
                "swr_alloc_set_opts2");
            ffmpeg.av_channel_layout_uninit(&stereo);
            int result = ffmpeg.swr_init(swr);
            if (result < 0)
            {
                ffmpeg.swr_free(&swr);
                FFmpegException.Check(result, "swr_init");
            }

            _swr = swr;
        }

        int capacity = ffmpeg.swr_get_out_samples(_swr, frame->nb_samples);
        if (_converted.Length < capacity * 2)
        {
            _converted = new float[capacity * 2];
        }

        fixed (float* output = _converted)
        {
            byte* outPlane = (byte*)output;
            return ffmpeg.swr_convert(_swr, &outPlane, capacity, frame->extended_data, frame->nb_samples);
        }
    }

    private static string? Tag(AVDictionary* metadata, string key)
    {
        AVDictionaryEntry* entry = ffmpeg.av_dict_get(metadata, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((nint)entry->value);
    }
}
