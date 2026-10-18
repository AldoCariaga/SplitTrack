using System.Diagnostics;
using SplitTrack.Capture.Video;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Encode.Video;
using SplitTrack.Engine.Recording;
using SplitTrack.FFmpeg;

namespace SplitTrack.Engine.Video;

/// <summary>The video half of a recording: D3D11 device, WGC capture, NV12 conversion, H.264 encoder and the CFR loop.</summary>
internal sealed class VideoRecorder : IDisposable
{
    // Monitor capture delivers a frame at once; a window that is not redrawing may not.
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(1);

    private readonly GraphicsDevice _device;
    private readonly WgcCaptureSource _capture;
    private readonly Nv12Converter _converter;
    private readonly VideoEncoder _encoder;
    private VideoPipeline? _pipeline;

    private VideoRecorder(GraphicsDevice device, WgcCaptureSource capture, Nv12Converter converter, VideoEncoder encoder)
    {
        _device = device;
        _capture = capture;
        _converter = converter;
        _encoder = encoder;
    }

    public string Description =>
        $"{_capture.Name} on {_device.Adapter.Name}: {_capture.Width}x{_capture.Height} -> {Settings.Width}x{Settings.Height} @ {Settings.Fps} fps, " +
        $"{Settings.BitrateKbps / 1000.0:0.#} Mbps, {_encoder.Kind.ShortName()} ({_encoder.Kind.CodecName()})";

    public VideoEncoderSettings Settings => _encoder.Settings;

    public VideoEncoderKind EncoderKind => _encoder.Kind;

    /// <summary>The captured window closed or its monitor went away.</summary>
    public bool IsClosed => _capture.IsClosed;

    /// <summary>The encode loop ended: its end was reached, or it failed (<see cref="Fault"/>).</summary>
    public bool HasEnded => _pipeline is { IsRunning: false };

    public Exception? Fault => _pipeline?.Fault;

    public static VideoRecorder Open(VideoOptions options)
    {
        if (!WgcCaptureSource.IsSupported)
        {
            throw new RecordingSetupException("Windows Graphics Capture is not available on this PC.");
        }

        var disposables = new Stack<IDisposable>();
        try
        {
            GraphicsDevice device = Push(disposables, GraphicsDevice.Create(options.Adapter));
            WgcCaptureSource capture = Push(disposables, OpenCapture(options, device));
            (int width, int height) = VideoEncoderSettings.ScaledSize(capture.Width, capture.Height, options.MaxHeight);
            var settings = new VideoEncoderSettings(width, height, options.Fps, options.BitrateKbps);
            VideoEncoderKind kind = options.Encoder ?? VideoEncoder.SelectAuto(device.Device, device.Adapter.VendorId, settings);
            Nv12Converter converter = Push(disposables, new Nv12Converter(device.Device, capture.Width, capture.Height, width, height, options.Fps));
            VideoEncoder encoder = Push(disposables, VideoEncoder.Open(device.Device, kind, settings));
            return new VideoRecorder(device, capture, converter, encoder);
        }
        catch
        {
            while (disposables.TryPop(out IDisposable? item))
            {
                item.Dispose();
            }

            throw;
        }
    }

    public MediaStreamInfo CreateStreamInfo(string title) => _encoder.CreateStreamInfo(title);

    /// <summary>Starts capturing and waits briefly for the first frame, so the recording does not open on black.</summary>
    public void StartCapture()
    {
        _capture.Start();
        var watch = Stopwatch.StartNew();
        while (_capture.GetStats().Frames == 0 && watch.Elapsed < FirstFrameTimeout)
        {
            Thread.Sleep(5);
        }

        if (_capture.GetStats().Frames == 0)
        {
            Log.Warn("No frame within 1 s of starting capture; recording starts on black");
        }
    }

    public void StartEncoding(long startQpc, Action<MediaPacket> output)
    {
        _pipeline = new VideoPipeline(_capture, _converter, _encoder, output);
        _pipeline.Start(startQpc);
    }

    public void StopAt(long endQpc) => _pipeline?.StopAt(endQpc);

    /// <summary>Waits for the encode loop to pass its end and drain the encoder.</summary>
    public void Join() => _pipeline?.Join();

    public VideoStatus GetStatus() =>
        new(_capture.Name, _pipeline?.GetStats() ?? default, _capture.GetStats(), Settings.Fps, Fault);

    public void Dispose()
    {
        _pipeline?.StopAt(long.MinValue);
        _pipeline?.Join();
        _encoder.Dispose();
        _converter.Dispose();
        _capture.Dispose();
        _device.Dispose();
    }

    private static WgcCaptureSource OpenCapture(VideoOptions options, GraphicsDevice device)
    {
        if (options.Window is { } spec)
        {
            WindowInfo window = CaptureTargets.FindWindow(spec)
                ?? throw new RecordingSetupException($"No open window matches '{spec}' (minimised windows cannot be captured).");
            return WgcCaptureSource.ForWindow(device, window, options.Cursor);
        }

        IReadOnlyList<MonitorInfo> monitors = CaptureTargets.ListMonitors();
        MonitorInfo monitor = options.Monitor < monitors.Count
            ? monitors[options.Monitor]
            : throw new RecordingSetupException($"There is no monitor {options.Monitor}; this PC has {monitors.Count}.");
        return WgcCaptureSource.ForMonitor(device, monitor, options.Cursor);
    }

    private static T Push<T>(Stack<IDisposable> stack, T item)
        where T : IDisposable
    {
        stack.Push(item);
        return item;
    }
}
