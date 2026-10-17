using System.Diagnostics;
using SplitTrack.Capture.Video;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Encode.Video;
using SplitTrack.Engine.Video;
using SplitTrack.Mux.Mkv;

namespace SplitTrack.VideoPoc.Commands;

internal static class RecordCommand
{
    // Monitor capture delivers a frame at once; a window that is not redrawing may not.
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(1);

    public static int Run(RecordOptions options)
    {
        if (!WgcCaptureSource.IsSupported)
        {
            Console.Error.WriteLine("Windows Graphics Capture is not available on this PC.");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        string path = Path.Combine(options.OutputDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss}_video.mkv");

        using GraphicsDevice device = GraphicsDevice.Create(options.Adapter);
        using WgcCaptureSource capture = OpenCapture(options, device);
        (int width, int height) = VideoEncoderSettings.ScaledSize(capture.Width, capture.Height, options.Height);
        var settings = new VideoEncoderSettings(width, height, options.Fps, options.BitrateKbps);

        VideoEncoderKind kind = options.Encoder ?? VideoEncoder.SelectAuto(device.Device, device.Adapter.VendorId, settings);
        using var converter = new Nv12Converter(device.Device, capture.Width, capture.Height, width, height, options.Fps);
        using VideoEncoder encoder = VideoEncoder.Open(device.Device, kind, settings);
        using var mkv = new MkvWriter(path);
        int stream;
        using (var info = encoder.CreateStreamInfo("Video"))
        {
            stream = mkv.AddStream(info);
        }

        mkv.Start();

        Console.WriteLine($"Capturing {capture.Name} on {device.Adapter.Name}");
        Console.WriteLine($"  {capture.Width}x{capture.Height} -> {width}x{height} @ {options.Fps} fps, " +
                          $"{options.BitrateKbps / 1000.0:0.#} Mbps, {kind.ShortName()} ({kind.CodecName()})");
        Console.WriteLine($"  -> {path}");
        Console.WriteLine();

        var pipeline = new VideoPipeline(capture, converter, encoder, packet => mkv.Write(stream, packet));
        capture.Start();
        WaitForFirstFrame(capture);

        long startQpc = QpcClock.Now;
        long? limitQpc = options.Seconds is { } s ? startQpc + (long)(s * QpcClock.TicksPerSecond) : null;
        if (limitQpc is { } limit)
        {
            pipeline.StopAt(limit);
        }

        pipeline.Start(startQpc);
        long endQpc = WaitForStop(pipeline, capture, startQpc, limitQpc);
        pipeline.StopAt(endQpc);
        pipeline.Join();
        mkv.Close();

        PrintSummary(pipeline, capture, mkv, startQpc, endQpc, options.Fps);
        return pipeline.Fault is not null || mkv.WriteError is not null ? 3 : 0;
    }

    private static WgcCaptureSource OpenCapture(RecordOptions options, GraphicsDevice device)
    {
        if (options.Window is { } spec)
        {
            WindowInfo window = CaptureTargets.FindWindow(spec)
                ?? throw new UsageException($"No capturable window matches '{spec}'. Run 'list' to see them.");
            return WgcCaptureSource.ForWindow(device, window, options.Cursor);
        }

        int index = options.Monitor ?? 0;
        IReadOnlyList<MonitorInfo> monitors = CaptureTargets.ListMonitors();
        MonitorInfo monitor = index < monitors.Count
            ? monitors[index]
            : throw new UsageException($"There is no monitor {index}; this PC has {monitors.Count}.");
        return WgcCaptureSource.ForMonitor(device, monitor, options.Cursor);
    }

    private static void WaitForFirstFrame(WgcCaptureSource capture)
    {
        var watch = Stopwatch.StartNew();
        while (capture.GetStats().Frames == 0 && watch.Elapsed < FirstFrameTimeout)
        {
            Thread.Sleep(5);
        }

        if (capture.GetStats().Frames == 0)
        {
            Log.Warn("No frame within 1 s of starting capture; recording starts on black");
        }
    }

    /// <summary>Returns the end instant: the limit when reached, otherwise the moment of the stop request.</summary>
    private static long WaitForStop(VideoPipeline pipeline, WgcCaptureSource capture, long startQpc, long? limitQpc)
    {
        using var stop = new ManualResetEventSlim();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        Console.CancelKeyPress += onCancel;

        bool readKeys = !Console.IsInputRedirected;
        Console.WriteLine(limitQpc is { } l
            ? $"Recording for {QpcClock.TicksToMilliseconds(l - startQpc) / 1000:0.#} s. Press Enter or Ctrl+C to stop early."
            : "Recording. Press Enter or Ctrl+C to stop.");

        LogLevel previousLevel = Log.ConsoleLevel;
        Log.ConsoleLevel = LogLevel.None;
        var status = new StatusDisplay(!Console.IsOutputRedirected);
        var lastStatus = Stopwatch.StartNew();
        PipelineStats lastStats = default;
        long lastFrames = 0;

        try
        {
            while (true)
            {
                long now = QpcClock.Now;
                if (now >= limitQpc)
                {
                    return limitQpc.Value;
                }

                // The loop ends early only on a fault, or after its last frame before the limit.
                if (!pipeline.IsRunning)
                {
                    return pipeline.Fault is null && limitQpc is { } reached ? reached : now;
                }

                if (stop.Wait(50) || capture.IsClosed ||
                    (readKeys && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter))
                {
                    long end = QpcClock.Now;
                    return limitQpc is { } limit ? Math.Min(end, limit) : end;
                }

                if (lastStatus.ElapsedMilliseconds >= 1000)
                {
                    double seconds = lastStatus.Elapsed.TotalSeconds;
                    lastStatus.Restart();
                    PipelineStats stats = pipeline.GetStats();
                    long frames = capture.GetStats().Frames;
                    status.Render(
                    [
                        $"  {TimeSpan.FromTicks(QpcClock.Now - startQpc):mm\\:ss}" +
                        $"  captured {(frames - lastFrames) / seconds,5:0.0} fps" +
                        $"  encoded {stats.Encoded,6}  repeated {stats.Duplicated,6}  skipped {stats.Skipped,4}" +
                        $"  encode {(stats.Encoded - lastStats.Encoded > 0 ? (stats.EncodeTotalMs - lastStats.EncodeTotalMs) / (stats.Encoded - lastStats.Encoded) : 0),5:0.00} ms" +
                        $"  {(stats.Bytes - lastStats.Bytes) * 8 / seconds / 1_000_000,5:0.0} Mbps",
                    ]);
                    lastStats = stats;
                    lastFrames = frames;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            Log.ConsoleLevel = previousLevel;
        }
    }

    private static void PrintSummary(VideoPipeline pipeline, WgcCaptureSource capture, MkvWriter mkv, long startQpc, long endQpc, int fps)
    {
        PipelineStats stats = pipeline.GetStats();
        CaptureStats captured = capture.GetStats();
        double seconds = QpcClock.TicksToMilliseconds(endQpc - startQpc) / 1000;
        long expected = QpcClock.TicksToFrames(endQpc - startQpc - 1, fps) + 1;

        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  Duration        {seconds:0.000} s ({expected} frame slots at {fps} fps)");
        Console.WriteLine($"  Encoded         {stats.Encoded} frames, {stats.Duplicated} repeats of an unchanged screen, {stats.Skipped} skipped (late)");
        Console.WriteLine($"  Captured        {captured.Frames} frames from WGC, latency {QpcClock.TicksToMilliseconds(captured.MinLatencyTicks):0.0} to {QpcClock.TicksToMilliseconds(captured.MaxLatencyTicks):0.0} ms, {captured.Resizes} resize(s)");
        Console.WriteLine($"  Encode time     avg {(stats.Encoded > 0 ? stats.EncodeTotalMs / stats.Encoded : 0):0.00} ms, max {stats.EncodeMaxMs:0.00} ms per frame");
        Console.WriteLine($"  File            {mkv.BytesWritten / 1024.0 / 1024.0:0.0} MB, {mkv.BytesWritten * 8 / Math.Max(seconds, 0.001) / 1_000_000:0.0} Mbps average");
        if (pipeline.Fault is { } fault)
        {
            Console.WriteLine($"  FAULT           {fault.Message}");
        }

        if (mkv.WriteError is { } writeError)
        {
            Console.WriteLine($"  WRITE ERROR     {writeError.Message}");
        }

        if (capture.IsClosed)
        {
            Console.WriteLine("  Stopped because the captured window closed.");
        }

        Console.WriteLine();
        Console.WriteLine($"File: {mkv.Path}");
        if (Log.FilePath is not null)
        {
            Console.WriteLine($"Log: {Log.FilePath}");
        }
    }
}
