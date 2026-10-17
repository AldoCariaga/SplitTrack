using SplitTrack.Capture.Video;
using SplitTrack.Encode.Video;

namespace SplitTrack.VideoPoc.Commands;

internal static class ListCommand
{
    private static readonly VideoEncoderSettings TestSettings = new(1280, 720, 60, 6_000);

    public static int Run()
    {
        IReadOnlyList<GpuAdapterInfo> adapters = GraphicsDevice.ListAdapters();
        Console.WriteLine("GPUs:");
        foreach (GpuAdapterInfo a in adapters)
        {
            Console.WriteLine($"  {a.Index}  {a.Name} ({a.VendorName}, {a.DedicatedMemoryBytes / (1024 * 1024)} MB){(a.IsSoftware ? "  software" : string.Empty)}");
        }

        Console.WriteLine();
        Console.WriteLine("Monitors:");
        foreach (MonitorInfo m in CaptureTargets.ListMonitors())
        {
            Console.WriteLine($"  {m.Index}  {m.DeviceName,-14} {m.Width}x{m.Height} at ({m.Left},{m.Top}){(m.IsPrimary ? "  primary" : string.Empty)}");
        }

        Console.WriteLine();
        Console.WriteLine("Windows:");
        Console.WriteLine($"  {"HWND",-10} {"PID",7}  {"Exe",-24} {"Size",-11} Title");
        foreach (WindowInfo w in CaptureTargets.ListWindows())
        {
            string title = w.Title.Length > 60 ? w.Title[..57] + "..." : w.Title;
            Console.WriteLine($"  0x{w.Handle:X8} {w.Pid,7}  {w.ExeName,-24} {$"{w.Width}x{w.Height}",-11} {title}");
        }

        Console.WriteLine();
        using GraphicsDevice device = GraphicsDevice.Create();
        Console.WriteLine($"H.264 encoders on GPU 0 ({TestSettings.Width}x{TestSettings.Height} test):");
        var working = new HashSet<VideoEncoderKind>();
        foreach (VideoEncoderKind kind in VideoEncoderKinds.All)
        {
            string? error = VideoEncoder.Test(device.Device, kind, TestSettings);
            if (error is null)
            {
                working.Add(kind);
            }

            Console.WriteLine($"  {kind.ShortName(),-6} {kind.CodecName(),-11} {(error is null ? "OK" : $"unavailable: {error}")}");
        }

        Console.WriteLine();
        VideoEncoderKind? chosen = VideoEncoderKinds.AutoOrder(device.Adapter.VendorId)
            .Cast<VideoEncoderKind?>()
            .FirstOrDefault(k => working.Contains(k!.Value));
        Console.WriteLine(chosen is { } c ? $"--encoder auto picks: {c.ShortName()}" : "No encoder works on this PC.");
        return 0;
    }
}
