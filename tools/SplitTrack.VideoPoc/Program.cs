using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Platform;
using SplitTrack.FFmpeg;
using SplitTrack.VideoPoc.Commands;

namespace SplitTrack.VideoPoc;

internal static class Program
{
    private const string Usage = """
        SplitTrack video proof of concept

        Usage:
          SplitTrack.VideoPoc list
              GPUs, monitors, capturable windows, and which H.264 encoders work on this PC.

          SplitTrack.VideoPoc record [options]
              --monitor <n>             Capture monitor n from 'list' (default 0, the primary)
              --window <hwnd|exe|text>  Capture a window instead, e.g. --window game.exe
              --fps <n>                 Frames per second (default 60)
              --bitrate <Mbps>          Video bitrate (default 20)
              --height <px>             Scale down to this height, keeping the aspect ratio
              --encoder <name>          auto, amf, nvenc, qsv or x264 (default auto)
              --no-cursor               Leave the mouse pointer out
              --adapter <n>             GPU to use (default 0)
              --seconds <n>             Stop after n seconds (otherwise Enter or Ctrl+C)
              --out <dir>               Output folder (default: current folder)

        Example:
          SplitTrack.VideoPoc record --monitor 0 --fps 60 --bitrate 20 --seconds 30 --out C:\Temp\poc
        """;

    private static int Main(string[] args)
    {
        Log.Init("videopoc");
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
