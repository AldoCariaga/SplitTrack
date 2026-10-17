using System.Runtime.ExceptionServices;

namespace SplitTrack.Capture.Audio.Wasapi;

/// <summary>Runs COM work in the MTA, hopping off an STA (e.g. a WPF UI thread) when needed.</summary>
internal static class ComApartment
{
    public static T RunInMta<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            return work();
        }

        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SplitTrack COM (MTA)",
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }
}
