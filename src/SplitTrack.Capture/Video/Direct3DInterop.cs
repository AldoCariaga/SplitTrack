using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SplitTrack.Capture.Video;

/// <summary>
/// The classic-COM bridges between WinRT capture objects and D3D11, called through raw vtable
/// slots: CsWin32 cannot generate them and CsWinRT does not project them.
/// </summary>
internal static unsafe partial class Direct3DInterop
{
    private static readonly Guid IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(nint monitor) => CreateItem(monitor, slot: 4);

    public static GraphicsCaptureItem CreateItemForWindow(nint window) => CreateItem(window, slot: 3);

    /// <summary>The D3D11 texture behind a capture frame's surface; the caller disposes it.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        nint unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint access = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IDirect3DDxgiInterfaceAccess, out access));
            Guid textureId = typeof(ID3D11Texture2D).GUID;
            nint texture;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
            return new ID3D11Texture2D(texture);
        }
        finally
        {
            if (access != 0)
            {
                Marshal.Release(access);
            }

            Marshal.Release(unknown);
        }
    }

    // IGraphicsCaptureItemInterop: slot 3 CreateForWindow(HWND), slot 4 CreateForMonitor(HMONITOR).
    private static GraphicsCaptureItem CreateItem(nint handle, int slot)
    {
        IObjectReference factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        nint interop = 0;
        nint item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IGraphicsCaptureItemInterop, out interop));
            Guid itemId = IGraphicsCaptureItem;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)interop)[slot];
            Marshal.ThrowExceptionForHR(create(interop, handle, &itemId, &item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            if (item != 0)
            {
                Marshal.Release(item);
            }

            if (interop != 0)
            {
                Marshal.Release(interop);
            }
        }
    }

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
