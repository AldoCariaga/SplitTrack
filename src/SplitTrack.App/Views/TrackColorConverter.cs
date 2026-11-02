using System.Globalization;
using System.Windows.Data;

namespace SplitTrack.App.Views;

/// <summary>A track's ColorIndex (see ViewModels.TrackColors) to its brush from <see cref="Palette.Tracks"/>.</summary>
public sealed class TrackColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Palette.TrackBrush(value is int index ? index : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
