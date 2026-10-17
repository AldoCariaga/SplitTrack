using System.Runtime.InteropServices;
using SplitTrack.Common.Audio;
using SplitTrack.Common.Diagnostics;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SplitTrack.Capture.Audio.Wasapi;

/// <summary>
/// Endpoint capture: a microphone, or loopback of a whole render device. Records in the
/// device's shared-mode mix format.
/// </summary>
public sealed class DeviceCaptureSource : IAudioSource
{
    private readonly IAudioClient _client;
    private readonly WasapiCaptureLoop _loop;
    private bool _disposed;

    private DeviceCaptureSource(string name, AudioDeviceInfo device, bool loopback, IAudioClient client, AudioFormat format)
    {
        Name = name;
        Device = device;
        IsLoopback = loopback;
        Format = format;
        _client = client;
        _loop = new WasapiCaptureLoop(name, client, format);
    }

    public string Name { get; }

    public AudioDeviceInfo Device { get; }

    public bool IsLoopback { get; }

    public AudioFormat Format { get; }

    public Exception? Fault => _loop.Fault;

    public static DeviceCaptureSource OpenCapture(string name, AudioDeviceInfo device) => Open(name, device, loopback: false);

    public static DeviceCaptureSource OpenRenderLoopback(string name, AudioDeviceInfo device) => Open(name, device, loopback: true);

    public void Start(IAudioSink sink) => _loop.Start(sink);

    public void Stop() => _loop.Stop();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loop.Dispose();
        Marshal.ReleaseComObject(_client);
    }

    private static DeviceCaptureSource Open(string name, AudioDeviceInfo device, bool loopback)
    {
        if (loopback != (device.Flow == AudioDeviceFlow.Render))
        {
            throw new ArgumentException("Loopback needs a render device; capture needs a capture device.", nameof(device));
        }

        return ComApartment.RunInMta(() => OpenInMta(name, device, loopback));
    }

    private static unsafe DeviceCaptureSource OpenInMta(string name, AudioDeviceInfo device, bool loopback)
    {
        IMMDevice endpoint = AudioDeviceEnumerator.GetEndpoint(device.Id);
        Guid iid = typeof(IAudioClient).GUID;
        endpoint.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out object activated);
        var client = (IAudioClient)activated;

        try
        {
            WAVEFORMATEX* mixFormat;
            client.GetMixFormat(&mixFormat);
            try
            {
                AudioFormat format = WaveFormatInterop.FromNative(mixFormat);
                uint flags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | (loopback ? PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
                client.Initialize(
                    AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                    flags,
                    WasapiCaptureLoop.BufferDurationTicks,
                    0,
                    mixFormat,
                    null);

                Log.Info($"{name}: {(loopback ? "loopback of" : "capture from")} '{device.Name}', {format}");
                return new DeviceCaptureSource(name, device, loopback, client, format);
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)mixFormat);
            }
        }
        catch
        {
            Marshal.ReleaseComObject(client);
            throw;
        }
    }
}
