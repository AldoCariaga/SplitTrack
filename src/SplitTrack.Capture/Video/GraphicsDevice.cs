using SplitTrack.Common.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace SplitTrack.Capture.Video;

public sealed record GpuAdapterInfo(int Index, string Name, uint VendorId, long DedicatedMemoryBytes, bool IsSoftware)
{
    public const uint VendorAmd = 0x1002;
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorIntel = 0x8086;

    public string VendorName => VendorId switch
    {
        VendorAmd => "AMD",
        VendorNvidia => "NVIDIA",
        VendorIntel => "Intel",
        _ => $"0x{VendorId:X4}",
    };
}

/// <summary>
/// The one D3D11 device shared by capture, colour conversion and the encoder. It is multithread
/// protected because WGC copies on its own thread while the encode thread converts and encodes.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
    ];

    private GraphicsDevice(ID3D11Device device, GpuAdapterInfo adapter, IDirect3DDevice winRtDevice)
    {
        Device = device;
        Adapter = adapter;
        WinRtDevice = winRtDevice;
    }

    public ID3D11Device Device { get; }

    public GpuAdapterInfo Adapter { get; }

    /// <summary>The same device as WinRT sees it, for the capture frame pool.</summary>
    public IDirect3DDevice WinRtDevice { get; }

    public static IReadOnlyList<GpuAdapterInfo> ListAdapters()
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var adapters = new List<GpuAdapterInfo>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
        {
            using (adapter)
            {
                adapters.Add(Describe((int)i, adapter!));
            }
        }

        return adapters;
    }

    /// <summary>Creates the device on adapter <paramref name="adapterIndex"/> (0 = the primary GPU).</summary>
    public static GraphicsDevice Create(int adapterIndex = 0)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory.EnumAdapters1((uint)adapterIndex, out IDXGIAdapter1? adapter).Failure)
        {
            throw new ArgumentOutOfRangeException(nameof(adapterIndex), $"There is no GPU adapter {adapterIndex}.");
        }

        using (adapter)
        {
            GpuAdapterInfo info = Describe(adapterIndex, adapter!);
            D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                FeatureLevels,
                out ID3D11Device? device).CheckError();

            using (ID3D11Multithread multithread = device!.QueryInterface<ID3D11Multithread>())
            {
                multithread.SetMultithreadProtected(true);
            }

            IDirect3DDevice winRtDevice = Direct3DInterop.CreateWinRtDevice(device);
            Log.Info($"D3D11 device on adapter {adapterIndex} '{info.Name}' ({info.VendorName}), feature level {device.FeatureLevel}");
            return new GraphicsDevice(device, info, winRtDevice);
        }
    }

    public void Dispose()
    {
        (WinRtDevice as IDisposable)?.Dispose();
        Device.Dispose();
    }

    private static GpuAdapterInfo Describe(int index, IDXGIAdapter1 adapter)
    {
        AdapterDescription1 desc = adapter.Description1;
        return new GpuAdapterInfo(
            index,
            desc.Description.TrimEnd('\0'),
            desc.VendorId,
            // Via ulong: a plain (long) cast picks SharpGen's checked uint conversion, which throws above 4 GB.
            (long)(ulong)desc.DedicatedVideoMemory,
            (desc.Flags & AdapterFlags.Software) != 0);
    }
}
