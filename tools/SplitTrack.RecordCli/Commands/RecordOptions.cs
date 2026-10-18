using System.Globalization;
using SplitTrack.Engine.Input;
using SplitTrack.Engine.Recording;
using SplitTrack.Engine.Settings;

namespace SplitTrack.RecordCli.Commands;

internal enum CliCommand
{
    Record,
    Replay,

    /// <summary>'settings save': the options change the settings file instead of one run.</summary>
    SaveSettings,
}

/// <summary>The settings file with this run's command-line options applied on top.</summary>
internal sealed class RecordOptions
{
    private RecordOptions(CliCommand command, string settingsPath, RecorderSettings settings)
    {
        Command = command;
        SettingsPath = settingsPath;
        Settings = settings;
    }

    public CliCommand Command { get; }

    public string SettingsPath { get; }

    public bool SettingsFileExists => File.Exists(SettingsPath);

    public RecorderSettings Settings { get; }

    public double? Seconds { get; private set; }

    /// <summary>Loads the settings file (--settings, or the default one) and applies the other options to it.</summary>
    /// <exception cref="SettingsException">The file is unreadable, or the result is not usable.</exception>
    public static RecordOptions Parse(string[] args, CliCommand command)
    {
        string path = Path.GetFullPath(FindSettingsPath(args) ?? SettingsFile.DefaultPath);
        var options = new RecordOptions(command, path, SettingsFile.Load(path));
        options.UsePreset(args);
        options.Apply(args);

        IReadOnlyList<string> errors = options.Settings.Validate();
        if (errors.Count > 0)
        {
            throw new SettingsException(DescribeErrors(path, errors, withCommandLine: args.Length > 0));
        }

        return options;
    }

    public static string? FindSettingsPath(string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        return at < 0 ? null : Value(args, ref at, "--settings");
    }

    public static string DescribeErrors(string path, IReadOnlyList<string> errors, bool withCommandLine) =>
        $"These settings cannot be used ({path}{(withCommandLine ? " plus the command line" : string.Empty)}):{Environment.NewLine}" +
        string.Join(Environment.NewLine, errors.Select(e => $"  {e}"));

    /// <summary>
    /// --preset makes that preset the active one, before the other options change it. 'settings save'
    /// creates a new name as a copy of the active preset (without its games); record and replay refuse it.
    /// </summary>
    private void UsePreset(string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals("--preset", StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return;
        }

        string name = Value(args, ref at, "--preset").Trim();
        RecorderSettings s = Settings;
        if (s.Find(name) is { } preset)
        {
            s.ActivePreset = preset.Name;
            return;
        }

        if (Command != CliCommand.SaveSettings)
        {
            throw new UsageException($"There is no preset named '{name}' (there are: {string.Join(", ", s.Presets.Select(p => p.Name))}).");
        }

        PresetSettings copy = SettingsFile.Clone(s.Active);
        copy.Name = name;
        copy.Games = [];
        s.Presets.Add(copy);
        s.ActivePreset = name;
    }

    private void Apply(string[] args)
    {
        RecorderSettings s = Settings;
        bool monitor = false, window = false, noVideo = false, noAudio = false;
        string? replayOnly = null;
        string? game = null;
        var apps = new List<TrackSettings>();
        TrackSettings? mic = null;
        bool system = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--settings":
                case "--preset":
                    i++;
                    break;
                case "--monitor":
                    monitor = true;
                    s.Video.Monitor = Integer(args, ref i, arg, Limit.Monitor);
                    s.Video.Window = null;
                    s.Video.Enabled = true;
                    break;
                case "--window":
                    window = true;
                    s.Video.Window = Value(args, ref i, arg);
                    s.Video.Enabled = true;
                    break;
                case "--no-video":
                    noVideo = true;
                    s.Video.Enabled = false;
                    break;
                case "--fps":
                    s.Video.Fps = Integer(args, ref i, arg, Limit.Fps);
                    break;
                case "--bitrate":
                    s.Video.BitrateMbps = Number(args, ref i, arg, Limit.BitrateMbps);
                    break;
                case "--height":
                    // 0 undoes a configured height: keep the capture size.
                    string height = Value(args, ref i, arg);
                    s.Video.MaxHeight = height == "0" ? null : ParseInteger(height, arg, Limit.MaxHeight);
                    break;
                case "--encoder":
                    string encoder = Value(args, ref i, arg);
                    try
                    {
                        VideoSettings.ParseEncoder(encoder);
                    }
                    catch (FormatException ex)
                    {
                        throw new UsageException($"--encoder: {ex.Message}.");
                    }

                    s.Video.Encoder = encoder.ToLowerInvariant();
                    break;
                case "--cursor":
                    s.Video.Cursor = true;
                    break;
                case "--no-cursor":
                    s.Video.Cursor = false;
                    break;
                case "--adapter":
                    s.Video.Adapter = Integer(args, ref i, arg, Limit.Adapter);
                    break;
                case "--game":
                    game = game is null ? Value(args, ref i, arg) : throw new UsageException("--game can only be given once.");
                    break;
                case "--app":
                    apps.Add(ParseApp(Value(args, ref i, arg)));
                    break;
                case "--mic":
                    mic = new TrackSettings { Title = "Mic", Kind = AudioTrackKind.Microphone };
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        mic.Devices = SplitList(args[++i], arg);
                    }

                    break;
                case "--system":
                    system = true;
                    break;
                case "--no-audio":
                    noAudio = true;
                    break;
                case "--audio-bitrate":
                    s.Audio.BitrateKbps = Integer(args, ref i, arg, Limit.AudioBitrateKbps);
                    break;
                case "--seconds":
                    Seconds = Command != CliCommand.SaveSettings
                        ? Number(args, ref i, arg, new Limit(0.5, 24 * 3600))
                        : throw new UsageException("--seconds applies to one run, not to the settings file.");
                    break;
                case "--out":
                    s.LibraryFolder = Path.GetFullPath(Value(args, ref i, arg));
                    break;
                case "--buffer":
                    replayOnly ??= arg;
                    s.Replay.BufferSeconds = Number(args, ref i, arg, Limit.BufferSeconds);
                    break;
                case "--hotkey":
                    replayOnly ??= arg;
                    string hotkey = Value(args, ref i, arg);
                    try
                    {
                        s.Hotkeys.SaveClip = HotkeyGesture.Parse(hotkey).ToString();
                    }
                    catch (FormatException ex)
                    {
                        throw new UsageException($"--hotkey: {ex.Message}");
                    }

                    break;
                case "--record":
                    replayOnly ??= arg;
                    s.Replay.AlsoRecord = true;
                    break;
                case "--no-record":
                    replayOnly ??= arg;
                    s.Replay.AlsoRecord = false;
                    break;
                default:
                    throw new UsageException($"Unknown option '{arg}'.");
            }
        }

        if (Command == CliCommand.Record && replayOnly is not null)
        {
            throw new UsageException($"{replayOnly} only applies to the replay command.");
        }

        if (monitor && window)
        {
            throw new UsageException("Use either --monitor or --window, not both.");
        }

        if (noVideo && (monitor || window))
        {
            throw new UsageException("--no-video cannot be combined with --monitor or --window.");
        }

        // Any track option replaces the configured tracks, in this order: Game, apps, Mic, System.
        var tracks = new List<TrackSettings>();
        if (game is not null)
        {
            tracks.Add(new TrackSettings { Title = "Game", Kind = AudioTrackKind.Game, Processes = SplitList(game, "--game") });
        }

        tracks.AddRange(apps);
        if (mic is not null)
        {
            tracks.Add(mic);
        }

        if (system)
        {
            tracks.Add(new TrackSettings { Title = "System", Kind = AudioTrackKind.System });
        }

        if (noAudio && tracks.Count > 0)
        {
            throw new UsageException("--no-audio cannot be combined with --game, --app, --mic or --system.");
        }

        if (noAudio || tracks.Count > 0)
        {
            s.Audio.Tracks = tracks;
        }
    }

    private static TrackSettings ParseApp(string spec)
    {
        int equals = spec.IndexOf('=');
        if (equals <= 0 || equals == spec.Length - 1)
        {
            throw new UsageException($"--app needs <Title>=<exe|pid>, e.g. --app Discord=Discord.exe; got '{spec}'.");
        }

        return new TrackSettings
        {
            Title = spec[..equals].Trim(),
            Kind = AudioTrackKind.App,
            Processes = SplitList(spec[(equals + 1)..], "--app"),
        };
    }

    /// <summary>"a.exe, b.exe" → [a.exe, b.exe].</summary>
    private static List<string> SplitList(string text, string option)
    {
        List<string> items = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        return items.Count > 0 ? items : throw new UsageException($"{option} needs a value, got '{text}'.");
    }

    private static string Value(string[] args, ref int i, string option) =>
        i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : throw new UsageException($"{option} needs a value.");

    private static double Number(string[] args, ref int i, string option, Limit limit)
    {
        string text = Value(args, ref i, option);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && limit.Contains(value)
            ? value
            : throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"{option} needs a number from {limit.Min} to {limit.Max}, got '{text}'."));
    }

    private static int Integer(string[] args, ref int i, string option, Limit limit) => ParseInteger(Value(args, ref i, option), option, limit);

    private static int ParseInteger(string text, string option, Limit limit) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && limit.Contains(value)
            ? value
            : throw new UsageException($"{option} needs a whole number from {limit.Min} to {limit.Max}, got '{text}'.");
}
