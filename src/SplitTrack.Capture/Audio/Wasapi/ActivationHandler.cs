using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SplitTrack.Capture.Audio.Wasapi;

/// <summary>
/// Completion callback for ActivateAudioInterfaceAsync. Windows requires it to be agile,
/// hence <see cref="IAgileObject"/>; the caller keeps it referenced until completion.
/// </summary>
[ComVisible(true)]
internal sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    private readonly ManualResetEventSlim _completed = new();
    private HRESULT _result;
    private object? _activated;

    public unsafe void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            HRESULT hr;
            activateOperation.GetActivateResult(&hr, out object activated);
            _result = hr;
            _activated = activated;
        }
        catch (Exception ex)
        {
            _result = new HRESULT(ex.HResult);
        }
        finally
        {
            _completed.Set();
        }
    }

    public T WaitFor<T>(TimeSpan timeout)
        where T : class
    {
        if (!_completed.Wait(timeout))
        {
            throw new TimeoutException("Audio interface activation did not complete.");
        }

        _result.ThrowOnFailure();
        return _activated as T ?? throw new InvalidCastException($"Activation did not return {typeof(T).Name}.");
    }
}
