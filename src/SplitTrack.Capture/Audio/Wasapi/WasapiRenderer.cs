using System.Runtime.InteropServices;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SplitTrack.Capture.Audio.Wasapi;

/// <summary>
/// Plays 48 kHz stereo float on the default output device (shared mode, converted to the device's
/// format by Windows), pulling audio from a callback. Every COM call runs on the render thread;
/// <see cref="Play"/>, <see cref="Pause"/> and <see cref="Reset"/> wait until it has applied them.
/// Without a usable device it keeps time on the QPC clock and discards the audio, so a player using
/// its <see cref="Position"/> still runs.
/// </summary>
public sealed unsafe class WasapiRenderer : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    // Short, so volume changes are heard quickly; a GC pause longer than this is an audible gap only.
    private const long BufferDurationTicks = 500_000;
    private const int PollMs = 10;

    private readonly Action<Span<float>> _fill;
    private readonly AutoResetEvent _bufferEvent = new(false);
    private readonly Lock _gate = new();
    private readonly Queue<(Command Command, ManualResetEventSlim Done)> _commands = new();
    private readonly Thread _thread;
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private IAudioClock? _clock;
    private ulong _clockFrequency;
    private uint _bufferFrames;
    private float[] _scratch = new float[SampleRate / 10 * Channels];
    private bool _running;
    private volatile bool _stopping;

    // Played frames since the last Reset and the QPC instant they were read at.
    private long _playedFrames;
    private long _playedAtQpc;

    // Device-less timekeeping: frames handed to the callback since the last Reset.
    private long _nullFrames;
    private long _nullLastQpc;

    private WasapiRenderer(Action<Span<float>> fill)
    {
        _fill = fill;
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio render" };
    }

    private enum Command
    {
        Play,
        Pause,
        Reset,
    }

    /// <summary>Why the device is not used (none, or it failed); null while audio is heard.</summary>
    public Exception? Fault { get; private set; }

    public bool IsPlaying => Volatile.Read(ref _running);

    /// <summary>Seconds played since the last <see cref="Reset"/>, interpolated between device reads.</summary>
    public double Position
    {
        get
        {
            lock (_gate)
            {
                double seconds = (double)_playedFrames / SampleRate;
                if (_running && _playedAtQpc != 0)
                {
                    // Interpolate up to one poll interval; the device read catches up after that.
                    double since = (double)(QpcClock.Now - _playedAtQpc) / QpcClock.TicksPerSecond;
                    seconds += Math.Clamp(since, 0, 0.05);
                }

                return seconds;
            }
        }
    }

    /// <param name="fill">
    /// Called on the render thread with interleaved stereo samples to fill completely; it decides
    /// what the next samples are, so it is also where the stream position advances.
    /// </param>
    public static WasapiRenderer Open(Action<Span<float>> fill)
    {
        var renderer = new WasapiRenderer(fill);
        renderer._thread.Start();
        return renderer;
    }

    public void Play() => Send(Command.Play);

    public void Pause() => Send(Command.Pause);

    /// <summary>Drops what is queued on the device and starts the position again at 0; pauses.</summary>
    public void Reset() => Send(Command.Reset);

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _bufferEvent.Set();
        _thread.Join();
        _bufferEvent.Dispose();
    }

    private void Send(Command command)
    {
        using var done = new ManualResetEventSlim();
        lock (_commands)
        {
            _commands.Enqueue((command, done));
        }

        _bufferEvent.Set();
        if (!done.Wait(TimeSpan.FromSeconds(2)))
        {
            Log.Warn($"Audio render: {command} was not applied within 2 s");
        }
    }

    private void Run()
    {
        try
        {
            OpenDevice();
        }
        catch (Exception ex)
        {
            Fault = ex;
            Log.Warn("Audio render: no output device; playing without sound", ex);
        }

        uint taskIndex = 0;
        using var mmcss = PInvoke.AvSetMmThreadCharacteristics("Audio", ref taskIndex);

        while (!_stopping)
        {
            _bufferEvent.WaitOne(PollMs);
            ApplyCommands();
            if (!_running)
            {
                continue;
            }

            if (_client is not null && Fault is null)
            {
                try
                {
                    FillDevice();
                    ReadClock();
                }
                catch (Exception ex)
                {
                    Fault = ex;
                    Log.Warn("Audio render: the output device failed; playing on without sound", ex);
                    lock (_gate)
                    {
                        _nullFrames = _playedFrames;
                        _nullLastQpc = QpcClock.Now;
                    }
                }
            }
            else
            {
                FillNull();
            }
        }

        // Unblocks callers that sent a command while the thread was stopping.
        ApplyCommands();
        CloseDevice();
    }

    private void OpenDevice()
    {
        IMMDeviceEnumerator enumerator = AudioDeviceEnumerator.CreateEnumerator();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice device);
        Guid iid = typeof(IAudioClient).GUID;
        device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out object activated);
        var client = (IAudioClient)activated;
        try
        {
            WAVEFORMATEXTENSIBLE format = WaveFormatInterop.ToNative(AudioFormat.Float48Stereo);
            uint flags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, BufferDurationTicks, 0, (WAVEFORMATEX*)&format, null);
            client.SetEventHandle(new HANDLE((void*)_bufferEvent.SafeWaitHandle.DangerousGetHandle()));
            client.GetBufferSize(out _bufferFrames);

            Guid renderIid = typeof(IAudioRenderClient).GUID;
            client.GetService(&renderIid, out void* render);
            Guid clockIid = typeof(IAudioClock).GUID;
            client.GetService(&clockIid, out void* clock);
            try
            {
                _render = (IAudioRenderClient)Marshal.GetObjectForIUnknown((nint)render);
                _clock = (IAudioClock)Marshal.GetObjectForIUnknown((nint)clock);
            }
            finally
            {
                Marshal.Release((nint)render);
                Marshal.Release((nint)clock);
            }

            _clock.GetFrequency(out _clockFrequency);
            _client = client;
            Log.Info($"Audio render: '{AudioDeviceEnumerator.GetFriendlyName(device)}', buffer {_bufferFrames} frames");
        }
        catch
        {
            Marshal.ReleaseComObject(client);
            throw;
        }
    }

    private void ApplyCommands()
    {
        while (true)
        {
            (Command Command, ManualResetEventSlim Done) item;
            lock (_commands)
            {
                if (!_commands.TryDequeue(out item))
                {
                    return;
                }
            }

            try
            {
                Apply(item.Command);
            }
            catch (Exception ex)
            {
                Log.Warn($"Audio render: {item.Command} failed", ex);
            }
            finally
            {
                item.Done.Set();
            }
        }
    }

    private void Apply(Command command)
    {
        bool device = _client is not null && Fault is null;
        switch (command)
        {
            case Command.Play when !_running:
                if (device)
                {
                    // Prefilled, so the first device period is not silence.
                    FillDevice();
                    _client!.Start();
                }

                lock (_gate)
                {
                    _running = true;
                    _playedAtQpc = QpcClock.Now;
                    _nullLastQpc = QpcClock.Now;
                }

                break;
            case Command.Pause when _running:
                if (device)
                {
                    _client!.Stop();
                    ReadClock();
                }

                lock (_gate)
                {
                    _running = false;
                    _nullFrames = _playedFrames;
                }

                break;
            case Command.Reset:
                if (device)
                {
                    if (_running)
                    {
                        _client!.Stop();
                    }

                    _client!.Reset();
                }

                lock (_gate)
                {
                    _running = false;
                    _playedFrames = 0;
                    _playedAtQpc = 0;
                    _nullFrames = 0;
                }

                break;
        }
    }

    private void FillDevice()
    {
        _client!.GetCurrentPadding(out uint padding);
        int frames = (int)(_bufferFrames - padding);
        if (frames <= 0)
        {
            return;
        }

        byte* data;
        _render!.GetBuffer((uint)frames, &data);
        var samples = new Span<float>(data, frames * Channels);
        try
        {
            _fill(samples);
        }
        catch (Exception ex)
        {
            samples.Clear();
            Log.Error("Audio render: the fill callback failed", ex);
        }

        _render.ReleaseBuffer((uint)frames, 0);
    }

    private void ReadClock()
    {
        _clock!.GetPosition(out ulong position, out ulong qpc);
        lock (_gate)
        {
            _playedFrames = (long)(position * SampleRate / Math.Max(1, _clockFrequency));
            _playedAtQpc = qpc != 0 ? (long)qpc : QpcClock.Now;
        }
    }

    /// <summary>No device: hands the callback as many frames as real time has passed, and drops them.</summary>
    private void FillNull()
    {
        long now = QpcClock.Now;
        int frames;
        lock (_gate)
        {
            frames = (int)((now - _nullLastQpc) * SampleRate / QpcClock.TicksPerSecond);
            if (frames <= 0)
            {
                return;
            }

            _nullLastQpc += frames * QpcClock.TicksPerSecond / SampleRate;
        }

        if (_scratch.Length < frames * Channels)
        {
            _scratch = new float[frames * Channels];
        }

        try
        {
            _fill(_scratch.AsSpan(0, frames * Channels));
        }
        catch (Exception ex)
        {
            Log.Error("Audio render: the fill callback failed", ex);
        }

        lock (_gate)
        {
            _nullFrames += frames;
            _playedFrames = _nullFrames;
            _playedAtQpc = now;
        }
    }

    private void CloseDevice()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            if (_running)
            {
                _client.Stop();
            }
        }
        catch (COMException)
        {
        }

        Marshal.ReleaseComObject(_render!);
        Marshal.ReleaseComObject(_clock!);
        Marshal.ReleaseComObject(_client);
        _client = null;
    }
}
