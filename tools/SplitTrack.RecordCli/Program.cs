using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Platform;
using SplitTrack.Engine.Input;
using SplitTrack.Engine.Recording;
using SplitTrack.Engine.Settings;
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
              Records the whole session to <out>\<time>_rec.mkv (+ .json sidecar, .jpg thumbnail).

          SplitTrack.RecordCli replay [options] [--buffer <s>] [--hotkey <keys>] [--record | --no-record]
              Keeps the last seconds in memory; the hotkey (or S in this window) saves them as
              <out>\<time>_clip.mkv (+ .json, .jpg).
              --buffer <s>              Seconds kept, i.e. the clip length (default 60)
              --hotkey <keys>           Save hotkey, works while a game has focus (default Alt+F10)
              --record                  Also record the whole session, as 'record' does

          SplitTrack.RecordCli settings
              Shows the settings file that record and replay start from.
          SplitTrack.RecordCli settings save [options]
              Writes the given record/replay options into the settings file (created if missing).

          record and replay start from the settings file; options given here override it for this
          run. Defaults below are those of a new settings file.

          Options for record, replay and settings save
            Preset
              --preset <name>           Use this preset (default: the active one). With settings save it
                                        becomes the active one; a new name starts as a copy of the active one
            Video
              --monitor <n>             Capture monitor n from 'list' (default 0, the primary)
              --window <hwnd|exe|text>  Capture a window instead
              --no-video                Audio only
              --fps <n>                 Frames per second (default 60)
              --bitrate <Mbps>          Video bitrate (default 20)
              --height <px>             Scale down to this height, keeping the aspect ratio (0: don't)
              --encoder <name>          auto, amf, nvenc, qsv or x264 (default auto)
              --cursor, --no-cursor     Show or leave out the mouse pointer (default shown)
              --adapter <n>             GPU to use (default 0)
            Audio tracks: any of these replaces the configured tracks, in this order in the file
              --game <exe[,exe...]>     "Game": the first of these running; may start later and restart.
                                        auto = whatever program is full screen in front
              --app <Title>=<exe[,...]> Another per-app track (repeatable), e.g. --app Discord=Discord.exe
              --mic [name[,name...]]    "Mic": default microphone, or the first present containing a name
              --system                  "System": everything except the game
              --no-audio                No audio tracks
              --audio-bitrate <kbps>    AAC bitrate per track (default 160)
              (default tracks: Game = auto, Mic, System)
            Output
              --out <dir>               Library folder (default: Videos\SplitTrack)
              --seconds <n>             Stop after n seconds (otherwise Enter or Ctrl+C); not saved
              --settings <file>         Settings file (default: %LOCALAPPDATA%\SplitTrack\settings.json)

        Examples:
          SplitTrack.RecordCli settings save --game game.exe,other.exe --app Discord=Discord.exe --mic --system
          SplitTrack.RecordCli replay
          SplitTrack.RecordCli record --seconds 60 --no-video
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

            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    FFmpegLibrary.Initialize();
                    return ListCommand.Run();
                case "record" or "replay":
                    RecordOptions options = RecordOptions.Parse(args[1..], args[0].Equals("replay", StringComparison.OrdinalIgnoreCase) ? CliCommand.Replay : CliCommand.Record);
                    Log.Info($"Settings: {options.SettingsPath}{(options.SettingsFileExists ? string.Empty : " (defaults, no file)")}");
                    FFmpegLibrary.Initialize();
                    return RecordCommand.Run(options);
                case "settings" when args.Length > 1 && args[1].Equals("save", StringComparison.OrdinalIgnoreCase):
                    return SettingsCommand.Save(RecordOptions.Parse(args[2..], CliCommand.SaveSettings));
                case "settings":
                    return SettingsCommand.Show(args[1..]);
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
        catch (Exception ex) when (ex is RecordingSetupException or HotkeyUnavailableException or SettingsException)
        {
            Log.Info(ex.Message);
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
