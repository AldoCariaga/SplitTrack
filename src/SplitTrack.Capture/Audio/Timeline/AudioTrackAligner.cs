using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;

namespace SplitTrack.Capture.Audio.Timeline;

public readonly record struct AlignerStats(
    long Packets,
    long SilentPackets,
    long Discontinuities,
    long TimestampErrors,
    long CapturedFrames,
    long PaddedFrames,
    long DroppedFrames,
    long EmittedFrames,
    long LastDriftTicks,
    long MaxAbsDriftTicks,
    long SmoothedDriftTicks);

/// <summary>
/// Places each packet on a QPC-anchored frame timeline starting at <see cref="Arm"/>: zero-fills
/// gaps, drops overlaps and pre-start audio, and pads to a shared end so every track has equal length.
/// </summary>
public sealed class AudioTrackAligner : IAudioSink
{
    private static readonly long GapThresholdTicks = QpcClock.MillisecondsToTicks(20);
    private static readonly long IdleLagTicks = QpcClock.MillisecondsToTicks(50);

    // Per measured packet; about 0.5 s of 10 ms packets.
    private const double DriftSmoothing = 0.02;

    private readonly IAudioFrameWriter _writer;
    private readonly string _name;
    private readonly Lock _gate = new();
    private readonly int _gapThresholdFrames;

    private long _startQpc = long.MaxValue;
    private long _endFrame = long.MaxValue;
    private long _emitted;
    private bool _resync;

    private long _packets;
    private long _silentPackets;
    private long _discontinuities;
    private long _timestampErrors;
    private long _capturedFrames;
    private long _paddedFrames;
    private long _droppedFrames;
    private long _lastDriftFrames;
    private long _maxAbsDriftFrames;
    private double _smoothedDriftFrames;

    public AudioTrackAligner(AudioFormat format, IAudioFrameWriter writer, string name = "track")
    {
        Format = format;
        _writer = writer;
        _name = name;
        _gapThresholdFrames = (int)QpcClock.TicksToFrames(GapThresholdTicks, format.SampleRate);
    }

    public AudioFormat Format { get; }

    public bool IsArmed => Volatile.Read(ref _startQpc) != long.MaxValue;

    /// <summary>
    /// Drift (packet position minus frames written) averaged over the last ~0.5 s; positive means the
    /// source delivers fewer frames than the clock says have passed.
    /// </summary>
    public double SmoothedDriftFrames
    {
        get
        {
            lock (_gate)
            {
                return _smoothedDriftFrames;
            }
        }
    }

    /// <summary>Frame 0 of the track is <paramref name="startQpc"/>; everything earlier is discarded.</summary>
    public void Arm(long startQpc)
    {
        lock (_gate)
        {
            _startQpc = startQpc;
        }
    }

    /// <summary>
    /// A new source follows: its first packet is placed exactly at its timestamp, padding or dropping
    /// as needed, instead of within the drift tolerance of what was written before.
    /// </summary>
    public void Resync()
    {
        lock (_gate)
        {
            _resync = true;
            _smoothedDriftFrames = 0;
        }
    }

    /// <summary>
    /// Fixes the track end; later audio is discarded and <see cref="Finish"/> pads up to it.
    /// Call while the end is still ~100 ms in the future: frames already written cannot be taken back.
    /// </summary>
    public void StopAt(long endQpc)
    {
        lock (_gate)
        {
            _endFrame = _startQpc == long.MaxValue ? 0 : Math.Max(0, ToFrame(endQpc));
            if (_emitted > _endFrame)
            {
                Log.Warn($"Track already {_emitted - _endFrame} frames past its end; it will be that much longer");
            }
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            if (_endFrame != long.MaxValue)
            {
                PadTo(_endFrame);
            }
        }
    }

    public AlignerStats GetStats()
    {
        lock (_gate)
        {
            int rate = Format.SampleRate;
            return new AlignerStats(
                _packets,
                _silentPackets,
                _discontinuities,
                _timestampErrors,
                _capturedFrames,
                _paddedFrames,
                _droppedFrames,
                _emitted,
                QpcClock.FramesToTicks(_lastDriftFrames, rate),
                QpcClock.FramesToTicks(_maxAbsDriftFrames, rate),
                QpcClock.FramesToTicks((long)Math.Round(_smoothedDriftFrames), rate));
        }
    }

    public void OnPacket(in AudioPacket packet)
    {
        lock (_gate)
        {
            _packets++;
            _capturedFrames += packet.Frames;
            if (packet.Silent)
            {
                _silentPackets++;
            }

            if (packet.Discontinuity)
            {
                _discontinuities++;
            }

            if (packet.TimestampError)
            {
                _timestampErrors++;
            }

            if (_startQpc == long.MaxValue)
            {
                return;
            }

            int skip = 0;
            long position = ToFrame(packet.QpcTicks);
            if (position < 0)
            {
                skip = (int)Math.Min(packet.Frames, -position);
                position += skip;
            }

            if (skip == packet.Frames || position >= _endFrame)
            {
                return;
            }

            // Positive drift: the packet starts later than what we have written so far.
            long drift = position - _emitted;
            bool exact = _emitted == 0 || _resync;
            if (!exact)
            {
                _lastDriftFrames = drift;
                _maxAbsDriftFrames = Math.Max(_maxAbsDriftFrames, Math.Abs(drift));

                // Gaps and overlaps are fixed below in one go; only tolerated drift is clock drift.
                if (Math.Abs(drift) <= _gapThresholdFrames)
                {
                    _smoothedDriftFrames += (drift - _smoothedDriftFrames) * DriftSmoothing;
                }
            }

            // A new source is placed exactly; later small drift is tolerated to avoid clicks.
            if (drift > _gapThresholdFrames || (exact && drift > 0))
            {
                LogCorrection("gap", drift, exact);
                PadTo(position);
            }
            else if (drift < -_gapThresholdFrames || (exact && drift < 0))
            {
                LogCorrection("overlap", -drift, exact);
                int overlap = (int)Math.Min(packet.Frames - skip, -drift);
                skip += overlap;
                _droppedFrames += overlap;
            }

            long count = Math.Min(packet.Frames - skip, _endFrame - _emitted);
            if (count <= 0)
            {
                return;
            }

            _resync = false;
            if (packet.Data.IsEmpty)
            {
                _writer.WriteSilence(count);
            }
            else
            {
                int blockAlign = Format.BlockAlign;
                _writer.WriteFrames(packet.Data.Slice(skip * blockAlign, (int)count * blockAlign), (int)count);
            }

            _emitted += count;
        }
    }

    public void OnIdle(long nowQpc)
    {
        lock (_gate)
        {
            if (_startQpc == long.MaxValue)
            {
                return;
            }

            // Stay a little behind "now" so a late packet still finds its place.
            long target = Math.Min(ToFrame(nowQpc - IdleLagTicks), _endFrame);
            if (target - _emitted > _gapThresholdFrames)
            {
                PadTo(target);
            }
        }
    }

    private void LogCorrection(string kind, long frames, bool exact)
    {
        if (!exact)
        {
            Log.Debug($"{_name}: {kind} of {frames * 1000.0 / Format.SampleRate:0.0} ms at {_emitted * 1000.0 / Format.SampleRate:0} ms");
        }
    }

    private long ToFrame(long qpcTicks) => QpcClock.TicksToFrames(qpcTicks - _startQpc, Format.SampleRate);

    private void PadTo(long frame)
    {
        long count = Math.Min(frame, _endFrame) - _emitted;
        if (count <= 0)
        {
            return;
        }

        _writer.WriteSilence(count);
        _emitted += count;
        _paddedFrames += count;
    }
}
