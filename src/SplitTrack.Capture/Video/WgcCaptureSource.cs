using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace SplitTrack.Capture.Video;

/// <summary>Latency = arrival minus the frame's own QPC timestamp; Min/Max are 0 until a frame arrives.</summary>
public readonly record struct CaptureStats(long Frames, long LastFrameQpc, long MinLatencyTicks, long MaxLatencyTicks, int Resizes);

/// <summary>
/// Windows Graphics Capture of one monitor or window. Every arriving frame is copied into a
/// fixed-size BGRA canvas and released at once; readers sample the canvas through
/// <see cref="ReadCanvas"/>. A window that changes size is cropped or padded with black.
/// </summary>
public sealed class WgcCaptureSource : IDisposable
{
    private const int PoolBuffers = 2;

    private readonly GraphicsDevice _device;
    private readonly nint _window;
    private readonly nint _monitor;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly ID3D11Texture2D _canvas;
    private readonly ID3D11RenderTargetView _canvasTarget;
    private readonly Lock _gate = new();

    private SizeInt32 _poolSize;
    private SizeInt32 _lastContentSize;
    private long _frames;
    private long _lastFrameQpc;
    private long _minLatencyTicks = long.MaxValue;
    private long _maxLatencyTicks = long.MinValue;
    private int _resizes;
    private bool _disposed;

    private WgcCaptureSource(GraphicsDevice device, GraphicsCaptureItem item, nint window, nint monitor, string name, bool captureCursor)
    {
        _device = device;
        _window = window;
        _monitor = monitor;
        _item = item;
        Name = name;

        // NV12 needs even dimensions.
        Width = Math.Max(2, item.Size.Width & ~1);
        Height = Math.Max(2, item.Size.Height & ~1);

        _canvas = device.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)Width,
            (uint)Height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        _canvasTarget = device.Device.CreateRenderTargetView(_canvas);
        device.Device.ImmediateContext.ClearRenderTargetView(_canvasTarget, new Color4(0, 0, 0, 1));

        _poolSize = item.Size;
        _lastContentSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, _poolSize);
        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = captureCursor;
        TryDisableBorder(_session);
    }

    public string Name { get; }

    /// <summary>Canvas size: the item's size when capture started, rounded down to even.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// The window was destroyed or the monitor unplugged. Polled, because GraphicsCaptureItem.Closed
    /// is only raised on a dispatcher thread, which a console app does not run.
    /// </summary>
    public unsafe bool IsClosed
    {
        get
        {
            if (_window != 0)
            {
                return !PInvoke.IsWindow((HWND)_window);
            }

            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            return !PInvoke.GetMonitorInfo((HMONITOR)_monitor, &info);
        }
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public static WgcCaptureSource ForMonitor(GraphicsDevice device, MonitorInfo monitor, bool captureCursor = true) =>
        new(device, Direct3DInterop.CreateItemForMonitor(monitor.Handle), 0, monitor.Handle,
            $"monitor {monitor.Index} ({monitor.DeviceName})", captureCursor);

    public static WgcCaptureSource ForWindow(GraphicsDevice device, WindowInfo window, bool captureCursor = true) =>
        new(device, Direct3DInterop.CreateItemForWindow(window.Handle), window.Handle, 0,
            $"window '{window.Title}' ({window.ExeName})", captureCursor);

    public void Start()
    {
        _session.StartCapture();
        Log.Info($"Capture started: {Name}, {Width}x{Height}");
    }

    /// <summary>Runs <paramref name="read"/> on the canvas while no frame is being copied into it.</summary>
    public void ReadCanvas<TState>(TState state, Action<ID3D11Texture2D, TState> read)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            read(_canvas, state);
        }
    }

    public CaptureStats GetStats()
    {
        lock (_gate)
        {
            return _frames == 0
                ? default
                : new CaptureStats(_frames, _lastFrameQpc, _minLatencyTicks, _maxLatencyTicks, _resizes);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        _canvasTarget.Dispose();
        _canvas.Dispose();
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        try
        {
            // Windows 11 lets unpackaged apps drop the yellow capture border.
            _ = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Result;
            session.IsBorderRequired = false;
        }
        catch (Exception ex)
        {
            Log.Debug($"Capture border stays on: {ex.Message}");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            long arrivedQpc = QpcClock.Now;
            long frameQpc = frame.SystemRelativeTime.Ticks;
            SizeInt32 contentSize = frame.ContentSize;

            using (ID3D11Texture2D texture = Direct3DInterop.GetTexture(frame.Surface))
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    CopyToCanvas(texture, contentSize);
                    _frames++;
                    _lastFrameQpc = frameQpc;
                    _minLatencyTicks = Math.Min(_minLatencyTicks, arrivedQpc - frameQpc);
                    _maxLatencyTicks = Math.Max(_maxLatencyTicks, arrivedQpc - frameQpc);
                }
            }

            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                // The next frames arrive at the new size; this one was already copied cropped.
                _poolSize = contentSize;
                sender.Recreate(_device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, contentSize);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Capture frame dropped ({Name})", ex);
        }
    }

    private void CopyToCanvas(ID3D11Texture2D texture, SizeInt32 contentSize)
    {
        ID3D11DeviceContext context = _device.Device.ImmediateContext;
        if (contentSize.Width != _lastContentSize.Width || contentSize.Height != _lastContentSize.Height)
        {
            // Smaller content would leave the old picture around it.
            context.ClearRenderTargetView(_canvasTarget, new Color4(0, 0, 0, 1));
            _lastContentSize = contentSize;
            _resizes++;
            Log.Info($"Capture size changed to {contentSize.Width}x{contentSize.Height} ({Name}); canvas stays {Width}x{Height}");
        }

        Texture2DDescription desc = texture.Description;
        int width = Math.Min(Math.Min(contentSize.Width, (int)desc.Width), Width);
        int height = Math.Min(Math.Min(contentSize.Height, (int)desc.Height), Height);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        context.CopySubresourceRegion(_canvas, 0, 0, 0, 0, texture, 0, new Box(0, 0, 0, width, height, 1));
    }
}
