using SplitTrack.Capture.Audio;
using SplitTrack.Capture.Audio.Sessions;
using SplitTrack.Capture.Audio.Wasapi;
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

    private readonly IReadOnlyList<AudioTrack> _tracks;
    private readonly AudioTrack? _game;
    private readonly AudioDeviceWatcher? _devices;
    private readonly Dictionary<AudioTrack, long> _retryAt = [];
    private readonly ManualResetEventSlim _stop = new();
    private int _devicesVersion = -1;
    private Thread? _thread;

    public SourceWatcher(IReadOnlyList<AudioTrack> tracks)
    {
        _tracks = tracks;
        _game = tracks.FirstOrDefault(t => t.Options.IsGame);
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
                    case AudioTrackKind.Process:
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
        string spec = track.Options.Target!;
        if (track.Identity is { } current && track.Fault is null && IsAlive(current, tree))
        {
            return;
        }

        if (!CanRetry(track, now))
        {
            return;
        }

        ProcessEntry? target = ProcessResolver.Resolve(spec, tree);
        if (target is null)
        {
            track.Detach();
            return;
        }

        var identity = new SourceIdentity($"{target.ExeName} (pid {target.Pid} tree)", target.Pid, tree.GetCreationTime(target.Pid));
        Attach(track, ProcessLoopbackSource.Open(track.Title, target.Pid, includeTree: true, AudioFormat.Float48Stereo), identity, now);
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

        AudioDeviceInfo? device = AudioDeviceEnumerator.Find(AudioDeviceFlow.Capture, track.Options.Target);
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

        Attach(track, DeviceCaptureSource.OpenCapture(track.Title, device), new SourceIdentity($"'{device.Name}'", DeviceId: device.Id), now);
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
