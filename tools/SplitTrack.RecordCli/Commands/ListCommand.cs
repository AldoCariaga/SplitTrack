using SplitTrack.Capture.Audio.Sessions;
using SplitTrack.Capture.Audio.Wasapi;
using SplitTrack.Capture.Video;
using SplitTrack.Encode.Video;

namespace SplitTrack.RecordCli.Commands;

internal static class ListCommand
{
    private static readonly VideoEncoderSettings TestSettings = new(1280, 720, 60, 6_000);

    public static int Run()
    {
        Console.WriteLine("Audio processes (--game / --app):");
        IReadOnlyList<AudioProcessInfo> processes = AudioSessionScanner.Scan();
        if (processes.Count == 0)
        {
            Console.WriteLine("  (none - start something that plays sound)");
        }

        foreach (AudioProcessInfo p in processes)
        {
            Console.WriteLine($"  {p.RootPid,-8} {p.ExeName,-28} {(p.IsActive ? "active" : "idle"),-7} {string.Join("; ", p.DeviceNames)}");
        }

        Console.WriteLine();
        Console.WriteLine("Microphones (--mic [name]):");
        foreach (AudioDeviceInfo d in AudioDeviceEnumerator.List(AudioDeviceFlow.Capture))
        {
            Console.WriteLine($"  {(d.IsDefault ? '*' : ' ')} {d.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("GPUs (--adapter):");
        foreach (GpuAdapterInfo a in GraphicsDevice.ListAdapters())
        {
            Console.WriteLine($"  {a.Index}  {a.Name} ({a.VendorName}){(a.IsSoftware ? "  software" : string.Empty)}");
        }

        Console.WriteLine();
        Console.WriteLine("Monitors (--monitor):");
        foreach (MonitorInfo m in CaptureTargets.ListMonitors())
        {
            Console.WriteLine($"  {m.Index}  {m.DeviceName,-14} {m.Width}x{m.Height}{(m.IsPrimary ? "  primary" : string.Empty)}");
        }

        Console.WriteLine();
        Console.WriteLine("Windows (--window):");
        foreach (WindowInfo w in CaptureTargets.ListWindows())
        {
            string title = w.Title.Length > 60 ? w.Title[..57] + "..." : w.Title;
            Console.WriteLine($"  0x{w.Handle:X8} {w.ExeName,-24} {$"{w.Width}x{w.Height}",-11} {title}");
        }

        Console.WriteLine();
        using GraphicsDevice device = GraphicsDevice.Create();
        Console.WriteLine("H.264 encoders on GPU 0 (--encoder):");
        foreach (VideoEncoderKind kind in VideoEncoderKinds.All)
        {
            string? error = VideoEncoder.Test(device.Device, kind, TestSettings);
            Console.WriteLine($"  {kind.ShortName(),-6} {(error is null ? "OK" : $"unavailable: {error}")}");
        }

        return 0;
    }
}
