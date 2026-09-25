using System.Runtime.InteropServices.WindowsRuntime;
using ScContractTracker.Core.Imaging;
using ScContractTracker.Core.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrLine = ScContractTracker.Core.Ocr.OcrLine;

namespace ScContractTracker.App.Services;

/// <summary>
/// OCR přes vestavěný Windows.Media.Ocr (bez dalších knihoven). Jazyk musí být ve Windows
/// nainstalován jako jazyková sada (Nastavení › Čas a jazyk › Jazyk › Přidat jazyk).
/// </summary>
public sealed class WindowsOcrService
{
    private OcrEngine? _engine;

    public string? RequestedLanguage { get; private set; }
    public string? ActiveLanguage => _engine?.RecognizerLanguage.LanguageTag;
    /// <summary>true = požadovaný jazyk chybí a použil se jazyk profilu.</summary>
    public bool IsFallback { get; private set; }
    public bool IsAvailable => _engine is not null;

    public void SetLanguage(string languageTag)
    {
        RequestedLanguage = languageTag;
        IsFallback = false;
        OcrEngine? engine = null;
        try
        {
            var lang = new Language(languageTag);
            if (OcrEngine.IsLanguageSupported(lang))
                engine = OcrEngine.TryCreateFromLanguage(lang);
        }
        catch (Exception) { /* neplatný nebo nepodporovaný tag */ }

        if (engine is null)
        {
            // Jiná varianta stejného jazyka (např. en-GB místo en-US) je pořád lepší než jazyk profilu.
            var primary = languageTag.Split('-')[0];
            var variant = OcrEngine.AvailableRecognizerLanguages
                .FirstOrDefault(l => l.LanguageTag.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase)
                                     || l.LanguageTag.Equals(primary, StringComparison.OrdinalIgnoreCase));
            if (variant is not null) engine = OcrEngine.TryCreateFromLanguage(variant);
        }

        if (engine is null)
        {
            engine = OcrEngine.TryCreateFromUserProfileLanguages();
            IsFallback = engine is not null;
        }
        _engine = engine;
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(CapturedFrame frame)
    {
        if (_engine is null) return Array.Empty<OcrLine>();

        // Windows OCR má limit velikosti obrázku (typicky 2600 px na stranu), 4K snímek zmenšíme.
        var max = (int)OcrEngine.MaxImageDimension;
        var factor = (int)Math.Ceiling(Math.Max(frame.Width, frame.Height) / (double)max);
        var img = frame.Downscale(Math.Max(1, factor));

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            img.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, img.Width, img.Height, BitmapAlphaMode.Ignore);
        var result = await _engine.RecognizeAsync(bitmap);

        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
                x1 = Math.Max(x1, r.X + r.Width); y1 = Math.Max(y1, r.Y + r.Height);
            }
            lines.Add(new OcrLine(line.Text,
                x0 / img.Width, y0 / img.Height, (x1 - x0) / img.Width, (y1 - y0) / img.Height));
        }
        return lines;
    }
}
