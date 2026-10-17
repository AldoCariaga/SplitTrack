using FFmpeg.AutoGen;
using SplitTrack.Common.Time;
using SplitTrack.FFmpeg;

namespace SplitTrack.Buffer;

/// <param name="HeldTicks">Time span the buffer can cut a clip from right now.</param>
public readonly record struct ReplayBufferStats(long HeldTicks, long Bytes, long Packets);

/// <summary>
/// The last few seconds of a session, already encoded: one queue of packets per stream, timestamps
/// in QPC ticks from the session start. The video queue is trimmed by whole GOPs, so it always
/// starts on a keyframe at or before "capacity ago"; the other streams follow the video front.
/// </summary>
public sealed class ReplayBuffer : IDisposable
{
    // Audio kept before the video front, so a clip cut at the front still gets the audio packet covering its start.
    private static readonly long AudioTrimLeadTicks = QpcClock.MillisecondsToTicks(100);

    private static readonly AVRational TickBase = new() { num = 1, den = (int)QpcClock.TicksPerSecond };

    private readonly Lock _gate = new();
    private readonly Queue<Entry>[] _queues;
    private readonly long[] _newest;
    private readonly Queue<long> _keyframes = new();
    private readonly int _videoStream;
    private long _bytes;
    private long _packets;
    private bool _disposed;

    /// <param name="videoStream">The stream whose keyframes clips start on, or -1 for audio only.</param>
    public ReplayBuffer(int streamCount, int videoStream, long capacityTicks)
    {
        _queues = Enumerable.Range(0, streamCount).Select(_ => new Queue<Entry>()).ToArray();
        _newest = Enumerable.Repeat(long.MinValue, streamCount).ToArray();
        _videoStream = videoStream;
        CapacityTicks = capacityTicks;
    }

    public long CapacityTicks { get; }

    /// <summary>Takes ownership of <paramref name="packet"/>; its pts must be relative to the session start.</summary>
    public unsafe void Add(int stream, MediaPacket packet)
    {
        packet.ShrinkToFit();
        long ticks = ffmpeg.av_rescale_q(packet.Pts, packet.TimeBase, TickBase);
        long durationTicks = ffmpeg.av_rescale_q(packet.Native->duration, packet.TimeBase, TickBase);
        var entry = new Entry(packet, ticks, durationTicks, packet.IsKeyFrame, packet.Size);
        lock (_gate)
        {
            if (_disposed)
            {
                packet.Dispose();
                return;
            }

            if (stream == _videoStream && entry.Key)
            {
                _keyframes.Enqueue(ticks);
            }

            _queues[stream].Enqueue(entry);
            _newest[stream] = Math.Max(_newest[stream], ticks);
            _bytes += entry.Size;
            _packets++;
            Trim();
        }
    }

    /// <summary>True once every stream has a packet at or after <paramref name="ticks"/> − <paramref name="toleranceTicks"/>.</summary>
    public bool HasReached(long ticks, long toleranceTicks)
    {
        lock (_gate)
        {
            return _newest.All(n => n != long.MinValue && n + toleranceTicks >= ticks);
        }
    }

    public ReplayBufferStats GetStats()
    {
        lock (_gate)
        {
            long held = 0;
            if (_videoStream >= 0)
            {
                if (_keyframes.TryPeek(out long front))
                {
                    held = _newest[_videoStream] - front;
                }
            }
            else if (FrontTicks() is { } front)
            {
                held = _newest.Max() - front;
            }

            return new ReplayBufferStats(Math.Max(0, held), _bytes, _packets);
        }
    }

    /// <summary>
    /// References to the packets of [end − length, end), starting on the latest keyframe at or
    /// before end − length (or the oldest one held), every stream rebased so the clip starts at 0.
    /// Null when nothing usable is buffered yet.
    /// </summary>
    public unsafe ReplayClip? Snapshot(long endTicks, long lengthTicks)
    {
        var packets = new List<ClipPacket>();
        long start;
        lock (_gate)
        {
            if (_videoStream >= 0)
            {
                if (!_keyframes.TryPeek(out start))
                {
                    return null;
                }

                foreach (long keyframe in _keyframes)
                {
                    if (keyframe > endTicks - lengthTicks)
                    {
                        break;
                    }

                    start = keyframe;
                }
            }
            else
            {
                if (FrontTicks() is not { } front)
                {
                    return null;
                }

                start = Math.Max(front, endTicks - lengthTicks);
            }

            if (start >= endTicks)
            {
                return null;
            }

            for (int stream = 0; stream < _queues.Length; stream++)
            {
                foreach (Entry entry in _queues[stream])
                {
                    // Video from the keyframe; other streams from the packet covering the start, whose decoded
                    // frame Matroska's CodecDelay drops (an AAC frame right after a cut is incomplete).
                    bool beforeStart = stream == _videoStream ? entry.Ticks < start : entry.Ticks + entry.DurationTicks <= start;
                    if (beforeStart || entry.Ticks >= endTicks)
                    {
                        continue;
                    }

                    MediaPacket copy = entry.Packet.Clone();
                    long offset = ffmpeg.av_rescale_q(start, TickBase, copy.TimeBase);
                    copy.Native->pts -= offset;
                    copy.Native->dts -= offset;
                    packets.Add(new ClipPacket(stream, copy, entry.Ticks - start));
                }
            }
        }

        // Interleaved by time, so the muxer does not have to hold a whole stream back.
        packets.Sort((a, b) => a.Ticks != b.Ticks ? a.Ticks.CompareTo(b.Ticks) : a.Stream.CompareTo(b.Stream));
        return new ReplayClip(start, endTicks, packets);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Queue<Entry> queue in _queues)
            {
                while (queue.TryDequeue(out Entry entry))
                {
                    entry.Packet.Dispose();
                }
            }

            _keyframes.Clear();
            _bytes = 0;
            _packets = 0;
        }
    }

    private void Trim()
    {
        long audioCutoff;
        if (_videoStream >= 0)
        {
            Queue<Entry> video = _queues[_videoStream];
            long oldestNeeded = _newest[_videoStream] - CapacityTicks;

            // Drop the first GOP while the next one still starts early enough for a full-length clip.
            while (_keyframes.Count >= 2 && _keyframes.ElementAt(1) <= oldestNeeded)
            {
                _keyframes.Dequeue();
                long nextKeyframe = _keyframes.Peek();
                while (video.TryPeek(out Entry entry) && entry.Ticks < nextKeyframe)
                {
                    Remove(video);
                }
            }

            // Packets before the first keyframe can never start a clip.
            while (video.TryPeek(out Entry entry) && !entry.Key)
            {
                Remove(video);
            }

            if (!video.TryPeek(out Entry first))
            {
                return;
            }

            audioCutoff = first.Ticks - AudioTrimLeadTicks;
        }
        else
        {
            audioCutoff = _newest.Max() - CapacityTicks - AudioTrimLeadTicks;
        }

        for (int stream = 0; stream < _queues.Length; stream++)
        {
            if (stream == _videoStream)
            {
                continue;
            }

            Queue<Entry> queue = _queues[stream];
            while (queue.TryPeek(out Entry entry) && entry.Ticks < audioCutoff)
            {
                Remove(queue);
            }
        }
    }

    private void Remove(Queue<Entry> queue)
    {
        Entry entry = queue.Dequeue();
        _bytes -= entry.Size;
        _packets--;
        entry.Packet.Dispose();
    }

    private long? FrontTicks()
    {
        long? front = null;
        foreach (Queue<Entry> queue in _queues)
        {
            if (queue.TryPeek(out Entry entry) && (front is null || entry.Ticks < front))
            {
                front = entry.Ticks;
            }
        }

        return front;
    }

    private readonly record struct Entry(MediaPacket Packet, long Ticks, long DurationTicks, bool Key, int Size);
}
