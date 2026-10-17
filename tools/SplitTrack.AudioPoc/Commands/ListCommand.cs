using SplitTrack.Capture.Audio.Sessions;
using SplitTrack.Capture.Audio.Wasapi;

namespace SplitTrack.AudioPoc.Commands;

internal static class ListCommand
{
    public static int Run()
    {
        ProcessTree tree = ProcessTree.Snapshot();
        IReadOnlyList<AudioProcessInfo> processes = AudioSessionScanner.Scan(tree);

        Console.WriteLine("Audio processes (render sessions, grouped by root process):");
        if (processes.Count == 0)
        {
            Console.WriteLine("  (none - start something that plays sound)");
        }
        else
        {
            Console.WriteLine($"  {"Root PID",-9} {"Exe",-28} {"State",-8} {"Session PIDs",-24} Output device(s)");
            foreach (AudioProcessInfo p in processes)
            {
                string pids = string.Join(",", p.SessionPids);
                Console.WriteLine(
                    $"  {p.RootPid,-9} {Truncate(p.ExeName, 28),-28} {(p.IsActive ? "active" : "idle"),-8} " +
                    $"{Truncate(pids, 24),-24} {string.Join("; ", p.DeviceNames)}");
            }
        }

        Console.WriteLine();
        PrintDevices("Capture devices (--mic):", AudioDeviceFlow.Capture);
        Console.WriteLine();
        PrintDevices("Render devices (--system-all uses the default):", AudioDeviceFlow.Render);
        return 0;
    }

    private static void PrintDevices(string title, AudioDeviceFlow flow)
    {
        Console.WriteLine(title);
        IReadOnlyList<AudioDeviceInfo> devices = AudioDeviceEnumerator.List(flow);
        if (devices.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        foreach (AudioDeviceInfo d in devices)
        {
            string roles = (d.IsDefault, d.IsDefaultCommunications) switch
            {
                (true, true) => "default, communications",
                (true, false) => "default",
                (false, true) => "communications",
                _ => string.Empty,
            };
            Console.WriteLine($"  {(d.IsDefault ? '*' : ' ')} {d.Name}{(roles.Length > 0 ? $"  [{roles}]" : string.Empty)}");
        }
    }

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : string.Concat(text.AsSpan(0, width - 1), "~");
}
