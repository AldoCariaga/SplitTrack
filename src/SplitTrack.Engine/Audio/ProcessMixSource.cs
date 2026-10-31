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
/// on as far as every input has arrived. Inputs are added and removed while it runs.
/// </summary>
internal sealed class ProcessMixSource : IAudioSource
{
    private const int Channels = 2;
    private const int CapacityFrames = 2 * 48_000;
    private const int MaxPacketFrames = 4_800;

    // An idle capture reports every 100 ms up to 50 ms before now; one further behind (stalled,
    // or not started yet) stops holding the mix back and loses what arrives too late.
    private static readonly long MaxLagTicks = QpcClock.MillisecondsToTicks(300);

    // With no input to wait for, the mix follows the clock this far behind, like an idle aligner.
    private static readonly long IdleLagTicks = QpcClock.MillisecondsToTicks(50);

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Input> _inputs = [];
    private readonly float[] _ring = new float[CapacityFrames * Channels];
    private readonly float[] _packet = new float[MaxPacketFrames * Channels];
    private readonly long _startQpc = QpcClock.Now;
    private long _out;
    private IAudioSink? _sink;

    public ProcessMixSource(string name) => Name = name;

    public string Name { get; }

    public AudioFormat Format => AudioResampler.OutputFormat;

    /// <summary>Always null: inputs fail one by one, see <see cref="FaultedPids"/>.</summary>
    public Exception? Fault => null;

    /// <summary>The root PIDs being captured.</summary>
    public IReadOnlyList<int> Pids
    {
        get
        {
            lock (_gate)
            {
                return _inputs.Keys.ToList();
            }
        }
    }

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

    /// <summary>Starts capturing <paramref name="pid"/>'s tree into the mix.</summary>
    public void Add(int pid, string exeName)
    {
        var source = ProcessLoopbackSource.Open($"{Name} ({exeName})", pid, includeTree: true, AudioFormat.Float48Stereo);
        Input input;
        try
        {
            input = new Input(this, pid, exeName, source);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        lock (_gate)
        {
            _inputs.Add(pid, input);
        }

        source.Start(input.Sink);
    }

    public void Remove(int pid)
    {
        Input? input;
        lock (_gate)
        {
            if (!_inputs.Remove(pid, out input))
            {
                return;
            }

            input.Removed = true;
        }

        // Outside the lock: its capture thread may be waiting for it.
        input.Dispose();
        Log.Info($"{Name}: {input.ExeName} (pid {pid} tree) left the mix");
    }

    /// <summary>Moves the mix on with the clock while no input does; call regularly.</summary>
    public void Pump()
    {
        lock (_gate)
        {
            Emit(QpcClock.Now, flush: false);
        }
    }

    public void Start(IAudioSink sink)
    {
        lock (_gate)
        {
            _sink = sink;
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

        lock (_gate)
        {
            Emit(QpcClock.Now, flush: true);
            _sink = null;
        }
    }

    public void Dispose()
    {
        Stop();
        List<Input> inputs;
        lock (_gate)
        {
            inputs = _inputs.Values.ToList();
            _inputs.Clear();
        }

        foreach (Input input in inputs)
        {
            input.Dispose();
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

            Emit(QpcClock.Now, flush: false);
        }
    }

    // Under the lock: hands on the frames every input that keeps up has delivered.
    private void Emit(long nowQpc, bool flush)
    {
        if (_sink is null)
        {
            return;
        }

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

        ready = Math.Min(ready, _out + CapacityFrames);
        while (_out < ready)
        {
            int frames = (int)Math.Min(ready - _out, MaxPacketFrames);
            for (int i = 0; i < frames; i++)
            {
                int index = (int)((_out + i) % CapacityFrames) * Channels;
                _packet[i * Channels] = _ring[index];
                _packet[(i * Channels) + 1] = _ring[index + 1];
                _ring[index] = 0;
                _ring[index + 1] = 0;
            }

            long qpc = _startQpc + QpcClock.FramesToTicks(_out, rate);
            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(_packet.AsSpan(0, frames * Channels));
            _sink.OnPacket(new AudioPacket(data, frames, qpc, silent: false, discontinuity: false, timestampError: false));
            _out += frames;
        }
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
