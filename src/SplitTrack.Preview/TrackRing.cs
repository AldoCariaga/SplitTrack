namespace SplitTrack.Preview;

/// <summary>
/// Decoded audio of one track, 48 kHz interleaved stereo, kept at absolute sample positions so
/// every track lines up with the others. Gaps read as silence; data arriving for positions already
/// played or held is dropped. One writer (decoder) and one reader (mixer).
/// </summary>
internal sealed class TrackRing(int capacityFrames)
{
    private const int Channels = 2;

    private readonly float[] _samples = new float[capacityFrames * Channels];
    private readonly Lock _gate = new();
    private long _start;
    private int _head;
    private int _count;

    public int CapacityFrames => capacityFrames;

    /// <summary>The position right after the last frame held.</summary>
    public long End
    {
        get
        {
            lock (_gate)
            {
                return _start + _count;
            }
        }
    }

    /// <summary>Empties the ring; the next frames expected are at <paramref name="position"/>.</summary>
    public void Reset(long position)
    {
        lock (_gate)
        {
            _start = position;
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Appends frames starting at <paramref name="position"/>; what does not fit is dropped.</summary>
    public void Write(long position, ReadOnlySpan<float> interleaved)
    {
        lock (_gate)
        {
            long end = _start + _count;
            if (position < end)
            {
                long skip = Math.Min(end - position, interleaved.Length / Channels);
                interleaved = interleaved[(int)(skip * Channels)..];
                position += skip;
            }

            if (interleaved.IsEmpty)
            {
                return;
            }

            // A hole in the track (or a late start) is silence.
            int gap = (int)Math.Min(position - end, capacityFrames - _count);
            for (int i = 0; i < gap; i++)
            {
                Put(0, 0);
            }

            int frames = Math.Min(interleaved.Length / Channels, capacityFrames - _count);
            for (int i = 0; i < frames; i++)
            {
                Put(interleaved[2 * i], interleaved[(2 * i) + 1]);
            }
        }
    }

    /// <summary>
    /// Adds the frames at [<paramref name="position"/>, + output frames) times <paramref name="gain"/>
    /// into <paramref name="output"/> and drops them (and anything older). Missing frames are silence.
    /// </summary>
    /// <returns>The loudest sample added.</returns>
    public float MixInto(long position, Span<float> output, float gain)
    {
        int frames = output.Length / Channels;
        float peak = 0;
        lock (_gate)
        {
            if (_start < position)
            {
                int stale = (int)Math.Min(_count, position - _start);
                Drop(stale);
                if (_count == 0)
                {
                    // Behind (underrun): late data for these positions is dropped when it arrives.
                    _start = position;
                }
            }

            int offset = (int)Math.Min(frames, Math.Max(0, _start - position));
            int available = Math.Min(_count, frames - offset);
            if (gain > 0)
            {
                for (int i = 0; i < available; i++)
                {
                    int index = ((_head + i) % capacityFrames) * Channels;
                    float left = _samples[index] * gain;
                    float right = _samples[index + 1] * gain;
                    int o = (offset + i) * Channels;
                    output[o] += left;
                    output[o + 1] += right;
                    peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
                }
            }

            Drop(available);
            if (_count == 0 && _start < position + frames)
            {
                _start = position + frames;
            }
        }

        return peak;
    }

    private void Put(float left, float right)
    {
        int index = ((_head + _count) % capacityFrames) * Channels;
        _samples[index] = left;
        _samples[index + 1] = right;
        _count++;
    }

    private void Drop(int frames)
    {
        _head = (_head + frames) % capacityFrames;
        _count -= frames;
        _start += frames;
    }
}
