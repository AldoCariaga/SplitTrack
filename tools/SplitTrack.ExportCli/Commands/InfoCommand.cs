using SplitTrack.Encode.Video;
using SplitTrack.Export;

namespace SplitTrack.ExportCli.Commands;

internal static class InfoCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            throw new UsageException("info takes one file: a recording's .mkv or its .json sidecar.");
        }

        PrintSource(ExportSource.Open(args[0]));
        return 0;
    }

    public static void PrintSource(ExportSource source)
    {
        MediaInfo media = source.Media;
        string sidecar = source.Sidecar is { } s ? $"{s.Kind.ToString().ToLowerInvariant()}, sidecar v{s.Version}" : "no sidecar";
        Console.WriteLine($"Source: {source.VideoPath}");
        Console.WriteLine($"  {media.DurationSeconds:0.000} s, {Exporter.Megabytes(media.SizeBytes)}, {sidecar}");
        if (source.Sidecar?.Game is { } game)
        {
            Console.WriteLine($"  Game: {game}");
        }

        Console.WriteLine(media.Video is { } v
            ? $"  Video: {v.Codec} {v.Width}x{v.Height} {v.Fps:0.##} fps" +
              (source.Sidecar?.Video is { } sv ? $", {sv.BitrateKbps / 1000.0:0.#} Mbps {sv.Encoder}" : string.Empty)
            : "  Video: none");
        foreach (SourceTrack track in source.Tracks)
        {
            AudioStreamInfo stream = media.Audio.First(a => a.Index == track.Stream);
            string kind = track.Kind is null ? string.Empty : $" {track.Kind}";
            string captured = track.Source is null ? string.Empty : $", captured {track.Source}";
            Console.WriteLine($"  Audio {track.Number}: {track.Title,-10}{kind}{captured} ({stream.SampleRate / 1000.0:0.#} kHz, {stream.Channels} ch)");
        }

        if (source.Tracks.Count == 0)
        {
            Console.WriteLine("  Audio: none");
        }
    }
}

internal static class EncodersCommand
{
    public static int Run()
    {
        var tool = FFmpegTool.FindDefault();
        Console.WriteLine("H.264 encoders for re-encoding exports:");
        bool any = false;
        foreach (VideoEncoderKind kind in VideoEncoderKinds.All)
        {
            bool ok = tool.TestEncoder(kind);
            any |= ok && kind != VideoEncoderKind.X264;
            Console.WriteLine($"  {kind.ShortName(),-6} {(ok ? "OK" : "not available")}");
        }

        Console.WriteLine($"auto uses: {tool.SelectEncoder().ShortName()}{(any ? string.Empty : " (no GPU encoder works)")}");
        return 0;
    }
}
