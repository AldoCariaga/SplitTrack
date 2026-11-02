using System.Windows;
using System.Windows.Controls;

namespace SplitTrack.App.Views;

/// <summary>
/// Lays children out in rows of equal-width columns that together fill the width: as many columns as fit
/// at <see cref="MinItemWidth"/>, then widened to share what is left. Unlike a WrapPanel, no ragged gap on the right.
/// </summary>
public sealed class FluidWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(FluidWrapPanel),
        new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        (int columns, double width) = Columns(availableSize.Width);
        double height = 0;
        double rowHeight = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            UIElement child = InternalChildren[i];
            child.Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if ((i + 1) % columns == 0 || i == InternalChildren.Count - 1)
            {
                height += rowHeight;
                rowHeight = 0;
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? columns * width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        (int columns, double width) = Columns(finalSize.Width);
        double y = 0;
        for (int start = 0; start < InternalChildren.Count; start += columns)
        {
            int end = Math.Min(start + columns, InternalChildren.Count);
            double rowHeight = 0;
            for (int i = start; i < end; i++)
            {
                rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);
            }

            for (int i = start; i < end; i++)
            {
                InternalChildren[i].Arrange(new Rect((i - start) * width, y, width, rowHeight));
            }

            y += rowHeight;
        }

        return finalSize;
    }

    private (int Columns, double Width) Columns(double available)
    {
        double min = Math.Max(1, MinItemWidth);
        if (double.IsInfinity(available) || double.IsNaN(available))
        {
            return (Math.Max(1, InternalChildren.Count), min);
        }

        int columns = Math.Max(1, (int)(available / min));
        return (columns, available / columns);
    }
}
