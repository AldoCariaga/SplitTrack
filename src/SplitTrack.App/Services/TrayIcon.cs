using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SplitTrack.App.Services;

public enum TrayState
{
    Idle,
    Replay,
    Recording,
    Error,
}

/// <summary>The notification-area icon and its menu (WinForms NotifyIcon showing the app logo).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _saveClip;
    private readonly ToolStripMenuItem _record;
    private readonly ToolStripMenuItem _replay;
    private readonly Dictionary<TrayState, Icon> _icons;
    private Action? _balloonClick;

    public TrayIcon(Action open, Action saveClip, Action toggleRecording, Action toggleReplay, Action exit)
    {
        _icons = Enum.GetValues<TrayState>().ToDictionary(s => s, Draw);
        _saveClip = new ToolStripMenuItem("Save clip", null, (_, _) => saveClip());
        _record = new ToolStripMenuItem("Start recording", null, (_, _) => toggleRecording());
        _replay = new ToolStripMenuItem("Replay buffer", null, (_, _) => toggleReplay());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open SplitTrack", null, (_, _) => open()) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_saveClip);
        menu.Items.Add(_record);
        menu.Items.Add(_replay);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new NotifyIcon { Icon = _icons[TrayState.Idle], Text = "SplitTrack", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                open();
            }
        };
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    /// <param name="tooltip">At most 127 characters; longer text is cut.</param>
    public void Update(TrayState state, string tooltip, bool replayOn, bool canSaveClip, bool recording, string? saveHotkey, string? recordHotkey)
    {
        _icon.Icon = _icons[state];
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _saveClip.Enabled = canSaveClip;
        _saveClip.ShortcutKeyDisplayString = saveHotkey;
        _record.Text = recording ? "Stop recording" : "Start recording";
        _record.ShortcutKeyDisplayString = recordHotkey;
        _replay.Checked = replayOn;
    }

    /// <param name="clicked">Runs when the balloon is clicked.</param>
    public void ShowBalloon(string title, string message, bool isError, Action? clicked = null)
    {
        _balloonClick = clicked;
        _icon.ShowBalloonTip(4000, title, message, isError ? ToolTipIcon.Warning : ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (Icon icon in _icons.Values)
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
    }

    /// <summary>The app logo at the tray's icon size, with a dot in the state's colour unless idle.</summary>
    private static Icon Draw(TrayState state)
    {
        Size size = SystemInformation.SmallIconSize;
        using Stream stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("SplitTrack.ico")!;
        using var logo = new Icon(stream, size);
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.DrawIcon(logo, new Rectangle(Point.Empty, size));
            Color? dot = state switch
            {
                TrayState.Replay => Color.FromArgb(0x2E, 0xCC, 0x71),
                TrayState.Recording => Color.FromArgb(0xE7, 0x4C, 0x3C),
                TrayState.Error => Color.FromArgb(0xF3, 0x9C, 0x12),
                _ => null,
            };
            if (dot is { } color)
            {
                // A white ring keeps the dot readable on the indigo tile.
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float diameter = size.Width * 0.55f;
                float ring = Math.Max(1f, size.Width / 16f);
                var outer = new RectangleF(size.Width - diameter, size.Height - diameter, diameter, diameter);
                using var white = new SolidBrush(Color.White);
                using var fill = new SolidBrush(color);
                g.FillEllipse(white, outer);
                g.FillEllipse(fill, RectangleF.Inflate(outer, -ring, -ring));
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
