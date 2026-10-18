using SplitTrack.Capture.Audio.Wasapi;

namespace SplitTrack.Preview;

/// <summary>One audio track of the preview; <see cref="Enabled"/> and <see cref="Volume"/> are heard within one device buffer.</summary>
public sealed class PreviewTrack
{
    private int _volumeBits = BitConverter.SingleToInt32Bits(1);
    private int _peakBits;
    private volatile bool _enabled = true;

    internal PreviewTrack(AudioDecoder decoder)
    {
        Decoder = decoder;
        Title = decoder.Title is { Length: > 0 } title ? title : $"Track {decoder.Number}";
    }

    /// <summary>Stream index in the file.</summary>
    public int Stream => Decoder.Stream;

    /// <summary>1-based position among the audio tracks.</summary>
    public int Number => Decoder.Number;

    public string Title { get; }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Gain, 1 = as recorded.</summary>
    public double Volume
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _volumeBits));
        set => Volatile.Write(ref _volumeBits, BitConverter.SingleToInt32Bits((float)Math.Max(0, value)));
    }

    internal AudioDecoder Decoder { get; }

    /// <summary>Loudest sample heard from this track since the last call (after its volume).</summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    internal void ReportPeak(float peak)
    {
        int current = Volatile.Read(ref _peakBits);
        while (peak > BitConverter.Int32BitsToSingle(current))
        {
            int seen = Interlocked.CompareExchange(ref _peakBits, BitConverter.SingleToInt32Bits(peak), current);
            if (seen == current)
            {
                break;
            }

            current = seen;
        }
    }
}

/// <summary>
/// Plays a recording with every audio track mixed live at its own volume. The audio clock is the
/// master: <see cref="Position"/> is what is being heard, and <see cref="TakeFrame"/> gives the
/// picture for it. Not thread-safe: call it from one (UI) thread.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    private readonly PreviewDecoder _decoder;
    private readonly WasapiRenderer _renderer;
    private readonly Lock _gate = new();
    private long _mixPosition;
    private double _baseSeconds;
    private int _generation;
    private bool _wantPlaying;
    private bool _ready;
    private bool _disposed;

    private PreviewPlayer(string path, int maxWidth, int maxHeight)
    {
        _decoder = new PreviewDecoder(path, maxWidth, maxHeight, () => Interlocked.Read(ref _mixPosition), () => Position);
        Tracks = _decoder.Audio.Select(a => new PreviewTrack(a)).ToList();
        _decoder.Ready += OnReady;
        _renderer = WasapiRenderer.Open(Mix);
        _decoder.Start();
    }

    public IReadOnlyList<PreviewTrack> Tracks { get; }

    public double Duration => _decoder.DurationSeconds;

    public bool HasVideo => _decoder.HasVideo;

    /// <summary>Size of the frames <see cref="TakeFrame"/> returns.</summary>
    public (int Width, int Height) FrameSize => (_decoder.FrameWidth, _decoder.FrameHeight);

    public (int Width, int Height) SourceSize => (_decoder.SourceWidth, _decoder.SourceHeight);

    /// <summary>Asked to play (it may still be waiting for data after a seek).</summary>
    public bool IsPlaying => _wantPlaying;

    /// <summary>Why there is no sound (no output device, or it failed); null while audio is heard.</summary>
    public Exception? AudioFault => _renderer.Fault;

    /// <summary>Seconds being heard right now.</summary>
    public double Position => Math.Min(Duration, Volatile.Read(ref _baseSeconds) + _renderer.Position);

    /// <param name="maxWidth">Frames are scaled down to fit this box; the preview does not need more.</param>
    public static PreviewPlayer Open(string path, int maxWidth = 1280, int maxHeight = 720) => new(path, maxWidth, maxHeight);

    public void Play()
    {
        if (Position >= Duration - 0.01)
        {
            Seek(0);
        }

        lock (_gate)
        {
            _wantPlaying = true;
            if (_ready)
            {
                _renderer.Play();
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _wantPlaying = false;
            _renderer.Pause();
        }
    }

    /// <summary>Jumps to <paramref name="seconds"/>; keeps playing (once the new position has data) if it was.</summary>
    public void Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.001));
        lock (_gate)
        {
            _ready = false;
            _renderer.Reset();
            Interlocked.Exchange(ref _mixPosition, (long)Math.Round(seconds * PreviewDecoder.SampleRate));
            Volatile.Write(ref _baseSeconds, seconds);
            _generation = _decoder.RequestSeek(seconds);
        }

        // Outside the lock: the decode thread may be waiting for it in OnReady.
        _decoder.WaitSeekApplied();
    }

    /// <summary>The picture for <see cref="Position"/> if it changed since the last call, else null. Give it back with <see cref="Release"/>.</summary>
    public VideoFrame? TakeFrame() => _decoder.TakeFrame(Position + 0.002);

    public void Release(VideoFrame frame) => _decoder.Release(frame);

    /// <summary>Stops at the end; call regularly (e.g. per UI frame). True when it just stopped.</summary>
    public bool CheckEnded()
    {
        lock (_gate)
        {
            if (!_wantPlaying || Position < Duration - 0.005)
            {
                return false;
            }

            _wantPlaying = false;
            _renderer.Pause();
            return true;
        }
    }

    public void Dispose()
    {
        // The decode thread must not start the renderer once it is going away.
        lock (_gate)
        {
            _disposed = true;
        }

        _renderer.Dispose();
        _decoder.Dispose();
    }

    // Decode thread: the position asked for last has its first picture and some audio.
    private void OnReady(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                return;
            }

            _ready = true;
            if (_wantPlaying)
            {
                _renderer.Play();
            }
        }
    }

    // Render thread: the next device buffer, from every track at its current volume.
    private void Mix(Span<float> output)
    {
        output.Clear();
        long position = Interlocked.Read(ref _mixPosition);
        foreach (PreviewTrack track in Tracks)
        {
            float gain = track.Enabled ? (float)track.Volume : 0;
            track.ReportPeak(track.Decoder.Ring.MixInto(position, output, gain));
        }

        for (int i = 0; i < output.Length; i++)
        {
            output[i] = Math.Clamp(output[i], -1f, 1f);
        }

        Interlocked.Add(ref _mixPosition, output.Length / WasapiRenderer.Channels);
    }
}
