using System.Windows.Media;

namespace SplitTrack.App.Views;

/// <summary>
/// The app's own colours, shared by the XAML (via x:Static), the toast and the tray icon.
/// Everything else comes from the Fluent theme, so light and dark mode both work.
/// </summary>
public static class Palette
{
    /// <summary>Recording, and destructive or failed actions.</summary>
    public static readonly Color Record = Color.FromRgb(0xE5, 0x48, 0x4D);

    /// <summary>The replay buffer is running; a save worked.</summary>
    public static readonly Color Live = Color.FromRgb(0x2F, 0x9E, 0x6A);

    /// <summary>Waiting, not capturing, warnings.</summary>
    public static readonly Color Caution = Color.FromRgb(0xD4, 0x90, 0x0A);

    /// <summary>The indigo of the app logo.</summary>
    public static readonly Color Brand = Color.FromRgb(0x63, 0x66, 0xF1);

    /// <summary>
    /// Track colours, picked by kind (ViewModels.TrackColors): game, app, microphone, system, then two more for further apps.
    /// A track has the same colour in the recorder, the player and settings.
    /// </summary>
    public static readonly Color[] Tracks =
    [
        Color.FromRgb(0x3B, 0x82, 0xC4),
        Color.FromRgb(0x8E, 0x6C, 0xD8),
        Color.FromRgb(0x1F, 0xA5, 0xA0),
        Color.FromRgb(0xD9, 0x77, 0x2B),
        Color.FromRgb(0xC2, 0x50, 0x8A),
        Color.FromRgb(0x7A, 0x8F, 0x2E),
    ];

    public static readonly SolidColorBrush RecordBrush = Freeze(Record);

    public static readonly SolidColorBrush LiveBrush = Freeze(Live);

    public static readonly SolidColorBrush CautionBrush = Freeze(Caution);

    public static readonly SolidColorBrush BrandBrush = Freeze(Brand);

    private static readonly SolidColorBrush[] TrackBrushes = Tracks.Select(Freeze).ToArray();

    public static SolidColorBrush TrackBrush(int index) => TrackBrushes[((index % TrackBrushes.Length) + TrackBrushes.Length) % TrackBrushes.Length];

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
