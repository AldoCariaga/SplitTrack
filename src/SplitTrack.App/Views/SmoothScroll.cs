using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SplitTrack.App.Views;

/// <summary>
/// Wheel scrolling that moves by how far the wheel actually turned, eased over a few frames.
/// WPF scrolls a full three lines per wheel message whatever its size, so touchpads and
/// high-resolution wheels (many small messages) fly through the list.
/// Only for pixel-scrolled viewers; a virtualizing list scrolls by items.
/// </summary>
public static class SmoothScroll
{
    // Matches WPF's own line height, so one notch moves as far as before.
    private const double PixelsPerLine = 16;

    // Share of the remaining distance covered each frame.
    private const double Easing = 0.3;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty GlideProperty = DependencyProperty.RegisterAttached(
        "Glide", typeof(Glide), typeof(SmoothScroll));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || FindScrollViewer((DependencyObject)sender) is not { } viewer || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        // WheelScrollLines is -1 when Windows is set to scroll a page at a time.
        int lines = SystemParameters.WheelScrollLines;
        double perNotch = lines > 0 ? lines * PixelsPerLine : viewer.ViewportHeight;

        if (viewer.GetValue(GlideProperty) is not Glide glide)
        {
            glide = new Glide(viewer);
            viewer.SetValue(GlideProperty, glide);
        }

        glide.Add(-e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * perNotch);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer)
        {
            return viewer;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class Glide(ScrollViewer viewer)
    {
        private double _target;
        private double _lastSet = double.NaN;
        private bool _running;

        public void Add(double pixels)
        {
            // A fresh scroll starts from wherever the scrollbar or keys left it.
            if (!_running)
            {
                _target = viewer.VerticalOffset;
            }

            _target = Math.Clamp(_target + pixels, 0, viewer.ScrollableHeight);
            if (!_running)
            {
                _running = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double current = viewer.VerticalOffset;

            // Something else moved it (a scrollbar drag, or the viewer went away): let that win.
            if (!double.IsNaN(_lastSet) && Math.Abs(current - _lastSet) > 1)
            {
                Stop();
                return;
            }

            double remaining = _target - current;
            if (Math.Abs(remaining) < 0.5)
            {
                viewer.ScrollToVerticalOffset(_target);
                Stop();
                return;
            }

            _lastSet = current + remaining * Easing;
            viewer.ScrollToVerticalOffset(_lastSet);
        }

        private void Stop()
        {
            _running = false;
            _lastSet = double.NaN;
            CompositionTarget.Rendering -= OnRendering;
        }
    }
}
