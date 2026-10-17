using System.Diagnostics;
using SplitTrack.Capture.Video;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Encode.Video;
using SplitTrack.FFmpeg;

namespace SplitTrack.Engine.Video;

/// <summary>Packets and bytes are the encoder's output.</summary>
public readonly record struct PipelineStats(
    long Encoded, long Duplicated, long Skipped, double EncodeTotalMs, double EncodeMaxMs, long Packets, long Bytes);

/// <summary>
/// Constant-frame-rate encode loop on the QPC clock. Tick i is due at start + i/fps; it converts
/// whatever the capture canvas holds then (repeating a frame when the screen did not change) and
/// is encoded with pts = i. A tick that is already a whole frame late is skipped, never shifted, so
/// video time stays wall-clock time.
/// </summary>
public sealed class VideoPipeline
{
    private readonly WgcCaptureSource _capture;
    private readonly Nv12Converter _converter;
    private readonly VideoEncoder _encoder;
    private readonly Action<MediaPacket> _output;
    private readonly Action<MediaPacket> _countAndOutput;
    private readonly int _fps;
    private readonly Lock _statsGate = new();
    private Thread? _thread;
    private long _startQpc;
    private long _endQpc = long.MaxValue;
    private PipelineStats _stats;

    /// <param name="output">Receives each encoded packet, on the encode thread, and owns it from then on.</param>
    public VideoPipeline(WgcCaptureSource capture, Nv12Converter converter, VideoEncoder encoder, Action<MediaPacket> output)
    {
        _capture = capture;
        _converter = converter;
        _encoder = encoder;
        _output = output;
        _countAndOutput = CountAndOutput;
        _fps = encoder.Settings.Fps;
    }

    public Exception? Fault { get; private set; }

    public bool IsRunning => _thread is { IsAlive: true };

    public void Start(long startQpc)
    {
        _startQpc = startQpc;
        _thread = new Thread(Run) { IsBackground = true, Name = "SplitTrack video encode", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Frames due at or after <paramref name="endQpc"/> are not encoded.</summary>
    public void StopAt(long endQpc) => Interlocked.Exchange(ref _endQpc, Math.Min(endQpc, Interlocked.Read(ref _endQpc)));

    public void Join() => _thread?.Join();

    public PipelineStats GetStats()
    {
        lock (_statsGate)
        {
            return _stats;
        }
    }

    private void Run()
    {
        try
        {
            using var timer = new PreciseTimer();
            long index = 0;
            long lastCaptured = -1;
            var watch = new Stopwatch();

            while (true)
            {
                long due = _startQpc + QpcClock.FramesToTicks(index, _fps);
                if (due >= Interlocked.Read(ref _endQpc))
                {
                    break;
                }

                timer.WaitUntil(due);

                long current = QpcClock.TicksToFrames(QpcClock.Now - _startQpc, _fps);
                long skipped = Math.Max(0, current - index);
                index += skipped;

                // The end may have been set while waiting.
                if (_startQpc + QpcClock.FramesToTicks(index, _fps) >= Interlocked.Read(ref _endQpc))
                {
                    break;
                }

                watch.Restart();
                (nint texture, uint slice) = _encoder.BeginFrame();
                _capture.ReadCanvas((_converter, texture, slice), static (canvas, s) => s._converter.Convert(canvas, s.texture, s.slice));
                long captured = _capture.GetStats().Frames;
                _encoder.EncodeFrame(index, _countAndOutput);
                double ms = watch.Elapsed.TotalMilliseconds;

                lock (_statsGate)
                {
                    _stats = _stats with
                    {
                        Encoded = _stats.Encoded + 1,
                        Duplicated = _stats.Duplicated + (captured == lastCaptured ? 1 : 0),
                        Skipped = _stats.Skipped + skipped,
                        EncodeTotalMs = _stats.EncodeTotalMs + ms,
                        EncodeMaxMs = Math.Max(_stats.EncodeMaxMs, ms),
                    };
                }

                lastCaptured = captured;
                index++;
            }

            _encoder.Flush(_countAndOutput);
            Log.Info($"Encode loop finished at frame {index}");
        }
        catch (Exception ex)
        {
            Fault = ex;
            Log.Error("Video encode loop failed", ex);
        }
    }

    private void CountAndOutput(MediaPacket packet)
    {
        lock (_statsGate)
        {
            _stats = _stats with { Packets = _stats.Packets + 1, Bytes = _stats.Bytes + packet.Size };
        }

        _output(packet);
    }
}
