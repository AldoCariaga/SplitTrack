using System.Collections.Concurrent;
using System.Diagnostics;
using SplitTrack.Capture.Audio.Timeline;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using SplitTrack.Engine.Input;
using SplitTrack.Engine.Recording;
using SplitTrack.Engine.Replay;
using SplitTrack.Engine.Video;
using SplitTrack.Library;

namespace SplitTrack.RecordCli.Commands;

internal static class RecordCommand
{
    private const int AudioRate = 48_000;

    public static int Run(RecordOptions options)
    {
        var library = new LibraryFolder(options.OutputDirectory);
        string? path = !options.Replay || options.AlsoRecord ? library.NewEntry(DateTime.Now, "rec").Video : null;
        var recording = new RecordingOptions(
            path,
            options.Video,
            options.AudioTracks,
            options.AudioBitrateKbps,
            options.Replay ? new ReplayOptions(options.BufferSeconds, library.Root) : null);

        // Registered first: a hotkey another program owns should fail before capture starts.
        RecordingSession? session = null;
        using GlobalHotkey? hotkey = options.Replay ? GlobalHotkey.Register(options.Hotkey, qpc => session?.SaveClip(qpc)) : null;

        var clips = new ConcurrentQueue<ClipResult>();
        using (session = RecordingSession.Open(recording))
        {
            session.ClipSaved += clips.Enqueue;
            PrintPlan(session, recording, hotkey?.Gesture, library);
            session.Start();

            long? limitQpc = options.Seconds is { } s ? session.StartQpc + (long)(s * QpcClock.TicksPerSecond) : null;
            if (limitQpc is { } limit)
            {
                session.StopAt(limit);
            }

            WaitForStop(session, limitQpc, clips);
            session.Complete();

            SessionStatus status = session.GetStatus();
            PrintSummary(session, status, clips.ToList());
            bool failed = status.WriteError is not null || status.Video?.Fault is not null || clips.Any(c => c.Error is not null);
            return failed ? 3 : 0;
        }
    }

    private static void PrintPlan(RecordingSession session, RecordingOptions recording, HotkeyGesture? hotkey, LibraryFolder library)
    {
        Console.WriteLine(session.VideoDescription is { } video ? $"Video: {video}" : "Video: none");
        foreach (AudioTrackOptions track in recording.AudioTracks)
        {
            string what = track.Kind switch
            {
                AudioTrackKind.Process => $"process tree of {track.Target}{(track.IsGame ? " (the game)" : string.Empty)}",
                AudioTrackKind.Microphone => track.Target is null ? "default microphone" : $"microphone matching '{track.Target}'",
                _ => "everything except the game",
            };
            Console.WriteLine($"Audio: {track.Title,-10} {what}, AAC {recording.AudioBitrateKbps} kbps");
        }

        if (recording.Replay is { } replay)
        {
            Console.WriteLine($"Replay buffer: last {replay.BufferSeconds:0.#} s; save a clip with {hotkey} (or S in this window)");
            Console.WriteLine($"  clips -> {library.Root}");
        }

        if (recording.OutputPath is { } path)
        {
            Console.WriteLine($"  -> {path}");
        }

        Console.WriteLine();
    }

    private static void WaitForStop(RecordingSession session, long? limitQpc, ConcurrentQueue<ClipResult> clips)
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
            ? $"Recording for {QpcClock.TicksToMilliseconds(l - session.StartQpc) / 1000:0.#} s. Press Enter or Ctrl+C to stop early."
            : "Recording. Press Enter or Ctrl+C to stop.");
        if (session.HasReplayBuffer)
        {
            Console.WriteLine("Press S (or the hotkey) to save a clip.");
        }

        // Warnings would break the redrawn status block; they still reach the log file.
        LogLevel previousLevel = Log.ConsoleLevel;
        Log.ConsoleLevel = LogLevel.None;
        var display = new StatusDisplay(!Console.IsOutputRedirected);
        var lastStatus = Stopwatch.StartNew();
        SessionStatus? previous = null;

        try
        {
            while (QpcClock.Now < session.EndQpc)
            {
                bool cancelled = stop.Wait(50);
                ConsoleKey? key = readKeys && Console.KeyAvailable ? Console.ReadKey(intercept: true).Key : null;
                if (key == ConsoleKey.S && session.HasReplayBuffer)
                {
                    session.SaveClip(QpcClock.Now);
                }

                string? reason = cancelled ? "Ctrl+C"
                    : session.VideoClosed ? "captured window closed"
                    : session.VideoStopped ? "video encoding failed"
                    : key == ConsoleKey.Enter ? "Enter"
                    : null;
                if (reason is not null)
                {
                    Log.Info($"Stop requested: {reason}");
                    session.StopSoon();
                    return;
                }

                if (lastStatus.ElapsedMilliseconds >= 1000)
                {
                    double seconds = lastStatus.Elapsed.TotalSeconds;
                    lastStatus.Restart();
                    SessionStatus status = session.GetStatus();
                    display.Render(BuildStatus(status, previous, seconds, session.OutputPath is not null, clips));
                    previous = status;
                }
            }

            Log.Info("Stop: --seconds limit reached");
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            Log.ConsoleLevel = previousLevel;
        }
    }

    private static List<string> BuildStatus(
        SessionStatus status, SessionStatus? previous, double seconds, bool hasFile, ConcurrentQueue<ClipResult> clips)
    {
        var lines = new List<string>
        {
            $"  {TimeSpan.FromTicks(status.ElapsedTicks):mm\\:ss}" +
            (hasFile ? $"  file {status.BytesWritten / 1024.0 / 1024.0,7:0.0} MB" : string.Empty) +
            (status.WriteError is null ? string.Empty : "  WRITE ERROR"),
        };

        if (status.Video is { } video)
        {
            PipelineStats p = video.Pipeline;
            PipelineStats last = previous?.Video?.Pipeline ?? default;
            long encoded = p.Encoded - last.Encoded;
            long captured = video.Capture.Frames - (previous?.Video?.Capture.Frames ?? 0);
            lines.Add(
                $"  {"Video",-10} captured {captured / seconds,5:0.0} fps  encoded {p.Encoded,6}  repeated {p.Duplicated,6}  skipped {p.Skipped,4}" +
                $"  encode {(encoded > 0 ? (p.EncodeTotalMs - last.EncodeTotalMs) / encoded : 0),5:0.00} ms" +
                $"  {(p.Bytes - last.Bytes) * 8 / seconds / 1_000_000,5:0.0} Mbps" +
                (video.Fault is null ? string.Empty : "  FAULT"));
        }

        foreach (AudioTrackStatus t in status.Audio)
        {
            lines.Add(
                $"  {t.Title,-10} {Bar(t.PeakDbfs)} {(t.PeakDbfs <= -120 ? "  -inf" : $"{t.PeakDbfs,6:0.0}")} dB" +
                $"  pad {FramesToMs(t.Stats.PaddedFrames),7:0} ms  drop {FramesToMs(t.Stats.DroppedFrames),5:0} ms" +
                $"  drift {SignedMs(t.Stats.SmoothedDriftTicks),6} ms  corr {t.CorrectionPpm,5:+0;-0;0} ppm" +
                $"  {(t.Fault is not null ? "FAULT" : t.Source ?? "waiting")}");
        }

        if (status.Buffer is { } buffer)
        {
            ClipResult? last = clips.LastOrDefault();
            string lastText = last is null ? string.Empty
                : last.Error is { } error ? $"  last FAILED: {error.Message}"
                : $"  last {Path.GetFileName(last.Path)} ({last.DurationSeconds:0.0} s)";
            lines.Add(
                $"  {"Buffer",-10} {QpcClock.TicksToMilliseconds(buffer.HeldTicks) / 1000,5:0.0} s  {buffer.Bytes / 1024.0 / 1024.0,6:0.0} MB" +
                $"  {buffer.Packets,6} packets  clips {clips.Count}{lastText}");
        }

        return lines;
    }

    private static void PrintSummary(RecordingSession session, SessionStatus status, IReadOnlyList<ClipResult> clips)
    {
        double seconds = QpcClock.TicksToMilliseconds(session.EndQpc - session.StartQpc) / 1000;

        Console.WriteLine();
        Console.WriteLine(session.OutputPath is null
            ? $"Summary: {seconds:0.000} s"
            : $"Summary: {seconds:0.000} s, {status.BytesWritten / 1024.0 / 1024.0:0.0} MB, {status.BytesWritten * 8 / Math.Max(seconds, 0.001) / 1_000_000:0.0} Mbps");
        if (status.Video is { } video)
        {
            PipelineStats p = video.Pipeline;
            Console.WriteLine($"  Video      {(double)p.Encoded / video.Fps,8:0.000} s  {p.Encoded} frames ({p.Duplicated} repeats, {p.Skipped} skipped)," +
                              $" encode avg {(p.Encoded > 0 ? p.EncodeTotalMs / p.Encoded : 0):0.00} / max {p.EncodeMaxMs:0.00} ms," +
                              $" WGC latency {QpcClock.TicksToMilliseconds(video.Capture.MinLatencyTicks):0.0} to {QpcClock.TicksToMilliseconds(video.Capture.MaxLatencyTicks):0.0} ms");
            if (video.Fault is { } fault)
            {
                Console.WriteLine($"             FAULT: {fault.Message}");
            }
        }

        Console.WriteLine($"  {"Track",-10} {"Seconds",8} {"Packets",8} {"Padded",10} {"Dropped",9} {"Disc",5} {"TsErr",6} {"MaxDrift",9} {"Corr",8}");
        foreach (AudioTrackStatus t in status.Audio)
        {
            AlignerStats s = t.Stats;
            Console.WriteLine(
                $"  {t.Title,-10} {(double)t.Frames / AudioRate,8:0.000} {s.Packets,8} {FramesToMs(s.PaddedFrames),7:0} ms {FramesToMs(s.DroppedFrames),6:0} ms " +
                $"{s.Discontinuities,5} {s.TimestampErrors,6} {QpcClock.TicksToMilliseconds(s.MaxAbsDriftTicks),6:0.0} ms {t.CorrectionPpm,4:+0;-0;0} ppm");
            if (t.Fault is { } fault)
            {
                Console.WriteLine($"  {string.Empty,-10} FAULT: {fault.Message}");
            }
        }

        if (status.WriteError is { } writeError)
        {
            Console.WriteLine($"  WRITE ERROR: {writeError.Message}");
        }

        if (session.VideoClosed)
        {
            Console.WriteLine("  Stopped because the captured window closed.");
        }

        Console.WriteLine();
        if (session.OutputPath is { } path)
        {
            Console.WriteLine($"File: {path}");
        }

        if (session.HasReplayBuffer)
        {
            Console.WriteLine(clips.Count == 0 ? "Clips: none saved" : $"Clips: {clips.Count}");
            foreach (ClipResult clip in clips)
            {
                Console.WriteLine(clip.Error is null
                    ? $"  {clip.Path}  {clip.DurationSeconds:0.000} s"
                    : $"  {clip.PressedAt:HH:mm:ss} FAILED: {clip.Error.Message}");
            }
        }

        if (Log.FilePath is not null)
        {
            Console.WriteLine($"Log: {Log.FilePath}");
        }
    }

    private static string Bar(double db)
    {
        const int width = 16;
        int filled = (int)Math.Round(Math.Clamp((db + 60) / 60, 0, 1) * width);
        return $"[{new string('#', filled)}{new string('.', width - filled)}]";
    }

    private static double FramesToMs(long frames) => frames * 1000.0 / AudioRate;

    private static string SignedMs(long ticks)
    {
        double ms = Math.Round(QpcClock.TicksToMilliseconds(ticks), 1);
        return ms < 0 ? $"-{-ms:0.0}" : $"+{ms:0.0}";
    }
}
