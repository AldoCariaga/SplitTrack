using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Export;
using SplitTrack.Library;

namespace SplitTrack.App.ViewModels;

public enum LibraryFilter
{
    All,
    Clips,
    Recordings,
}

/// <summary>One recording or clip in the library.</summary>
public sealed class LibraryEntry : ObservableObject
{
    private ImageSource? _thumbnail;
    private double? _durationSeconds;

    public LibraryEntry(string videoPath, Sidecar? sidecar, long sizeBytes, DateTime lastWrite)
    {
        VideoPath = videoPath;
        Sidecar = sidecar;
        SizeBytes = sizeBytes;
        LastWrite = lastWrite;
        Created = sidecar?.Created.LocalDateTime ?? File.GetCreationTime(videoPath);
        _durationSeconds = sidecar?.DurationSeconds;
        Kind = sidecar?.Kind ?? (videoPath.EndsWith("_clip.mkv", StringComparison.OrdinalIgnoreCase) ? EntryKind.Clip : EntryKind.Recording);
    }

    public string VideoPath { get; }

    public Sidecar? Sidecar { get; }

    public long SizeBytes { get; }

    public DateTime LastWrite { get; }

    public DateTime Created { get; }

    public EntryKind Kind { get; }

    public string Name => Path.GetFileNameWithoutExtension(VideoPath);

    public string KindText => Kind == EntryKind.Clip ? "Clip" : "Recording";

    public string CreatedText => Created.Date == DateTime.Today
        ? $"Today {Created:HH:mm}"
        : Created.Date == DateTime.Today.AddDays(-1)
            ? $"Yesterday {Created:HH:mm}"
            : Created.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    public string? Game => Sidecar?.Game is { Length: > 0 } game ? Path.GetFileNameWithoutExtension(game) : null;

    public bool IsAudioOnly => Sidecar is not null && Sidecar.Video is null;

    public string Details => string.Join("  ·  ", new[]
    {
        DurationText,
        Formatting.Size(SizeBytes),
        Sidecar?.Video is { } video ? $"{video.Height}p{video.Fps}" : IsAudioOnly ? "audio only" : null,
        Sidecar?.AudioTracks is { Count: > 0 } tracks ? $"{tracks.Count} track{(tracks.Count == 1 ? string.Empty : "s")}" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string? DurationText => _durationSeconds is { } seconds ? Formatting.Duration(seconds) : null;

    public double? DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (Set(ref _durationSeconds, value))
            {
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    public string ThumbnailPath => Sidecar?.Thumbnail is { } name
        ? Path.Combine(Path.GetDirectoryName(VideoPath)!, name)
        : EntryPaths.ForVideo(VideoPath).Thumbnail;

    public bool IsSameFile(LibraryEntry other) =>
        other.VideoPath.Equals(VideoPath, StringComparison.OrdinalIgnoreCase) && other.SizeBytes == SizeBytes && other.LastWrite == LastWrite
        && (other.Sidecar is null) == (Sidecar is null);
}

/// <summary>The library folder as a thumbnail grid: newest first, filterable, kept up to date by a file watcher.</summary>
public sealed class LibraryViewModel : ObservableObject, IDisposable
{
    // A file written this recently may still be in use (a recording or clip being saved).
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly Func<string?> _activeRecording;
    private readonly DispatcherTimer _debounce;
    private readonly ICollectionView _view;
    private FileSystemWatcher? _watcher;
    private string _root = string.Empty;
    private LibraryFilter _filter;
    private bool _scanning;
    private bool _rescan;

    public LibraryViewModel(Func<string?> activeRecording)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _activeRecording = activeRecording;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };

        _view = CollectionViewSource.GetDefaultView(Entries);
        _view.Filter = item => item is LibraryEntry entry && _filter switch
        {
            LibraryFilter.Clips => entry.Kind == EntryKind.Clip,
            LibraryFilter.Recordings => entry.Kind == EntryKind.Recording,
            _ => true,
        };

        RefreshCommand = new RelayCommand(Refresh);
        OpenCommand = new RelayCommand(p => Open((LibraryEntry)p!), p => p is LibraryEntry);
        OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(_root));
        ShowInFolderCommand = new RelayCommand(p => Shell.ShowInFolder(((LibraryEntry)p!).VideoPath), p => p is LibraryEntry);
        DeleteCommand = new RelayCommand(p => Delete((LibraryEntry)p!), p => p is LibraryEntry);
    }

    /// <summary>Raised to open an entry in the player.</summary>
    public event Action<LibraryEntry>? OpenRequested;

    public ObservableCollection<LibraryEntry> Entries { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand ShowInFolderCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public string Root
    {
        get => _root;
        private set => Set(ref _root, value);
    }

    public LibraryFilter Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                _view.Refresh();
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string Summary
    {
        get
        {
            int clips = Entries.Count(e => e.Kind == EntryKind.Clip);
            int recordings = Entries.Count - clips;
            return Entries.Count == 0
                ? "Nothing here yet. Save a clip with the hotkey, or start a recording."
                : $"{clips} clip{(clips == 1 ? string.Empty : "s")}, {recordings} recording{(recordings == 1 ? string.Empty : "s")}, {Formatting.Size(Entries.Sum(e => e.SizeBytes))}";
        }
    }

    public bool IsEmpty => Entries.Count == 0;

    public void Open(LibraryEntry entry) => OpenRequested?.Invoke(entry);

    /// <summary>Shows <paramref name="root"/>; rescans when it changed.</summary>
    public void SetRoot(string root)
    {
        if (root.Equals(Root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Root = root;
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            Directory.CreateDirectory(root);
            _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            _watcher.Created += OnFolderChanged;
            _watcher.Deleted += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Cannot watch the library folder {root}", ex);
        }

        Entries.Clear();
        Refresh();
    }

    /// <summary>Rescans soon (debounced), e.g. after a save.</summary>
    public void RefreshSoon() => _dispatcher.BeginInvoke(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    public async void Refresh()
    {
        if (_scanning)
        {
            _rescan = true;
            return;
        }

        _scanning = true;
        try
        {
            do
            {
                _rescan = false;
                string root = Root;
                string? skip = _activeRecording();
                List<LibraryEntry> found = await Task.Run(() => Scan(root, skip));
                if (root == Root)
                {
                    Merge(found);
                }
            }
            while (_rescan);
        }
        finally
        {
            _scanning = false;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Stop();
    }

    private static List<LibraryEntry> Scan(string root, string? skip)
    {
        var entries = new List<LibraryEntry>();
        if (!Directory.Exists(root))
        {
            return entries;
        }

        foreach (string path in Directory.EnumerateFiles(root, "*.mkv", SearchOption.TopDirectoryOnly))
        {
            if (skip is not null && path.Equals(skip, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                string sidecarPath = EntryPaths.ForVideo(path).Sidecar;
                Sidecar? sidecar = null;
                if (File.Exists(sidecarPath))
                {
                    try
                    {
                        sidecar = LibraryFolder.ReadSidecar(sidecarPath);
                    }
                    catch (JsonException ex)
                    {
                        Log.Warn($"Ignoring sidecar {sidecarPath}", ex);
                    }
                }
                else if (DateTime.Now - info.LastWriteTime < SettleTime)
                {
                    // Still being written; the watcher sees its sidecar arrive.
                    continue;
                }

                entries.Add(new LibraryEntry(path, sidecar, info.Length, info.LastWriteTime));
            }
            catch (IOException)
            {
                // Deleted while scanning.
            }
        }

        return entries.OrderByDescending(e => e.Created).ToList();
    }

    private void Merge(List<LibraryEntry> found)
    {
        var old = Entries.ToDictionary(e => e.VideoPath, StringComparer.OrdinalIgnoreCase);
        var merged = found.Select(e => old.TryGetValue(e.VideoPath, out LibraryEntry? existing) && existing.IsSameFile(e) ? existing : e).ToList();

        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (!merged.Contains(Entries[i]))
            {
                Entries.RemoveAt(i);
            }
        }

        for (int i = 0; i < merged.Count; i++)
        {
            int at = Entries.IndexOf(merged[i]);
            if (at < 0)
            {
                Entries.Insert(i, merged[i]);
            }
            else if (at != i)
            {
                Entries.Move(at, i);
            }
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsEmpty));
        _ = LoadDetailsAsync(merged.Where(e => e.Thumbnail is null || e.DurationSeconds is null).ToList());
    }

    /// <summary>Thumbnails (made from the video when missing) and durations for files without a sidecar.</summary>
    private static async Task LoadDetailsAsync(List<LibraryEntry> entries)
    {
        foreach (LibraryEntry entry in entries)
        {
            (ImageSource? image, double? duration) = await Task.Run(() => LoadDetails(entry));
            entry.Thumbnail ??= image;
            entry.DurationSeconds ??= duration;
        }
    }

    private static (ImageSource? Image, double? Duration) LoadDetails(LibraryEntry entry)
    {
        double? duration = entry.DurationSeconds;
        try
        {
            if (duration is null)
            {
                duration = MediaInfo.Probe(entry.VideoPath).DurationSeconds;
            }

            string thumbnail = entry.ThumbnailPath;
            if (!File.Exists(thumbnail) && !entry.IsAudioOnly)
            {
                Thumbnail.Write(entry.VideoPath, thumbnail);
            }

            if (!File.Exists(thumbnail))
            {
                return (null, duration);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320;
            image.UriSource = new Uri(thumbnail);
            image.EndInit();
            image.Freeze();
            return (image, duration);
        }
        catch (Exception ex)
        {
            Log.Debug($"No thumbnail for {entry.VideoPath}: {ex.Message}");
            return (null, duration);
        }
    }

    private void Delete(LibraryEntry entry)
    {
        MessageBoxResult answer = MessageBox.Show(
            $"Move {entry.Name} ({Formatting.Size(entry.SizeBytes)}) to the Recycle Bin?",
            "Delete",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        EntryPaths paths = EntryPaths.ForVideo(entry.VideoPath);
        try
        {
            foreach (string path in new[] { paths.Video, paths.Sidecar, entry.ThumbnailPath }.Distinct())
            {
                if (File.Exists(path))
                {
                    Shell.Recycle(path);
                }
            }

            Log.Info($"Deleted {entry.VideoPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Deleting {entry.VideoPath} failed", ex);
            MessageBox.Show(ex.Message, "Could not delete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name is { } name && (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)))
        {
            RefreshSoon();
        }
    }
}

public static class Formatting
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.0} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        _ => $"{bytes / 1000.0:0} KB",
    };

    public static string Duration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>m:ss.f, for the player's position and trim times.</summary>
    public static string Position(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }
}

public static class Shell
{
    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public static void ShowInFolder(string file) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });

    public static void OpenFile(string file) => Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });

    public static void Recycle(string file) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            file, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}
