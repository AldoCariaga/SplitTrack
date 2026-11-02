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
/// track excluding the current game and app tracks, follows the microphone across device
/// changes, and pads tracks that have no source. Runs on its own thread once started.
/// </summary>
internal sealed class SourceWatcher : IDisposable
{
    // Short, so a newly started game loses little audio before it is attached; a Toolhelp snapshot costs ~1 ms.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    // Pause before retrying a source that failed to open or faulted.
    private static readonly long RetryTicks = QpcClock.MillisecondsToTicks(2_000);

    // The System track's source in the sidecar once it left the apps out; earlier recordings have none.
    private const string MixSourceName = "all but the game and app tracks";

    // Programs that go full screen without being games: the desktop and shell, and browsers playing video.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "LockApp.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
        "ApplicationFrameHost.exe", "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
    };

    private readonly IReadOnlyList<AudioTrack> _tracks;
    private readonly AudioTrack? _game;
    private readonly IReadOnlyList<AudioTrack> _apps;

    // Exe names app tracks record, e.g. Discord full screen during a stream is not the game.
    private readonly HashSet<string> _appExes;
    private readonly AudioDeviceWatcher? _devices;
    private readonly Dictionary<AudioTrack, long> _retryAt = [];

    // When to retry a program the System mix could not capture, by PID.
    private readonly Dictionary<int, long> _mixRetryAt = [];

    // Creation time of each program in the System mix, to tell a reused PID apart.
    private readonly Dictionary<int, long?> _mixCreated = [];
    private bool _mixScanFailing;
    private readonly ManualResetEventSlim _stop = new();
    private int _devicesVersion = -1;

    // The System track's source while it is a mix, and the identity it was attached with.
    private ProcessMixSource? _systemMix;
    private SourceIdentity? _systemMixIdentity;
    private Thread? _thread;

    public SourceWatcher(IReadOnlyList<AudioTrack> tracks)
    {
        _tracks = tracks;
        _game = tracks.FirstOrDefault(t => t.Options.Kind == AudioTrackKind.Game);
        _apps = tracks.Where(t => t.Options.Kind == AudioTrackKind.App).ToList();
        _appExes = _apps
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

        // Process tracks first: System depends on which game and app processes are attached.
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
        SourceIdentity? game = _game?.Identity;
        List<SourceIdentity> excluded = _apps.Select(t => t.Identity).OfType<SourceIdentity>().ToList();
        if (excluded.Count > 0)
        {
            if (game is not null)
            {
                excluded.Add(game);
            }

            ReconcileSystemMix(track, tree, excluded, now);
            return;
        }

        // One capture of everything except one tree. With no game that is our own process, whose only
        // sound is the player's (clip previews), so it means "everything" and leaves previews out. With
        // a game only the game can be left out, so previews are recorded then; the mix leaves them out.
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
        (_systemMix, _systemMixIdentity) = (null, null);
        Attach(track, ProcessLoopbackSource.Open(track.Title, excludePid, includeTree: false, AudioFormat.Float48Stereo),
            new SourceIdentity(description, excludePid), now);
    }

    /// <summary>
    /// Windows leaves only one process tree out of a capture, so to leave out the apps as well as the
    /// game, System captures every other program with an audio session on its own and mixes them.
    /// </summary>
    private void ReconcileSystemMix(AudioTrack track, ProcessTree tree, IReadOnlyList<SourceIdentity> excluded, long now)
    {
        if (_systemMix is null || track.Identity is null || !ReferenceEquals(track.Identity, _systemMixIdentity) || track.Fault is not null)
        {
            if (!CanRetry(track, now))
            {
                return;
            }

            var mix = new ProcessMixSource(track.Title);
            var identity = new SourceIdentity(
                "every other program, one by one (game and app tracks left out)", Name: MixSourceName);
            Attach(track, mix, identity, now);
            (_systemMix, _systemMixIdentity) = (mix, identity);
            _mixRetryAt.Clear();
            _mixCreated.Clear();
        }

        // The mix hands itself on with the clock, so a failed scan only delays changes to who is in it.
        try
        {
            UpdateMix(_systemMix, tree, excluded, now);
            if (_mixScanFailing)
            {
                _mixScanFailing = false;
                Log.Info($"{track.Title}: listing programs with sound works again");
            }
        }
        catch (Exception ex)
        {
            if (!_mixScanFailing)
            {
                _mixScanFailing = true;
                Log.Warn($"{track.Title}: could not list programs with sound; trying again every {Interval.TotalMilliseconds:0} ms", ex);
            }
        }
    }

    /// <summary>
    /// Brings programs with a new audio session into the mix. A program stays in until it exits or
    /// is no longer allowed, not just until its sound ends: one that opens a stream per sound would
    /// otherwise lose the start of each one while it is captured again.
    /// </summary>
    private void UpdateMix(ProcessMixSource mix, ProcessTree tree, IReadOnlyList<SourceIdentity> excluded, long now)
    {
        // Our own process plays the player's audio.
        List<int> excludedRoots = excluded.Select(i => i.Pid).OfType<int>().Append(Environment.ProcessId).ToList();
        foreach (int pid in mix.FaultedPids.Concat(mix.TakeFailedPids()))
        {
            mix.Remove(pid);
            _mixCreated.Remove(pid);
            _mixRetryAt[pid] = now + RetryTicks;
        }

        IReadOnlyList<int> current = mix.Pids;
        var stillRunning = current.Where(pid => IsSameProcess(pid, tree)).ToHashSet();
        HashSet<int> targets = FindMixTargets(tree, AudioSessionScanner.ScanSessionPids().Concat(stillRunning), excludedRoots);

        // A reused PID is taken in again as the new program on the next pass.
        foreach (int pid in current.Where(p => !targets.Contains(p) || !stillRunning.Contains(p)))
        {
            mix.Remove(pid);
            _mixCreated.Remove(pid);
        }

        foreach (int pid in targets.Where(p => !current.Contains(p)))
        {
            if (_mixRetryAt.TryGetValue(pid, out long at) && now < at)
            {
                continue;
            }

            _mixRetryAt.Remove(pid);
            _mixCreated[pid] = tree.GetCreationTime(pid);
            mix.Request(pid, tree.Get(pid)!.ExeName);
        }

        foreach (int pid in _mixRetryAt.Keys.Where(p => tree.Get(p) is null).ToList())
        {
            _mixRetryAt.Remove(pid);
        }
    }

    // A program in the mix is still the one it was added as: running, and not a reused PID.
    private bool IsSameProcess(int pid, ProcessTree tree) =>
        tree.Get(pid) is not null &&
        (!_mixCreated.TryGetValue(pid, out long? created) || created is null || tree.GetCreationTime(pid) == created);

    /// <summary>
    /// Of <paramref name="candidates"/> (programs with an audio session, and those already in the mix),
    /// the running ones outside the excluded trees, each the topmost of them in its tree (a tree
    /// capture includes the rest). One whose tree holds an excluded process, e.g. the launcher that
    /// started the game, is left out: capturing it would bring the game back. One walk up the tree
    /// per process.
    /// </summary>
    private static HashSet<int> FindMixTargets(ProcessTree tree, IEnumerable<int> candidates, IReadOnlyList<int> excludedRoots)
    {
        var excludedSet = excludedRoots.ToHashSet();

        // The excluded roots and everything above them: any of these would capture an excluded tree.
        var holdsExcluded = excludedRoots.SelectMany(tree.GetAncestry).ToHashSet();
        var pool = new Dictionary<int, IReadOnlyList<int>>();
        foreach (int pid in candidates.Distinct())
        {
            if (tree.Get(pid) is null || holdsExcluded.Contains(pid))
            {
                continue;
            }

            IReadOnlyList<int> ancestry = tree.GetAncestry(pid);
            if (!ancestry.Any(excludedSet.Contains))
            {
                pool[pid] = ancestry;
            }
        }

        return pool
            .Where(entry => !entry.Value.Skip(1).Any(pool.ContainsKey))
            .Select(entry => entry.Key)
            .ToHashSet();
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
