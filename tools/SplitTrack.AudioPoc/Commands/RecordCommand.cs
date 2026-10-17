using System.Diagnostics;
using System.Text;
using SplitTrack.AudioPoc.Metering;
using SplitTrack.Capture.Audio;
using SplitTrack.Capture.Audio.Sessions;
using SplitTrack.Capture.Audio.Timeline;
using SplitTrack.Capture.Audio.Wasapi;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Mux.Wav;

namespace SplitTrack.AudioPoc.Commands;

internal static class RecordCommand
{
    // The end must be set while still ahead of every track, which can lead the clock by its drift tolerance.
    private static readonly long StopLeadTicks = QpcClock.MillisecondsToTicks(100);

    // Lets packets captured before the end instant arrive before the streams close.
    private static readonly long StopGraceTicks = QpcClock.MillisecondsToTicks(150);

    public static int Run(RecordOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var tracks = new List<Track>();

        try
        {
            OpenTracks(options, stamp, tracks);
            PrintPlan(tracks);

            foreach (Track track in tracks)
            {
                track.Source.Start(track.Aligner);
            }

            long startQpc = QpcClock.Now;
            foreach (Track track in tracks)
            {
                track.Aligner.Arm(startQpc);
            }

            Log.Info($"Recording armed at qpc {startQpc}");
            long? plannedEndQpc = options.Seconds is { } s ? startQpc + (long)(s * QpcClock.TicksPerSecond) : null;
            if (plannedEndQpc is { } planned)
            {
                tracks.ForEach(t => t.Aligner.StopAt(planned));
            }

            long endQpc = WaitForStop(tracks, startQpc, plannedEndQpc);
            tracks.ForEach(t => t.Aligner.StopAt(endQpc));

            long waitTicks = endQpc + StopGraceTicks - QpcClock.Now;
            if (waitTicks > 0)
            {
                Thread.Sleep(TimeSpan.FromTicks(waitTicks));
            }

            foreach (Track track in tracks)
            {
                track.Source.Stop();
                track.Aligner.Finish();
                track.Wav.Dispose();
            }

            Log.Info($"Recording stopped after {QpcClock.TicksToMilliseconds(endQpc - startQpc):0} ms");
            PrintSummary(tracks);
            return tracks.Any(t => t.Source.Fault is not null) ? 3 : 0;
        }
        finally
        {
            foreach (Track track in tracks)
            {
                track.Dispose();
            }
        }
    }

    private static void OpenTracks(RecordOptions options, string stamp, List<Track> tracks)
    {
        ProcessTree tree = ProcessTree.Snapshot();
        IReadOnlyList<AudioProcessInfo> audio = AudioSessionScanner.Scan(tree);
        var usedPids = new HashSet<int>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string spec in options.Processes)
        {
            ProcessEntry target = ResolveProcess(spec, tree, audio);
            if (!usedPids.Add(target.Pid))
            {
                throw new UsageException($"--process {spec} resolves to pid {target.Pid}, which is already recorded.");
            }

            string exe = Path.GetFileNameWithoutExtension(target.ExeName);
            string tag = usedNames.Add(exe) ? exe : $"{exe}_{target.Pid}";
            var source = ProcessLoopbackSource.Open($"proc {tag}", target.Pid, includeTree: true, options.Format);
            tracks.Add(new Track(source, $"{target.ExeName} (pid {target.Pid} tree)", FilePath(options, stamp, $"proc_{tag}"), source.Format));
        }

        if (options.Exclude is { } excludeSpec)
        {
            ProcessEntry target = ResolveProcess(excludeSpec, tree, audio);
            var source = ProcessLoopbackSource.Open("system", target.Pid, includeTree: false, options.Format);
            tracks.Add(new Track(source, $"everything except {target.ExeName} (pid {target.Pid} tree)", FilePath(options, stamp, "system"), source.Format));
        }

        if (options.SystemAll)
        {
            AudioDeviceInfo device = AudioDeviceEnumerator.GetDefault(AudioDeviceFlow.Render)
                ?? throw new InvalidOperationException("There is no default output device.");
            var source = DeviceCaptureSource.OpenRenderLoopback("sysall", device);
            tracks.Add(new Track(source, $"loopback of '{device.Name}'", FilePath(options, stamp, "sysall"), source.Format));
        }

        if (options.Mic)
        {
            AudioDeviceInfo device = AudioDeviceEnumerator.Find(AudioDeviceFlow.Capture, options.MicFilter)
                ?? throw new UsageException(options.MicFilter is null
                    ? "There is no default microphone."
                    : $"No capture device name contains '{options.MicFilter}'. Run 'list' to see them.");
            var source = DeviceCaptureSource.OpenCapture("mic", device);
            tracks.Add(new Track(source, $"mic '{device.Name}'", FilePath(options, stamp, "mic"), source.Format));
        }
    }

    /// <summary>
    /// A PID is used as given. An exe name resolves to its root process; with several roots the one
    /// owning the most audio sessions wins, then the oldest.
    /// </summary>
    private static ProcessEntry ResolveProcess(string spec, ProcessTree tree, IReadOnlyList<AudioProcessInfo> audio)
    {
        if (int.TryParse(spec, out int pid))
        {
            return tree.Get(pid) ?? throw new UsageException($"No running process has pid {pid}.");
        }

        List<int> roots = tree.FindByExe(spec).Select(p => tree.FindRoot(p.Pid)).Distinct().ToList();
        if (roots.Count == 0)
        {
            throw new UsageException($"No running process is named '{ProcessTree.NormalizeExeName(spec)}'. Start it first, or run 'list'.");
        }

        int chosen = roots
            .OrderByDescending(r => audio.FirstOrDefault(a => a.RootPid == r)?.SessionPids.Count ?? 0)
            .ThenBy(r => tree.GetCreationTime(r) ?? long.MaxValue)
            .First();

        if (roots.Count > 1)
        {
            Log.Warn($"'{spec}' has {roots.Count} separate process trees ({string.Join(", ", roots)}); recording pid {chosen}");
        }

        return tree.Get(chosen)!;
    }

    private static string FilePath(RecordOptions options, string stamp, string suffix)
    {
        string safe = string.Concat(suffix.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(options.OutputDirectory, $"{stamp}_{safe}.wav");
    }

    private static void PrintPlan(List<Track> tracks)
    {
        Console.WriteLine($"Recording {tracks.Count} track(s):");
        foreach (Track t in tracks)
        {
            Console.WriteLine($"  {t.Source.Name,-20} {t.Description}");
            Console.WriteLine($"  {string.Empty,-20} {t.Source.Format} -> {t.Wav.Path}");
        }

        Console.WriteLine();
    }

    /// <summary>Returns the end instant: the limit when reached, otherwise slightly after the stop request.</summary>
    private static long WaitForStop(List<Track> tracks, long startQpc, long? limitQpc)
    {
        using var stop = new ManualResetEventSlim();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        Console.CancelKeyPress += onCancel;

        bool interactive = !Console.IsOutputRedirected;
        bool readKeys = !Console.IsInputRedirected;
        Console.WriteLine(limitQpc is { } l
            ? $"Recording for {QpcClock.TicksToMilliseconds(l - startQpc) / 1000:0.#} s. Press Enter or Ctrl+C to stop early."
            : "Recording. Press Enter or Ctrl+C to stop.");

        // Warnings would break the redrawn status block; they still reach the log file.
        LogLevel previousLevel = Log.ConsoleLevel;
        Log.ConsoleLevel = LogLevel.None;
        var status = new StatusDisplay(interactive);
        var lastStatus = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                long now = QpcClock.Now;
                if (now >= limitQpc)
                {
                    return limitQpc.Value;
                }

                int waitMs = limitQpc is { } limit ? (int)Math.Clamp((limit - now) / 10_000 + 1, 1, 100) : 100;
                if (stop.Wait(waitMs) ||
                    (readKeys && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter))
                {
                    long end = QpcClock.Now + StopLeadTicks;
                    return limitQpc is { } limitEnd ? Math.Min(end, limitEnd) : end;
                }

                if (lastStatus.ElapsedMilliseconds >= 1000)
                {
                    lastStatus.Restart();
                    status.Render(BuildStatus(tracks, QpcClock.TicksToMilliseconds(QpcClock.Now - startQpc) / 1000));
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            Log.ConsoleLevel = previousLevel;
        }
    }

    private static List<string> BuildStatus(List<Track> tracks, double elapsedSeconds)
    {
        var lines = new List<string> { $"  {TimeSpan.FromSeconds(elapsedSeconds):mm\\:ss}" };
        foreach (Track t in tracks)
        {
            AlignerStats stats = t.Aligner.GetStats();
            int rate = t.Source.Format.SampleRate;
            double db = PeakMeter.ToDbfs(t.Output.Meter.TakePeak());
            lines.Add(
                $"  {t.Source.Name,-18} {Bar(db)} {(db <= -120 ? "  -inf" : $"{db,6:0.0}")} dB" +
                $"  pkts {stats.Packets,6}" +
                $"  pad {FramesToMs(stats.PaddedFrames, rate),7:0} ms" +
                $"  drop {FramesToMs(stats.DroppedFrames, rate),5:0} ms" +
                $"  drift {SignedMs(stats.LastDriftTicks),6} ms" +
                (t.Source.Fault is null ? string.Empty : "  FAULT"));
        }

        return lines;
    }

    private static string Bar(double db)
    {
        const int width = 20;
        int filled = (int)Math.Round(Math.Clamp((db + 60) / 60, 0, 1) * width);
        return $"[{new string('#', filled)}{new string('.', width - filled)}]";
    }

    private static void PrintSummary(List<Track> tracks)
    {
        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  {"Track",-18} {"Seconds",9} {"Packets",8} {"Silent",7} {"Padded",10} {"Dropped",9} {"Disc",5} {"TsErr",6} {"MaxDrift",9}");
        foreach (Track t in tracks)
        {
            AlignerStats s = t.Aligner.GetStats();
            int rate = t.Source.Format.SampleRate;
            Console.WriteLine(
                $"  {t.Source.Name,-18} {(double)t.Wav.Frames / rate,9:0.000} {s.Packets,8} {s.SilentPackets,7} " +
                $"{FramesToMs(s.PaddedFrames, rate),7:0} ms {FramesToMs(s.DroppedFrames, rate),6:0} ms " +
                $"{s.Discontinuities,5} {s.TimestampErrors,6} {QpcClock.TicksToMilliseconds(s.MaxAbsDriftTicks),6:0.0} ms");
            if (t.Source.Fault is { } fault)
            {
                Console.WriteLine($"  {string.Empty,-18} FAULT: {fault.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Files:");
        foreach (Track t in tracks)
        {
            Console.WriteLine($"  {t.Wav.Path}");
        }

        if (Log.FilePath is not null)
        {
            Console.WriteLine($"Log: {Log.FilePath}");
        }
    }

    private static double FramesToMs(long frames, int rate) => frames * 1000.0 / rate;

    private static string SignedMs(long ticks)
    {
        double ms = Math.Round(QpcClock.TicksToMilliseconds(ticks), 1);
        return ms < 0 ? $"-{-ms:0.0}" : $"+{ms:0.0}";
    }

    private sealed class Track : IDisposable
    {
        public Track(IAudioSource source, string description, string path, AudioFormat format)
        {
            Source = source;
            Description = description;
            Wav = new WavWriter(path, format);
            Output = new TrackOutput(Wav, new PeakMeter(format.SampleType));
            Aligner = new AudioTrackAligner(format, Output);
        }

        public IAudioSource Source { get; }

        public string Description { get; }

        public WavWriter Wav { get; }

        public TrackOutput Output { get; }

        public AudioTrackAligner Aligner { get; }

        public void Dispose()
        {
            Source.Dispose();
            Wav.Dispose();
        }
    }

    private sealed class TrackOutput(WavWriter wav, PeakMeter meter) : IAudioFrameWriter
    {
        public PeakMeter Meter => meter;

        public void WriteFrames(ReadOnlySpan<byte> data, int frames)
        {
            meter.Process(data);
            wav.Write(data);
        }

        public void WriteSilence(long frames) => wav.WriteSilence(frames);
    }

    /// <summary>Redraws a block of lines in place on a console, or prints every 5 s when redirected.</summary>
    private sealed class StatusDisplay(bool interactive)
    {
        private int _top = -1;
        private int _renders;

        public void Render(List<string> lines)
        {
            if (!interactive)
            {
                if (_renders++ % 5 == 0)
                {
                    lines.ForEach(Console.WriteLine);
                }

                return;
            }

            int width = Math.Max(20, Console.WindowWidth - 1);
            var block = new StringBuilder();
            foreach (string line in lines)
            {
                block.AppendLine(line.Length > width ? line[..width] : line.PadRight(width));
            }

            if (_top >= 0)
            {
                Console.SetCursorPosition(0, _top);
            }

            Console.Write(block);
            _top = Console.CursorTop - lines.Count;
        }
    }
}
