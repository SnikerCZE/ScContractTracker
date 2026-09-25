using ScContractTracker.Core.Imaging;

namespace ScContractTracker.App.Services;

/// <summary>
/// Snímání klientské oblasti okna hry přes GDI – nejstarší metoda, jen jako záloha za
/// Windows.Graphics.Capture (viz <see cref="CaptureService"/>). U DirectX/Vulkan okon často vrací černý obraz.
/// </summary>
public static class ScreenCapture
{
    public static CapturedFrame? CaptureGameWindow()
    {
        var hwnd = Native.FindGameWindow();
        if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd)) return null;
        if (!Native.GetClientRect(hwnd, out var rc) || rc.Width < 200 || rc.Height < 200) return null;
        var origin = new Native.POINT();
        Native.ClientToScreen(hwnd, ref origin);
        return CaptureScreenRect(origin.X, origin.Y, rc.Width, rc.Height);
    }

    public static CapturedFrame? CaptureScreenRect(int x, int y, int w, int h)
    {
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var bmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        var old = Native.SelectObject(memDc, bmp);
        try
        {
            if (!Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY | Native.CAPTUREBLT))
                return null;
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = 40,
                biWidth = w,
                biHeight = -h, // záporná výška = řádky shora dolů
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };
            var pixels = new byte[w * h * 4];
            Native.SelectObject(memDc, old);
            var lines = Native.GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref header, 0);
            return lines == h ? new CapturedFrame { Pixels = pixels, Width = w, Height = h, Source = "GDI" } : null;
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(bmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
