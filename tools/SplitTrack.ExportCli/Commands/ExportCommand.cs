using SplitTrack.Common.Diagnostics;
using SplitTrack.Encode.Video;
using SplitTrack.Export;

namespace SplitTrack.ExportCli.Commands;

internal static class ExportCommand
{
    public static int Run(ExportOptions options)
    {
        ExportSource source = ExportSource.Open(options.File);
        List<TrackMix> tracks = ChooseTracks(source, options);
        string output = OutputPath(source, options);

        var exporter = new Exporter(FFmpegTool.FindDefault());
        ExportPlan plan = exporter.Plan(new ExportRequest(source, tracks, output)
        {
            From = options.From,
            To = options.To,
            Precise = options.Precise,
            MaxBytes = options.MaxBytes,
            MaxHeight = options.Height,
            VideoBitrateKbps = options.VideoBitrateKbps,
            Reencode = options.Reencode,
            Encoder = options.Encoder,
            AudioBitrateKbps = options.AudioBitrateKbps,
        });

        InfoCommand.PrintSource(source);
        Console.WriteLine();
        PrintPlan(plan, source);

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        LogLevel previousLevel = Log.ConsoleLevel;
        Log.ConsoleLevel = LogLevel.None;
        var display = new StatusDisplay(!Console.IsOutputRedirected);
        try
        {
            ExportResult result = exporter.RunAsync(plan, p => display.Render([ProgressLine(p)]), cancel.Token).GetAwaiter().GetResult();
            PrintResult(result);
            return 0;
        }
        finally
        {
            Log.ConsoleLevel = previousLevel;
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static List<TrackMix> ChooseTracks(ExportSource source, ExportOptions options)
    {
        if (options.NoAudio)
        {
            return [];
        }

        if (options.Tracks is null)
        {
            return source.Tracks.Select(t => new TrackMix(t, 1)).ToList();
        }

        var chosen = new List<TrackMix>();
        foreach (TrackChoice choice in options.Tracks)
        {
            SourceTrack track = source.FindTrack(choice.Spec)
                ?? throw new UsageException(
                    $"--tracks: '{choice.Spec}' is not a track of this file. Tracks: " +
                    (source.Tracks.Count == 0 ? "none." : string.Join(", ", source.Tracks.Select(t => $"{t.Number} {t.Title}")) + "."));
            if (chosen.Any(c => c.Track == track))
            {
                throw new UsageException($"--tracks: {track.Title} is listed twice.");
            }

            chosen.Add(new TrackMix(track, choice.Volume));
        }

        return chosen;
    }

    private static string OutputPath(ExportSource source, ExportOptions options)
    {
        if (options.Out is not { } output)
        {
            return source.DefaultOutputPath();
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || !Path.HasExtension(output))
        {
            string name = Path.GetFileName(source.DefaultOutputPath());
            output = Path.Combine(output, name);
        }

        if (string.Equals(output, source.VideoPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("--out cannot be the source file.");
        }

        if (File.Exists(output) && !options.Overwrite)
        {
            throw new UsageException($"{output} already exists; add --overwrite to replace it.");
        }

        return output;
    }

    private static void PrintPlan(ExportPlan plan, ExportSource source)
    {
        ExportRequest request = plan.Request;
        string tracks = request.Tracks.Count == 0
            ? "none"
            : string.Join(" + ", request.Tracks.Select(t => $"{t.Track.Title} {t.Volume * 100:0.#}%"));
        string[] leftOut = source.Tracks.Where(t => request.Tracks.All(c => c.Track != t)).Select(t => t.Title).ToArray();
        Console.WriteLine($"Audio: {tracks}{(leftOut.Length > 0 && request.Tracks.Count > 0 ? $" (left out: {string.Join(", ", leftOut)})" : string.Empty)}");
        if (OverlapNote(request.Tracks) is { } overlap)
        {
            Console.WriteLine($"  Note: {overlap}");
        }

        Console.WriteLine($"Part: {Exporter.Time(plan.Start)} to {Exporter.Time(plan.End)} ({plan.Duration:0.###} s)");
        Console.WriteLine(plan.Video switch
        {
            VideoMode.None => "Video: none",
            VideoMode.Copy => "Video: copied as recorded",
            _ => $"Video: re-encoded with {plan.Encoder?.ShortName()} at {plan.VideoBitrateKbps / 1000.0:0.00} Mbps" +
                 (plan.OutputHeight is { } h ? $", scaled to {h} px high" : string.Empty),
        });
        foreach (string note in plan.Notes)
        {
            Console.WriteLine($"  Note: {note}");
        }

        Console.WriteLine($"Output: {request.OutputPath}{(request.MaxBytes is { } max ? $" (at most {Exporter.Megabytes(max)})" : string.Empty)}");
        Console.WriteLine();
    }

    // Unless System left the app tracks out (the default since sidecar version 3, and the sidecar says so),
    // it recorded everything except the game, so an app track that captured something (e.g. Discord) is in it too.
    private static string? OverlapNote(IReadOnlyList<TrackMix> tracks)
    {
        TrackMix? system = tracks.FirstOrDefault(t => t.Track.Kind == "system");
        string[] apps = tracks.Where(t => t.Track is { Kind: "app", Source: not null } && t.Volume > 0).Select(t => t.Track.Title).ToArray();
        return system is { Track.LeavesOutApps: false, Volume: > 0 } && apps.Length > 0
            ? $"{system.Track.Title} also contains {string.Join(" and ", apps)}, so it is heard twice; leave one out if that sounds wrong."
            : null;
    }

    private static string ProgressLine(ExportProgress p) =>
        $"{(p.Attempt > 1 ? $"Try {p.Attempt}: " : string.Empty)}{(p.Plan.Video == VideoMode.Encode ? "Encoding" : "Exporting")} " +
        $"{p.Fraction * 100,3:0}%  {p.DoneSeconds,6:0.0} / {p.Plan.Duration:0.0} s" +
        (p.Speed is { } speed ? $"  {speed:0.0}x" : string.Empty) +
        $"  {Exporter.Megabytes(p.Bytes)}";

    private static void PrintResult(ExportResult result)
    {
        Console.WriteLine();
        if (result.Attempts > 1)
        {
            Console.WriteLine($"Re-encoded at {result.Plan.VideoBitrateKbps / 1000.0:0.00} Mbps to fit ({result.Attempts} tries).");
        }

        Console.WriteLine($"Saved {result.Path}");
        Console.WriteLine($"  {Exporter.Megabytes(result.Bytes)}, {result.Plan.Duration:0.###} s, took {result.Elapsed.TotalSeconds:0.0} s");
    }
}
