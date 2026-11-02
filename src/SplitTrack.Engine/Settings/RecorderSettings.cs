using System.Globalization;
using System.Text.Json.Serialization;
using SplitTrack.Capture.Audio.Sessions;
using SplitTrack.Encode.Video;
using SplitTrack.Engine.Input;
using SplitTrack.Engine.Recording;
using LibraryFolderPaths = SplitTrack.Library.LibraryFolder;

namespace SplitTrack.Engine.Settings;

/// <summary>
/// What recording and replay sessions are configured by; stored as settings.json by <see cref="SettingsFile"/>.
/// Plain mutable data so a UI or the command line can change it; <see cref="Validate"/> before use.
/// </summary>
public sealed class RecorderSettings
{
    /// <summary>Version 2 moved video, audio and replay into presets.</summary>
    public const int CurrentVersion = 2;

    public const string DefaultPresetName = "Default";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Where recordings and clips go; null means Videos\SplitTrack. %VARIABLES% are expanded.</summary>
    public string? LibraryFolder { get; set; }

    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>The preset in use while none of the presets' games runs; chosen by hand.</summary>
    public string ActivePreset { get; set; } = DefaultPresetName;

    public List<PresetSettings> Presets { get; set; } = [new()];

    /// <summary>The <see cref="ActivePreset"/>, or the first one when that name is missing (see <see cref="Validate"/>).</summary>
    [JsonIgnore]
    public PresetSettings Active => Find(ActivePreset) ?? Presets[0];

    /// <summary>The active preset's video settings.</summary>
    [JsonIgnore]
    public VideoSettings Video => Active.Video;

    /// <summary>The active preset's audio settings.</summary>
    [JsonIgnore]
    public AudioSettings Audio => Active.Audio;

    /// <summary>The active preset's replay settings.</summary>
    [JsonIgnore]
    public ReplaySettings Replay => Active.Replay;

    [JsonIgnore]
    public string LibraryRoot => string.IsNullOrWhiteSpace(LibraryFolder)
        ? LibraryFolderPaths.DefaultRoot
        : Path.GetFullPath(Environment.ExpandEnvironmentVariables(LibraryFolder.Trim()));

    // Version 1 kept these at the top level; SettingsFile.Load moves them into a preset.
    [JsonInclude, JsonPropertyName("video"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal VideoSettings? V1Video { get; set; }

    [JsonInclude, JsonPropertyName("audio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal AudioSettings? V1Audio { get; set; }

    [JsonInclude, JsonPropertyName("replay"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal ReplaySettings? V1Replay { get; set; }

    /// <summary>Null when there is no preset of that name (ignoring case and surrounding spaces).</summary>
    public PresetSettings? Find(string? name) => Presets.FirstOrDefault(p => p is not null && NameEquals(p.Name, name));

    public static bool NameEquals(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything wrong with these settings, one message per problem, named by JSON path; empty when usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        try
        {
            if (LibraryFolder?.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new ArgumentException("it contains a character not allowed in paths");
            }

            _ = LibraryRoot;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"libraryFolder: '{LibraryFolder}' is not a usable folder path ({ex.Message})");
        }

        Hotkeys.Validate(errors);
        ValidatePresets(errors);
        return errors;
    }

    /// <param name="outputPath">The whole session as one MKV, or null for none.</param>
    /// <param name="replayBuffer">Keep a replay buffer that clips can be saved from.</param>
    /// <param name="preset">What to capture; null for the active preset.</param>
    public RecordingOptions ToRecordingOptions(string? outputPath, bool replayBuffer, PresetSettings? preset = null)
    {
        preset ??= Active;
        return new(
            outputPath,
            preset.Video.ToOptions(),
            preset.Audio.ToOptions(),
            preset.Audio.BitrateKbps,
            replayBuffer ? new ReplayOptions(preset.Replay.BufferSeconds, LibraryRoot) : null);
    }

    /// <summary>The first preset one of whose games is running, with that game's exe name.</summary>
    public (PresetSettings Preset, string Game)? FindRunningGame(ProcessTree tree)
    {
        foreach (PresetSettings preset in Presets)
        {
            if (preset.Games.FirstOrDefault(g => tree.FindByExe(g).Count > 0) is { } game)
            {
                return (preset, ProcessTree.NormalizeExeName(game));
            }
        }

        return null;
    }

    /// <summary>Makes sections set to null in the file usable again (their defaults).</summary>
    internal void FillMissing()
    {
        Hotkeys ??= new();
        Presets ??= [];
        foreach (PresetSettings? preset in Presets)
        {
            preset?.FillMissing();
        }
    }

    /// <summary>A version 1 file's top-level video, audio and replay become the only preset.</summary>
    internal void UpgradeFromVersion1()
    {
        Presets = [new PresetSettings { Video = V1Video ?? new(), Audio = V1Audio ?? new(), Replay = V1Replay ?? new() }];
        ActivePreset = DefaultPresetName;
        V1Video = null;
        V1Audio = null;
        V1Replay = null;
    }

    /// <summary>Top-level video, audio or replay in a file of version 2 or later, which belong in a preset.</summary>
    internal bool HasVersion1Sections => V1Video is not null || V1Audio is not null || V1Replay is not null;

    private void ValidatePresets(List<string> errors)
    {
        if (Presets.Count == 0)
        {
            errors.Add("presets: there must be at least one preset");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gameOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Presets.Count; i++)
        {
            if (Presets[i] is not { } preset)
            {
                errors.Add($"presets[{i}]: is empty");
                continue;
            }

            string name = preset.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                errors.Add($"presets[{i}].name: is missing");
            }
            else if (!names.Add(name))
            {
                errors.Add($"presets[{i}].name: '{name}' is used by another preset; names must be unique");
            }

            // With one preset its name adds nothing to the messages.
            var presetErrors = new List<string>();
            preset.Validate(presetErrors);
            errors.AddRange(Presets.Count == 1 ? presetErrors : presetErrors.Select(e => $"preset '{name}': {e}"));

            foreach (string game in preset.Games.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => ProcessTree.NormalizeExeName(g.Trim())))
            {
                if (gameOwners.TryGetValue(game, out string? owner) && !NameEquals(owner, name))
                {
                    errors.Add($"preset '{name}': games: {game} is also in preset '{owner}'; a game can switch to one preset only");
                }

                gameOwners.TryAdd(game, name);
            }
        }

        if (Find(ActivePreset) is null)
        {
            errors.Add($"activePreset: there is no preset named '{ActivePreset}'");
        }
    }

    internal static void CheckRange(List<string> errors, string name, double value, Limit limit)
    {
        if (!limit.Contains(value))
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: must be from {limit.Min} to {limit.Max}, got {value}"));
        }
    }
}

/// <summary>A named capture setup: video, audio tracks and the replay buffer, and the games that switch to it.</summary>
public sealed class PresetSettings
{
    public string Name { get; set; } = RecorderSettings.DefaultPresetName;

    /// <summary>Exe names; while one of them runs, the app uses this preset. Empty: chosen by hand only.</summary>
    public List<string> Games { get; set; } = [];

    public VideoSettings Video { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public ReplaySettings Replay { get; set; } = new();

    internal void FillMissing()
    {
        Games ??= [];
        Video ??= new();
        Audio ??= new();
        Audio.Tracks ??= [];
        Replay ??= new();
    }

    internal void Validate(List<string> errors)
    {
        Video.Validate(errors);
        Audio.Validate(errors);
        Replay.Validate(errors);

        if (!Video.Enabled && Audio.Tracks.Count == 0)
        {
            errors.Add("Nothing to record: video is off (video.enabled) and audio.tracks is empty.");
        }

        if (Games.Any(AudioTrackOptions.IsFullScreen))
        {
            errors.Add($"games: '{AudioTrackOptions.FullScreen}' only works in a game track; list exe names here");
        }
    }
}

/// <summary>Allowed values, shared by settings validation and the command line.</summary>
public readonly record struct Limit(double Min, double Max)
{
    public static readonly Limit Monitor = new(0, 63);
    public static readonly Limit Adapter = new(0, 15);
    public static readonly Limit Fps = new(1, 240);
    public static readonly Limit BitrateMbps = new(0.5, 200);
    public static readonly Limit MaxHeight = new(144, 4320);
    public static readonly Limit AudioBitrateKbps = new(32, 512);
    public static readonly Limit BufferSeconds = new(5, 600);

    public bool Contains(double value) => value >= Min && value <= Max;
}

public sealed class VideoSettings
{
    /// <summary>False records audio only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Monitor index from 'list'; used while <see cref="Window"/> is empty.</summary>
    public int Monitor { get; set; }

    /// <summary>Capture this window (handle, exe name or title text) instead of a monitor.</summary>
    public string? Window { get; set; }

    public int Fps { get; set; } = 60;

    public double BitrateMbps { get; set; } = 20;

    /// <summary>Scale down to this height, keeping the aspect ratio; null keeps the capture size.</summary>
    public int? MaxHeight { get; set; }

    /// <summary>auto, amf, nvenc, qsv or x264.</summary>
    public string Encoder { get; set; } = "auto";

    public bool Cursor { get; set; } = true;

    /// <summary>GPU index from 'list'.</summary>
    public int Adapter { get; set; }

    public VideoOptions? ToOptions() => Enabled
        ? new VideoOptions(
            Monitor,
            string.IsNullOrWhiteSpace(Window) ? null : Window.Trim(),
            Fps,
            (int)Math.Round(BitrateMbps * 1000),
            MaxHeight,
            ParseEncoder(Encoder),
            Cursor,
            Adapter)
        : null;

    internal void Validate(List<string> errors)
    {
        RecorderSettings.CheckRange(errors, "video.monitor", Monitor, Limit.Monitor);
        RecorderSettings.CheckRange(errors, "video.fps", Fps, Limit.Fps);
        RecorderSettings.CheckRange(errors, "video.bitrateMbps", BitrateMbps, Limit.BitrateMbps);
        if (MaxHeight is { } height)
        {
            RecorderSettings.CheckRange(errors, "video.maxHeight", height, Limit.MaxHeight);
        }

        RecorderSettings.CheckRange(errors, "video.adapter", Adapter, Limit.Adapter);
        try
        {
            ParseEncoder(Encoder);
        }
        catch (FormatException ex)
        {
            errors.Add($"video.encoder: {ex.Message}");
        }
    }

    /// <summary>Null for auto.</summary>
    public static VideoEncoderKind? ParseEncoder(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : VideoEncoderKinds.Parse(name.Trim())
              ?? throw new FormatException($"unknown encoder '{name}' (use auto, amf, nvenc, qsv or x264)");
}

public sealed class AudioSettings
{
    /// <summary>AAC bitrate of each track.</summary>
    public int BitrateKbps { get; set; } = 160;

    /// <summary>In the order they appear in the file; the first one is what plain players play.</summary>
    public List<TrackSettings> Tracks { get; set; } =
    [
        new() { Title = "Game", Kind = AudioTrackKind.Game, Processes = [AudioTrackOptions.FullScreen] },
        new() { Title = "Mic", Kind = AudioTrackKind.Microphone },
        new() { Title = "System", Kind = AudioTrackKind.System },
    ];

    public IReadOnlyList<AudioTrackOptions> ToOptions() => Tracks.Select(t => t.ToOptions()).ToList();

    internal void Validate(List<string> errors)
    {
        RecorderSettings.CheckRange(errors, "audio.bitrateKbps", BitrateKbps, Limit.AudioBitrateKbps);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Tracks.Count; i++)
        {
            string name = $"audio.tracks[{i}]";
            if (Tracks[i] is not { } track)
            {
                errors.Add($"{name}: is empty");
                continue;
            }

            track.Validate(errors, name);
            if (!string.IsNullOrWhiteSpace(track.Title) && !titles.Add(track.Title.Trim()))
            {
                errors.Add($"{name}.title: '{track.Title.Trim()}' is used by another track; titles must be unique");
            }
        }

        foreach (AudioTrackKind single in (AudioTrackKind[])[AudioTrackKind.Game, AudioTrackKind.System])
        {
            if (Tracks.Count(t => t?.Kind == single) > 1)
            {
                errors.Add($"audio.tracks: only one track can be of kind '{SettingsFile.KindName(single)}'");
            }
        }
    }
}

/// <summary>One audio track: what it records and what it is called in the file.</summary>
public sealed class TrackSettings
{
    public string Title { get; set; } = string.Empty;

    /// <summary>game, app, microphone or system; required.</summary>
    public AudioTrackKind? Kind { get; set; }

    /// <summary>Game / app: exe names or PIDs; the first one running is recorded. Game: "auto" is the full-screen program.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Processes { get; set; }

    /// <summary>Microphone: device name parts; the first one present is recorded. Empty or missing: the default microphone.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Devices { get; set; }

    /// <summary>
    /// System: false records everything except the game in one capture, app tracks included, so no
    /// Windows sound is missed. Missing or true leaves the app tracks out too.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LeaveOutApps { get; set; }

    public AudioTrackOptions ToOptions()
    {
        AudioTrackKind kind = Kind ?? throw new InvalidOperationException($"Track '{Title}' has no kind; validate first.");
        List<string>? targets = kind switch
        {
            AudioTrackKind.Game or AudioTrackKind.App => Processes,
            AudioTrackKind.Microphone => Devices,
            _ => null,
        };
        return new AudioTrackOptions(Title.Trim(), kind, Clean(targets)) { LeaveOutApps = LeaveOutApps ?? true };
    }

    internal void Validate(List<string> errors, string name)
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            errors.Add($"{name}.title: is missing");
        }

        if (Kind is not { } kind)
        {
            errors.Add($"{name}.kind: is missing (game, app, microphone or system)");
            return;
        }

        bool process = kind is AudioTrackKind.Game or AudioTrackKind.App;
        if (process && Clean(Processes).Count == 0)
        {
            errors.Add($"{name}.processes: a {SettingsFile.KindName(kind)} track needs at least one exe name or PID");
        }

        if (!process && Clean(Processes).Count > 0)
        {
            errors.Add($"{name}.processes: only game and app tracks record processes");
        }

        if (kind == AudioTrackKind.App && Clean(Processes).Any(AudioTrackOptions.IsFullScreen))
        {
            errors.Add($"{name}.processes: '{AudioTrackOptions.FullScreen}' (the full-screen game) is only for the game track");
        }

        if (kind != AudioTrackKind.Microphone && Clean(Devices).Count > 0)
        {
            errors.Add($"{name}.devices: only microphone tracks record devices");
        }

        if (kind != AudioTrackKind.System && LeaveOutApps is not null)
        {
            errors.Add($"{name}.leaveOutApps: only the system track leaves app tracks out");
        }
    }

    private static List<string> Clean(List<string>? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList() ?? [];
}

public sealed class ReplaySettings
{
    /// <summary>The app keeps the replay buffer running while it is open (RecordCli's replay command ignores this).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds kept in memory, i.e. the length of a saved clip.</summary>
    public double BufferSeconds { get; set; } = 60;

    /// <summary>While the replay buffer runs, also record the whole session to a file.</summary>
    public bool AlsoRecord { get; set; }

    internal void Validate(List<string> errors) =>
        RecorderSettings.CheckRange(errors, "replay.bufferSeconds", BufferSeconds, Limit.BufferSeconds);
}

public sealed class HotkeySettings
{
    /// <summary>Saves a clip from the replay buffer, e.g. Alt+F10 or Ctrl+Shift+F8.</summary>
    public string SaveClip { get; set; } = "Alt+F10";

    /// <summary>Starts or stops recording to a file in the app; empty for none.</summary>
    public string? ToggleRecording { get; set; } = "Alt+F9";

    [JsonIgnore]
    public HotkeyGesture SaveClipGesture => HotkeyGesture.Parse(SaveClip);

    /// <summary>Null when none is set.</summary>
    [JsonIgnore]
    public HotkeyGesture? ToggleRecordingGesture => string.IsNullOrWhiteSpace(ToggleRecording) ? null : HotkeyGesture.Parse(ToggleRecording);

    internal void Validate(List<string> errors)
    {
        HotkeyGesture? saveClip = null;
        try
        {
            saveClip = HotkeyGesture.Parse(SaveClip ?? string.Empty);
        }
        catch (FormatException ex)
        {
            errors.Add($"hotkeys.saveClip: {ex.Message}");
        }

        try
        {
            if (ToggleRecordingGesture is { } toggle && toggle == saveClip)
            {
                errors.Add("hotkeys.toggleRecording: is the same as hotkeys.saveClip");
            }
        }
        catch (FormatException ex)
        {
            errors.Add($"hotkeys.toggleRecording: {ex.Message}");
        }
    }
}
