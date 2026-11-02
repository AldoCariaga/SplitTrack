using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SplitTrack.App.ViewModels;
using SplitTrack.Preview;

namespace SplitTrack.App.Views;

/// <summary>Plays a recording with a live per-track mix and exports it. Frames are copied into a WriteableBitmap once per UI frame.</summary>
public partial class PlayerWindow : Window
{
    // While dragging the seek bar, at most this many seeks per second (each decodes from a keyframe).
    private const double DragSeekIntervalMs = 80;

    // The Fluent slider's track starts and ends this far inside the control (half the thumb).
    private const double SliderInset = 9;

    private readonly PlayerViewModel _viewModel;
    private readonly WriteableBitmap? _bitmap;
    private readonly Stopwatch _lastDragSeek = Stopwatch.StartNew();
    private bool _updatingSlider;
    private bool _dragging;
    private bool _resumeAfterDrag;

    public PlayerWindow(string videoPath)
    {
        _viewModel = new PlayerViewModel(videoPath);
        DataContext = _viewModel;
        InitializeComponent();

        if (_viewModel.HasVideo)
        {
            (int width, int height) = _viewModel.Player.FrameSize;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            VideoImage.Source = _bitmap;
        }

        _viewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnRendering;
            UpdateTrimRange();
            _viewModel.TogglePlay();
        };
        SeekSlider.SizeChanged += (_, _) => UpdateTrimRange();
    }

    public string VideoPath => _viewModel.VideoPath;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsExporting
            && MessageBox.Show(this, "An export is running. Cancel it and close?", "SplitTrack", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
        Owner?.Activate();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _viewModel.Tick();
        if (_bitmap is not null && _viewModel.Player.TakeFrame() is { } frame)
        {
            try
            {
                if (frame.Width == _bitmap.PixelWidth && frame.Height == _bitmap.PixelHeight)
                {
                    _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
                }
            }
            finally
            {
                _viewModel.Player.Release(frame);
            }
        }

        if (!_dragging)
        {
            _updatingSlider = true;
            SeekSlider.Value = _viewModel.Position;
            _updatingSlider = false;
        }
    }

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider)
        {
            return;
        }

        if (_dragging && _lastDragSeek.Elapsed.TotalMilliseconds < DragSeekIntervalMs)
        {
            return;
        }

        _lastDragSeek.Restart();
        _viewModel.Seek(e.NewValue);
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        _dragging = true;
        _resumeAfterDrag = _viewModel.IsPlaying;
        _viewModel.Pause();
    }

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        _viewModel.Seek(SeekSlider.Value);
        if (_resumeAfterDrag)
        {
            _viewModel.TogglePlay();
        }
    }

    private void OnVideoClick(object sender, MouseButtonEventArgs e) => _viewModel.TogglePlay();

    private void OnVolumeReset(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlayerTrack track })
        {
            track.VolumePercent = 100;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 5;
        switch (e.Key)
        {
            case Key.Space:
                _viewModel.TogglePlay();
                break;
            case Key.Left:
                _viewModel.Seek(_viewModel.Position - step);
                break;
            case Key.Right:
                _viewModel.Seek(_viewModel.Position + step);
                break;
            case Key.Home:
                _viewModel.Seek(0);
                break;
            case Key.I:
                _viewModel.SetStartCommand.Execute(null);
                break;
            case Key.O:
                _viewModel.SetEndCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.TrimStart) or nameof(PlayerViewModel.TrimEnd))
        {
            UpdateTrimRange();
        }
    }

    /// <summary>Shades the part that will be exported under the seek bar.</summary>
    private void UpdateTrimRange()
    {
        double duration = _viewModel.Duration;
        double width = Math.Max(0, SeekSlider.ActualWidth - (2 * SliderInset));
        bool trimmed = _viewModel.TrimStart is not null || _viewModel.TrimEnd is not null;
        if (duration <= 0 || width <= 0 || !trimmed)
        {
            TrimRange.Visibility = Visibility.Collapsed;
            TrimStartMark.Visibility = Visibility.Collapsed;
            TrimEndMark.Visibility = Visibility.Collapsed;
            return;
        }

        double start = Math.Clamp((_viewModel.TrimStart ?? 0) / duration, 0, 1);
        double end = Math.Clamp((_viewModel.TrimEnd ?? duration) / duration, 0, 1);
        TrimRange.Visibility = Visibility.Visible;
        Canvas.SetLeft(TrimRange, start * width);
        TrimRange.Width = Math.Max(2, (end - start) * width);

        // A tick at each end that was set.
        TrimStartMark.Visibility = _viewModel.TrimStart is not null ? Visibility.Visible : Visibility.Collapsed;
        TrimEndMark.Visibility = _viewModel.TrimEnd is not null ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(TrimStartMark, (start * width) - (TrimStartMark.Width / 2));
        Canvas.SetLeft(TrimEndMark, (end * width) - (TrimEndMark.Width / 2));
    }
}
