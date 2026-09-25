using ScContractTracker.Core.Imaging;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScContractTracker.App.Services;

public enum CaptureMethod
{
    /// <summary>WGC okno → WGC monitor → GDI, první nečerný snímek vyhrává.</summary>
    Auto,
    WindowsGraphicsCapture,
    Gdi
}

/// <summary>
/// Získá snímek okna hry nejvhodnější dostupnou metodou.
///
/// - Windows.Graphics.Capture (okno): funguje v okně, borderless i ve „fullscreenu“ s kompozicí DWM
///   (fullscreen optimalizace Windows 10/11), i když je okno překryté. Hlavní metoda.
/// - Windows.Graphics.Capture (monitor): záloha, když snímek okna vychází černý.
/// - GDI BitBlt: poslední záloha pro starší systémy.
///
/// Pravý exkluzivní fullscreen (mimo DWM) nezachytí žádné veřejné API bez injektáže do procesu hry,
/// kterou kvůli Easy Anti-Cheat záměrně nepoužíváme. Pro ten případ slouží <see cref="GameScreenshotWatcher"/>.
/// </summary>
public sealed class CaptureService : IDisposable
{
    private IDirect3DDevice? _device;
    private WgcSession? _session;
    private bool _preferMonitor;
    private bool _wgcBroken;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CaptureMethod Method { get; set; } = CaptureMethod.Auto;

    /// <summary>Metoda, která dodala poslední použitelný snímek.</summary>
    public string? LastSource { get; private set; }

    /// <summary>true = hra běží, ale všechny metody vrátily černý obraz.</summary>
    public bool LastWasBlank { get; private set; }

    public async Task<CapturedFrame?> CaptureAsync()
    {
        await _gate.WaitAsync();
        try { return await CaptureCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task<CapturedFrame?> CaptureCoreAsync()
    {
        var hwnd = Native.FindGameWindow();
        if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd)) return null;
        var sawBlank = false;

        if (Method != CaptureMethod.Gdi && !_wgcBroken && WgcSession.IsSupported)
        {
            foreach (var monitor in _preferMonitor ? new[] { true } : new[] { false, true })
            {
                // Monitor obsahuje i to, co je přes hru – má smysl jen s hrou v popředí.
                if (monitor && !Native.IsGameForeground(out _)) continue;
                var frame = await TryWgcAsync(hwnd, monitor);
                if (frame is null) continue;
                if (!frame.IsBlank())
                {
                    if (monitor) _preferMonitor = true;
                    return Success(frame);
                }
                sawBlank = true;
            }
        }

        if (Method != CaptureMethod.WindowsGraphicsCapture && Native.IsGameForeground(out _))
        {
            var gdi = ScreenCapture.CaptureGameWindow();
            if (gdi is not null)
            {
                if (!gdi.IsBlank()) return Success(gdi);
                sawBlank = true;
            }
        }

        LastWasBlank = sawBlank;
        return null;
    }

    private CapturedFrame Success(CapturedFrame frame)
    {
        LastSource = frame.Source;
        LastWasBlank = false;
        return frame;
    }

    private async Task<CapturedFrame?> TryWgcAsync(IntPtr hwnd, bool monitor)
    {
        try
        {
            _device ??= WgcInterop.CreateDevice();
            var handle = monitor ? WgcInterop.MonitorFromWindow(hwnd, WgcInterop.MONITOR_DEFAULTTONEAREST) : hwnd;
            if (_session is null || _session.IsMonitor != monitor || _session.Handle != handle)
            {
                _session?.Dispose();
                await WgcSession.RequestBorderlessAsync();
                _session = WgcSession.Start(_device, hwnd, monitor);
            }
            return await _session.GrabAsync(TimeSpan.FromMilliseconds(800));
        }
        catch (Exception ex)
        {
            // Např. starý ovladač GPU / Windows bez podpory – dál už WGC nezkoušíme.
            AppPaths.LogError(ex);
            _session?.Dispose();
            _session = null;
            _wgcBroken = true;
            return null;
        }
    }

    /// <summary>Ukončí běžící relaci (konec sledování mobiGlasu).</summary>
    public void EndSession()
    {
        _gate.Wait();
        try
        {
            _session?.Dispose();
            _session = null;
            _preferMonitor = false;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        EndSession();
        (_device as IDisposable)?.Dispose();
        _device = null;
    }
}
