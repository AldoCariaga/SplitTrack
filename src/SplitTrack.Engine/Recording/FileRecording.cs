using FFmpeg.AutoGen;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.FFmpeg;
using SplitTrack.Library;
using SplitTrack.Mux.Mkv;

namespace SplitTrack.Engine.Recording;

/// <param name="StartedAt">When recording was asked for.</param>
/// <param name="Path">The saved MKV, or null when nothing was saved.</param>
public sealed record RecordingResult(DateTimeOffset StartedAt, string? Path, double DurationSeconds, Exception? Error);

/// <param name="ElapsedTicks">Recorded so far, in QPC ticks; 0 until the first keyframe.</param>
public sealed record RecordingStatus(string Path, long ElapsedTicks, long BytesWritten, Exception? WriteError);

/// <summary>
/// An MKV started and stopped while its session keeps running. Starts on the first video keyframe
/// at or after the request (audio packets are held until then; the one covering the start is kept,
/// as for clips), every stream rebased to it. Packets arrive with pts relative to the session start.
/// </summary>
internal sealed class FileRecording : IDisposable
{
    // As ClipSaver: streams hand out packets a little after their instant.
    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromSeconds(1);
    private static readonly long CatchUpToleranceTicks = QpcClock.MillisecondsToTicks(25);
    private static readonly AVRational TickBase = new() { num = 1, den = (int)QpcClock.TicksPerSecond };

    public delegate Sidecar Describe(EntryPaths paths, DateTimeOffset created, double durationSeconds, bool hasThumbnail);

    private readonly Lock _gate = new();
    private readonly EntryPaths _paths;
    private readonly MkvWriter _mkv;
    private readonly int _videoStream;
    private readonly long _requestedTicks;
    private readonly List<(MediaPacket Packet, long Ticks, long EndTicks)>[] _held;
    private readonly long[] _newest;
    private readonly ManualResetEventSlim _inputEnded = new();
    private readonly Describe _describe;
    private readonly Action<RecordingResult> _completed;
    private long _startTicks = -1;
    private long _endTicks = long.MaxValue;
    private bool _closed;
    private Thread? _closer;

    /// <param name="requestedTicks">The press, in ticks from the session start.</param>
    public FileRecording(
        EntryPaths paths, IReadOnlyList<SessionStream> streams, int videoStream, long requestedTicks, Describe describe, Action<RecordingResult> completed)
    {
        _paths = paths;
        _videoStream = videoStream;
        _requestedTicks = requestedTicks;
        _describe = describe;
        _completed = completed;
        _held = streams.Select(_ => new List<(MediaPacket, long, long)>()).ToArray();
        _newest = Enumerable.Repeat(long.MinValue, streams.Count).ToArray();
        StartedAt = DateTimeOffset.Now;

        _mkv = new MkvWriter(paths.Video);
        try
        {
            foreach (SessionStream stream in streams)
            {
                _mkv.AddStream(stream.Info, stream.IsDefault);
            }

            _mkv.Start();
        }
        catch
        {
            _mkv.Dispose();
            throw;
        }

        // Audio only: nothing to wait for.
        if (videoStream < 0)
        {
            _startTicks = requestedTicks;
        }
    }

    public string Path => _paths.Video;

    public DateTimeOffset StartedAt { get; }

    public bool IsStopping => Volatile.Read(ref _endTicks) != long.MaxValue;

    /// <param name="nowTicks">Ticks from the session start.</param>
    public RecordingStatus GetStatus(long nowTicks)
    {
        long start = Interlocked.Read(ref _startTicks);
        long end = Math.Min(nowTicks, Interlocked.Read(ref _endTicks));
        return new RecordingStatus(Path, start < 0 ? 0 : Math.Max(0, end - start), _mkv.BytesWritten, _mkv.WriteError);
    }

    /// <summary>Takes ownership of <paramref name="packet"/>.</summary>
    public unsafe void Offer(int stream, MediaPacket packet)
    {
        long ticks = ffmpeg.av_rescale_q(packet.Pts, packet.TimeBase, TickBase);
        long endTicks = ticks + ffmpeg.av_rescale_q(packet.Native->duration, packet.TimeBase, TickBase);
        lock (_gate)
        {
            _newest[stream] = Math.Max(_newest[stream], ticks);
            if (_closed || ticks >= _endTicks)
            {
                packet.Dispose();
                return;
            }

            if (_startTicks < 0)
            {
                if (stream != _videoStream)
                {
                    // The start will be at or after the press, so audio ending before it is never needed.
                    if (endTicks > _requestedTicks)
                    {
                        _held[stream].Add((packet, ticks, endTicks));
                    }
                    else
                    {
                        packet.Dispose();
                    }

                    return;
                }

                if (!packet.IsKeyFrame || ticks < _requestedTicks)
                {
                    packet.Dispose();
                    return;
                }

                _startTicks = ticks;
                Log.Info($"Recording {Path} starts at {QpcClock.TicksToMilliseconds(ticks) / 1000:0.000} s " +
                         $"({QpcClock.TicksToMilliseconds(ticks - _requestedTicks):0} ms after the press)");
                WriteHeld();
            }

            WriteOrDrop(stream, packet, ticks, endTicks);
        }
    }

    /// <summary>Ends the file at <paramref name="endTicks"/> (from the session start) and finishes it in the background.</summary>
    public void Stop(long endTicks)
    {
        lock (_gate)
        {
            if (_closer is not null)
            {
                return;
            }

            _endTicks = Math.Max(endTicks, _requestedTicks);
            _closer = new Thread(Finish) { IsBackground = true, Name = "SplitTrack recording close", Priority = ThreadPriority.BelowNormal };
            _closer.Start();
        }
    }

    /// <summary>No more packets will come (the session is stopping); the close need not wait for streams to catch up.</summary>
    public void EndInput() => _inputEnded.Set();

    /// <summary>Waits until a stopped recording is closed and reported.</summary>
    public void Join() => _closer?.Join();

    public void Dispose()
    {
        _inputEnded.Set();
        if (Thread.CurrentThread != _closer)
        {
            Join();
        }

        lock (_gate)
        {
            _closed = true;
            DisposeHeld();
        }

        _mkv.Dispose();
        _inputEnded.Dispose();
    }

    private void WriteHeld()
    {
        for (int stream = 0; stream < _held.Length; stream++)
        {
            foreach ((MediaPacket packet, long ticks, long endTicks) in _held[stream])
            {
                WriteOrDrop(stream, packet, ticks, endTicks);
            }

            _held[stream].Clear();
        }
    }

    private unsafe void WriteOrDrop(int stream, MediaPacket packet, long ticks, long endTicks)
    {
        // Other streams from the packet covering the start (its decoded frame is the one Matroska's CodecDelay drops).
        bool beforeStart = stream == _videoStream ? ticks < _startTicks : endTicks <= _startTicks;
        if (beforeStart || ticks >= _endTicks)
        {
            packet.Dispose();
            return;
        }

        long offset = ffmpeg.av_rescale_q(_startTicks, TickBase, packet.TimeBase);
        packet.Native->pts -= offset;
        packet.Native->dts -= offset;
        _mkv.Write(stream, packet);
    }

    private void Finish()
    {
        RecordingResult result;
        try
        {
            result = Close();
        }
        catch (Exception ex)
        {
            Log.Error($"Finishing the recording {Path} failed", ex);
            result = new RecordingResult(StartedAt, null, 0, ex);
        }
        finally
        {
            _mkv.Dispose();
        }

        try
        {
            _completed(result);
        }
        catch (Exception ex)
        {
            Log.Error("Recording completion handler failed", ex);
        }
    }

    private RecordingResult Close()
    {
        long end = Interlocked.Read(ref _endTicks);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!HasReached(end) && watch.Elapsed < CatchUpTimeout && !_inputEnded.Wait(10))
        {
        }

        long start;
        lock (_gate)
        {
            _closed = true;
            DisposeHeld();
            start = _startTicks;
        }

        _mkv.Close();
        if (start < 0)
        {
            TryDelete(Path);
            Log.Info($"Recording {Path} stopped before its first keyframe; nothing saved");
            return new RecordingResult(StartedAt, null, 0, null);
        }

        if (_mkv.WriteError is { } error)
        {
            throw new IOException($"Writing {Path} failed: {error.Message}", error);
        }

        double seconds = (double)(end - start) / QpcClock.TicksPerSecond;
        bool hasThumbnail = _videoStream >= 0 && Replay.ClipSaver.TryWriteThumbnail(_paths);
        LibraryFolder.WriteSidecar(_paths.Sidecar, _describe(_paths, StartedAt, seconds, hasThumbnail));
        Log.Info($"Recording saved: {Path}, {seconds:0.000} s, {_mkv.BytesWritten / 1024.0 / 1024.0:0.0} MB");
        return new RecordingResult(StartedAt, Path, Math.Round(seconds, 3), null);
    }

    private bool HasReached(long ticks)
    {
        lock (_gate)
        {
            return _newest.All(n => n != long.MinValue && n + CatchUpToleranceTicks >= ticks);
        }
    }

    private void DisposeHeld()
    {
        foreach (var list in _held)
        {
            foreach (var entry in list)
            {
                entry.Packet.Dispose();
            }

            list.Clear();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Log.Warn($"Could not delete {path}", ex);
        }
    }
}
