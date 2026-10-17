using System.Diagnostics;
using System.Globalization;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Encode.Video;

namespace SplitTrack.Export;

/// <summary>
/// Turns a recording into a plain MP4: chosen tracks mixed into one AAC track, video copied when it
/// can be (re-encoded for an exact start, a smaller size or height), moov at the front.
/// </summary>
public sealed class Exporter(FFmpegTool tool)
{
    /// <summary>Below this the picture is mush; a size limit that needs less is refused.</summary>
    public const int MinVideoKbps = 150;

    private const int MaxAttempts = 3;

    // Share of a size limit given to the streams; the rest covers MP4 overhead and encoder overshoot.
    private const double SizeMargin = 0.96;

    // Assumed for audio streams whose file does not say (Matroska); what the recorder writes.
    private const int AssumedAudioKbps = 160;

    // The audio input is opened this much before the start and trimmed to the sample (see BuildArguments).
    private const double AudioPreroll = 2;

    /// <summary>Decides copy or re-encode, the exact start and the bitrate. May test encoders (a second or two).</summary>
    /// <exception cref="ExportException">The range, the tracks or the size limit cannot work.</exception>
    public ExportPlan Plan(ExportRequest request)
    {
        MediaInfo media = request.Source.Media;
        double total = media.DurationSeconds;
        double from = request.From ?? 0;
        double to = Math.Min(request.To ?? total, total);
        if (from < 0 || from >= total)
        {
            throw new ExportException($"The start ({Time(from)}) is not inside the recording (0 to {Time(total)}).");
        }

        if (to <= from)
        {
            throw new ExportException($"The end ({Time(to)}) must come after the start ({Time(from)}).");
        }

        if (media.Video is not { } video)
        {
            return request.Tracks.Count > 0
                ? new ExportPlan(request, from, to, VideoMode.None, null, null, null, [])
                : throw new ExportException("Nothing to export: the file has no video and no audio track is chosen.");
        }

        var notes = new List<string>();
        int? height = request.MaxHeight is { } h && h < video.Height ? Math.Max(2, h & ~1) : null;
        bool encode = request.Reencode || request.VideoBitrateKbps is not null || height is not null;
        double keyframe = from > 0 ? media.KeyframeAtOrBefore(from) ?? 0 : 0;
        bool onKeyframe = from - keyframe < 0.5 / Math.Max(1, video.Fps);
        encode |= request.Precise && !onKeyframe;

        int audioKbps = request.Tracks.Count > 0 ? request.AudioBitrateKbps : 0;
        if (request.MaxBytes is { } max && !encode)
        {
            long estimate = EstimateCopyBytes(media, to - keyframe, audioKbps);
            if (estimate > max * SizeMargin)
            {
                notes.Add($"A copy would be about {Megabytes(estimate)}; re-encoding to fit {Megabytes(max)}.");
                encode = true;
            }
        }

        var plan = new ExportPlan(request, keyframe, to, VideoMode.Copy, null, null, null, notes);
        if (encode)
        {
            return Encoding(plan, from, height);
        }

        if (!onKeyframe)
        {
            notes.Add($"Start moved back {from - keyframe:0.000} s to the keyframe at {Time(keyframe)}; an exact start re-encodes the video.");
        }

        return plan;
    }

    /// <summary>Runs the plan into <c>OutputPath</c> (through a .partial file); redoes it at a lower bitrate if it came out too large.</summary>
    /// <exception cref="ExportException">ffmpeg failed, or the size limit could not be met.</exception>
    public async Task<ExportResult> RunAsync(ExportPlan plan, Action<ExportProgress>? progress, CancellationToken cancellation)
    {
        ExportRequest request = plan.Request;
        string partial = request.OutputPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
        var elapsed = Stopwatch.StartNew();
        long previousBytes = long.MaxValue;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                ExportPlan current = plan;
                Log.Info($"Export attempt {attempt}: {Describe(current)} -> {request.OutputPath}");
                await tool.RunAsync(
                    BuildArguments(current, partial),
                    p => progress?.Invoke(new ExportProgress(current, attempt, p.DoneSeconds, p.Speed, p.Bytes)),
                    cancellation);

                long bytes = new FileInfo(partial).Length;
                if (request.MaxBytes is { } max && bytes > max)
                {
                    // GPU encoders stop at their highest quantiser; asking for less then changes nothing.
                    if (current.Video == VideoMode.Encode && bytes > previousBytes * 0.98)
                    {
                        throw new ExportException(
                            $"{current.Encoder?.ShortName()} cannot make this video smaller than about {Megabytes(bytes)} " +
                            $"(limit {Megabytes(max)}); try the x264 encoder, a smaller height or a shorter part.");
                    }

                    if (attempt >= MaxAttempts)
                    {
                        throw new ExportException($"The export is still {Megabytes(bytes)}, over the {Megabytes(max)} limit, after {attempt} tries.");
                    }

                    previousBytes = current.Video == VideoMode.Encode ? bytes : long.MaxValue;
                    plan = plan.Video == VideoMode.Copy
                        ? Encoding(plan with { Notes = [$"The copy came out at {Megabytes(bytes)}; re-encoding to fit."] }, request.From ?? 0, null)
                        : Shrink(plan, bytes, max);
                    Log.Info($"Export: {Megabytes(bytes)} is over the {Megabytes(max)} limit, trying again");
                    continue;
                }

                File.Move(partial, request.OutputPath, overwrite: true);
                Log.Info($"Exported {request.OutputPath}: {Megabytes(bytes)} in {elapsed.Elapsed.TotalSeconds:0.0} s");
                return new ExportResult(request.OutputPath, bytes, current, attempt, elapsed.Elapsed);
            }
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>The ffmpeg.exe arguments for one attempt.</summary>
    public static List<string> BuildArguments(ExportPlan plan, string outputPath)
    {
        ExportRequest request = plan.Request;
        string source = request.Source.VideoPath;
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning", "-nostats", "-progress", "pipe:1", "-y" };

        bool video = plan.Video != VideoMode.None;
        if (video)
        {
            // A copy's start is a keyframe time, so seeking lands exactly on it; an encode decodes up to it.
            if (plan.Start > 0)
            {
                args.AddRange(["-ss", Seconds(plan.Start)]);
            }

            args.AddRange(["-i", source]);
        }

        // Seeking the input the audio comes from drops the AAC frame across the seek point and moved
        // the audio ~13 ms early in tests; a second input opened earlier and trimmed is sample-exact.
        int audioInput = 0;
        double audioTrim = 0;
        if (request.Tracks.Count > 0 && (!video || plan.Start > 0))
        {
            double seek = Math.Max(0, plan.Start - AudioPreroll);
            audioInput = video ? 1 : 0;
            audioTrim = plan.Start - seek;
            if (seek > 0)
            {
                args.AddRange(["-ss", Seconds(seek)]);
            }

            args.AddRange(["-i", source]);
        }

        args.AddRange(["-t", Seconds(plan.Duration)]);

        if (video)
        {
            args.AddRange(["-map", $"0:{request.Source.Media.Video!.Index}"]);
            args.AddRange(plan.Video == VideoMode.Copy ? ["-c:v", "copy"] : VideoEncodeArguments(plan));
        }

        if (request.Tracks.Count > 0)
        {
            args.AddRange(["-filter_complex", MixGraph(request.Tracks, audioInput, audioTrim), "-map", "[aout]"]);
            args.AddRange(["-c:a", "aac", "-b:a", $"{request.AudioBitrateKbps}k", "-ar", "48000", "-ac", "2"]);
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart", "-f", "mp4", outputPath]);
        return args;
    }

    /// <summary>Volume per track, then a plain sum (amix normalize=0, so levels match the recording), then a limiter if the sum can clip.</summary>
    private static string MixGraph(IReadOnlyList<TrackMix> tracks, int input, double trim)
    {
        var chains = new List<string>();
        for (int i = 0; i < tracks.Count; i++)
        {
            var filters = new List<string>();
            if (trim > 0)
            {
                filters.Add($"atrim=start={Seconds(trim)}");
                filters.Add("asetpts=PTS-STARTPTS");
            }

            if (tracks[i].Volume != 1)
            {
                filters.Add($"volume={tracks[i].Volume.ToString("0.###", CultureInfo.InvariantCulture)}");
            }

            string chain = filters.Count > 0 ? string.Join(',', filters) : "anull";
            chains.Add($"[{input}:{tracks[i].Track.Stream}]{chain}[a{i}]");
        }

        string inputs = string.Concat(Enumerable.Range(0, tracks.Count).Select(i => $"[a{i}]"));
        string mix = tracks.Count > 1 ? $"{inputs}amix=inputs={tracks.Count}:duration=longest:normalize=0" : $"{inputs}anull";
        if (tracks.Count > 1 || tracks.Any(t => t.Volume > 1))
        {
            // Lookahead limiter at -0.25 dBFS; level=0 keeps it from raising quiet audio, latency=1 keeps sync.
            mix += ",alimiter=limit=0.97:level=0:latency=1";
        }

        return string.Join(';', chains.Append($"{mix}[aout]"));
    }

    private static List<string> VideoEncodeArguments(ExportPlan plan)
    {
        VideoEncoderKind kind = plan.Encoder!.Value;
        int kbps = plan.VideoBitrateKbps!.Value;
        VideoStreamInfo video = plan.Request.Source.Media.Video!;
        int gop = Math.Max(1, (int)Math.Round(video.Fps * 2));
        var args = new List<string> { "-c:v", kind.CodecName() };

        // Offline, so x264 and QSV get slower presets than the recorder's; AMF / NVENC as recorded.
        args.AddRange(kind switch
        {
            VideoEncoderKind.Amf => ["-usage", "transcoding", "-quality", "balanced", "-rc", "vbr_peak"],
            VideoEncoderKind.Nvenc => ["-preset", "p5", "-tune", "hq", "-rc", "vbr"],
            VideoEncoderKind.Qsv => ["-preset", "medium"],
            _ => ["-preset", "faster", "-pix_fmt", "yuv420p"],
        });
        args.AddRange(["-profile:v", "high", "-g", gop.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k"]);
        if (plan.OutputHeight is { } height)
        {
            args.AddRange(["-vf", $"scale=-2:{height}"]);
        }

        return args;
    }

    private ExportPlan Encoding(ExportPlan plan, double from, int? height)
    {
        ExportRequest request = plan.Request;
        VideoStreamInfo video = request.Source.Media.Video!;
        double duration = plan.End - from;
        int audioKbps = request.Tracks.Count > 0 ? request.AudioBitrateKbps : 0;
        (int width, int outHeight) = VideoEncoderSettings.ScaledSize(video.Width, video.Height, height);
        var notes = plan.Notes.ToList();

        int kbps;
        if (request.MaxBytes is { } max)
        {
            kbps = (int)((max * SizeMargin * 8 / duration / 1000) - audioKbps);
            if (kbps < MinVideoKbps)
            {
                throw new ExportException(
                    $"{Megabytes(max)} is too small for {duration:0.#} s; export a shorter part or allow more.");
            }

            kbps = Math.Min(kbps, request.VideoBitrateKbps ?? int.MaxValue);
        }
        else
        {
            // Same quality per pixel as the recording.
            double pixels = (double)width * outHeight / ((double)video.Width * video.Height);
            kbps = request.VideoBitrateKbps ?? Math.Max(500, (int)(SourceVideoKbps(request.Source.Media) * pixels));
        }

        double bitsPerPixel = kbps * 1000.0 / (width * (double)outHeight * Math.Max(1, video.Fps));
        if (bitsPerPixel < 0.02 && outHeight > 720)
        {
            notes.Add($"{kbps / 1000.0:0.0} Mbps is little for {width}x{outHeight} at {video.Fps:0.##} fps; a height of 720 would look better.");
        }

        // GPU encoders hit a quality floor at low bitrates (QSV gave 1.07 Mbps when asked for 0.3-0.6),
        // so a size limit goes to x264, whose rate control lands on target.
        VideoEncoderKind encoder = request.Encoder ?? (request.MaxBytes is not null ? VideoEncoderKind.X264 : tool.SelectEncoder());
        return plan with
        {
            Start = from,
            Video = VideoMode.Encode,
            Encoder = encoder,
            VideoBitrateKbps = kbps,
            OutputHeight = height is null ? null : outHeight,
            Notes = notes,
        };
    }

    /// <summary>Lowers the video bitrate so that the video part of the next attempt fits.</summary>
    private static ExportPlan Shrink(ExportPlan plan, long bytes, long max)
    {
        ExportRequest request = plan.Request;
        double audioBytes = (request.Tracks.Count > 0 ? request.AudioBitrateKbps : 0) * 1000.0 / 8 * plan.Duration;
        double scale = ((max * 0.97) - audioBytes) / Math.Max(1, bytes - audioBytes);
        int kbps = (int)(plan.VideoBitrateKbps!.Value * Math.Min(scale, 0.97));
        if (kbps < MinVideoKbps)
        {
            throw new ExportException($"Cannot fit {plan.Duration:0.#} s into {Megabytes(max)}; export a shorter part or allow more.");
        }

        return plan with { VideoBitrateKbps = kbps };
    }

    private static long EstimateCopyBytes(MediaInfo media, double duration, int audioKbps)
    {
        double total = Math.Max(0.001, media.DurationSeconds);
        double sourceAudio = media.Audio.Sum(a => (a.BitrateKbps > 0 ? a.BitrateKbps : AssumedAudioKbps) * 1000.0 / 8 * total);
        double video = Math.Max(0, media.SizeBytes - sourceAudio) * duration / total;
        return (long)(video + (audioKbps * 1000.0 / 8 * duration));
    }

    // Measured, not the sidecar's setting: VBR on a static window averaged 7.7 Mbps of a 20 Mbps target.
    private static double SourceVideoKbps(MediaInfo media)
    {
        double total = Math.Max(0.001, media.DurationSeconds);
        return EstimateCopyBytes(media, total, 0) * 8 / total / 1000;
    }

    private static string Describe(ExportPlan plan) =>
        $"{Time(plan.Start)}-{Time(plan.End)}, video {plan.Video}" +
        (plan.Video == VideoMode.Encode ? $" {plan.Encoder?.ShortName()} {plan.VideoBitrateKbps} kbps" : string.Empty) +
        $", tracks {(plan.Request.Tracks.Count == 0 ? "none" : string.Join(" + ", plan.Request.Tracks.Select(t => $"{t.Track.Title} {t.Volume:0.##}")))}";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Log.Warn($"Could not delete {path}", ex);
        }
    }

    public static string Time(double seconds) => $"{seconds.ToString("0.000", CultureInfo.InvariantCulture)} s";

    public static string Megabytes(long bytes) => $"{(bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)} MB";

    private static string Seconds(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);
}
