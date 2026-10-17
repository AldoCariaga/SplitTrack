using SplitTrack.AudioPoc.Commands;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Platform;

namespace SplitTrack.AudioPoc;

internal static class Program
{
    private const string Usage = """
        SplitTrack audio proof of concept

        Usage:
          SplitTrack.AudioPoc list
              Processes that own audio sessions, plus capture and render devices.

          SplitTrack.AudioPoc record [options]
              --process <pid|exe>       Capture one process tree (repeatable), e.g. --process Discord.exe
              --exclude <pid|exe>       "System" track: everything except this process tree
              --system-all              Plain loopback of the default output device
              --mic [name]              Microphone (default device, or first name containing <name>)
              --seconds <n>             Stop after n seconds (otherwise Enter or Ctrl+C)
              --out <dir>               Output folder (default: current folder)
              --format float48|pcm44    Process-loopback format (default float48)

        Example:
          SplitTrack.AudioPoc record --process Discord.exe --process game.exe --exclude game.exe --mic --seconds 30 --out C:\Temp\poc
        """;

    private static int Main(string[] args)
    {
        Log.Init("audiopoc");
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

            return args[0].ToLowerInvariant() switch
            {
                "list" => ListCommand.Run(),
                "record" => RecordCommand.Run(RecordOptions.Parse(args[1..])),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
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
