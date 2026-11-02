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
    private readonly ToolStripMenuItem _presets;
    private readonly Action<string> _selectPreset;
    private readonly Dictionary<TrayState, Icon> _icons;

    public TrayIcon(Action open, Action saveClip, Action toggleRecording, Action toggleReplay, Action<string> selectPreset, Action exit)
    {
        _icons = Enum.GetValues<TrayState>().ToDictionary(s => s, Draw);
        _selectPreset = selectPreset;
        _saveClip = new ToolStripMenuItem("Save clip", null, (_, _) => saveClip());
        _record = new ToolStripMenuItem("Start recording", null, (_, _) => toggleRecording());
        _replay = new ToolStripMenuItem("Replay buffer", null, (_, _) => toggleReplay());
        _presets = new ToolStripMenuItem("Preset") { Visible = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open SplitTrack", null, (_, _) => open()) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_saveClip);
        menu.Items.Add(_record);
        menu.Items.Add(_replay);
        menu.Items.Add(_presets);
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
    }

    /// <param name="tooltip">At most 127 characters; longer text is cut.</param>
    /// <param name="presets">All preset names; the submenu shows only when there are several.</param>
    /// <param name="preset">The preset in use, which is ticked.</param>
    public void Update(
        TrayState state, string tooltip, bool replayOn, bool canSaveClip, bool recording, string? saveHotkey, string? recordHotkey,
        IReadOnlyList<string> presets, string preset)
    {
        _icon.Icon = _icons[state];
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _saveClip.Enabled = canSaveClip;
        _saveClip.ShortcutKeyDisplayString = saveHotkey;
        _record.Text = recording ? "Stop recording" : "Start recording";
        _record.ShortcutKeyDisplayString = recordHotkey;
        _replay.Checked = replayOn;

        _presets.Visible = presets.Count > 1;
        if (!presets.SequenceEqual(_presets.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text)))
        {
            _presets.DropDownItems.Clear();
            foreach (string name in presets)
            {
                _presets.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => _selectPreset(name)));
            }
        }

        foreach (ToolStripMenuItem item in _presets.DropDownItems.OfType<ToolStripMenuItem>())
        {
            item.Checked = string.Equals(item.Text, preset, StringComparison.OrdinalIgnoreCase);
        }
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
                TrayState.Replay => ToDrawing(Views.Palette.Live),
                TrayState.Recording => ToDrawing(Views.Palette.Record),
                TrayState.Error => ToDrawing(Views.Palette.Caution),
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

    private static Color ToDrawing(System.Windows.Media.Color color) => Color.FromArgb(color.R, color.G, color.B);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
