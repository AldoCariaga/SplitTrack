using System.Collections.Concurrent;
using System.Diagnostics;
using SplitTrack.Buffer;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Engine.Recording;
using SplitTrack.Library;
using SplitTrack.Mux.Mkv;

namespace SplitTrack.Engine.Replay;

/// <param name="PressedAt">When the clip was asked for; its end.</param>
/// <param name="Path">The saved MKV, or null when saving failed.</param>
public sealed record ClipResult(DateTime PressedAt, string? Path, double DurationSeconds, Exception? Error);

/// <summary>
/// Saves clips from a replay buffer on its own thread, one at a time in request order: waits until
/// every stream has caught up with the requested end, cuts the clip, writes MKV, thumbnail, sidecar.
/// </summary>
internal sealed class ClipSaver : IDisposable
{
    // Encoders and aligners hand out packets a little after their instant (a track without a source up to ~300 ms).
    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromSeconds(1);
    private static readonly long CatchUpToleranceTicks = QpcClock.MillisecondsToTicks(25);

    public delegate Sidecar Describe(EntryPaths paths, DateTimeOffset created, double durationSeconds, bool hasThumbnail);

    private readonly ReplayBuffer _buffer;
    private readonly IReadOnlyList<SessionStream> _streams;
    private readonly LibraryFolder _library;
    private readonly Describe _describe;
    private readonly Action<ClipResult> _completed;
    private readonly bool _hasVideo;
    private readonly BlockingCollection<(long EndTicks, DateTime PressedAt)> _requests = new();
    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private bool _closed;

    public ClipSaver(
        ReplayBuffer buffer, IReadOnlyList<SessionStream> streams, bool hasVideo, LibraryFolder library, Describe describe, Action<ClipResult> completed)
    {
        _buffer = buffer;
        _streams = streams;
        _hasVideo = hasVideo;
        _library = library;
        _describe = describe;
        _completed = completed;
        _thread = new Thread(Run) { IsBackground = true, Name = "SplitTrack clip saver", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <param name="endTicks">The clip's end, in ticks from the session start.</param>
    public void Request(long endTicks, DateTime pressedAt)
    {
        lock (_gate)
        {
            if (_closed)
            {
                Log.Info("Clip requested after the session ended; ignored");
                return;
            }

            _requests.Add((endTicks, pressedAt));
        }
    }

    /// <summary>Finishes the clips already requested; later requests are ignored.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _requests.CompleteAdding();
        }

        _thread.Join();
        _requests.Dispose();
    }

    private void Run()
    {
        foreach ((long endTicks, DateTime pressedAt) in _requests.GetConsumingEnumerable())
        {
            ClipResult result;
            try
            {
                result = Save(endTicks, pressedAt);
            }
            catch (Exception ex)
            {
                Log.Error("Saving a clip failed", ex);
                result = new ClipResult(pressedAt, null, 0, ex);
            }

            try
            {
                _completed(result);
            }
            catch (Exception ex)
            {
                Log.Error("Clip completion handler failed", ex);
            }
        }
    }

    private ClipResult Save(long endTicks, DateTime pressedAt)
    {
        var watch = Stopwatch.StartNew();
        while (!_buffer.HasReached(endTicks, CatchUpToleranceTicks) && watch.Elapsed < CatchUpTimeout)
        {
            Thread.Sleep(10);
        }

        double waitedMs = watch.Elapsed.TotalMilliseconds;
        using ReplayClip? clip = _buffer.Snapshot(endTicks, _buffer.CapacityTicks);
        if (clip is null)
        {
            throw new InvalidOperationException("Nothing is buffered yet.");
        }

        double snapshotMs = watch.Elapsed.TotalMilliseconds - waitedMs;
        EntryPaths paths = _library.NewEntry(pressedAt, "clip");
        try
        {
            WriteMkv(clip, paths.PartialVideo);
            File.Move(paths.PartialVideo, paths.Video);
        }
        catch
        {
            TryDelete(paths.PartialVideo);
            throw;
        }

        bool hasThumbnail = _hasVideo && TryWriteThumbnail(paths);
        double seconds = (double)clip.DurationTicks / QpcClock.TicksPerSecond;
        LibraryFolder.WriteSidecar(paths.Sidecar, _describe(paths, pressedAt, seconds, hasThumbnail));
        Log.Info(
            $"Clip saved: {paths.Video}, {seconds:0.000} s from {QpcClock.TicksToMilliseconds(clip.StartTicks) / 1000:0.000} s, " +
            $"{clip.Packets.Count} packets; waited {waitedMs:0} ms, snapshot {snapshotMs:0.0} ms, total {watch.Elapsed.TotalMilliseconds:0} ms");
        return new ClipResult(pressedAt, paths.Video, seconds, null);
    }

    private void WriteMkv(ReplayClip clip, string path)
    {
        using var mkv = new MkvWriter(path);
        foreach (SessionStream stream in _streams)
        {
            mkv.AddStream(stream.Info, stream.IsDefault);
        }

        mkv.Start();
        foreach (ClipPacket packet in clip.Packets)
        {
            mkv.Write(packet.Stream, packet.Packet);
        }

        mkv.Close();
        if (mkv.WriteError is { } error)
        {
            throw new IOException($"Writing {path} failed: {error.Message}", error);
        }
    }

    internal static bool TryWriteThumbnail(EntryPaths paths)
    {
        try
        {
            Thumbnail.Write(paths.Video, paths.Thumbnail);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No thumbnail for {paths.Video}", ex);
            TryDelete(paths.Thumbnail);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
