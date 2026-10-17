using System.Globalization;
using SplitTrack.Encode.Video;

namespace SplitTrack.VideoPoc.Commands;

internal sealed class RecordOptions
{
    public int? Monitor { get; private set; }

    public string? Window { get; private set; }

    public int Fps { get; private set; } = 60;

    public int BitrateKbps { get; private set; } = 20_000;

    public int? Height { get; private set; }

    /// <summary>Null means auto.</summary>
    public VideoEncoderKind? Encoder { get; private set; }

    public bool Cursor { get; private set; } = true;

    public int Adapter { get; private set; }

    public double? Seconds { get; private set; }

    public string OutputDirectory { get; private set; } = Environment.CurrentDirectory;

    public static RecordOptions Parse(string[] args)
    {
        var options = new RecordOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--monitor":
                    options.Monitor = Integer(args, ref i, arg, 0, 63);
                    break;
                case "--window":
                    options.Window = Value(args, ref i, arg);
                    break;
                case "--fps":
                    options.Fps = Integer(args, ref i, arg, 1, 240);
                    break;
                case "--bitrate":
                    options.BitrateKbps = (int)Math.Round(Number(args, ref i, arg, 0.5, 200) * 1000);
                    break;
                case "--height":
                    options.Height = Integer(args, ref i, arg, 144, 4320);
                    break;
                case "--encoder":
                    string encoder = Value(args, ref i, arg);
                    options.Encoder = encoder.Equals("auto", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : VideoEncoderKinds.Parse(encoder)
                          ?? throw new UsageException($"Unknown --encoder '{encoder}' (use auto, amf, nvenc, qsv or x264).");
                    break;
                case "--no-cursor":
                    options.Cursor = false;
                    break;
                case "--adapter":
                    options.Adapter = Integer(args, ref i, arg, 0, 15);
                    break;
                case "--seconds":
                    options.Seconds = Number(args, ref i, arg, 0.1, 24 * 3600);
                    break;
                case "--out":
                    options.OutputDirectory = Path.GetFullPath(Value(args, ref i, arg));
                    break;
                default:
                    throw new UsageException($"Unknown option '{arg}'.");
            }
        }

        if (options.Monitor is not null && options.Window is not null)
        {
            throw new UsageException("Use either --monitor or --window, not both.");
        }

        return options;
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
