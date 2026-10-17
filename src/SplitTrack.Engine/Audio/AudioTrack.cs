using SplitTrack.Capture.Audio;
using SplitTrack.Capture.Audio.Timeline;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Encode.Audio;
using SplitTrack.Engine.Recording;
using SplitTrack.FFmpeg;

namespace SplitTrack.Engine.Audio;

/// <summary>Who a track's current source is, so the watcher can tell when it has to change.</summary>
/// <param name="Pid">Process (or excluded process) the stream was opened on.</param>
/// <param name="CreationTime">That process's creation time, to tell a reused PID apart.</param>
/// <param name="DeviceId">Endpoint ID for a microphone.</param>
/// <param name="Name">What the library shows as the track's source: exe name or device name.</param>
internal sealed record SourceIdentity(string Description, int? Pid = null, long? CreationTime = null, string? DeviceId = null, string? Name = null);

/// <summary>
/// One audio track of the recording: a fixed aligner → AAC chain, fed by whichever source is
/// attached at the moment. Sources come and go (process restarts, device changes); the track's
/// timeline and stream never break, gaps become silence.
/// </summary>
internal sealed class AudioTrack : IDisposable
{
    private readonly Lock _gate = new();
    private readonly AacEncoder _encoder;
    private readonly AacTrackWriter _writer;
    private IAudioSource? _source;
    private ResamplingSink? _sink;

    public AudioTrack(AudioTrackOptions options, int bitrateKbps, Action<MediaPacket> output)
    {
        Options = options;
        _encoder = new AacEncoder(bitrateKbps);
        _writer = new AacTrackWriter(_encoder, output);
        Aligner = new AudioTrackAligner(AacEncoder.InputFormat, _writer, options.Title);
    }

    public AudioTrackOptions Options { get; }

    public string Title => Options.Title;

    public AudioTrackAligner Aligner { get; }

    public PeakMeter Meter => _writer.Meter;

    /// <summary>Frames encoded so far, i.e. the track's length at 48 kHz.</summary>
    public long Frames => _writer.Frames;

    /// <summary>Null while nothing is attached.</summary>
    public SourceIdentity? Identity { get; private set; }

    /// <summary>The most recent source's <see cref="SourceIdentity.Name"/>; kept after it stops.</summary>
    public string? LastSourceName { get; private set; }

    public Exception? Fault
    {
        get
        {
            lock (_gate)
            {
                return _source?.Fault;
            }
        }
    }

    public double CorrectionPpm
    {
        get
        {
            lock (_gate)
            {
                return _sink?.CorrectionPpm ?? 0;
            }
        }
    }

    public MediaStreamInfo CreateStreamInfo() => _encoder.CreateStreamInfo(Title);

    /// <summary>Replaces the current source; the new one's first packet lands exactly at its timestamp.</summary>
    public void Attach(IAudioSource source, SourceIdentity identity)
    {
        lock (_gate)
        {
            DetachLocked();
            Aligner.Resync();
            _sink = new ResamplingSink(source.Format, Aligner);
            _source = source;
            Identity = identity;
            LastSourceName = identity.Name ?? LastSourceName;
            source.Start(_sink);
        }

        Log.Info($"{Title}: now recording {identity.Description} ({source.Format})");
    }

    public void Detach()
    {
        lock (_gate)
        {
            if (_source is null)
            {
                return;
            }

            Log.Info($"{Title}: {Identity?.Description} stopped; track continues as silence");
            DetachLocked();
        }
    }

    /// <summary>Keeps a track without a source in step with the clock; a live source does this itself.</summary>
    public void PadIfIdle(long nowQpc)
    {
        lock (_gate)
        {
            if (_source is null)
            {
                Aligner.OnIdle(nowQpc);
            }
        }
    }

    /// <summary>Pads to the end set by <see cref="AudioTrackAligner.StopAt"/> and drains the encoder. Detach first.</summary>
    public void Finish()
    {
        Aligner.Finish();
        _writer.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DetachLocked();
        }

        _encoder.Dispose();
    }

    private void DetachLocked()
    {
        _source?.Stop();
        _source?.Dispose();
        _sink?.Dispose();
        _source = null;
        _sink = null;
        Identity = null;
    }
}
