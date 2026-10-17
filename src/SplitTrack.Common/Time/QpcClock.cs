using System.Diagnostics;

namespace SplitTrack.Common.Time;

/// <summary>
/// The single session clock: QueryPerformanceCounter expressed in 100 ns ticks,
/// the same unit WASAPI uses for its QPC packet positions.
/// </summary>
public static class QpcClock
{
    public const long TicksPerSecond = 10_000_000;

    private static readonly long Frequency = Stopwatch.Frequency;

    // Stopwatch.GetTimestamp is QueryPerformanceCounter on Windows.
    public static long Now => FromQpc(Stopwatch.GetTimestamp());

    public static long FromQpc(long qpc) =>
        Frequency == TicksPerSecond ? qpc : (long)((Int128)qpc * TicksPerSecond / Frequency);

    /// <summary>Whole frames covered by <paramref name="ticks"/>, rounded towards negative infinity.</summary>
    public static long TicksToFrames(long ticks, int sampleRate)
    {
        Int128 scaled = (Int128)ticks * sampleRate;
        Int128 frames = scaled / TicksPerSecond;
        if (scaled < 0 && frames * TicksPerSecond != scaled)
        {
            frames--;
        }

        return (long)frames;
    }

    public static long FramesToTicks(long frames, int sampleRate) =>
        (long)((Int128)frames * TicksPerSecond / sampleRate);

    public static double TicksToMilliseconds(long ticks) => ticks / 10_000.0;

    public static long MillisecondsToTicks(double ms) => (long)(ms * 10_000);
}
