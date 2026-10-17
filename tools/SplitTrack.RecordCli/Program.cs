using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Platform;
using SplitTrack.Engine.Recording;
using SplitTrack.FFmpeg;
using SplitTrack.RecordCli.Commands;

namespace SplitTrack.RecordCli;

internal static class Program
{
    private const string Usage = """
        SplitTrack recorder: video plus one audio track per source, in one MKV

        Usage:
          SplitTrack.RecordCli list
              Audio processes, microphones, GPUs, monitors, windows and working H.264 encoders.

          SplitTrack.RecordCli record [options]
            Video
              --monitor <n>             Capture monitor n from 'list' (default 0, the primary)
              --window <hwnd|exe|text>  Capture a window instead
              --no-video                Audio only
              --fps <n>                 Frames per second (default 60)
              --bitrate <Mbps>          Video bitrate (default 20)
              --height <px>             Scale down to this height, keeping the aspect ratio
              --encoder <name>          auto, amf, nvenc, qsv or x264 (default auto)
              --no-cursor               Leave the mouse pointer out
              --adapter <n>             GPU to use (default 0)
            Audio tracks, in this order in the file
              --game <exe|pid>          "Game": that process tree; may start later and restart
              --app <Title>=<exe|pid>   Another per-app track (repeatable), e.g. --app Discord=Discord.exe
              --mic [name]              "Mic": default microphone, or first name containing <name>
              --system                  "System": everything except the game
              --audio-bitrate <kbps>    AAC bitrate per track (default 160)
            Output
              --seconds <n>             Stop after n seconds (otherwise Enter or Ctrl+C)
              --out <dir>               Output folder (default: current folder)

        Example:
          SplitTrack.RecordCli record --game game.exe --app Discord=Discord.exe --mic --system --seconds 60 --out C:\Temp\poc
        """;

    private static int Main(string[] args)
    {
        Log.Init("recordcli");
        Log.Info($"Started: {string.Join(' ', args)} (Windows {WindowsVersion.Current})");

        try
        {
            if (WindowsVersion.GetUnsupportedReason() is { } reason)
            {
                Console.Error.WriteLine(reason);
                return 2;
            }

            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            string command = args[0].ToLowerInvariant();
            if (command is not ("list" or "record"))
            {
                throw new UsageException($"Unknown command '{args[0]}'.");
            }

            RecordOptions? options = command == "record" ? RecordOptions.Parse(args[1..]) : null;
            FFmpegLibrary.Initialize();
            return options is null ? ListCommand.Run() : RecordCommand.Run(options);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 1;
        }
        catch (RecordingSetupException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
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
