using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ScContractTracker.Core.Imaging;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace ScContractTracker.App.Services;

/// <summary>
/// Nativní interop pro Windows.Graphics.Capture: vytvoření D3D11 zařízení a GraphicsCaptureItem z HWND/HMONITOR.
/// Stejný postup jako oficiální ukázky Microsoftu (robmikh/Win32CaptureSample), jen bez dalších knihoven.
/// </summary>
internal static unsafe class WgcInterop
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static IDirect3DDevice CreateDevice()
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out var device, out _, out var context));
        try
        {
            var iid = IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.Release(device);
        }
    }

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd) => CreateItem(hwnd, forMonitor: false);

    public static GraphicsCaptureItem CreateForMonitor(IntPtr hmonitor) => CreateItem(hmonitor, forMonitor: true);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool forMonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        IntPtr factory;
        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out factory));
        }
        finally { WindowsDeleteString(hstring); }

        try
        {
            // IGraphicsCaptureItemInterop : IUnknown { CreateForWindow (3); CreateForMonitor (4) }
            var vtable = *(IntPtr**)factory;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[forMonitor ? 4 : 3];
            var itemIid = IID_IGraphicsCaptureItem;
            IntPtr item;
            Marshal.ThrowExceptionForHR(create(factory, handle, &itemIid, &item));
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(factory); }
    }
}

/// <summary>
/// Jedna běžící WGC relace (okno nebo celý monitor). Drží se po dobu sledování mobiGlasu,
/// aby se nevytvářela při každém snímku (a na Windows 10 neblikal žlutý rámeček).
/// </summary>
public sealed class WgcSession : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _size;
    private CapturedFrame? _last;

    public string Kind { get; }
    public IntPtr Handle { get; }
    public bool IsMonitor { get; }

    private WgcSession(IDirect3DDevice device, GraphicsCaptureItem item, IntPtr handle, bool monitor)
    {
        _device = device;
        Handle = handle;
        IsMonitor = monitor;
        Kind = monitor ? "WGC monitor" : "WGC okno";
        _size = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _session = _pool.CreateCaptureSession(item);

        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            _session.IsCursorCaptureEnabled = false;
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try { _session.IsBorderRequired = false; }
            catch (Exception) { /* bez povolení Borderless zůstane rámeček */ }
        }
        _session.StartCapture();
    }

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception) { return false; }
        }
    }

    private static bool _borderlessRequested;

    /// <summary>Windows 11: požádá o snímání bez žlutého rámečku (jednou za běh aplikace).</summary>
    public static async Task RequestBorderlessAsync()
    {
        if (_borderlessRequested) return;
        _borderlessRequested = true;
        try
        {
            if (ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
        }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    public static WgcSession Start(IDirect3DDevice device, IntPtr gameWindow, bool monitor)
    {
        var handle = monitor ? WgcInterop.MonitorFromWindow(gameWindow, WgcInterop.MONITOR_DEFAULTTONEAREST) : gameWindow;
        var item = monitor ? WgcInterop.CreateForMonitor(handle) : WgcInterop.CreateForWindow(handle);
        return new WgcSession(device, item, handle, monitor);
    }

    /// <summary>Vrátí nejnovější snímek. Když od minula žádný nepřišel (obraz se nemění), vrátí poslední.</summary>
    public async Task<CapturedFrame?> GrabAsync(TimeSpan timeout)
    {
        Direct3D11CaptureFrame? frame = null;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Direct3D11CaptureFrame? next;
            while ((next = _pool.TryGetNextFrame()) is not null)
            {
                frame?.Dispose();
                frame = next;
            }
            if (frame is not null || _last is not null || DateTime.UtcNow >= deadline) break;
            await Task.Delay(30);
        }
        if (frame is null) return _last;

        using (frame)
        {
            if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
            {
                // Změna rozlišení / velikosti okna
                _size = frame.ContentSize;
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            }
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
            _last = BitmapConvert.ToFrame(bitmap, Kind);
        }
        return _last;
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
    }
}

internal static class BitmapConvert
{
    /// <summary>SoftwareBitmap → těsně zarovnaný BGRA snímek (respektuje stride).</summary>
    public static CapturedFrame ToFrame(SoftwareBitmap source, string kind)
    {
        SoftwareBitmap bitmap = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? source
            : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        try
        {
            int start, stride;
            using (var locked = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
            {
                var plane = locked.GetPlaneDescription(0);
                start = plane.StartIndex;
                stride = plane.Stride;
            }
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var raw = new byte[start + stride * height];
            bitmap.CopyToBuffer(raw.AsBuffer());
            return CapturedFrame.FromBgra(raw.AsSpan(start), width, height, stride, kind);
        }
        finally
        {
            if (!ReferenceEquals(bitmap, source)) bitmap.Dispose();
        }
    }
}
