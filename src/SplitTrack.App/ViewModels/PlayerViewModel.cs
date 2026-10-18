using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Encode.Video;
using SplitTrack.Export;
using SplitTrack.Preview;

namespace SplitTrack.App.ViewModels;

/// <summary>One audio track in the player: what is heard live and what the export mixes.</summary>
public sealed class PlayerTrack : ObservableObject
{
    private readonly PreviewTrack _preview;
    private double _level;

    public PlayerTrack(PreviewTrack preview, SourceTrack? source)
    {
        _preview = preview;
        Source = source;
        Title = source?.Title ?? preview.Title;
        Description = string.Join("  ·  ", new[]
        {
            source?.Kind switch
            {
                "game" => "Game",
                "app" => "App",
                "microphone" => "Microphone",
                "system" => "Everything else",
                _ => null,
            },
            source?.Source,
        }.Where(s => s is not null));
    }

    public SourceTrack? Source { get; }

    public string Title { get; }

    public string Description { get; }

    public bool Enabled
    {
        get => _preview.Enabled;
        set
        {
            _preview.Enabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>0 to 400 %.</summary>
    public double VolumePercent
    {
        get => Math.Round(_preview.Volume * 100);
        set
        {
            _preview.Volume = Math.Clamp(Math.Round(value), 0, 400) / 100;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
        }
    }

    public string VolumeText => $"{VolumePercent:0} %";

    public double Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    public bool IsHeard => Enabled && _preview.Volume > 0;

    public void UpdateLevel()
    {
        float peak = _preview.TakePeak();
        double level = peak <= 1e-6f ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
        Level = Math.Max(level, Level * 0.8);
    }
}

public sealed record SizeChoice(double? Megabytes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The player window: live mix of the tracks, a part to keep, and export to MP4.</summary>
public sealed class PlayerViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _export;
    private double _position;
    private bool _isPlaying;
    private double? _trimStart;
    private double? _trimEnd;
    private SizeChoice _size;
    private string _customMegabytes = "25";
    private int _height;
    private bool _precise;
    private bool _isExporting;
    private double _progress;
    private string? _exportStatus;
    private bool _exportFailed;
    private string? _resultPath;

    public PlayerViewModel(string videoPath)
    {
        Source = ExportSource.Open(videoPath);
        Player = PreviewPlayer.Open(Source.VideoPath);
        foreach (PreviewTrack track in Player.Tracks)
        {
            Tracks.Add(new PlayerTrack(track, Source.Tracks.FirstOrDefault(t => t.Stream == track.Stream)));
        }

        _size = SizeChoices[0];
        PlayPauseCommand = new RelayCommand(TogglePlay);
        SetStartCommand = new RelayCommand(() => TrimStart = Position);
        SetEndCommand = new RelayCommand(() => TrimEnd = Position);
        ClearTrimCommand = new RelayCommand(() => (TrimStart, TrimEnd) = (null, null), () => TrimStart is not null || TrimEnd is not null);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => !IsExporting);
        CancelCommand = new RelayCommand(() => _export?.Cancel(), () => IsExporting);
        ShowResultCommand = new RelayCommand(() => Shell.ShowInFolder(ResultPath!), () => ResultPath is not null);
        CopyResultCommand = new RelayCommand(CopyResult, () => ResultPath is not null);
        OpenResultCommand = new RelayCommand(() => Shell.OpenFile(ResultPath!), () => ResultPath is not null);
    }

    public static IReadOnlyList<SizeChoice> SizeChoices { get; } =
    [
        new(null, "No limit"),
        new(10, "10 MB (Discord)"),
        new(50, "50 MB (Discord Nitro Basic)"),
        new(500, "500 MB (Discord Nitro)"),
        new(-1, "Other…"),
    ];

    public static IReadOnlyList<Choice<int>> HeightChoices { get; } =
        [new(0, "Same as recorded"), new(1080, "1080p"), new(720, "720p"), new(480, "480p")];

    public ExportSource Source { get; }

    public PreviewPlayer Player { get; }

    public string VideoPath => Source.VideoPath;

    public string Title => Path.GetFileName(Source.VideoPath);

    public ObservableCollection<PlayerTrack> Tracks { get; } = [];

    public RelayCommand PlayPauseCommand { get; }

    public RelayCommand SetStartCommand { get; }

    public RelayCommand SetEndCommand { get; }

    public RelayCommand ClearTrimCommand { get; }

    public RelayCommand ExportCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ShowResultCommand { get; }

    public RelayCommand CopyResultCommand { get; }

    public RelayCommand OpenResultCommand { get; }

    public double Duration => Player.Duration;

    public string DurationText => Formatting.Position(Duration);

    public bool HasVideo => Player.HasVideo;

    public string? AudioWarning => Player.AudioFault is not null ? "No sound device: playing without sound." : null;

    public double Position
    {
        get => _position;
        private set
        {
            if (Set(ref _position, value))
            {
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public string PositionText => Formatting.Position(Position);

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => Set(ref _isPlaying, value);
    }

    public double? TrimStart
    {
        get => _trimStart;
        set
        {
            if (Set(ref _trimStart, value))
            {
                OnTrimChanged();
            }
        }
    }

    public double? TrimEnd
    {
        get => _trimEnd;
        set
        {
            if (Set(ref _trimEnd, value))
            {
                OnTrimChanged();
            }
        }
    }

    public string TrimText
    {
        get
        {
            double start = TrimStart ?? 0;
            double end = TrimEnd ?? Duration;
            return end <= start
                ? "The end is before the start"
                : $"{Formatting.Position(start)} to {Formatting.Position(end)}  ({Formatting.Position(end - start)})";
        }
    }

    public string TrimStartText => TrimStart is { } s ? Formatting.Position(s) : "start";

    public string TrimEndText => TrimEnd is { } e ? Formatting.Position(e) : "end";

    public SizeChoice Size
    {
        get => _size;
        set
        {
            if (Set(ref _size, value))
            {
                OnPropertyChanged(nameof(IsCustomSize));
            }
        }
    }

    public bool IsCustomSize => Size.Megabytes is < 0;

    public string CustomMegabytes
    {
        get => _customMegabytes;
        set => Set(ref _customMegabytes, value);
    }

    public int Height
    {
        get => _height;
        set => Set(ref _height, value);
    }

    public bool Precise
    {
        get => _precise;
        set => Set(ref _precise, value);
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set => Set(ref _isExporting, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string? ExportStatus
    {
        get => _exportStatus;
        private set => Set(ref _exportStatus, value);
    }

    public bool ExportFailed
    {
        get => _exportFailed;
        private set => Set(ref _exportFailed, value);
    }

    public string? ResultPath
    {
        get => _resultPath;
        private set
        {
            if (Set(ref _resultPath, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => ResultPath is not null;

    /// <summary>Called once per rendered UI frame by the window.</summary>
    public void Tick()
    {
        if (Player.CheckEnded())
        {
            IsPlaying = false;
        }

        Position = Player.Position;
        foreach (PlayerTrack track in Tracks)
        {
            track.UpdateLevel();
        }
    }

    public void Seek(double seconds)
    {
        Player.Seek(seconds);
        Position = Player.Position;
    }

    public void TogglePlay()
    {
        if (Player.IsPlaying)
        {
            Player.Pause();
        }
        else
        {
            Player.Play();
        }

        IsPlaying = Player.IsPlaying;
    }

    public void Pause()
    {
        Player.Pause();
        IsPlaying = false;
    }

    public void Dispose()
    {
        _export?.Cancel();
        Player.Dispose();
    }

    private void OnTrimChanged()
    {
        OnPropertyChanged(nameof(TrimText));
        OnPropertyChanged(nameof(TrimStartText));
        OnPropertyChanged(nameof(TrimEndText));
    }

    private async Task ExportAsync()
    {
        long? maxBytes = null;
        if (Size.Megabytes is { } mb)
        {
            if (mb < 0)
            {
                if (!double.TryParse(CustomMegabytes, NumberStyles.Float, CultureInfo.CurrentCulture, out mb) || mb <= 0)
                {
                    ShowFailure("Enter the size limit in MB, e.g. 25.");
                    return;
                }
            }

            maxBytes = (long)(mb * 1_000_000);
        }

        List<TrackMix> mix = Tracks
            .Where(t => t.IsHeard && t.Source is not null)
            .Select(t => new TrackMix(t.Source!, t.VolumePercent / 100))
            .ToList();
        var request = new ExportRequest(Source, mix, Source.DefaultOutputPath())
        {
            From = TrimStart is > 0 ? TrimStart : null,
            To = TrimEnd,
            Precise = Precise,
            MaxBytes = maxBytes,
            MaxHeight = Height > 0 ? Height : null,
        };

        Pause();
        _export = new CancellationTokenSource();
        IsExporting = true;
        ExportFailed = false;
        ResultPath = null;
        Progress = 0;
        ExportStatus = "Preparing…";
        try
        {
            var exporter = new Exporter(FFmpegTool.FindDefault());
            ExportPlan plan = await Task.Run(() => exporter.Plan(request));
            string what = plan.Video switch
            {
                VideoMode.Copy => "copying the video",
                VideoMode.Encode => $"re-encoding the video ({plan.Encoder?.ShortName()}, {plan.VideoBitrateKbps / 1000.0:0.0} Mbps)",
                _ => "audio only",
            };
            string notes = plan.Notes.Count > 0 ? $"{Environment.NewLine}{string.Join(Environment.NewLine, plan.Notes)}" : string.Empty;
            ExportStatus = $"Exporting, {what}…{notes}";

            var progress = new Progress<ExportProgress>(p =>
            {
                Progress = p.Fraction;
                ExportStatus = $"Exporting, {what}: {p.Fraction:P0}{(p.Attempt > 1 ? $" (try {p.Attempt}, to fit the size)" : string.Empty)}{notes}";
            });
            IProgress<ExportProgress> report = progress;
            ExportResult result = await exporter.RunAsync(plan, p => report.Report(p), _export.Token);

            Progress = 1;
            ResultPath = result.Path;
            ExportStatus = $"Saved {Path.GetFileName(result.Path)}  ·  {Formatting.Size(result.Bytes)}  ·  {result.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException)
        {
            ExportStatus = "Export cancelled.";
            Progress = 0;
        }
        catch (Exception ex) when (ex is ExportException or FileNotFoundException or IOException)
        {
            Log.Warn("Export failed", ex);
            ShowFailure(ex.Message);
        }
        finally
        {
            IsExporting = false;
            _export?.Dispose();
            _export = null;
            CommandManagerInvalidate();
        }
    }

    private void ShowFailure(string message)
    {
        ExportFailed = true;
        ExportStatus = message;
        Progress = 0;
    }

    private void CopyResult()
    {
        try
        {
            Clipboard.SetFileDropList([ResultPath!]);
            ExportStatus = $"Copied {Path.GetFileName(ResultPath)}: paste it into Discord or a folder.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            ShowFailure($"Could not copy: {ex.Message}");
        }
    }

    private static void CommandManagerInvalidate() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();
}
