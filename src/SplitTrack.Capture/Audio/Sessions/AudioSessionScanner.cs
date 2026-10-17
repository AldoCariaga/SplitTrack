using System.Runtime.InteropServices;
using SplitTrack.Capture.Audio.Wasapi;
using SplitTrack.Common.Diagnostics;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SplitTrack.Capture.Audio.Sessions;

/// <summary>Finds processes that own audio render sessions on any active output device.</summary>
public static class AudioSessionScanner
{
    private const int SOk = 0;

    private readonly record struct SessionInfo(int Pid, bool Active, string DeviceName);

    public static IReadOnlyList<AudioProcessInfo> Scan(ProcessTree? tree = null)
    {
        List<SessionInfo> sessions = ComApartment.RunInMta(ScanSessions);
        tree ??= ProcessTree.Snapshot();

        return sessions
            .Where(s => tree.Get(s.Pid) is not null)
            .GroupBy(s => tree.FindRoot(s.Pid))
            .Select(g => new AudioProcessInfo(
                g.Key,
                tree.Get(g.Key)?.ExeName ?? $"pid {g.Key}",
                g.Select(s => s.Pid).Distinct().Order().ToList(),
                g.Any(s => s.Active),
                g.Select(s => s.DeviceName).Distinct().ToList()))
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.ExeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static unsafe List<SessionInfo> ScanSessions()
    {
        var result = new List<SessionInfo>();
        IMMDeviceEnumerator enumerator = AudioDeviceEnumerator.CreateEnumerator();
        enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE.DEVICE_STATE_ACTIVE, out IMMDeviceCollection devices);
        devices.GetCount(out uint deviceCount);

        for (uint d = 0; d < deviceCount; d++)
        {
            devices.Item(d, out IMMDevice device);
            string deviceName = AudioDeviceEnumerator.GetFriendlyName(device);
            try
            {
                Guid iid = typeof(IAudioSessionManager2).GUID;
                device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out object activated);
                var manager = (IAudioSessionManager2)activated;
                IAudioSessionEnumerator sessionList = manager.GetSessionEnumerator();
                sessionList.GetCount(out int sessionCount);

                for (int i = 0; i < sessionCount; i++)
                {
                    sessionList.GetSession(i, out IAudioSessionControl control);
                    if (TryRead((IAudioSessionControl2)control, deviceName) is { } info)
                    {
                        result.Add(info);
                    }
                }
            }
            catch (COMException ex)
            {
                Log.Warn($"Could not list audio sessions on '{deviceName}'", ex);
            }
        }

        return result;
    }

    private static unsafe SessionInfo? TryRead(IAudioSessionControl2 control, string deviceName)
    {
        try
        {
            if (control.IsSystemSoundsSession().Value == SOk)
            {
                return null;
            }

            AudioSessionState state;
            control.GetState(&state);
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                return null;
            }

            control.GetProcessId(out uint pid);
            return pid == 0 ? null : new SessionInfo((int)pid, state == AudioSessionState.AudioSessionStateActive, deviceName);
        }
        catch (COMException)
        {
            // Cross-process sessions have no single PID; nothing to attach to.
            return null;
        }
    }
}
