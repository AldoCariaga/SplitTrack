using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace SplitTrack.Capture.Audio.Wasapi;

public enum AudioDeviceFlow
{
    Render,
    Capture,
}

public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceFlow Flow, bool IsDefault, bool IsDefaultCommunications);

/// <summary>Active audio endpoints with friendly names and default roles.</summary>
public static class AudioDeviceEnumerator
{
    private const int ErrorNotFound = unchecked((int)0x80070490);

    public static IReadOnlyList<AudioDeviceInfo> List(AudioDeviceFlow flow) =>
        ComApartment.RunInMta(() =>
        {
            IMMDeviceEnumerator enumerator = CreateEnumerator();
            EDataFlow dataFlow = ToDataFlow(flow);
            string? defaultId = GetDefaultId(enumerator, dataFlow, ERole.eConsole);
            string? communicationsId = GetDefaultId(enumerator, dataFlow, ERole.eCommunications);

            enumerator.EnumAudioEndpoints(dataFlow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out IMMDeviceCollection collection);
            collection.GetCount(out uint count);

            var devices = new List<AudioDeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out IMMDevice device);
                string id = GetId(device);
                devices.Add(new AudioDeviceInfo(id, GetFriendlyName(device), flow, id == defaultId, id == communicationsId));
            }

            return (IReadOnlyList<AudioDeviceInfo>)devices;
        });

    public static AudioDeviceInfo? GetDefault(AudioDeviceFlow flow) => List(flow).FirstOrDefault(d => d.IsDefault);

    /// <summary>The default device, or the first whose name contains <paramref name="nameFilter"/>.</summary>
    public static AudioDeviceInfo? Find(AudioDeviceFlow flow, string? nameFilter) =>
        string.IsNullOrWhiteSpace(nameFilter)
            ? GetDefault(flow)
            : List(flow).FirstOrDefault(d => d.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

    internal static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    internal static unsafe IMMDevice GetEndpoint(string id)
    {
        fixed (char* idPtr = id)
        {
            CreateEnumerator().GetDevice(idPtr, out IMMDevice device);
            return device;
        }
    }

    internal static unsafe string GetId(IMMDevice device)
    {
        PWSTR id;
        device.GetId(&id);
        try
        {
            return id.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)id.Value);
        }
    }

    internal static unsafe string GetFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out IPropertyStore store);
            PROPERTYKEY key = PInvoke.PKEY_Device_FriendlyName;
            store.GetValue(&key, out PROPVARIANT value);
            try
            {
                return value.vt == VARENUM.VT_LPWSTR ? value.pwszVal.ToString() : "(unnamed device)";
            }
            finally
            {
                PInvoke.PropVariantClear(ref value);
            }
        }
        catch (COMException)
        {
            return "(unnamed device)";
        }
    }

    internal static EDataFlow ToDataFlow(AudioDeviceFlow flow) =>
        flow == AudioDeviceFlow.Render ? EDataFlow.eRender : EDataFlow.eCapture;

    private static string? GetDefaultId(IMMDeviceEnumerator enumerator, EDataFlow flow, ERole role)
    {
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, role, out IMMDevice device);
            return GetId(device);
        }
        catch (COMException ex) when (ex.HResult == ErrorNotFound)
        {
            return null;
        }
    }
}
