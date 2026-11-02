using SplitTrack.Buffer;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Encode.Video;
using SplitTrack.Engine.Audio;
using SplitTrack.Engine.Replay;
using SplitTrack.Engine.Video;
using SplitTrack.FFmpeg;
using SplitTrack.Library;
using SplitTrack.Mux.Mkv;

namespace SplitTrack.Engine.Recording;

/// <summary>
/// One capture session: video plus one AAC track per audio source, all on the QPC clock from the
/// same start instant to the same end instant. Packets go to the session MKV, the replay buffer,
/// recordings started mid-session, or any mix of them.
/// Open → Start → (SaveClip, StartRecording/StopRecording...) → StopAt/StopSoon → Complete.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    // The end must be set while still ahead of every track, which can lead the clock by its drift tolerance.
    private static readonly long StopLeadTicks = QpcClock.MillisecondsToTicks(100);

    // Lets packets captured before the end instant arrive before the streams close.
    private static readonly long StopGraceTicks = QpcClock.MillisecondsToTicks(150);

    private readonly RecordingOptions _options;
    private readonly List<AudioTrack> _tracks = [];
    private readonly List<SessionStream> _streams = [];
    private readonly Lock _recordingGate = new();
    private FileRecording? _recording;

    // The current recording plus stopped ones still waiting for their last packets.
    private FileRecording[] _recordings = [];
    private MkvWriter? _mkv;
    private ReplayBuffer? _replay;
    private ClipSaver? _saver;
    private VideoRecorder? _video;
    private SourceWatcher? _watcher;
    private int _videoStream = -1;
    private long _endQpc = long.MaxValue;
    private DateTimeOffset _startedAt;
    private bool _completed;

    private RecordingSession(RecordingOptions options) => _options = options;

    /// <summary>A clip was saved or failed to save; raised on the saver thread.</summary>
    public event Action<ClipResult>? ClipSaved;

    /// <summary>A recording started with <see cref="StartRecording"/> was finished; raised on a background thread.</summary>
    public event Action<RecordingResult>? RecordingSaved;

    /// <summary>The session MKV, or null without one.</summary>
    public string? OutputPath => _options.OutputPath;

    public bool HasReplayBuffer => _replay is not null;

    /// <summary>A recording started with <see cref="StartRecording"/> is running.</summary>
    public bool IsRecording => Volatile.Read(ref _recording) is not null;

    /// <summary>The first frame's instant; 0 until <see cref="Start"/>.</summary>
    public long StartQpc { get; private set; }

    /// <summary>The scheduled end, or <see cref="long.MaxValue"/> while open-ended.</summary>
    public long EndQpc => Interlocked.Read(ref _endQpc);

    public string? VideoDescription => _video?.Description;

    /// <summary>The video cannot continue: its window closed or the encode loop failed.</summary>
    public bool VideoStopped => _video is { } video && (video.IsClosed || (video.HasEnded && EndQpc == long.MaxValue));

    public bool VideoClosed => _video?.IsClosed ?? false;

    /// <summary>Creates the outputs, the encoders and the video capture; nothing is recorded until <see cref="Start"/>.</summary>
    public static RecordingSession Open(RecordingOptions options)
    {
        if (options.Video is null && options.AudioTracks.Count == 0)
        {
            throw new RecordingSetupException("Nothing to record: no video and no audio tracks.");
        }

        if (options.AudioTracks.Count(t => t.Kind == AudioTrackKind.Game) > 1 || options.AudioTracks.Count(t => t.Kind == AudioTrackKind.System) > 1)
        {
            throw new RecordingSetupException("A session has at most one Game track and one System track.");
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
        _startedAt = DateTimeOffset.Now;
        foreach (AudioTrack track in _tracks)
        {
            track.Aligner.Arm(StartQpc);
        }

        _video?.StartEncoding(StartQpc, packet => Route(_videoStream, packet));
        _watcher.Start();
        Log.Info($"Session started at qpc {StartQpc}: {OutputPath ?? "no file"}{(_replay is null ? string.Empty : $", replay buffer {_options.Replay!.BufferSeconds:0.#} s")}");
    }

    /// <summary>
    /// Saves the replay buffer's last <see cref="ReplayOptions.BufferSeconds"/> up to
    /// <paramref name="pressedQpc"/> as a clip, in the background; <see cref="ClipSaved"/> reports the result.
    /// </summary>
    public void SaveClip(long pressedQpc)
    {
        if (_options.Replay is null)
        {
            throw new InvalidOperationException("This session has no replay buffer.");
        }

        if (StartQpc == 0)
        {
            return;
        }

        Log.Info($"Clip requested at {QpcClock.TicksToMilliseconds(pressedQpc - StartQpc) / 1000:0.000} s");
        _saver!.Request(Math.Min(pressedQpc, EndQpc) - StartQpc, DateTime.Now);
    }

    /// <summary>
    /// Starts recording to <paramref name="paths"/> on the next video keyframe (at most one GOP away),
    /// while the session and its replay buffer keep running.
    /// </summary>
    public void StartRecording(EntryPaths paths)
    {
        if (StartQpc == 0 || _completed || QpcClock.Now >= EndQpc)
        {
            throw new InvalidOperationException("The session is not running.");
        }

        lock (_recordingGate)
        {
            if (_recording is not null)
            {
                throw new InvalidOperationException("A recording is already running.");
            }

            FileRecording? recording = null;
            recording = new FileRecording(
                paths,
                _streams,
                _videoStream,
                QpcClock.Now - StartQpc,
                (p, created, seconds, thumbnail) => Describe(EntryKind.Recording, p, created, seconds, thumbnail),
                result => OnRecordingFinished(recording!, result));
            Volatile.Write(ref _recording, recording);
            Volatile.Write(ref _recordings, [.. _recordings, recording]);
            Log.Info($"Recording requested: {paths.Video}");
        }
    }

    /// <summary>Ends the running recording now; it is finished in the background and reported by <see cref="RecordingSaved"/>.</summary>
    public void StopRecording()
    {
        FileRecording? recording;
        lock (_recordingGate)
        {
            recording = _recording;
            Volatile.Write(ref _recording, null);
        }

        recording?.Stop(Math.Min(QpcClock.Now, EndQpc) - StartQpc);
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

    /// <summary>
    /// Waits for the end instant, then stops the sources, drains the encoders, finishes requested
    /// clips and closes the session MKV (writing its sidecar and thumbnail).
    /// </summary>
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

        _mkv?.Close();
        _saver?.Dispose();
        StopRecording();
        foreach (FileRecording recording in Volatile.Read(ref _recordings))
        {
            recording.EndInput();
            recording.Join();
        }

        _completed = true;
        Log.Info($"Session stopped after {QpcClock.TicksToMilliseconds(EndQpc - StartQpc):0} ms");

        if (_mkv is not null && _mkv.WriteError is null)
        {
            WriteRecordingSidecar(_mkv.Path);
        }
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
        return new SessionStatus(
            Math.Max(0, elapsed), _video?.GetStatus(), audio, _mkv?.BytesWritten ?? 0, _mkv?.WriteError, _replay?.GetStats(),
            Volatile.Read(ref _recording)?.GetStatus(elapsed));
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
        _saver?.Dispose();
        foreach (FileRecording recording in Volatile.Read(ref _recordings))
        {
            recording.Dispose();
        }

        _replay?.Dispose();
        foreach (SessionStream stream in _streams)
        {
            stream.Info.Dispose();
        }
    }

    private void Initialize()
    {
        if (_options.Video is { } videoOptions)
        {
            _video = VideoRecorder.Open(videoOptions);
            _videoStream = _streams.Count;
            _streams.Add(new SessionStream(_video.CreateStreamInfo("Video"), IsDefault: true));
        }

        foreach (AudioTrackOptions trackOptions in _options.AudioTracks)
        {
            int stream = _streams.Count;
            var track = new AudioTrack(trackOptions, _options.AudioBitrateKbps, packet => Route(stream, packet));
            _tracks.Add(track);
            _streams.Add(new SessionStream(track.CreateStreamInfo(), IsDefault: _tracks.Count == 1));
        }

        if (_options.OutputPath is { } path)
        {
            _mkv = new MkvWriter(path);
            foreach (SessionStream stream in _streams)
            {
                _mkv.AddStream(stream.Info, stream.IsDefault);
            }

            _mkv.Start();
        }

        if (_options.Replay is { } replay)
        {
            _replay = new ReplayBuffer(_streams.Count, _videoStream, (long)(replay.BufferSeconds * QpcClock.TicksPerSecond));
            _saver = new ClipSaver(
                _replay,
                _streams,
                _video is not null,
                new LibraryFolder(replay.LibraryDirectory),
                (paths, created, seconds, thumbnail) => Describe(EntryKind.Clip, paths, created, seconds, thumbnail),
                result => ClipSaved?.Invoke(result));
        }

        _watcher = new SourceWatcher(_tracks);
    }

    /// <summary>Hands an encoder's packet to every output, each with its own reference (shared data, no copy).</summary>
    private void Route(int stream, MediaPacket packet)
    {
        foreach (FileRecording recording in Volatile.Read(ref _recordings))
        {
            recording.Offer(stream, packet.Clone());
        }

        if (_replay is not null)
        {
            _replay.Add(stream, _mkv is null ? packet : packet.Clone());
        }

        if (_mkv is not null)
        {
            _mkv.Write(stream, packet);
        }
        else if (_replay is null)
        {
            packet.Dispose();
        }
    }

    // Runs on the recording's own close thread, so it is dropped from routing, not disposed, here.
    private void OnRecordingFinished(FileRecording recording, RecordingResult result)
    {
        lock (_recordingGate)
        {
            Volatile.Write(ref _recordings, _recordings.Where(r => r != recording).ToArray());
        }

        RecordingSaved?.Invoke(result);
    }

    private void WriteRecordingSidecar(string videoPath)
    {
        try
        {
            EntryPaths paths = EntryPaths.ForVideo(videoPath);
            bool hasThumbnail = _video is not null && ClipSaver.TryWriteThumbnail(paths);
            double seconds = (double)(EndQpc - StartQpc) / QpcClock.TicksPerSecond;
            LibraryFolder.WriteSidecar(paths.Sidecar, Describe(EntryKind.Recording, paths, _startedAt, seconds, hasThumbnail));
        }
        catch (Exception ex)
        {
            Log.Error($"Writing the sidecar for {videoPath} failed", ex);
        }
    }

    private Sidecar Describe(EntryKind kind, EntryPaths paths, DateTimeOffset created, double seconds, bool hasThumbnail)
    {
        SidecarVideo? video = _video is null
            ? null
            : new SidecarVideo(_video.Settings.Width, _video.Settings.Height, _video.Settings.Fps, _video.Settings.BitrateKbps, _video.EncoderKind.ShortName());
        var tracks = _tracks
            .Select((t, i) => new SidecarTrack(
                _videoStream + 1 + i, t.Title, t.Options.Kind.ToString().ToLowerInvariant(), t.Options.Targets, t.LastSourceName,
                t.Options.Kind == AudioTrackKind.System ? t.Options.LeaveOutApps : null))
            .ToList();
        return new Sidecar(
            Sidecar.CurrentVersion,
            kind,
            Path.GetFileName(paths.Video),
            hasThumbnail ? Path.GetFileName(paths.Thumbnail) : null,
            created,
            Math.Round(seconds, 3),
            _tracks.FirstOrDefault(t => t.Options.Kind == AudioTrackKind.Game)?.LastSourceName,
            video,
            tracks,
            kind == EntryKind.Clip ? _options.Replay?.BufferSeconds : null);
    }
}
