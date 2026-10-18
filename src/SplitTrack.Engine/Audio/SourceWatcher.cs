using SplitTrack.Capture.Audio;
using SplitTrack.Capture.Audio.Sessions;
using SplitTrack.Capture.Audio.Wasapi;
using SplitTrack.Capture.Video;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Engine.Recording;

namespace SplitTrack.Engine.Audio;

/// <summary>
/// Keeps every track attached to the right source: follows game/app restarts, keeps the System
/// track excluding the current game, follows the microphone across device changes, and pads
/// tracks that have no source. Runs on its own thread once started.
/// </summary>
internal sealed class SourceWatcher : IDisposable
{
    // Short, so a newly started game loses little audio before it is attached; a Toolhelp snapshot costs ~1 ms.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    // Pause before retrying a source that failed to open or faulted.
    private static readonly long RetryTicks = QpcClock.MillisecondsToTicks(2_000);

    // Programs that go full screen without being games: the desktop and shell, and browsers playing video.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "LockApp.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
        "ApplicationFrameHost.exe", "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
    };

    private readonly IReadOnlyList<AudioTrack> _tracks;
    private readonly AudioTrack? _game;

    // Exe names app tracks record, e.g. Discord full screen during a stream is not the game.
    private readonly HashSet<string> _appExes;
    private readonly AudioDeviceWatcher? _devices;
    private readonly Dictionary<AudioTrack, long> _retryAt = [];
    private readonly ManualResetEventSlim _stop = new();
    private int _devicesVersion = -1;
    private Thread? _thread;

    public SourceWatcher(IReadOnlyList<AudioTrack> tracks)
    {
        _tracks = tracks;
        _game = tracks.FirstOrDefault(t => t.Options.Kind == AudioTrackKind.Game);
        _appExes = tracks
            .Where(t => t.Options.Kind == AudioTrackKind.App)
            .SelectMany(t => t.Options.Targets)
            .Where(t => !int.TryParse(t, out _))
            .Select(ProcessTree.NormalizeExeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tracks.Any(t => t.Options.Kind == AudioTrackKind.Microphone))
        {
            _devices = AudioDeviceWatcher.Start();
        }
    }

    /// <summary>Attaches whatever is available now; the thread keeps it up to date afterwards.</summary>
    public void Reconcile()
    {
        long now = QpcClock.Now;
        ProcessTree tree = ProcessTree.Snapshot();
        int devicesVersion = _devices?.Version ?? 0;
        bool devicesChanged = devicesVersion != _devicesVersion;
        _devicesVersion = devicesVersion;

        // Process tracks first: System depends on which game process is attached.
        foreach (AudioTrack track in _tracks.OrderBy(t => t.Options.Kind == AudioTrackKind.System))
        {
            try
            {
                switch (track.Options.Kind)
                {
                    case AudioTrackKind.Game or AudioTrackKind.App:
                        ReconcileProcess(track, tree, now);
                        break;
                    case AudioTrackKind.System:
                        ReconcileSystem(track, tree, now);
                        break;
                    case AudioTrackKind.Microphone:
                        ReconcileMicrophone(track, devicesChanged, now);
                        break;
                }
            }
            catch (Exception ex)
            {
                _retryAt[track] = now + RetryTicks;
                Log.Warn($"{track.Title}: could not open its source, retrying", ex);
            }
        }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SplitTrack source watcher" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();
        _thread?.Join();
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _devices?.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        while (!_stop.Wait(Interval))
        {
            Reconcile();
            long now = QpcClock.Now;
            foreach (AudioTrack track in _tracks)
            {
                track.PadIfIdle(now);
            }
        }
    }

    private void ReconcileProcess(AudioTrack track, ProcessTree tree, long now)
    {
        IReadOnlyList<string> targets = track.Options.Targets;
        ProcessEntry? fullScreen = track.Options.Kind == AudioTrackKind.Game && targets.Any(AudioTrackOptions.IsFullScreen)
            ? FindFullScreenGame(tree)
            : null;

        // A running target is kept even if an earlier one in the list starts later; a detected one
        // gives way only when another program takes over the screen.
        if (track.Identity is { } current && track.Fault is null && IsAlive(current, tree) &&
            !(current.FullScreen && fullScreen is not null && fullScreen.Pid != current.Pid))
        {
            return;
        }

        if (!CanRetry(track, now))
        {
            return;
        }

        ProcessEntry? target = null;
        bool detected = false;
        foreach (string spec in targets)
        {
            detected = AudioTrackOptions.IsFullScreen(spec);
            target = detected ? fullScreen : ProcessResolver.Resolve(spec, tree);
            if (target is not null)
            {
                break;
            }
        }

        if (target is null)
        {
            track.Detach();
            return;
        }

        var identity = new SourceIdentity(
            $"{target.ExeName} (pid {target.Pid} tree{(detected ? ", full screen" : "")})", target.Pid, tree.GetCreationTime(target.Pid),
            Name: target.ExeName, FullScreen: detected);
        Attach(track, ProcessLoopbackSource.Open(track.Title, target.Pid, includeTree: true, AudioFormat.Float48Stereo), identity, now);
    }

    /// <summary>The root process of the full-screen window in front, unless it is a known non-game or an app track's.</summary>
    private ProcessEntry? FindFullScreenGame(ProcessTree tree)
    {
        if (CaptureTargets.FindFullScreenWindow(tree) is not { } window ||
            window.Pid == Environment.ProcessId ||
            NotGames.Contains(window.ExeName) ||
            _appExes.Contains(window.ExeName))
        {
            return null;
        }

        return tree.Get(tree.FindRoot(window.Pid));
    }

    private void ReconcileSystem(AudioTrack track, ProcessTree tree, long now)
    {
        // Our own process plays nothing, so excluding it means "everything".
        SourceIdentity? game = _game?.Identity;
        int excludePid = game?.Pid ?? Environment.ProcessId;
        if (track.Identity is { } current && track.Fault is null && current.Pid == excludePid)
        {
            return;
        }

        if (!CanRetry(track, now))
        {
            return;
        }

        string description = game is null ? "everything (no game running)" : $"everything except {game.Description}";
        Attach(track, ProcessLoopbackSource.Open(track.Title, excludePid, includeTree: false, AudioFormat.Float48Stereo),
            new SourceIdentity(description, excludePid), now);
    }

    private void ReconcileMicrophone(AudioTrack track, bool devicesChanged, long now)
    {
        if (track.Identity is not null && track.Fault is null && !devicesChanged)
        {
            return;
        }

        if (!CanRetry(track, now))
        {
            return;
        }

        IReadOnlyList<string> filters = track.Options.Targets;
        AudioDeviceInfo? device = filters.Count == 0
            ? AudioDeviceEnumerator.Find(AudioDeviceFlow.Capture, null)
            : filters.Select(f => AudioDeviceEnumerator.Find(AudioDeviceFlow.Capture, f)).FirstOrDefault(d => d is not null);
        if (device is null)
        {
            track.Detach();
            _retryAt[track] = now + RetryTicks;
            return;
        }

        if (track.Identity?.DeviceId == device.Id && track.Fault is null)
        {
            return;
        }

        Attach(track, DeviceCaptureSource.OpenCapture(track.Title, device), new SourceIdentity($"'{device.Name}'", DeviceId: device.Id, Name: device.Name), now);
    }

    private void Attach(AudioTrack track, IAudioSource source, SourceIdentity identity, long now)
    {
        try
        {
            track.Attach(source, identity);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        _retryAt.Remove(track);
    }

    private bool CanRetry(AudioTrack track, long now) => !_retryAt.TryGetValue(track, out long at) || now >= at;

    /// <summary>The same process is still running: same PID, same exe, same creation time when known.</summary>
    private static bool IsAlive(SourceIdentity identity, ProcessTree tree)
    {
        if (identity.Pid is not { } pid || tree.Get(pid) is null)
        {
            return false;
        }

        return identity.CreationTime is not { } created || tree.GetCreationTime(pid) == created;
    }
}
