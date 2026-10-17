using System.Globalization;
using SplitTrack.Common.Audio;

namespace SplitTrack.AudioPoc.Commands;

internal sealed class RecordOptions
{
    public List<string> Processes { get; } = [];

    public string? Exclude { get; private set; }

    public bool SystemAll { get; private set; }

    public bool Mic { get; private set; }

    public string? MicFilter { get; private set; }

    public double? Seconds { get; private set; }

    public string OutputDirectory { get; private set; } = Environment.CurrentDirectory;

    public AudioFormat Format { get; private set; } = AudioFormat.Float48Stereo;

    public static RecordOptions Parse(string[] args)
    {
        var options = new RecordOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--process":
                    options.Processes.Add(Value(args, ref i, arg));
                    break;
                case "--exclude":
                    options.Exclude = options.Exclude is null
                        ? Value(args, ref i, arg)
                        : throw new UsageException("--exclude can only be given once (Windows excludes one process tree per stream).");
                    break;
                case "--system-all":
                    options.SystemAll = true;
                    break;
                case "--mic":
                    options.Mic = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options.MicFilter = args[++i];
                    }

                    break;
                case "--seconds":
                    string seconds = Value(args, ref i, arg);
                    options.Seconds = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0
                        ? s
                        : throw new UsageException($"--seconds needs a positive number, got '{seconds}'.");
                    break;
                case "--out":
                    options.OutputDirectory = Path.GetFullPath(Value(args, ref i, arg));
                    break;
                case "--format":
                    options.Format = Value(args, ref i, arg).ToLowerInvariant() switch
                    {
                        "float48" => AudioFormat.Float48Stereo,
                        "pcm44" => AudioFormat.Pcm16Stereo44,
                        var other => throw new UsageException($"Unknown --format '{other}' (use float48 or pcm44)."),
                    };
                    break;
                default:
                    throw new UsageException($"Unknown option '{arg}'.");
            }
        }

        if (options.Processes.Count == 0 && options.Exclude is null && !options.SystemAll && !options.Mic)
        {
            throw new UsageException("Nothing to record: add --process, --exclude, --system-all or --mic.");
        }

        return options;
    }

    private static string Value(string[] args, ref int i, string option) =>
        i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : throw new UsageException($"{option} needs a value.");
}
