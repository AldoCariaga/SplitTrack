using System.Collections.ObjectModel;
using System.Globalization;
using SplitTrack.App.Services;
using SplitTrack.Capture.Audio.Sessions;
using SplitTrack.Capture.Audio.Wasapi;
using SplitTrack.Capture.Video;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Platform;
using SplitTrack.Engine.Recording;
using SplitTrack.Engine.Settings;

namespace SplitTrack.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One audio track row on the settings page.</summary>
public sealed class TrackEditor : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private string _title;
    private AudioTrackKind _kind;
    private string _targets;

    public TrackEditor(SettingsViewModel owner, TrackSettings track)
    {
        _owner = owner;
        _title = track.Title;
        _kind = track.Kind ?? AudioTrackKind.App;
        _targets = string.Join(", ", (_kind == AudioTrackKind.Microphone ? track.Devices : track.Processes) ?? []);
        RemoveCommand = new RelayCommand(() => owner.Tracks.Remove(this));
        UpCommand = new RelayCommand(() => Move(-1), () => owner.Tracks.IndexOf(this) > 0);
        DownCommand = new RelayCommand(() => Move(1), () => owner.Tracks.IndexOf(this) < owner.Tracks.Count - 1);
        AddSuggestionCommand = new RelayCommand(p => AddTarget(p as string));
    }

    public static IReadOnlyList<Choice<AudioTrackKind>> Kinds { get; } =
    [
        new(AudioTrackKind.Game, "Game"),
        new(AudioTrackKind.App, "App, e.g. Discord"),
        new(AudioTrackKind.Microphone, "Microphone"),
        new(AudioTrackKind.System, "System (all but the game)"),
    ];

    public RelayCommand RemoveCommand { get; }

    public RelayCommand UpCommand { get; }

    public RelayCommand DownCommand { get; }

    public RelayCommand AddSuggestionCommand { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public AudioTrackKind Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                OnPropertyChanged(nameof(TargetsLabel));
                OnPropertyChanged(nameof(HasTargets));
                OnPropertyChanged(nameof(Suggestions));
                OnPropertyChanged(nameof(TargetsHint));
            }
        }
    }

    public string Targets
    {
        get => _targets;
        set => Set(ref _targets, value);
    }

    public bool HasTargets => Kind != AudioTrackKind.System;

    public string TargetsLabel => Kind switch
    {
        AudioTrackKind.Microphone => "Microphones",
        AudioTrackKind.System => string.Empty,
        _ => "Programs",
    };

    public string TargetsHint => Kind switch
    {
        AudioTrackKind.Microphone => "Name parts, first one present wins; empty = default microphone",
        AudioTrackKind.Game => "Exe names, first one running wins, e.g. cs2.exe; auto = whatever is full screen in front",
        AudioTrackKind.App => "Exe names, e.g. Discord.exe",
        _ => "Everything you hear except the game",
    };

    public IReadOnlyList<string> Suggestions => Kind == AudioTrackKind.Microphone ? _owner.Microphones : _owner.Programs;

    public TrackSettings ToSettings()
    {
        List<string> targets = Targets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return new TrackSettings
        {
            Title = Title.Trim(),
            Kind = Kind,
            Processes = Kind is AudioTrackKind.Game or AudioTrackKind.App ? targets : null,
            Devices = Kind == AudioTrackKind.Microphone && targets.Count > 0 ? targets : null,
        };
    }

    public void RefreshSuggestions() => OnPropertyChanged(nameof(Suggestions));

    private void AddTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        List<string> targets = Targets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!targets.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            targets.Add(value);
            Targets = string.Join(", ", targets);
        }
    }

    private void Move(int delta)
    {
        int index = _owner.Tracks.IndexOf(this);
        int target = index + delta;
        if (target >= 0 && target < _owner.Tracks.Count)
        {
            _owner.Tracks.Move(index, target);
        }
    }
}

/// <summary>One preset in the settings page's list; the page's video, audio and replay fields edit the selected one.</summary>
public sealed class PresetItem(PresetSettings data) : ObservableObject
{
    private string _name = data.Name;

    /// <summary>The page's working copy; the fields are written back when another preset is selected or on save.</summary>
    public PresetSettings Data { get; } = data;

    public string Name { get => _name; set => Set(ref _name, value); }

    public override string ToString() => Name;
}

/// <summary>The settings page: an editable copy of settings.json, applied (saved and restarted) with Save.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly RecorderService _service;
    private PresetItem? _selectedPreset;
    private PresetItem? _defaultPreset;
    private string _presetGames = string.Empty;
    private string _libraryFolder = string.Empty;
    private bool _videoEnabled;
    private bool _captureWindow;
    private int _monitor;
    private string _window = string.Empty;
    private int _fps;
    private double _bitrateMbps;
    private int _maxHeight;
    private string _encoder = "auto";
    private bool _cursor;
    private int _adapter;
    private int _audioBitrate;
    private bool _replayEnabled;
    private double _bufferSeconds;
    private bool _alsoRecord;
    private string _saveClipHotkey = string.Empty;
    private string _recordHotkey = string.Empty;
    private bool _startWithWindows;
    private string? _message;
    private bool _messageIsError;
    private IReadOnlyList<string> _errors = [];

    public SettingsViewModel(RecorderService service)
    {
        _service = service;
        SaveCommand = new RelayCommand(Save);
        RevertCommand = new RelayCommand(Load);
        AddTrackCommand = new RelayCommand(() => Tracks.Add(new TrackEditor(this, new TrackSettings { Title = NewTitle(), Kind = AudioTrackKind.App, Processes = [] })));
        RescanCommand = new RelayCommand(Rescan);
        OpenLogsCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.Logs));
        NewPresetCommand = new RelayCommand(NewPreset);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => Presets.Count > 1);
        Rescan();
        Load();
    }

    public ObservableCollection<PresetItem> Presets { get; } = [];

    /// <summary>The preset the video, audio and replay fields show; selecting another stores them into this one first.</summary>
    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is null || value == _selectedPreset)
            {
                return;
            }

            if (_selectedPreset is not null)
            {
                StorePreset(_selectedPreset);
            }

            _selectedPreset = value;
            LoadPreset(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PresetName));
            OnPropertyChanged(nameof(IsDefaultPreset));
        }
    }

    public string PresetName
    {
        get => _selectedPreset?.Name ?? string.Empty;
        set
        {
            if (_selectedPreset is not null && _selectedPreset.Name != value)
            {
                _selectedPreset.Name = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Exe names, comma separated, that switch to the selected preset while they run.</summary>
    public string PresetGames { get => _presetGames; set => Set(ref _presetGames, value); }

    /// <summary>The selected preset is the one used while no preset's game runs; there is always exactly one.</summary>
    public bool IsDefaultPreset
    {
        get => _selectedPreset is not null && _selectedPreset == _defaultPreset;
        set
        {
            if (value && _selectedPreset is not null)
            {
                _defaultPreset = _selectedPreset;
            }

            OnPropertyChanged();
        }
    }

    public RelayCommand NewPresetCommand { get; }

    public RelayCommand DeletePresetCommand { get; }

    public static IReadOnlyList<Choice<int>> FpsChoices { get; } = [new(30, "30"), new(60, "60"), new(120, "120"), new(144, "144")];

    public static IReadOnlyList<Choice<int>> HeightChoices { get; } =
        [new(0, "Same as captured"), new(2160, "2160p"), new(1440, "1440p"), new(1080, "1080p"), new(720, "720p")];

    public static IReadOnlyList<Choice<string>> EncoderChoices { get; } =
        [new("auto", "Automatic (GPU first)"), new("amf", "AMD (AMF)"), new("nvenc", "NVIDIA (NVENC)"), new("qsv", "Intel (Quick Sync)"), new("x264", "CPU (x264)")];

    public static IReadOnlyList<Choice<int>> AudioBitrateChoices { get; } =
        [new(96, "96 kbps"), new(128, "128 kbps"), new(160, "160 kbps"), new(192, "192 kbps"), new(256, "256 kbps"), new(320, "320 kbps")];

    public ObservableCollection<TrackEditor> Tracks { get; } = [];

    public ObservableCollection<Choice<int>> Monitors { get; } = [];

    /// <summary>Exe names of open windows and of programs playing audio, for the window and program pickers.</summary>
    public IReadOnlyList<string> Programs { get; private set; } = [];

    public IReadOnlyList<string> Microphones { get; private set; } = [];

    public RelayCommand SaveCommand { get; }

    public RelayCommand RevertCommand { get; }

    public RelayCommand AddTrackCommand { get; }

    public RelayCommand RescanCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public string SettingsPath => _service.SettingsPath;

    public string LibraryFolder { get => _libraryFolder; set => Set(ref _libraryFolder, value); }

    public bool VideoEnabled { get => _videoEnabled; set => Set(ref _videoEnabled, value); }

    public bool CaptureWindow
    {
        get => _captureWindow;
        set
        {
            if (Set(ref _captureWindow, value))
            {
                OnPropertyChanged(nameof(CaptureMonitor));
            }
        }
    }

    public bool CaptureMonitor
    {
        get => !_captureWindow;
        set => CaptureWindow = !value;
    }

    public int Monitor { get => _monitor; set => Set(ref _monitor, value); }

    public string Window { get => _window; set => Set(ref _window, value); }

    public int Fps { get => _fps; set => Set(ref _fps, value); }

    public double BitrateMbps { get => _bitrateMbps; set => Set(ref _bitrateMbps, value); }

    public int MaxHeight { get => _maxHeight; set => Set(ref _maxHeight, value); }

    public string Encoder { get => _encoder; set => Set(ref _encoder, value); }

    public bool Cursor { get => _cursor; set => Set(ref _cursor, value); }

    public int Adapter { get => _adapter; set => Set(ref _adapter, value); }

    public int AudioBitrate { get => _audioBitrate; set => Set(ref _audioBitrate, value); }

    public bool ReplayEnabled { get => _replayEnabled; set => Set(ref _replayEnabled, value); }

    public double BufferSeconds { get => _bufferSeconds; set => Set(ref _bufferSeconds, value); }

    public bool AlsoRecord { get => _alsoRecord; set => Set(ref _alsoRecord, value); }

    public string SaveClipHotkey { get => _saveClipHotkey; set => Set(ref _saveClipHotkey, value); }

    public string RecordHotkey { get => _recordHotkey; set => Set(ref _recordHotkey, value); }

    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

    public string? Message { get => _message; private set => Set(ref _message, value); }

    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    public IReadOnlyList<string> Errors { get => _errors; private set => Set(ref _errors, value); }

    /// <summary>Fills the page from the settings in use, showing the preset in use.</summary>
    public void Load()
    {
        RecorderSettings s = SettingsFile.Clone(_service.Settings);
        LibraryFolder = s.LibraryRoot;
        SaveClipHotkey = s.Hotkeys.SaveClip;
        RecordHotkey = s.Hotkeys.ToggleRecording ?? string.Empty;
        StartWithWindows = StartupEntry.IsEnabled;

        // Cleared first, so selecting below does not store the page into a preset from before.
        _selectedPreset = null;
        Presets.Clear();
        foreach (PresetSettings preset in s.Presets)
        {
            Presets.Add(new PresetItem(preset));
        }

        _defaultPreset = Presets.First(p => p.Data == s.Active);
        SelectedPreset = Presets.FirstOrDefault(p => RecorderSettings.NameEquals(p.Name, _service.Preset.Name)) ?? _defaultPreset;

        Errors = [];
        Message = null;
    }

    /// <summary>The page as settings; not validated.</summary>
    public RecorderSettings Build()
    {
        if (_selectedPreset is not null)
        {
            StorePreset(_selectedPreset);
        }

        RecorderSettings settings = SettingsFile.Clone(_service.Settings);
        settings.LibraryFolder = string.IsNullOrWhiteSpace(LibraryFolder)
            || Path.GetFullPath(LibraryFolder.Trim()).Equals(SplitTrack.Library.LibraryFolder.DefaultRoot, StringComparison.OrdinalIgnoreCase)
                ? null
                : LibraryFolder.Trim();
        settings.Hotkeys.SaveClip = SaveClipHotkey.Trim();
        settings.Hotkeys.ToggleRecording = string.IsNullOrWhiteSpace(RecordHotkey) ? null : RecordHotkey.Trim();
        settings.Presets = Presets.Select(p => SettingsFile.Clone(p.Data)).ToList();
        settings.ActivePreset = (_defaultPreset ?? Presets[0]).Name.Trim();
        return settings;
    }

    public void AddPresetGame(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        List<string> games = SplitList(PresetGames);
        if (!games.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            games.Add(exe);
            PresetGames = string.Join(", ", games);
        }
    }

    private void LoadPreset(PresetItem item)
    {
        PresetSettings p = item.Data;
        PresetGames = string.Join(", ", p.Games);
        VideoEnabled = p.Video.Enabled;
        CaptureWindow = !string.IsNullOrWhiteSpace(p.Video.Window);
        Monitor = p.Video.Monitor;
        Window = p.Video.Window ?? string.Empty;
        Fps = p.Video.Fps;
        BitrateMbps = p.Video.BitrateMbps;
        MaxHeight = p.Video.MaxHeight ?? 0;
        Encoder = (p.Video.Encoder ?? "auto").ToLowerInvariant();
        Cursor = p.Video.Cursor;
        Adapter = p.Video.Adapter;
        AudioBitrate = p.Audio.BitrateKbps;
        ReplayEnabled = p.Replay.Enabled;
        BufferSeconds = p.Replay.BufferSeconds;
        AlsoRecord = p.Replay.AlsoRecord;
        Tracks.Clear();
        foreach (TrackSettings track in p.Audio.Tracks)
        {
            Tracks.Add(new TrackEditor(this, track));
        }

        if (!Monitors.Any(m => m.Value == Monitor))
        {
            Monitors.Add(new Choice<int>(Monitor, $"Monitor {Monitor + 1} (not connected)"));
        }
    }

    private void StorePreset(PresetItem item)
    {
        PresetSettings p = item.Data;
        p.Name = item.Name.Trim();
        p.Games = SplitList(PresetGames);
        p.Video.Enabled = VideoEnabled;
        p.Video.Monitor = Monitor;
        p.Video.Window = CaptureWindow && !string.IsNullOrWhiteSpace(Window) ? Window.Trim() : null;
        p.Video.Fps = Fps;
        p.Video.BitrateMbps = BitrateMbps;
        p.Video.MaxHeight = MaxHeight > 0 ? MaxHeight : null;
        p.Video.Encoder = Encoder;
        p.Video.Cursor = Cursor;
        p.Video.Adapter = Adapter;
        p.Audio.BitrateKbps = AudioBitrate;
        p.Audio.Tracks = Tracks.Select(t => t.ToSettings()).ToList();
        p.Replay.Enabled = ReplayEnabled;
        p.Replay.BufferSeconds = BufferSeconds;
        p.Replay.AlsoRecord = AlsoRecord;
    }

    /// <summary>A copy of the selected preset under a new name, without its games (a game switches to one preset only).</summary>
    private void NewPreset()
    {
        if (_selectedPreset is null)
        {
            return;
        }

        StorePreset(_selectedPreset);
        PresetSettings copy = SettingsFile.Clone(_selectedPreset.Data);
        copy.Games = [];
        for (int n = 2; ; n++)
        {
            copy.Name = $"Preset {n}";
            if (!Presets.Any(p => RecorderSettings.NameEquals(p.Name, copy.Name)))
            {
                break;
            }
        }

        var item = new PresetItem(copy);
        Presets.Add(item);
        SelectedPreset = item;
    }

    private void DeletePreset()
    {
        if (_selectedPreset is not { } deleted || Presets.Count <= 1)
        {
            return;
        }

        int index = Presets.IndexOf(deleted);
        _selectedPreset = null;
        Presets.Remove(deleted);
        if (_defaultPreset == deleted)
        {
            _defaultPreset = Presets[0];
        }

        SelectedPreset = Presets[Math.Min(index, Presets.Count - 1)];
    }

    private static List<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void Save()
    {
        RecorderSettings settings;
        try
        {
            settings = Build();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowErrors([$"Library folder: {ex.Message}"]);
            return;
        }

        // Only the shown preset can have "One window" picked with nothing in the box.
        if (CaptureWindow && _selectedPreset is not null && settings.Presets[Presets.IndexOf(_selectedPreset)].Video.Window is null)
        {
            ShowErrors(["Window: choose a window or program to capture, or capture a monitor."]);
            return;
        }

        IReadOnlyList<string> errors = settings.Validate();
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return;
        }

        try
        {
            _service.ApplySettings(settings);
            if (StartWithWindows != StartupEntry.IsEnabled)
            {
                StartupEntry.Set(StartWithWindows);
            }

            Errors = [];
            MessageIsError = false;
            Message = _service.IsRecording ? "Saved. The new settings apply when the recording stops." : "Saved. Capture restarted with the new settings.";
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            ShowErrors([ex.Message]);
        }
    }

    private void ShowErrors(IReadOnlyList<string> errors)
    {
        Errors = errors;
        MessageIsError = true;
        Message = errors.Count == 1 ? "Not saved: fix this first." : $"Not saved: fix these {errors.Count} things first.";
    }

    /// <summary>Monitors, windows, audio programs and microphones present right now.</summary>
    private void Rescan()
    {
        Monitors.Clear();
        try
        {
            foreach (MonitorInfo monitor in CaptureTargets.ListMonitors())
            {
                Monitors.Add(new Choice<int>(
                    monitor.Index,
                    string.Create(CultureInfo.CurrentCulture, $"Monitor {monitor.Index + 1}{(monitor.IsPrimary ? " (main)" : string.Empty)}  ·  {monitor.Width}x{monitor.Height}")));
            }

            var programs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AudioProcessInfo process in AudioSessionScanner.Scan())
            {
                programs.Add(process.ExeName);
            }

            foreach (WindowInfo window in CaptureTargets.ListWindows())
            {
                programs.Add(window.ExeName);
            }

            programs.RemoveWhere(p => p is "?" or "explorer.exe" or "TextInputHost.exe" or "ApplicationFrameHost.exe");
            Programs = programs.ToList();
            Microphones = AudioDeviceEnumerator.List(AudioDeviceFlow.Capture).Select(d => d.Name).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Listing capture targets failed", ex);
        }

        OnPropertyChanged(nameof(Programs));
        OnPropertyChanged(nameof(Microphones));
        foreach (TrackEditor track in Tracks)
        {
            track.RefreshSuggestions();
        }

        if (!Monitors.Any(m => m.Value == Monitor))
        {
            Monitors.Add(new Choice<int>(Monitor, $"Monitor {Monitor + 1} (not connected)"));
        }
    }

    private string NewTitle()
    {
        for (int n = 1; ; n++)
        {
            string title = n == 1 ? "Discord" : $"App {n}";
            if (!Tracks.Any(t => t.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
            {
                return title;
            }
        }
    }
}
