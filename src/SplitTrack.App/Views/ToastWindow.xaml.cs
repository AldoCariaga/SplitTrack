using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SplitTrack.App.Views;

public enum ToastKind
{
    Info,
    Success,
    Error,
}

/// <summary>
/// An in-game style notification in the top-right corner of the monitor the user is looking at (the foreground window's).
/// It never takes focus, so a game keeps its input; a new toast replaces the one on screen.
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ShowErrorFor = TimeSpan.FromSeconds(7);

    private readonly IEasingFunction _ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    private AnimationClock? _countdown;
    private Action? _clicked;
    private bool _shown;

    public ToastWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PlaceInCorner();
        DpiChanged += (_, _) => PlaceInCorner();
    }

    /// <param name="clicked">Runs when the toast is clicked.</param>
    public void ShowToast(string title, string message, ToastKind kind, Action? clicked = null)
    {
        _clicked = clicked;
        TitleText.Text = title;
        MessageText.Text = message;
        // Segoe Fluent Icons: check mark, warning, info.
        (int glyph, Color color) = kind switch
        {
            ToastKind.Success => (0xE73E, Palette.Live),
            ToastKind.Error => (0xE7BA, Palette.Caution),
            _ => (0xE946, Palette.Brand),
        };
        Glyph.Text = char.ConvertFromUtf32(glyph);
        Badge.Background = new SolidColorBrush(color);
        Countdown.Fill = new SolidColorBrush(color);

        if (!_shown)
        {
            _shown = true;
            Show();
            PlaceInCorner();
            Animate(Card, OpacityProperty, 0, 1, 220);
            Animate(Slide, TranslateTransform.XProperty, 60, 0, 280);
        }
        else
        {
            // Another notice while one is up: keep the card where it is, just stay on top.
            PlaceInCorner();
        }

        var countdown = new DoubleAnimation(1, 0, kind == ToastKind.Error ? ShowErrorFor : ShowFor);
        _countdown = countdown.CreateClock();
        _countdown.Completed += (sender, _) =>
        {
            if (sender == _countdown)
            {
                Dismiss();
            }
        };
        CountdownScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _countdown);
        if (Card.IsMouseOver)
        {
            _countdown.Controller.Pause();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // No activation on show or click, and no taskbar or Alt+Tab entry.
        nint hwnd = new WindowInteropHelper(this).Handle;
        nint style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void Dismiss()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        _countdown?.Controller.Stop();
        _countdown = null;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            // A toast shown during the fade keeps the window up.
            if (!_shown)
            {
                Hide();
            }
        };
        Card.BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(180)));
    }

    private void Animate(IAnimatable target, DependencyProperty property, double from, double to, int milliseconds) =>
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = _ease });

    /// <summary>Top-right of the work area of the foreground window's monitor, in physical pixels (the monitors may differ in scale).</summary>
    private void PlaceInCorner()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out RECT window))
        {
            return;
        }

        nint monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        int x = info.rcWork.Right - (window.Right - window.Left);
        int y = info.rcWork.Top;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        Action? clicked = _clicked;
        Dismiss();
        clicked?.Invoke();
    }

    private void OnCloseClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    private void OnCardMouseEnter(object sender, MouseEventArgs e) => _countdown?.Controller.Pause();

    private void OnCardMouseLeave(object sender, MouseEventArgs e) => _countdown?.Controller.Resume();

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TOOLWINDOW = 0x80;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint SWP_NOSIZE = 0x1;
    private const uint SWP_NOACTIVATE = 0x10;
    private static readonly nint HWND_TOPMOST = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
