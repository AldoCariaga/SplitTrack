using System.Runtime.InteropServices;
using SplitTrack.Capture.Audio;
using SplitTrack.Capture.Audio.Timeline;
using SplitTrack.Capture.Audio.Wasapi;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Encode.Audio;

namespace SplitTrack.Engine.Audio;

/// <summary>
/// Several process trees captured one by one and summed into one stream. Windows can leave only
/// one tree out of a capture, so this is how the System track leaves out the game and every app
/// track. Each input has its own resampler and aligner on a shared QPC timeline; the sum is handed
/// on as far as every input has arrived. Inputs are opened in the background and added and removed
/// while it runs; the mix's own thread hands the sum on, so capture threads only add to it.
/// </summary>
internal sealed class ProcessMixSource : IAudioSource
{
    private const int Channels = 2;
    private const int CapacityFrames = 2 * 48_000;
    private const int MaxPacketFrames = 4_800;

    // How often the mix hands on what is ready: well inside an input's 100 ms idle reports.
    private static readonly TimeSpan EmitInterval = TimeSpan.FromMilliseconds(10);

    // An idle capture reports every 100 ms up to 50 ms before now; one further behind (stalled,
    // or not started yet) stops holding the mix back and loses what arrives too late.
    private static readonly long MaxLagTicks = QpcClock.MillisecondsToTicks(300);

    // With no input to wait for, the mix follows the clock this far behind, like an idle aligner.
    private static readonly long IdleLagTicks = QpcClock.MillisecondsToTicks(50);

    // _gate guards the inputs, the ring and _out, and is held only briefly. _emitGate keeps one
    // thread at a time handing packets to the sink, in order, without holding _gate meanwhile.
    private readonly Lock _gate = new();
    private readonly Lock _emitGate = new();
    private readonly Dictionary<int, Input> _inputs = [];
    private readonly HashSet<int> _opening = [];
    private readonly List<int> _failed = [];
    private readonly float[] _ring = new float[CapacityFrames * Channels];
    private readonly float[] _packet = new float[MaxPacketFrames * Channels];
    private readonly long _startQpc = QpcClock.Now;
    private readonly ManualResetEventSlim _stopEmitter = new();
    private long _out;
    private IAudioSink? _sink;
    private Thread? _emitter;
    private Exception? _fault;
    private bool _disposed;

    public ProcessMixSource(string name) => Name = name;

    public string Name { get; }

    public AudioFormat Format => AudioResampler.OutputFormat;

    /// <summary>Set when handing the mix on failed; inputs fail one by one, see <see cref="FaultedPids"/>.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>The root PIDs being captured or opened.</summary>
    public IReadOnlyList<int> Pids
    {
        get
        {
            lock (_gate)
            {
                return [.. _inputs.Keys, .. _opening];
            }
        }
    }

    /// <summary>Inputs whose capture failed after it started.</summary>
    public IReadOnlyList<int> FaultedPids
    {
        get
        {
            lock (_gate)
            {
                return _inputs.Values.Where(i => i.Source.Fault is not null).Select(i => i.Pid).ToList();
            }
        }
    }

    /// <summary>Starts capturing <paramref name="pid"/>'s tree into the mix, opening it in the background.</summary>
    public void Request(int pid, string exeName)
    {
        lock (_gate)
        {
            if (_disposed || _inputs.ContainsKey(pid) || !_opening.Add(pid))
            {
                return;
            }
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ => Open(pid, exeName), null);
    }

    /// <summary>PIDs that could not be opened since the last call; they are no longer in <see cref="Pids"/>.</summary>
    public IReadOnlyList<int> TakeFailedPids()
    {
        lock (_gate)
        {
            List<int> failed = [.. _failed];
            _failed.Clear();
            return failed;
        }
    }

    public void Remove(int pid)
    {
        Input? input;
        lock (_gate)
        {
            // Still opening: Open sees it is no longer wanted and closes it.
            if (_opening.Remove(pid) || !_inputs.Remove(pid, out input))
            {
                return;
            }

            input.Removed = true;
        }

        // Outside the lock: its capture thread may be waiting for it.
        input.Dispose();
        Log.Info($"{Name}: {input.ExeName} (pid {pid} tree) left the mix");
    }

    public void Start(IAudioSink sink)
    {
        lock (_gate)
        {
            _sink = sink;
        }

        if (_emitter is null)
        {
            _stopEmitter.Reset();
            _emitter = new Thread(RunEmitter) { IsBackground = true, Name = $"{Name} mix" };
            _emitter.Start();
        }
    }

    /// <summary>Stops every input and hands on what they delivered up to then.</summary>
    public void Stop()
    {
        List<Input> inputs;
        lock (_gate)
        {
            inputs = _inputs.Values.ToList();
        }

        foreach (Input input in inputs)
        {
            input.Source.Stop();
        }

        _stopEmitter.Set();
        _emitter?.Join();
        _emitter = null;
        EmitReady(flush: true);
        lock (_gate)
        {
            _sink = null;
        }
    }

    public void Dispose()
    {
        Stop();
        List<Input> inputs;
        lock (_gate)
        {
            _disposed = true;
            inputs = _inputs.Values.ToList();
            _inputs.Clear();
            _opening.Clear();
        }

        foreach (Input input in inputs)
        {
            input.Dispose();
        }

        _stopEmitter.Dispose();
    }

    // A thread-pool thread: opening a process loopback capture waits for Windows, up to seconds.
    private void Open(int pid, string exeName)
    {
        Input input;
        try
        {
            var source = ProcessLoopbackSource.Open($"{Name} ({exeName})", pid, includeTree: true, AudioFormat.Float48Stereo);
            try
            {
                input = new Input(this, pid, exeName, source);
                source.Start(input.Sink);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_opening.Remove(pid))
                {
                    _failed.Add(pid);
                }
            }

            Log.Warn($"{Name}: could not capture {exeName} (pid {pid}), retrying", ex);
            return;
        }

        bool wanted;
        lock (_gate)
        {
            // Frames it delivered before this already sit in the ring at their place.
            wanted = _opening.Remove(pid) && !_disposed;
            if (wanted)
            {
                _inputs.Add(pid, input);
            }
            else
            {
                input.Removed = true;
            }
        }

        if (!wanted)
        {
            input.Dispose();
            return;
        }

        Log.Info($"{Name}: {exeName} (pid {pid} tree) joined the mix");
    }

    private void RunEmitter()
    {
        try
        {
            while (!_stopEmitter.Wait(EmitInterval))
            {
                EmitReady(flush: false);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, ex);
            Log.Error($"{Name}: mixing stopped", ex);
        }
    }

    // An input's aligner thread: frames for [input.Position, + frames); empty samples are silence.
    private void OnFrames(Input input, ReadOnlySpan<float> samples, long frames)
    {
        lock (_gate)
        {
            if (input.Removed)
            {
                return;
            }

            long start = input.Position;
            input.Position += frames;
            if (!samples.IsEmpty)
            {
                // Late frames (the mix has moved on) and frames too far ahead are dropped.
                long from = Math.Max(start, _out);
                long to = Math.Min(input.Position, _out + CapacityFrames);
                for (long frame = from; frame < to; frame++)
                {
                    int source = (int)(frame - start) * Channels;
                    int target = (int)(frame % CapacityFrames) * Channels;
                    _ring[target] += samples[source];
                    _ring[target + 1] += samples[source + 1];
                }
            }
        }
    }

    /// <summary>
    /// Hands on the frames every input that keeps up has delivered, a packet at a time: each packet
    /// is taken out of the ring under the lock and handed to the sink outside it.
    /// </summary>
    private void EmitReady(bool flush)
    {
        lock (_emitGate)
        {
            while (true)
            {
                IAudioSink? sink;
                int frames;
                long qpc;
                lock (_gate)
                {
                    sink = _sink;
                    long ready = ReadyUpTo(QpcClock.Now, flush);
                    frames = (int)Math.Min(ready - _out, MaxPacketFrames);
                    if (sink is null || frames <= 0)
                    {
                        return;
                    }

                    for (int i = 0; i < frames; i++)
                    {
                        int index = (int)((_out + i) % CapacityFrames) * Channels;
                        _packet[i * Channels] = _ring[index];
                        _packet[(i * Channels) + 1] = _ring[index + 1];
                        _ring[index] = 0;
                        _ring[index + 1] = 0;
                    }

                    qpc = _startQpc + QpcClock.FramesToTicks(_out, Format.SampleRate);
                    _out += frames;
                }

                // _packet is only touched under _emitGate.
                ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(_packet.AsSpan(0, frames * Channels));
                sink.OnPacket(new AudioPacket(data, frames, qpc, silent: false, discontinuity: false, timestampError: false));
            }
        }
    }

    // Under _gate: the frame the mix can hand on up to now.
    private long ReadyUpTo(long nowQpc, bool flush)
    {
        int rate = Format.SampleRate;
        long clock = QpcClock.TicksToFrames(nowQpc - _startQpc, rate);
        long oldest = clock - QpcClock.TicksToFrames(MaxLagTicks, rate);
        long ready = long.MaxValue;
        foreach (Input input in _inputs.Values)
        {
            if (flush)
            {
                ready = ready == long.MaxValue ? input.Position : Math.Max(ready, input.Position);
            }
            else if (input.Position >= oldest)
            {
                ready = Math.Min(ready, input.Position);
            }
        }

        if (ready == long.MaxValue)
        {
            ready = clock - QpcClock.TicksToFrames(IdleLagTicks, rate);
        }

        return Math.Min(ready, _out + CapacityFrames);
    }

    /// <summary>One process tree: its capture, resampled and aligned to the mix's timeline.</summary>
    private sealed class Input : IAudioFrameWriter, IDisposable
    {
        private readonly ProcessMixSource _owner;

        public Input(ProcessMixSource owner, int pid, string exeName, ProcessLoopbackSource source)
        {
            _owner = owner;
            Pid = pid;
            ExeName = exeName;
            Source = source;
            Aligner = new AudioTrackAligner(AudioResampler.OutputFormat, this, source.Name);
            Aligner.Arm(owner._startQpc);
            Sink = new ResamplingSink(source.Format, Aligner);
        }

        public int Pid { get; }

        public string ExeName { get; }

        public ProcessLoopbackSource Source { get; }

        public AudioTrackAligner Aligner { get; }

        public ResamplingSink Sink { get; }

        /// <summary>Frames delivered so far on the mix's timeline; under the owner's lock.</summary>
        public long Position { get; set; }

        /// <summary>Out of the mix; what it still delivers is ignored. Under the owner's lock.</summary>
        public bool Removed { get; set; }

        public void WriteFrames(ReadOnlySpan<byte> data, int frames) =>
            _owner.OnFrames(this, MemoryMarshal.Cast<byte, float>(data), frames);

        public void WriteSilence(long frames) => _owner.OnFrames(this, ReadOnlySpan<float>.Empty, frames);

        public void Dispose()
        {
            Source.Dispose();
            Sink.Dispose();
        }
    }
}
