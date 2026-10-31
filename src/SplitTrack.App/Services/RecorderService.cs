using System.Collections.Concurrent;
using SplitTrack.Capture.Audio.Sessions;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Engine.Input;
using SplitTrack.Engine.Recording;
using SplitTrack.Engine.Replay;
using SplitTrack.Engine.Settings;
using SplitTrack.Library;

namespace SplitTrack.App.Services;

public enum RecorderState
{
    Off,
    Starting,

    /// <summary>A game chose the preset but its window is not open yet; retried quietly.</summary>
    Waiting,
    Running,
    Error,
}

/// <param name="Path">The saved file, when the notice is about one.</param>
public sealed record RecorderNotice(string Title, string Message, bool IsError, string? Path = null);

/// <summary>
/// Owns the capture session for the app: runs it while the replay buffer is on or a recording is
/// running, starts and stops recordings inside it, and owns the global hotkeys. Session work runs
/// on one worker thread in order; events are raised on background threads.
/// </summary>
public sealed class RecorderService : IDisposable
{
    // A session that could not start because of the setup (e.g. the window is not open yet) is retried.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    // A game's process starts well before its window; waiting this long for the window is not an error yet.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(90);

    private readonly Lock _gate = new();
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly Timer _watchdog;
    private RecorderSettings _settings;
    private RecordingSession? _session;
    private GlobalHotkey? _saveHotkey;
    private GlobalHotkey? _recordHotkey;
    private RecorderState _state;
    private string? _error;
    private bool _retry;
    private DateTime _nextRetry;
    private DateTime? _waitingSince;
    private bool _waitReported;
    private bool _wantRecording;
    private bool _restartPending;
    private string? _recordingPath;
    private bool _disposed;

    // The preset the session runs (or will, once a recording ends), and the game that chose it, if one did.
    private string _presetName;
    private string? _presetGame;

    // The preset whose game was running at the last check; switching happens only when this changes.
    private string? _gamePreset;

    public RecorderService(string settingsPath, RecorderSettings settings)
    {
        SettingsPath = settingsPath;
        _settings = settings;
        _presetName = settings.Active.Name;
        _worker = new Thread(Work) { IsBackground = true, Name = "SplitTrack recorder" };
        _worker.Start();
        _watchdog = new Timer(_ => Post(Watch), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>State, buttons or settings changed.</summary>
    public event Action? Changed;

    public event Action<RecorderNotice>? Notice;

    /// <summary>A clip or recording was saved to the library (its video path).</summary>
    public event Action<string>? EntrySaved;

    public string SettingsPath { get; }

    /// <summary>The settings in use; treat as read-only and pass a changed copy to <see cref="ApplySettings"/>.</summary>
    public RecorderSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public RecorderState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Why the session is not running, when <see cref="State"/> is Error or Waiting.</summary>
    public string? Error
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    /// <summary>The preset in use: the active one, or the one whose game is running.</summary>
    public PresetSettings Preset
    {
        get
        {
            lock (_gate)
            {
                return _settings.Find(_presetName) ?? _settings.Active;
            }
        }
    }

    /// <summary>The exe whose running chose <see cref="Preset"/>; null when it was chosen by hand.</summary>
    public string? PresetGame
    {
        get
        {
            lock (_gate)
            {
                return _presetGame;
            }
        }
    }

    public bool ReplayEnabled => Preset.Replay.Enabled;

    public bool HasReplayBuffer
    {
        get
        {
            lock (_gate)
            {
                return _session?.HasReplayBuffer ?? false;
            }
        }
    }

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _session?.IsRecording ?? false;
            }
        }
    }

    /// <summary>The MKV being recorded, which the library should not list yet.</summary>
    public string? RecordingPath
    {
        get
        {
            lock (_gate)
            {
                return _recordingPath;
            }
        }
    }

    public HotkeyGesture? SaveClipHotkey => _saveHotkey?.Gesture;

    public HotkeyGesture? RecordHotkey => _recordHotkey?.Gesture;

    public SessionStatus? GetStatus()
    {
        lock (_gate)
        {
            return _session?.GetStatus();
        }
    }

    public string? VideoDescription
    {
        get
        {
            lock (_gate)
            {
                return _session?.VideoDescription;
            }
        }
    }

    /// <summary>Registers the hotkeys and starts the replay buffer if it is on.</summary>
    public void Start() => Post(() =>
    {
        RegisterHotkeys();
        FollowGames();
        Reconcile();
    });

    /// <summary>Saves the replay buffer up to now as a clip.</summary>
    public void SaveClip() => SaveClipAt(QpcClock.Now);

    public void ToggleRecording() => Post(() =>
    {
        _wantRecording = !IsRecording;
        if (_wantRecording)
        {
            lock (_gate)
            {
                _retry = false;
                _error = null;
            }
        }

        Reconcile();
    });

    /// <summary>Turns the replay buffer of the preset in use on or off, and saves that.</summary>
    public void SetReplayEnabled(bool enabled)
    {
        RecorderSettings settings = SettingsFile.Clone(Settings);
        (settings.Find(Preset.Name) ?? settings.Active).Replay.Enabled = enabled;
        try
        {
            SettingsFile.Save(SettingsPath, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save the replay setting", ex);
        }

        // Taken at once, so the UI shows the new state; the worker restarts the session after.
        lock (_gate)
        {
            _settings = settings;
            _error = null;
        }

        Post(() =>
        {
            lock (_gate)
            {
                _restartPending |= _session is not null && _session.HasReplayBuffer != (_settings.Find(_presetName) ?? _settings.Active).Replay.Enabled;
            }

            Reconcile();
        });
    }

    /// <summary>Saves <paramref name="settings"/> to the file and restarts the session with them (once a running recording ends).</summary>
    /// <exception cref="SettingsException">They are not valid; nothing was saved.</exception>
    public void ApplySettings(RecorderSettings settings)
    {
        if (settings.Validate() is { Count: > 0 } errors)
        {
            throw new SettingsException(string.Join(Environment.NewLine, errors));
        }

        SettingsFile.Save(SettingsPath, settings);
        Log.Info($"Settings saved to {SettingsPath}");
        Post(() =>
        {
            lock (_gate)
            {
                _settings = settings;
                _restartPending = _session is not null;
                _error = null;
                _retry = false;
                _waitingSince = null;
                _waitReported = false;

                // Start over from the active preset; FollowGames below picks a running game's preset again.
                _presetName = settings.Active.Name;
                _presetGame = null;
                _gamePreset = null;
            }

            RegisterHotkeys();
            FollowGames(notify: false);
            if (IsRecording)
            {
                Notify("Settings saved", "They apply when the recording stops.", false);
            }

            Reconcile();
        });
    }

    /// <summary>Makes <paramref name="name"/> the active preset, saves that, and switches to it (once a running recording ends).</summary>
    public void SelectPreset(string name)
    {
        RecorderSettings settings = SettingsFile.Clone(Settings);
        if (settings.Find(name) is not { } preset)
        {
            return;
        }

        settings.ActivePreset = preset.Name;
        try
        {
            SettingsFile.Save(SettingsPath, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save the active preset", ex);
        }

        Post(() =>
        {
            lock (_gate)
            {
                _settings = settings;
            }

            // A game still running keeps its edge state, so the choice is not undone until it closes.
            SwitchPreset(preset.Name, null, notify: false);
            Reconcile();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog.Dispose();
        Post(() =>
        {
            _wantRecording = false;
            CloseSession();
            _saveHotkey?.Dispose();
            _recordHotkey?.Dispose();
        });
        _work.CompleteAdding();
        _worker.Join();
        _work.Dispose();
    }

    private void SaveClipAt(long qpc)
    {
        lock (_gate)
        {
            if (_session is { HasReplayBuffer: true } session)
            {
                session.SaveClip(qpc);
                return;
            }
        }

        Notify("No clip saved", ReplayEnabled ? "The replay buffer is not running." : "The replay buffer is off.", true);
    }

    private void Post(Action action)
    {
        if (!_work.IsAddingCompleted)
        {
            try
            {
                _work.Add(action);
            }
            catch (InvalidOperationException)
            {
                // Disposed meanwhile.
            }
        }
    }

    private void Work()
    {
        foreach (Action action in _work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Recorder action failed", ex);
                Notify("SplitTrack error", ex.Message, true);
            }
        }
    }

    /// <summary>Brings the session in line with what is wanted: running or not, recording or not, current settings.</summary>
    private void Reconcile()
    {
        RecorderSettings settings = Settings;
        PresetSettings preset = Preset;
        bool needSession = preset.Replay.Enabled || _wantRecording;
        RecordingSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is not null && !session.IsRecording && (_restartPending || !needSession))
        {
            CloseSession();
            session = null;
        }

        bool retry;
        lock (_gate)
        {
            retry = _retry;
        }

        // After a failure only setup problems are retried (Watch paces that); anything else waits for a change.
        if (session is null && needSession && (Error is null || retry))
        {
            session = OpenSession(settings, preset);
        }

        if (session is not null)
        {
            if (_wantRecording && !session.IsRecording)
            {
                try
                {
                    StartRecording(session, settings);
                }
                catch (Exception ex)
                {
                    _wantRecording = false;
                    Log.Error("Starting a recording failed", ex);
                    Notify("Recording not started", ex.Message, true);
                }
            }
            else if (!_wantRecording && session.IsRecording)
            {
                session.StopRecording();
                lock (_gate)
                {
                    _recordingPath = null;
                }
            }
        }
        else
        {
            _wantRecording = false;
        }

        Changed?.Invoke();
    }

    private RecordingSession? OpenSession(RecorderSettings settings, PresetSettings preset)
    {
        SetState(RecorderState.Starting, null);
        RecordingSession? session = null;
        try
        {
            session = RecordingSession.Open(settings.ToRecordingOptions(null, preset.Replay.Enabled, preset));
            session.ClipSaved += OnClipSaved;
            session.RecordingSaved += OnRecordingSaved;
            session.Start();
            lock (_gate)
            {
                _session = session;
                _state = RecorderState.Running;
                _error = null;
                _retry = false;
                _restartPending = false;
                _waitingSince = null;
                _waitReported = false;
            }

            Log.Info($"App session started: preset '{preset.Name}', {session.VideoDescription ?? "audio only"}, replay {(session.HasReplayBuffer ? "on" : "off")}");
            return session;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            bool setup = ex is RecordingSetupException;
            if (!setup)
            {
                Log.Error("Starting the capture session failed", ex);
            }

            bool repeated;
            string? waitingFor = null;
            lock (_gate)
            {
                // Retries after a setup failure (or a closed window) stay quiet; the first message said so.
                repeated = _retry && setup;
                _retry = setup;
                _nextRetry = DateTime.UtcNow + RetryInterval;

                // The game that chose this preset is starting but has no window yet: wait, quietly, up to a limit.
                if (setup && _presetGame is { } game && preset.Video.Enabled && !string.IsNullOrWhiteSpace(preset.Video.Window))
                {
                    _waitingSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - _waitingSince < WaitLimit)
                    {
                        waitingFor = game;
                    }
                    else
                    {
                        // The limit passed: report it once, like a first failure.
                        repeated = _waitReported;
                        _waitReported = true;
                    }
                }
            }

            if (waitingFor is not null)
            {
                Log.Info($"Waiting for the {waitingFor} window: {ex.Message}");
                SetState(RecorderState.Waiting, $"Waiting for the {waitingFor} window…");
                return null;
            }

            SetState(RecorderState.Error, ex.Message);
            if (!repeated)
            {
                Notify("Capture not running", setup ? $"{ex.Message} Retrying every few seconds." : ex.Message, true);
            }

            return null;
        }
    }

    private void StartRecording(RecordingSession session, RecorderSettings settings)
    {
        EntryPaths paths = new LibraryFolder(settings.LibraryRoot).NewEntry(DateTime.Now, "rec");
        session.StartRecording(paths);
        lock (_gate)
        {
            _recordingPath = paths.Video;
        }
    }

    private void CloseSession()
    {
        RecordingSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _recordingPath = null;
            if (_state != RecorderState.Error)
            {
                _state = RecorderState.Off;
            }
        }

        if (session is null)
        {
            return;
        }

        try
        {
            session.Complete();
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the capture session failed", ex);
        }
        finally
        {
            session.Dispose();
        }

        Log.Info("App session stopped");
        Changed?.Invoke();
    }

    /// <summary>Once a second: follows games to their presets, ends a session whose video stopped, retries one that could not start.</summary>
    private void Watch()
    {
        if (FollowGames())
        {
            Reconcile();
        }

        RecordingSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is not null && session.VideoStopped)
        {
            string reason = session.VideoClosed ? "The captured window closed." : "Video encoding failed; see the log.";
            bool windowCapture = !string.IsNullOrWhiteSpace(Preset.Video.Window);
            Log.Info($"App session ends: {reason}");
            _wantRecording = false;
            CloseSession();
            lock (_gate)
            {
                _retry = session.VideoClosed && windowCapture;
                _nextRetry = DateTime.UtcNow + RetryInterval;
            }

            SetState(RecorderState.Error, reason);
            Notify("Capture stopped", reason, true);
            Changed?.Invoke();
            return;
        }

        bool retry;
        lock (_gate)
        {
            retry = _session is null && _retry && DateTime.UtcNow >= _nextRetry;
        }

        if (retry)
        {
            Reconcile();
        }
    }

    /// <summary>
    /// Switches to the preset whose game just started, and back to the active preset when it closes.
    /// Acts only on a change, so a preset chosen by hand meanwhile stays. True when it switched.
    /// </summary>
    private bool FollowGames(bool notify = true)
    {
        RecorderSettings settings = Settings;
        (PresetSettings Preset, string Game)? running = settings.Presets.Any(p => p.Games.Count > 0)
            ? settings.FindRunningGame(ProcessTree.Snapshot())
            : null;

        string? wanted = running?.Preset.Name;
        lock (_gate)
        {
            if (RecorderSettings.NameEquals(wanted, _gamePreset))
            {
                return false;
            }

            _gamePreset = wanted;
        }

        return SwitchPreset(wanted ?? settings.Active.Name, running?.Game, notify);
    }

    /// <summary>Runs <paramref name="name"/> from now on; the session restarts with it once no recording is running.</summary>
    private bool SwitchPreset(string name, string? game, bool notify)
    {
        bool changed;
        bool recording;
        lock (_gate)
        {
            changed = !RecorderSettings.NameEquals(_presetName, name);
            _presetName = name;
            _presetGame = game;
            recording = _session?.IsRecording ?? false;
            if (changed)
            {
                _restartPending = _session is not null;
                _error = null;
                _retry = false;
                _waitingSince = null;
                _waitReported = false;
            }
        }

        if (changed)
        {
            Log.Info($"Preset '{name}'{(game is null ? string.Empty : $" ({game} is running)")}");
            if (notify)
            {
                string why = game is null ? "No preset's game is running." : $"{game} is running.";
                Notify($"Preset: {name}", recording ? $"{why} It applies when the recording stops." : why, false);
            }
        }

        Changed?.Invoke();
        return changed;
    }

    private void RegisterHotkeys()
    {
        _saveHotkey?.Dispose();
        _saveHotkey = null;
        _recordHotkey?.Dispose();
        _recordHotkey = null;

        RecorderSettings settings = Settings;
        var problems = new List<string>();
        try
        {
            _saveHotkey = GlobalHotkey.Register(settings.Hotkeys.SaveClipGesture, SaveClipAt);
        }
        catch (Exception ex) when (ex is HotkeyUnavailableException or FormatException)
        {
            problems.Add($"Save clip: {ex.Message}");
        }

        try
        {
            if (settings.Hotkeys.ToggleRecordingGesture is { } record)
            {
                _recordHotkey = GlobalHotkey.Register(record, _ => ToggleRecording());
            }
        }
        catch (Exception ex) when (ex is HotkeyUnavailableException or FormatException)
        {
            problems.Add($"Start/stop recording: {ex.Message}");
        }

        if (problems.Count > 0)
        {
            Notify("Hotkey not available", string.Join(Environment.NewLine, problems), true);
        }
    }

    private void OnClipSaved(ClipResult result)
    {
        if (result.Path is { } path)
        {
            Notify("Clip saved", $"{Path.GetFileName(path)} ({result.DurationSeconds:0.0} s)", false, path);
            EntrySaved?.Invoke(path);
        }
        else
        {
            Notify("Clip not saved", result.Error?.Message ?? "Unknown error", true);
        }
    }

    private void OnRecordingSaved(RecordingResult result)
    {
        if (result.Path is { } path)
        {
            Notify("Recording saved", $"{Path.GetFileName(path)} ({TimeSpan.FromSeconds(result.DurationSeconds):h\\:mm\\:ss})", false, path);
            EntrySaved?.Invoke(path);
        }
        else if (result.Error is { } error)
        {
            Notify("Recording failed", error.Message, true);
        }

        Changed?.Invoke();

        // A restart (or stop) waiting for the recording to end can happen now.
        Post(Reconcile);
    }

    private void SetState(RecorderState state, string? error)
    {
        lock (_gate)
        {
            _state = state;
            _error = error;
        }

        Changed?.Invoke();
    }

    private void Notify(string title, string message, bool isError, string? path = null)
    {
        if (isError)
        {
            Log.Warn($"{title}: {message}");
        }

        Notice?.Invoke(new RecorderNotice(title, message, isError, path));
    }
}
