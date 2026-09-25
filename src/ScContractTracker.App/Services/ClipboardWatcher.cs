using System.Windows;
using System.Windows.Interop;

namespace ScContractTracker.App.Services;

/// <summary>
/// Poslouchá změny schránky. Příkaz /showlocation v chatu hry zkopíruje souřadnice do schránky
/// ("Coordinates: x:… y:… z:…"), odtud bereme aktuální polohu hráče.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private HwndSource? _source;

    public event Action<string>? TextCopied;

    public void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        Native.AddClipboardFormatListener(handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (text.Length < 500) TextCopied?.Invoke(text);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Schránku má právě otevřenou jiný proces – ignorujeme.
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is null) return;
        Native.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source = null;
    }
}
