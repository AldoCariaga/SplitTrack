using System.Collections.ObjectModel;
using System.Windows.Threading;
using SplitTrack.App.Services;
using SplitTrack.Common.Time;
using SplitTrack.Engine.Recording;

namespace SplitTrack.App.ViewModels;

/// <summary>A live level meter for one recorded track.</summary>
public sealed class TrackMeter(string title) : ObservableObject
{
    private double _level;
    private string _source = "waiting";
    private bool _hasSignal;

    public string Title { get; } = title;

    /// <summary>0 (−60 dBFS or quieter) to 1 (0 dBFS), falling back slowly like a meter.</summary>
    public double Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    public string Source
    {
        get => _source;
        private set => Set(ref _source, value);
    }

    public bool HasSignal
    {
        get => _hasSignal;
        private set => Set(ref _hasSignal, value);
    }

    public void Update(AudioTrackStatus status)
    {
        double level = Math.Clamp((status.PeakDbfs + 60) / 60, 0, 1);
        Level = Math.Max(level, Level * 0.8);
        HasSignal = status.PeakDbfs > -60;
        Source = status.Fault is not null ? "failed (see log)" : status.Source ?? "waiting";
    }
}

/// <summary>The recorder strip at the top of the window: state, buttons and one meter per track.</summary>
public sealed class RecorderViewModel : ObservableObject
{
    private readonly RecorderService _service;
    private readonly DispatcherTimer _timer;
    private string _stateText = "Off";
    private string? _detailText;
    private string _videoText = string.Empty;
    private string _presetText = string.Empty;
    private bool _isRecording;
    private bool _replayOn;
    private bool _canSaveClip;
    private string _recordingText = string.Empty;
    private TrayState _trayState;
    private long _fpsFrames;
    private long _fpsTicks;
    private double _fps;

    public RecorderViewModel(RecorderService service)
    {
        _service = service;
        SaveClipCommand = new RelayCommand(_service.SaveClip, () => CanSaveClip);
        ToggleRecordingCommand = new RelayCommand(_service.ToggleRecording);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
        Refresh();
    }

    public ObservableCollection<TrackMeter> Meters { get; } = [];

    public RelayCommand SaveClipCommand { get; }

    public RelayCommand ToggleRecordingCommand { get; }

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    /// <summary>The error, or what the buffer holds.</summary>
    public string? DetailText
    {
        get => _detailText;
        private set => Set(ref _detailText, value);
    }

    public string VideoText
    {
        get => _videoText;
        private set => Set(ref _videoText, value);
    }

    /// <summary>The preset in use and why; empty while there is only one preset.</summary>
    public string PresetText
    {
        get => _presetText;
        private set => Set(ref _presetText, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set => Set(ref _isRecording, value);
    }

    public string RecordingText
    {
        get => _recordingText;
        private set => Set(ref _recordingText, value);
    }

    /// <summary>Setting it turns the replay buffer on or off (and saves that).</summary>
    public bool ReplayOn
    {
        get => _replayOn;
        set
        {
            if (Set(ref _replayOn, value))
            {
                _service.SetReplayEnabled(value);
            }
        }
    }

    public bool CanSaveClip
    {
        get => _canSaveClip;
        private set => Set(ref _canSaveClip, value);
    }

    public bool HasError => _service.State == RecorderState.Error;

    public TrayState TrayState
    {
        get => _trayState;
        private set => Set(ref _trayState, value);
    }

    public string SaveClipHint => _service.SaveClipHotkey is { } hotkey ? $"Save clip ({hotkey})" : "Save clip";

    public string RecordHint => _service.RecordHotkey is { } hotkey ? $" ({hotkey})" : string.Empty;

    /// <summary>Meters only move while the window shows them; the state is kept current for the tray either way.</summary>
    public bool MetersVisible
    {
        set
        {
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    public void Refresh()
    {
        RecorderState state = _service.State;
        SessionStatus? status = _service.GetStatus();
        Set(ref _replayOn, _service.ReplayEnabled, nameof(ReplayOn));
        // Buttons only re-check their commands on input, not on timer-driven changes.
        if (Set(ref _canSaveClip, _service.HasReplayBuffer, nameof(CanSaveClip)) | Set(ref _isRecording, status?.Recording is not null, nameof(IsRecording)))
        {
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(SaveClipHint));
        OnPropertyChanged(nameof(RecordHint));

        if (status?.Recording is { } recording)
        {
            RecordingText = $"{Formatting.Duration(QpcClock.TicksToMilliseconds(recording.ElapsedTicks) / 1000)}  ·  {Formatting.Size(recording.BytesWritten)}";
        }

        (StateText, DetailText) = state switch
        {
            RecorderState.Starting => ("Starting…", null),
            RecorderState.Error => ("Not capturing", _service.Error),
            RecorderState.Running when status?.Buffer is { } buffer =>
                ("Replay buffer on", HeldText(buffer.HeldTicks) + $"  ·  {Formatting.Size(buffer.Bytes)} in memory"),
            RecorderState.Running => ("Capturing", "Replay buffer off"),
            _ => ("Off", _service.ReplayEnabled ? null : "Replay buffer off. Turn it on to save clips with the hotkey."),
        };

        PresetText = _service.Settings.Presets.Count > 1
            ? $"Preset: {_service.Preset.Name}{(_service.PresetGame is { } game ? $" ({game} is running)" : string.Empty)}"
            : string.Empty;

        if (status?.Video is { } video)
        {
            // Frames encoded over the last second or so, not since the start.
            long encoded = video.Pipeline.Encoded;
            double seconds = QpcClock.TicksToMilliseconds(status.ElapsedTicks - _fpsTicks) / 1000;
            if (seconds >= 1 || encoded < _fpsFrames)
            {
                _fps = seconds > 0 && encoded >= _fpsFrames ? (encoded - _fpsFrames) / seconds : 0;
                _fpsFrames = encoded;
                _fpsTicks = status.ElapsedTicks;
            }

            VideoText = $"Video: {_service.VideoDescription}  ·  {_fps:0} fps";
        }
        else
        {
            VideoText = status is not null ? "Audio only" : string.Empty;
        }

        TrayState = IsRecording ? TrayState.Recording
            : state == RecorderState.Error ? TrayState.Error
            : state == RecorderState.Running ? TrayState.Replay
            : TrayState.Idle;

        UpdateMeters(status);
    }

    // The buffer holds up to one GOP more than its length; a clip is never longer than asked plus that.
    private string HeldText(long heldTicks)
    {
        double length = _service.Preset.Replay.BufferSeconds;
        double held = Math.Min(length, QpcClock.TicksToMilliseconds(heldTicks) / 1000);
        return held >= length ? $"{length:0} s ready" : $"{held:0} of {length:0} s held";
    }

    private void UpdateMeters(SessionStatus? status)
    {
        IReadOnlyList<AudioTrackStatus> tracks = status?.Audio ?? [];
        if (!tracks.Select(t => t.Title).SequenceEqual(Meters.Select(m => m.Title)))
        {
            Meters.Clear();
            foreach (AudioTrackStatus track in tracks)
            {
                Meters.Add(new TrackMeter(track.Title));
            }
        }

        for (int i = 0; i < tracks.Count; i++)
        {
            Meters[i].Update(tracks[i]);
        }
    }
}
