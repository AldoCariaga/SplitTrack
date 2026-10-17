using System.Globalization;
using SplitTrack.Encode.Video;

namespace SplitTrack.ExportCli.Commands;

/// <param name="Spec">Title or 1-based number, resolved once the file is open.</param>
/// <param name="Volume">1 = 100 %.</param>
internal sealed record TrackChoice(string Spec, double Volume);

/// <summary>The export command line, before the source file is opened.</summary>
internal sealed class ExportOptions
{
    public required string File { get; init; }

    /// <summary>Null: every track at 100 %.</summary>
    public List<TrackChoice>? Tracks { get; private set; }

    public bool NoAudio { get; private set; }

    public int AudioBitrateKbps { get; private set; } = 160;

    public double? From { get; private set; }

    public double? To { get; private set; }

    public bool Precise { get; private set; }

    public long? MaxBytes { get; private set; }

    public int? Height { get; private set; }

    public int? VideoBitrateKbps { get; private set; }

    public bool Reencode { get; private set; }

    /// <summary>Null: auto.</summary>
    public VideoEncoderKind? Encoder { get; private set; }

    public string? Out { get; private set; }

    public bool Overwrite { get; private set; }

    public static ExportOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException("export needs a file: the recording's .mkv or its .json sidecar.");
        }

        var options = new ExportOptions { File = args[0] };
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--tracks":
                    options.Tracks ??= [];
                    options.Tracks.AddRange(Value(args, ref i, arg).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ParseTrack));
                    break;
                case "--no-audio":
                    options.NoAudio = true;
                    break;
                case "--audio-bitrate":
                    options.AudioBitrateKbps = (int)Number(args, ref i, arg, 64, 320);
                    break;
                case "--from":
                    options.From = Time(args, ref i, arg);
                    break;
                case "--to":
                    options.To = Time(args, ref i, arg);
                    break;
                case "--precise":
                    options.Precise = true;
                    break;
                case "--size":
                    options.MaxBytes = (long)(Number(args, ref i, arg, 0.1, 100_000) * 1_000_000);
                    break;
                case "--height":
                    options.Height = (int)Number(args, ref i, arg, 144, 4320);
                    break;
                case "--bitrate":
                    options.VideoBitrateKbps = (int)(Number(args, ref i, arg, 0.2, 200) * 1000);
                    break;
                case "--reencode":
                    options.Reencode = true;
                    break;
                case "--encoder":
                    string encoder = Value(args, ref i, arg);
                    options.Encoder = encoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : VideoEncoderKinds.Parse(encoder) ?? throw new UsageException($"--encoder: '{encoder}' is not one of auto, amf, nvenc, qsv, x264.");
                    break;
                case "--out":
                    options.Out = Value(args, ref i, arg);
                    break;
                case "--overwrite":
                    options.Overwrite = true;
                    break;
                default:
                    throw new UsageException(arg.StartsWith("--", StringComparison.Ordinal)
                        ? $"Unknown option '{arg}'."
                        : $"Unexpected '{arg}': export takes one file (put names with spaces in quotes).");
            }
        }

        if (options.NoAudio && options.Tracks is not null)
        {
            throw new UsageException("--no-audio cannot be combined with --tracks.");
        }

        if (options is { From: { } from, To: { } to } && to <= from)
        {
            throw new UsageException("--to must be after --from.");
        }

        return options;
    }

    /// <summary>Seconds ("75.5") or colon-separated "m:ss", "h:mm:ss", each with an optional fraction.</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        string[] parts = text.Split(':');
        if (parts.Length > 3)
        {
            return false;
        }

        for (int p = 0; p < parts.Length; p++)
        {
            bool last = p == parts.Length - 1;
            NumberStyles style = last ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            if (!double.TryParse(parts[p], style, CultureInfo.InvariantCulture, out double value) || (p > 0 && value >= 60))
            {
                return false;
            }

            seconds = (seconds * 60) + value;
        }

        return true;
    }

    private static TrackChoice ParseTrack(string spec)
    {
        // The volume follows the last '=', so a title may itself contain one.
        int equals = spec.LastIndexOf('=');
        if (equals < 0)
        {
            return new TrackChoice(spec, 1);
        }

        string volume = spec[(equals + 1)..].Trim().TrimEnd('%');
        return double.TryParse(volume, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double percent) && percent <= 400
            ? new TrackChoice(spec[..equals].Trim(), percent / 100)
            : throw new UsageException($"--tracks: the volume in '{spec}' must be a percentage from 0 to 400, e.g. Mic=150.");
    }

    private static double Time(string[] args, ref int i, string option)
    {
        string text = Value(args, ref i, option);
        return TryParseTime(text, out double seconds)
            ? seconds
            : throw new UsageException($"{option} needs a time such as 75.5, 1:15.5 or 1:02:03, got '{text}'.");
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
            : throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"{option} needs a number from {min} to {max}, got '{text}'."));
    }
}
