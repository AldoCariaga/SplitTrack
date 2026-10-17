using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace SplitTrack.Capture.Audio.Wasapi;

/// <summary>
/// Counts audio endpoint changes (default device, added, removed, enabled, disabled). Windows calls
/// in on its own threads, so this only bumps <see cref="Version"/>; the owner re-resolves devices.
/// </summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly Client _client;
    private bool _disposed;

    private AudioDeviceWatcher(IMMDeviceEnumerator enumerator, Client client)
    {
        _enumerator = enumerator;
        _client = client;
    }

    /// <summary>Changes whenever an endpoint changes; compare with the value seen last time.</summary>
    public int Version => Volatile.Read(ref _client.Version);

    public static AudioDeviceWatcher Start() =>
        ComApartment.RunInMta(() =>
        {
            IMMDeviceEnumerator enumerator = AudioDeviceEnumerator.CreateEnumerator();
            var client = new Client();
            enumerator.RegisterEndpointNotificationCallback(client);
            return new AudioDeviceWatcher(enumerator, client);
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComApartment.RunInMta(() =>
        {
            _enumerator.UnregisterEndpointNotificationCallback(_client);
            Marshal.ReleaseComObject(_enumerator);
            return 0;
        });
    }

    // Referenced by the watcher until unregistered, as the API requires.
    [ComVisible(true)]
    private sealed class Client : IMMNotificationClient
    {
        public int Version;

        public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState) => Interlocked.Increment(ref Version);

        public void OnDeviceAdded(PCWSTR pwstrDeviceId) => Interlocked.Increment(ref Version);

        public void OnDeviceRemoved(PCWSTR pwstrDeviceId) => Interlocked.Increment(ref Version);

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId) => Interlocked.Increment(ref Version);

        public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
        {
            // Fires constantly (volume, formats); nothing we act on.
        }
    }
}
