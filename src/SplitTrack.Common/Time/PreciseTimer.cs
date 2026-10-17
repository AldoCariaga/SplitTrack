using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace SplitTrack.Common.Time;

/// <summary>
/// Waits until a <see cref="QpcClock"/> instant with sub-millisecond accuracy. Thread.Sleep rounds
/// up to the 15.6 ms system tick, which is a whole frame at 60 fps.
/// </summary>
public sealed unsafe class PreciseTimer : IDisposable
{
    private readonly SafeFileHandle _timer;

    public PreciseTimer()
    {
        _timer = PInvoke.CreateWaitableTimerEx(
            null,
            (string?)null,
            PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            (uint)SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_ALL_ACCESS);
        if (_timer.IsInvalid)
        {
            throw new Win32Exception();
        }
    }

    public void WaitUntil(long qpcTicks)
    {
        long remaining = qpcTicks - QpcClock.Now;
        if (remaining <= 0)
        {
            return;
        }

        // Negative due time = relative, in 100 ns units.
        long due = -remaining;
        if (!PInvoke.SetWaitableTimer(_timer, in due, 0, null, null, false))
        {
            throw new Win32Exception();
        }

        PInvoke.WaitForSingleObject(_timer, uint.MaxValue);
    }

    public void Dispose() => _timer.Dispose();
}
