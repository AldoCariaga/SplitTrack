using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;
using SplitTrack.Common.Diagnostics;

namespace SplitTrack.FFmpeg;

/// <summary>Loads the FFmpeg DLLs and routes FFmpeg's log into <see cref="Log"/>.</summary>
public static unsafe class FFmpegLibrary
{
    private const int LineSize = 1024;

    private static readonly Lock Gate = new();

    // Kept in a field: FFmpeg calls it from native threads for the life of the process.
    private static readonly av_log_set_callback_callback LogCallback = OnLog;

    [ThreadStatic]
    private static StringBuilder? _pendingLine;

    private static bool _initialized;

    public static string Version { get; private set; } = "not loaded";

    /// <summary>Loads from <paramref name="directory"/>, by default the <c>ffmpeg</c> folder next to the exe.</summary>
    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            directory ??= Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            int expectedMajor = ffmpeg.LibraryVersionMap["avcodec"];
            string avcodec = Path.Combine(directory, $"avcodec-{expectedMajor}.dll");
            if (!File.Exists(avcodec))
            {
                throw new FileNotFoundException(
                    $"FFmpeg is missing: {avcodec} not found. Copy the 'ffmpeg' folder next to the exe " +
                    @"(or run tools\get-ffmpeg.ps1 when building).", avcodec);
            }

            ffmpeg.RootPath = directory;
            int loadedMajor = (int)(ffmpeg.avcodec_version() >> 16);
            if (loadedMajor != expectedMajor)
            {
                throw new InvalidOperationException($"FFmpeg avcodec major {loadedMajor} loaded, bindings expect {expectedMajor}.");
            }

            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);
            ffmpeg.av_log_set_callback(LogCallback);
            Version = ffmpeg.av_version_info();
            _initialized = true;
            Log.Info($"FFmpeg {Version} loaded from {directory}");
        }
    }

    private static void OnLog(void* avcl, int level, string format, byte* vl)
    {
        if (level > ffmpeg.av_log_get_level())
        {
            return;
        }

        byte* buffer = stackalloc byte[LineSize];
        int printPrefix = 1;
        ffmpeg.av_log_format_line2(avcl, level, format, vl, buffer, LineSize, &printPrefix);
        string? text = Marshal.PtrToStringUTF8((nint)buffer);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // FFmpeg often logs one line in several calls; emit only whole lines.
        _pendingLine ??= new StringBuilder();
        _pendingLine.Append(text);
        if (text[^1] != '\n')
        {
            return;
        }

        string line = _pendingLine.ToString().TrimEnd();
        _pendingLine.Clear();

        // Failures also surface as return codes, so FFmpeg's own errors stay out of the console.
        if (level <= ffmpeg.AV_LOG_WARNING)
        {
            Log.Info($"ffmpeg: {line}");
        }
        else
        {
            Log.Debug($"ffmpeg: {line}");
        }
    }
}
