using ScContractTracker.Core.Imaging;
using Windows.Graphics.Imaging;

namespace ScContractTracker.App.Services;

/// <summary>
/// Sleduje složku LIVE\ScreenShots. Snímek obrazovky pořízený hrou (výchozí klávesa PrtScn)
/// vyrenderuje sama hra, takže funguje v jakémkoli režimu zobrazení včetně exkluzivního fullscreenu.
/// Hráč stiskne PrtScn na záložce Contracts › Accepted a aplikace snímek přečte přes OCR.
/// </summary>
public sealed class GameScreenshotWatcher : IDisposable
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? ScreenshotCreated;

    public string? Folder { get; private set; }

    /// <summary>
    /// Sleduje celou složku kanálu hry (LIVE), protože ScreenShots vznikne až s prvním snímkem
    /// a do Program Files ji bez práv administrátora nevytvoříme.
    /// </summary>
    public void Start(string gamePath)
    {
        Stop();
        if (!Directory.Exists(gamePath)) return;
        Folder = Path.Combine(gamePath, "ScreenShots");
        _watcher = new FileSystemWatcher(gamePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, e) => OnFile(e.FullPath);
        _watcher.Renamed += (_, e) => OnFile(e.FullPath);
    }

    private void OnFile(string path)
    {
        if (Folder is null || !path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) return;
        if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return;
        lock (_seen)
            if (!_seen.Add(path)) return;
        _ = Task.Run(async () =>
        {
            // Hra soubor ještě zapisuje – počkáme, až půjde otevřít.
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(200);
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (fs.Length > 0) break;
                }
                catch (IOException) { }
            }
            ScreenshotCreated?.Invoke(path);
        });
    }

    public static async Task<CapturedFrame?> LoadAsync(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var stream = fs.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            return BitmapConvert.ToFrame(bitmap, "Screenshot");
        }
        catch (Exception ex)
        {
            AppPaths.LogError(ex);
            return null;
        }
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose() => Stop();
}
