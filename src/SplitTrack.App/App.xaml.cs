using System.Windows;
using System.Windows.Threading;
using SplitTrack.App.Services;
using SplitTrack.App.ViewModels;
using SplitTrack.App.Views;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Platform;
using SplitTrack.Engine.Settings;
using SplitTrack.FFmpeg;

namespace SplitTrack.App;

/// <summary>Tray app: starts in the notification area, keeps the replay buffer running, opens the window on demand.</summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\SplitTrack.App";
    private const string ShowEventName = @"Local\SplitTrack.App.Show";
    private const string ExitEventName = @"Local\SplitTrack.App.Exit";

    // Starting a second copy with this argument closes the running one cleanly (recordings are finished).
    private const string ExitArgument = "--exit";

    private Mutex? _instance;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private RecorderService? _recorder;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private RecorderViewModel? _recorderViewModel;
    private LibraryViewModel? _library;
    private bool _toldAboutTray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        bool exit = e.Args.Contains(ExitArgument, StringComparer.OrdinalIgnoreCase);
        if (!first || exit)
        {
            // Already running: bring that one up (or, with --exit, close it) instead.
            if (EventWaitHandle.TryOpenExisting(exit ? ExitEventName : ShowEventName, out EventWaitHandle? signal))
            {
                signal.Set();
                signal.Dispose();
            }

            Shutdown();
            return;
        }

        Log.Init("app");
        Log.Info($"Started: {string.Join(' ', e.Args)} (Windows {WindowsVersion.Current})");
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        if (WindowsVersion.GetUnsupportedReason() is { } reason)
        {
            Fail(reason);
            return;
        }

        // --settings <file> uses another settings file (testing, or a second setup).
        int settingsArg = Array.FindIndex(e.Args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        string settingsPath = settingsArg >= 0 && settingsArg + 1 < e.Args.Length ? Path.GetFullPath(e.Args[settingsArg + 1]) : SettingsFile.DefaultPath;
        RecorderSettings settings;
        try
        {
            FFmpegLibrary.Initialize();
            settings = SettingsFile.Load(settingsPath);
            if (settings.Validate() is { Count: > 0 } errors)
            {
                MessageBox.Show(
                    $"{settingsPath} has problems, so the defaults are used until you save the settings again:{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
                    "SplitTrack settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                settings = new RecorderSettings();
            }
        }
        catch (Exception ex) when (ex is SettingsException or FileNotFoundException or InvalidOperationException)
        {
            Fail(ex.Message);
            return;
        }

        _recorder = new RecorderService(settingsPath, settings);
        _recorderViewModel = new RecorderViewModel(_recorder);
        _library = new LibraryViewModel(() => _recorder.RecordingPath);
        _library.SetRoot(settings.LibraryRoot);
        _library.OpenRequested += OpenPlayer;

        _tray = new TrayIcon(ShowWindow, _recorder.SaveClip, _recorder.ToggleRecording, () => _recorder.SetReplayEnabled(!_recorder.ReplayEnabled), ExitApp);
        _recorder.Changed += () => Dispatcher.BeginInvoke(OnRecorderChanged);
        _recorder.Notice += notice => Dispatcher.BeginInvoke(() => ShowNotice(notice));
        _recorder.EntrySaved += _ => _library.RefreshSoon();
        _recorderViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RecorderViewModel.TrayState))
            {
                UpdateTray();
            }
        };

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var listener = new Thread(() =>
        {
            WaitHandle[] signals = [_showEvent, _exitEvent];
            while (true)
            {
                if (WaitHandle.WaitAny(signals) == 0)
                {
                    Dispatcher.BeginInvoke(ShowWindow);
                }
                else
                {
                    Log.Info("Exit requested by --exit");
                    Dispatcher.BeginInvoke(() => Shutdown());
                    return;
                }
            }
        })
        { IsBackground = true, Name = "SplitTrack show listener" };
        listener.Start();

        _recorder.Start();
        if (!e.Args.Contains(StartupEntry.MinimizedArgument, StringComparer.OrdinalIgnoreCase))
        {
            ShowWindow();
        }
        else
        {
            _toldAboutTray = true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Exiting");
        _tray?.Dispose();
        _recorder?.Dispose();
        _library?.Dispose();
        Log.Close();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void ShowWindow()
    {
        if (_recorder is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow(_recorderViewModel!, _library!, new SettingsViewModel(_recorder));
            _window.Closing += (_, args) =>
            {
                // Closing the window keeps the app (and the replay buffer) running in the tray.
                args.Cancel = true;
                _window.Hide();
                _recorderViewModel!.MetersVisible = false;
                if (!_toldAboutTray)
                {
                    _toldAboutTray = true;
                    _tray?.ShowBalloon("SplitTrack is still running", "The replay buffer keeps going. Exit from the tray icon's menu.", false);
                }
            };
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _recorderViewModel!.MetersVisible = true;
    }

    private void OpenPlayer(LibraryEntry entry)
    {
        foreach (PlayerWindow open in Windows.OfType<PlayerWindow>())
        {
            if (open.VideoPath.Equals(entry.VideoPath, StringComparison.OrdinalIgnoreCase))
            {
                open.Activate();
                return;
            }
        }

        try
        {
            var player = new PlayerWindow(entry.VideoPath) { Owner = _window };
            player.Show();
        }
        catch (Exception ex)
        {
            Log.Error($"Opening {entry.VideoPath} failed", ex);
            MessageBox.Show(ex.Message, "Cannot open this file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRecorderChanged()
    {
        if (_recorder is null)
        {
            return;
        }

        _recorderViewModel?.Refresh();
        _library?.SetRoot(_recorder.Settings.LibraryRoot);
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_recorder is null || _tray is null || _recorderViewModel is null)
        {
            return;
        }

        string tooltip = $"SplitTrack: {_recorderViewModel.StateText}{(_recorderViewModel.IsRecording ? ", recording" : string.Empty)}";
        _tray.Update(
            _recorderViewModel.TrayState,
            tooltip,
            _recorder.ReplayEnabled,
            _recorder.HasReplayBuffer,
            _recorderViewModel.IsRecording,
            _recorder.SaveClipHotkey?.ToString(),
            _recorder.RecordHotkey?.ToString());
    }

    private void ShowNotice(RecorderNotice notice)
    {
        Action? clicked = notice.Path is { } path ? () => Shell.ShowInFolder(path) : ShowWindow;
        _tray?.ShowBalloon(notice.Title, notice.Message, notice.IsError, clicked);
    }

    private void ExitApp()
    {
        if (_recorder?.IsRecording == true
            && MessageBox.Show("A recording is running. Stop it and exit?", "SplitTrack", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        foreach (Window window in Windows.OfType<PlayerWindow>().ToList())
        {
            window.Close();
        }

        Shutdown();
    }

    private void Fail(string message)
    {
        Log.Error($"Cannot start: {message}");
        MessageBox.Show(message, "SplitTrack cannot start", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show($"{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}Details: {Log.FilePath}", "SplitTrack error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
