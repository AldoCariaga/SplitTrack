using System.Diagnostics;
using System.Globalization;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Encode.Video;

namespace SplitTrack.Export;

/// <param name="DoneSeconds">Output time written so far.</param>
/// <param name="Speed">Times real time, when ffmpeg knows it.</param>
public readonly record struct ToolProgress(double DoneSeconds, double? Speed, long Bytes);

/// <summary>Runs ffmpeg.exe (the BtbN build whose DLLs the recorder uses) and follows its progress.</summary>
public sealed class FFmpegTool(string exePath)
{
    private const int KeptErrorLines = 15;

    private readonly Lock _gate = new();
    private VideoEncoderKind? _autoEncoder;

    public string ExePath { get; } = exePath;

    /// <summary>ffmpeg.exe in the <c>ffmpeg</c> folder next to this program.</summary>
    /// <exception cref="FileNotFoundException">It is not there.</exception>
    public static FFmpegTool FindDefault()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        return File.Exists(path)
            ? new FFmpegTool(path)
            : throw new FileNotFoundException($"ffmpeg.exe is missing: {path} not found. Reinstall SplitTrack, or copy the 'ffmpeg' folder next to the exe.", path);
    }

    /// <summary>Runs ffmpeg with <paramref name="arguments"/>; cancelling kills it.</summary>
    /// <exception cref="ExportException">ffmpeg failed; the message has its last error lines.</exception>
    public async Task RunAsync(IReadOnlyList<string> arguments, Action<ToolProgress>? progress, CancellationToken cancellation)
    {
        Log.Info($"ffmpeg {string.Join(' ', arguments.Select(Quote))}");
        using Process process = Start(arguments);
        var errors = new Queue<string>();
        Task stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
            {
                Log.Debug($"ffmpeg: {line}");
                lock (errors)
                {
                    errors.Enqueue(line);
                    if (errors.Count > KeptErrorLines)
                    {
                        errors.Dequeue();
                    }
                }
            }
        }, CancellationToken.None);
        Task stdout = ReadProgressAsync(process.StandardOutput, progress);

        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            Log.Info("ffmpeg cancelled");
            throw;
        }
        finally
        {
            await Task.WhenAll(stderr, stdout);
        }

        if (process.ExitCode != 0)
        {
            string detail;
            lock (errors)
            {
                detail = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "(no output)";
            }

            throw new ExportException($"ffmpeg failed (exit code {process.ExitCode}):{Environment.NewLine}{detail}");
        }
    }

    /// <summary>Whether ffmpeg.exe can encode a few H.264 frames with <paramref name="kind"/> on this PC.</summary>
    public bool TestEncoder(VideoEncoderKind kind)
    {
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30:d=0.2",
            "-c:v", kind.CodecName(), "-f", "null", "-",
        ];
        using Process process = Start(arguments);
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
        {
            process.Kill(entireProcessTree: true);
            Log.Info($"Export encoder {kind.ShortName()}: test timed out");
            return false;
        }

        bool ok = process.ExitCode == 0;
        Log.Info($"Export encoder {kind.ShortName()}: {(ok ? "OK" : $"unavailable ({stderr.Result.Trim().ReplaceLineEndings(" | ")})")}");
        return ok;
    }

    /// <summary>The first encoder that works, GPU ones first (AMF, NVENC, QSV), else x264. Tested once per instance.</summary>
    public VideoEncoderKind SelectEncoder()
    {
        lock (_gate)
        {
            _autoEncoder ??= VideoEncoderKinds.All.FirstOrDefault(k => k != VideoEncoderKind.X264 && TestEncoder(k), VideoEncoderKind.X264);
            return _autoEncoder.Value;
        }
    }

    // No window: Ctrl+C in our console then reaches only us, and we decide whether ffmpeg stops.
    private Process Start(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new ExportException($"Could not start {ExePath}.");
    }

    // -progress pipe:1 writes key=value blocks, each ending with progress=continue or progress=end.
    private static async Task ReadProgressAsync(StreamReader output, Action<ToolProgress>? progress)
    {
        double done = 0;
        double? speed = null;
        long bytes = 0;
        while (await output.ReadLineAsync() is { } line)
        {
            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string key = line[..equals];
            string value = line[(equals + 1)..].Trim();
            switch (key)
            {
                case "out_time_us" when long.TryParse(value, CultureInfo.InvariantCulture, out long us):
                    done = Math.Max(0, us / 1_000_000.0);
                    break;
                case "total_size" when long.TryParse(value, CultureInfo.InvariantCulture, out long size):
                    bytes = size;
                    break;
                case "speed":
                    speed = double.TryParse(value.TrimEnd('x'), CultureInfo.InvariantCulture, out double x) ? x : null;
                    break;
                case "progress":
                    progress?.Invoke(new ToolProgress(done, speed, bytes));
                    break;
            }
        }
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or ';' or '[') ? argument : $"\"{argument}\"";
}
