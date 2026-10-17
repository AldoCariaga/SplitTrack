using FFmpeg.AutoGen;

namespace SplitTrack.FFmpeg;

public sealed class FFmpegException(string message, int errorCode) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;

    /// <summary>Returns <paramref name="result"/> when it is not an error, otherwise throws with FFmpeg's text for it.</summary>
    public static int Check(int result, string operation) =>
        result >= 0 ? result : throw new FFmpegException($"{operation} failed: {Describe(result)}", result);

    public static unsafe string Describe(int errorCode)
    {
        const int size = 256;
        byte* buffer = stackalloc byte[size];
        return ffmpeg.av_strerror(errorCode, buffer, size) == 0
            ? $"{System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buffer)} ({errorCode})"
            : $"error {errorCode}";
    }
}
