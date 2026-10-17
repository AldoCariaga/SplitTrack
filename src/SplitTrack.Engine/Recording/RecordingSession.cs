using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Engine.Audio;
using SplitTrack.Engine.Video;
using SplitTrack.Mux.Mkv;

namespace SplitTrack.Engine.Recording;

/// <summary>
/// One recording into one MKV: video plus one AAC track per audio source, all on the QPC clock
/// from the same start instant to the same end instant. Open → Start → StopAt/StopSoon → Complete.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    // The end must be set while still ahead of every track, which can lead the clock by its drift tolerance.
    private static readonly long StopLeadTicks = QpcClock.MillisecondsToTicks(100);

    // Lets packets captured before the end instant arrive before the streams close.
    private static readonly long StopGraceTicks = QpcClock.MillisecondsToTicks(150);

    private readonly RecordingOptions _options;
    private readonly List<AudioTrack> _tracks = [];
    private MkvWriter? _mkv;
    private VideoRecorder? _video;
    private SourceWatcher? _watcher;
    private int _videoStream = -1;
    private long _endQpc = long.MaxValue;
    private bool _completed;

    private RecordingSession(RecordingOptions options) => _options = options;

    public string OutputPath => _options.OutputPath;

    /// <summary>The first frame's instant; 0 until <see cref="Start"/>.</summary>
    public long StartQpc { get; private set; }

    /// <summary>The scheduled end, or <see cref="long.MaxValue"/> while open-ended.</summary>
    public long EndQpc => Interlocked.Read(ref _endQpc);

    public string? VideoDescription => _video?.Description;

    /// <summary>The video cannot continue: its window closed or the encode loop failed.</summary>
    public bool VideoStopped => _video is { } video && (video.IsClosed || (video.HasEnded && EndQpc == long.MaxValue));

    public bool VideoClosed => _video?.IsClosed ?? false;

    /// <summary>Creates the file, the encoders and the video capture; nothing is recorded until <see cref="Start"/>.</summary>
    public static RecordingSession Open(RecordingOptions options)
    {
        if (options.Video is null && options.AudioTracks.Count == 0)
        {
            throw new RecordingSetupException("Nothing to record: no video and no audio tracks.");
        }

        var session = new RecordingSession(options);
        try
        {
            session.Initialize();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Start()
    {
        // Sources are attached before the start instant, so their first packets are already flowing.
        _watcher!.Reconcile();
        _video?.StartCapture();

        StartQpc = QpcClock.Now;
        foreach (AudioTrack track in _tracks)
        {
            track.Aligner.Arm(StartQpc);
        }

        _video?.StartEncoding(StartQpc, packet => _mkv!.Write(_videoStream, packet));
        _watcher.Start();
        Log.Info($"Recording started at qpc {StartQpc}: {OutputPath}");
    }

    /// <summary>
    /// Sets (or brings forward) the end. Call it while the end is still at least 100 ms away, e.g.
    /// right after <see cref="Start"/> for a fixed length; <see cref="StopSoon"/> does that for "now".
    /// </summary>
    public void StopAt(long endQpc)
    {
        long current = Interlocked.Read(ref _endQpc);
        if (endQpc >= current)
        {
            return;
        }

        Interlocked.Exchange(ref _endQpc, endQpc);
        _video?.StopAt(endQpc);
        foreach (AudioTrack track in _tracks)
        {
            track.Aligner.StopAt(endQpc);
        }
    }

    /// <summary>Ends the recording as soon as every track can still end on the same instant; returns that instant.</summary>
    public long StopSoon()
    {
        StopAt(QpcClock.Now + StopLeadTicks);
        return EndQpc;
    }

    /// <summary>Waits for the end instant, then stops the sources, drains the encoders and closes the file.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        if (EndQpc == long.MaxValue)
        {
            StopSoon();
        }

        long waitTicks = EndQpc + StopGraceTicks - QpcClock.Now;
        if (waitTicks > 0)
        {
            Thread.Sleep(TimeSpan.FromTicks(waitTicks));
        }

        _watcher!.Stop();
        _video?.Join();
        foreach (AudioTrack track in _tracks)
        {
            track.Detach();
            track.Finish();
        }

        _mkv!.Close();
        _completed = true;
        Log.Info($"Recording stopped after {QpcClock.TicksToMilliseconds(EndQpc - StartQpc):0} ms");
    }

    public SessionStatus GetStatus()
    {
        var audio = _tracks
            .Select(t => new AudioTrackStatus(
                t.Title,
                t.Identity?.Description,
                t.Aligner.GetStats(),
                PeakMeter.ToDbfs(t.Meter.TakePeak()),
                t.CorrectionPpm,
                t.Frames,
                t.Fault))
            .ToList();

        long elapsed = StartQpc == 0 ? 0 : Math.Min(QpcClock.Now, EndQpc) - StartQpc;
        return new SessionStatus(Math.Max(0, elapsed), _video?.GetStatus(), audio, _mkv?.BytesWritten ?? 0, _mkv?.WriteError);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _video?.Dispose();
        foreach (AudioTrack track in _tracks)
        {
            track.Dispose();
        }

        _mkv?.Dispose();
    }

    private void Initialize()
    {
        _mkv = new MkvWriter(OutputPath);
        if (_options.Video is { } videoOptions)
        {
            _video = VideoRecorder.Open(videoOptions);
            using var info = _video.CreateStreamInfo("Video");
            _videoStream = _mkv.AddStream(info);
        }

        foreach (AudioTrackOptions trackOptions in _options.AudioTracks)
        {
            int stream = -1;
            var track = new AudioTrack(trackOptions, _options.AudioBitrateKbps, packet => _mkv.Write(stream, packet));
            _tracks.Add(track);
            using var info = track.CreateStreamInfo();
            stream = _mkv.AddStream(info, isDefault: _tracks.Count == 1);
        }

        _watcher = new SourceWatcher(_tracks);
        _mkv.Start();
    }
}
