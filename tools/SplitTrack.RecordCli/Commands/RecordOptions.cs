using System.Globalization;
using SplitTrack.Encode.Video;
using SplitTrack.Engine.Input;
using SplitTrack.Engine.Recording;
using SplitTrack.Library;

namespace SplitTrack.RecordCli.Commands;

internal sealed class RecordOptions
{
    private int? _monitor;
    private string? _window;
    private bool _noVideo;
    private int _fps = 60;
    private int _bitrateKbps = 20_000;
    private int? _height;
    private VideoEncoderKind? _encoder;
    private bool _cursor = true;
    private int _adapter;
    private string? _game;
    private readonly List<(string Title, string Target)> _apps = [];
    private bool _mic;
    private string? _micFilter;
    private bool _system;
    private string? _replayOnlyOption;

    public int AudioBitrateKbps { get; private set; } = 160;

    public double? Seconds { get; private set; }

    /// <summary>The library folder: recordings, clips and their sidecars.</summary>
    public string OutputDirectory { get; private set; } = LibraryFolder.DefaultRoot;

    /// <summary>The replay command: keep a buffer and save clips from it.</summary>
    public bool Replay { get; private set; }

    public double BufferSeconds { get; private set; } = 60;

    public HotkeyGesture Hotkey { get; private set; } = HotkeyGesture.Parse("Alt+F10");

    /// <summary>Replay: also write the whole session to a file.</summary>
    public bool AlsoRecord { get; private set; }

    public VideoOptions? Video =>
        _noVideo ? null : new VideoOptions(_monitor ?? 0, _window, _fps, _bitrateKbps, _height, _encoder, _cursor, _adapter);

    /// <summary>Game, apps, Mic, System: the order of the tracks in the file.</summary>
    public IReadOnlyList<AudioTrackOptions> AudioTracks
    {
        get
        {
            var tracks = new List<AudioTrackOptions>();
            if (_game is not null)
            {
                tracks.Add(new AudioTrackOptions("Game", AudioTrackKind.Process, _game, IsGame: true));
            }

            tracks.AddRange(_apps.Select(a => new AudioTrackOptions(a.Title, AudioTrackKind.Process, a.Target)));
            if (_mic)
            {
                tracks.Add(new AudioTrackOptions("Mic", AudioTrackKind.Microphone, _micFilter));
            }

            if (_system)
            {
                tracks.Add(new AudioTrackOptions("System", AudioTrackKind.System));
            }

            return tracks;
        }
    }

    /// <param name="replay">Parsing the replay command, which also takes --buffer, --hotkey and --record.</param>
    public static RecordOptions Parse(string[] args, bool replay)
    {
        var options = new RecordOptions { Replay = replay };
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--monitor":
                    options._monitor = Integer(args, ref i, arg, 0, 63);
                    break;
                case "--window":
                    options._window = Value(args, ref i, arg);
                    break;
                case "--no-video":
                    options._noVideo = true;
                    break;
                case "--fps":
                    options._fps = Integer(args, ref i, arg, 1, 240);
                    break;
                case "--bitrate":
                    options._bitrateKbps = (int)Math.Round(Number(args, ref i, arg, 0.5, 200) * 1000);
                    break;
                case "--height":
                    options._height = Integer(args, ref i, arg, 144, 4320);
                    break;
                case "--encoder":
                    string encoder = Value(args, ref i, arg);
                    options._encoder = encoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : VideoEncoderKinds.Parse(encoder)
                          ?? throw new UsageException($"Unknown --encoder '{encoder}' (use auto, amf, nvenc, qsv or x264).");
                    break;
                case "--no-cursor":
                    options._cursor = false;
                    break;
                case "--adapter":
                    options._adapter = Integer(args, ref i, arg, 0, 15);
                    break;
                case "--game":
                    options._game = options._game is null
                        ? Value(args, ref i, arg)
                        : throw new UsageException("--game can only be given once.");
                    break;
                case "--app":
                    options._apps.Add(ParseApp(Value(args, ref i, arg)));
                    break;
                case "--mic":
                    options._mic = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options._micFilter = args[++i];
                    }

                    break;
                case "--system":
                    options._system = true;
                    break;
                case "--audio-bitrate":
                    options.AudioBitrateKbps = Integer(args, ref i, arg, 32, 512);
                    break;
                case "--seconds":
                    options.Seconds = Number(args, ref i, arg, 0.5, 24 * 3600);
                    break;
                case "--out":
                    options.OutputDirectory = Path.GetFullPath(Value(args, ref i, arg));
                    break;
                case "--buffer":
                    options._replayOnlyOption ??= arg;
                    options.BufferSeconds = Number(args, ref i, arg, 5, 600);
                    break;
                case "--hotkey":
                    options._replayOnlyOption ??= arg;
                    string hotkey = Value(args, ref i, arg);
                    try
                    {
                        options.Hotkey = HotkeyGesture.Parse(hotkey);
                    }
                    catch (FormatException ex)
                    {
                        throw new UsageException($"--hotkey: {ex.Message}");
                    }

                    break;
                case "--record":
                    options._replayOnlyOption ??= arg;
                    options.AlsoRecord = true;
                    break;
                default:
                    throw new UsageException($"Unknown option '{arg}'.");
            }
        }

        if (!replay && options._replayOnlyOption is { } replayOnly)
        {
            throw new UsageException($"{replayOnly} only applies to the replay command.");
        }

        if (options._monitor is not null && options._window is not null)
        {
            throw new UsageException("Use either --monitor or --window, not both.");
        }

        if (options._noVideo && (options._monitor is not null || options._window is not null))
        {
            throw new UsageException("--no-video cannot be combined with --monitor or --window.");
        }

        List<string> titles = options.AudioTracks.Select(t => t.Title).ToList();
        if (titles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != titles.Count)
        {
            throw new UsageException($"Track titles must be unique: {string.Join(", ", titles)}.");
        }

        if (options._noVideo && titles.Count == 0)
        {
            throw new UsageException("Nothing to record: --no-video needs at least one of --game, --app, --mic or --system.");
        }

        return options;
    }

    private static (string Title, string Target) ParseApp(string spec)
    {
        int equals = spec.IndexOf('=');
        if (equals <= 0 || equals == spec.Length - 1)
        {
            throw new UsageException($"--app needs <Title>=<exe|pid>, e.g. --app Discord=Discord.exe; got '{spec}'.");
        }

        return (spec[..equals].Trim(), spec[(equals + 1)..].Trim());
    }

    private static string Value(string[] args, ref int i, string option) =>
        i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : throw new UsageException($"{option} needs a value.");

    private static double Number(string[] args, ref int i, string option, double min, double max)
    {
        string text = Value(args, ref i, option);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= min && value <= max
            ? value
            : throw new UsageException($"{option} needs a number from {min} to {max}, got '{text}'.");
    }

    private static int Integer(string[] args, ref int i, string option, int min, int max)
    {
        string text = Value(args, ref i, option);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max
            ? value
            : throw new UsageException($"{option} needs a whole number from {min} to {max}, got '{text}'.");
    }
}
