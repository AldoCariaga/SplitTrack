using SplitTrack.Common.Diagnostics;
using SplitTrack.Export;
using SplitTrack.ExportCli.Commands;
using SplitTrack.FFmpeg;

namespace SplitTrack.ExportCli;

internal static class Program
{
    private const string Usage = """
        SplitTrack export: a recording or clip to one MP4 with the chosen tracks mixed

        Usage:
          SplitTrack.ExportCli info <file>
              Length, video and the audio tracks (number, title, kind, what was captured).

          SplitTrack.ExportCli export <file> [options]
              Writes an MP4 (H.264 + one AAC track, playable on phones and in Discord) next to the
              source, e.g. 20261018_140322_clip.mp4.

          SplitTrack.ExportCli encoders
              Which H.264 encoders work for re-encoding on this PC.

          <file> is a recording's .mkv or its .json sidecar.

          Options for export
            Audio
              --tracks <t[=vol]>[,...]  Tracks to mix, by title or number, each with a volume in percent
                                        (default 100), e.g. --tracks Game,Mic=150. Default: all at 100
              --no-audio                No audio
              --audio-bitrate <kbps>    AAC bitrate of the mix (default 160)
            Part
              --from <time>             Start, as seconds (75.5) or m:ss / h:mm:ss (1:15.5)
              --to <time>               End (default: the end)
              --precise                 Start exactly at --from; otherwise a copy starts on the keyframe
                                        before it (at most 1 s earlier)
            Video: copied as recorded unless one of these needs a re-encode
              --size <MB>               Keep the file under this size (MB = 1,000,000 bytes), e.g. 10 for
                                        Discord without Nitro; re-encodes only if a copy would be larger
              --height <px>             Scale down to this height, e.g. 720
              --bitrate <Mbps>          Video bitrate
              --reencode                Re-encode even if nothing above asks for it
              --encoder <name>          auto, amf, nvenc, qsv or x264 for re-encoding (default auto:
                                        the first GPU encoder that works, else x264; x264 with --size)
            Output
              --out <file|dir>          Where to write (default: next to the source)
              --overwrite               Replace an existing --out file

        Examples:
          SplitTrack.ExportCli info 20261018_140322_clip.mkv
          SplitTrack.ExportCli export 20261018_140322_clip.mkv --tracks Game,Mic
          SplitTrack.ExportCli export 20261018_140322_rec.mkv --from 12:30 --to 13:05 --tracks Game,Discord=80 --size 10
        """;

    private static int Main(string[] args)
    {
        Log.Init("exportcli");
        Log.Info($"Started: {string.Join(' ', args)}");

        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "info":
                    FFmpegLibrary.Initialize();
                    return InfoCommand.Run(args[1..]);
                case "export":
                    ExportOptions options = ExportOptions.Parse(args[1..]);
                    FFmpegLibrary.Initialize();
                    return ExportCommand.Run(options);
                case "encoders":
                    return EncodersCommand.Run();
                default:
                    throw new UsageException($"Unknown command '{args[0]}'.");
            }
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 1;
        }
        catch (Exception ex) when (ex is ExportException or FileNotFoundException or FFmpegException)
        {
            Log.Info(ex.Message);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Export cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Log.ConsoleLevel = LogLevel.None;
            Log.Error("Fatal error", ex);
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Log.FilePath is not null)
            {
                Console.Error.WriteLine($"Details: {Log.FilePath}");
            }

            return 1;
        }
        finally
        {
            Log.Close();
        }
    }
}

internal sealed class UsageException(string message) : Exception(message);
