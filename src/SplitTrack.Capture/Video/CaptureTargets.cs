using SplitTrack.Capture.Audio.Sessions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SplitTrack.Capture.Video;

public sealed record MonitorInfo(int Index, nint Handle, string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary);

public sealed record WindowInfo(nint Handle, int Pid, string ExeName, string Title, int Width, int Height);

/// <summary>What can be captured: monitors (primary first) and top-level windows a user would pick.</summary>
public static unsafe class CaptureTargets
{
    /// <summary>Coordinates are physical pixels only when the process is per-monitor DPI aware.</summary>
    public static IReadOnlyList<MonitorInfo> ListMonitors()
    {
        var found = new List<(HMONITOR Handle, MONITORINFOEXW Info)>();
        var callback = new MONITORENUMPROC((monitor, _, _, _) =>
        {
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                found.Add((monitor, info));
            }

            return true;
        });

        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, callback, 0);
        GC.KeepAlive(callback);

        return found
            .OrderByDescending(m => (m.Info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0)
            .ThenBy(m => m.Info.monitorInfo.rcMonitor.left)
            .ThenBy(m => m.Info.monitorInfo.rcMonitor.top)
            .Select((m, i) =>
            {
                RECT r = m.Info.monitorInfo.rcMonitor;
                string name = m.Info.szDevice.ToString();
                return new MonitorInfo(i, (nint)m.Handle, name, r.left, r.top, r.right - r.left, r.bottom - r.top,
                    (m.Info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0);
            })
            .ToList();
    }

    /// <summary>Visible, uncloaked, titled, unowned top-level windows that are not tool windows.</summary>
    public static IReadOnlyList<WindowInfo> ListWindows()
    {
        ProcessTree tree = ProcessTree.Snapshot();
        int ownPid = Environment.ProcessId;
        var windows = new List<WindowInfo>();
        var callback = new WNDENUMPROC((hwnd, _) =>
        {
            if (Describe(hwnd, tree) is { } window && window.Pid != ownPid)
            {
                windows.Add(window);
            }

            return true;
        });

        PInvoke.EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return windows;
    }

    /// <summary>
    /// A window by handle (decimal or 0x hex), exe name, or title substring. With several matches the
    /// largest window wins, so a game's main window beats its launcher's.
    /// </summary>
    public static WindowInfo? FindWindow(string spec)
    {
        IReadOnlyList<WindowInfo> windows = ListWindows();
        if (TryParseHandle(spec, out nint handle))
        {
            return windows.FirstOrDefault(w => w.Handle == handle);
        }

        string exe = ProcessTree.NormalizeExeName(spec);
        IEnumerable<WindowInfo> matches = windows.Where(w => w.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (!matches.Any())
        {
            matches = windows.Where(w => w.Title.Contains(spec, StringComparison.OrdinalIgnoreCase));
        }

        return matches.OrderByDescending(w => (long)w.Width * w.Height).FirstOrDefault();
    }

    private static bool TryParseHandle(string spec, out nint handle)
    {
        handle = 0;
        if (spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(spec[2..], System.Globalization.NumberStyles.HexNumber, null, out long hex) && (handle = (nint)hex) != 0;
        }

        return long.TryParse(spec, out long value) && (handle = (nint)value) != 0;
    }

    private static WindowInfo? Describe(HWND hwnd, ProcessTree tree)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd) || PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null)
        {
            return null;
        }

        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0)
        {
            return null;
        }

        // Cloaked windows (other virtual desktops, suspended UWP apps) are visible but not on screen.
        uint cloaked = 0;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded && cloaked != 0)
        {
            return null;
        }

        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length == 0)
        {
            return null;
        }

        char* title = stackalloc char[length + 1];
        int copied = PInvoke.GetWindowText(hwnd, title, length + 1);
        if (copied == 0 || !PInvoke.GetClientRect(hwnd, out RECT client))
        {
            return null;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        string exe = tree.Get((int)pid)?.ExeName ?? "?";
        return new WindowInfo((nint)hwnd.Value, (int)pid, exe, new string(title, 0, copied), client.right - client.left, client.bottom - client.top);
    }
}
