using System.Text.Json;

namespace ScContractTracker.Core.Settings;

public sealed class AppSettings
{
    public const string DefaultGamePath = @"C:\Program Files\Roberts Space Industries\StarCitizen\LIVE";

    /// <summary>Složka kanálu hry (LIVE/PTU), obsahuje Game.log a Data.p4k.</summary>
    public string GamePath { get; set; } = DefaultGamePath;

    /// <summary>Jazyk rozhraní aplikace (Languages/app/&lt;kód&gt;.json).</summary>
    public string AppLanguage { get; set; } = "en";

    /// <summary>Jazyk hry – určuje texty notifikací, OCR a global.ini (Languages/game/&lt;kód&gt;.json).</summary>
    public string GameLanguage { get; set; } = "en";

    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool IncludePrereleases { get; set; }

    public bool OcrEnabled { get; set; } = true;
    /// <summary>Klávesy, po kterých se čeká na mobiGlas (F1 = mobiGlas, F2 = Starmap).</summary>
    public List<string> MobiGlasHotkeys { get; set; } = ["F1", "F2"];
    /// <summary>Jak dlouho po stisku klávesy zkoušet OCR, než se uživatel přepne na záložku Accepted.</summary>
    public int OcrWatchSeconds { get; set; } = 30;
    public int OcrIntervalMs { get; set; } = 1200;
    public double ListColumnSplit { get; set; } = 0.42;

    /// <summary>Způsob snímání obrazu hry: Auto, WindowsGraphicsCapture, Gdi.</summary>
    public string CaptureMethod { get; set; } = "Auto";

    /// <summary>Číst i snímky obrazovky pořízené hrou (LIVE\ScreenShots) – funguje v každém režimu zobrazení.</summary>
    public bool WatchGameScreenshots { get; set; } = true;

    /// <summary>Jazyky OCR, jejichž automatickou instalaci uživatel odmítl (dotaz UAC).</summary>
    public List<string> OcrInstallDeclined { get; set; } = new();

    /// <summary>Sledovat schránku kvůli příkazu /showlocation.</summary>
    public bool WatchClipboard { get; set; } = true;

    public bool AlwaysOnTop { get; set; }

    /// <summary>Výchozí režim dokončení nových kontraktů: "Auto" (podle logu) nebo "Manual" (tlačítkem).</summary>
    public string DefaultCompletionMode { get; set; } = "Auto";

    /// <summary>Skryté sloupce tabulek: klíč tabulky ("history", "stats") → klíče sloupců.</summary>
    /// <summary>Ručně nastavené šířky sloupců (px): klíč tabulky → klíč sloupce → šířka.</summary>
    public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; } = new();

    /// <summary>Pořadí sloupců po přetažení: klíč tabulky → klíče sloupců zleva doprava.</summary>
    public Dictionary<string, List<string>> ColumnOrder { get; set; } = new();

    public Dictionary<string, List<string>> HiddenColumns { get; set; } = new()
    {
        // výchozí: méně používané sloupce skryté (zapnou se pravým tlačítkem na hlavičce)
        ["history"] = ["person", "finished", "items"],
        ["stats"] = ["active", "range", "meddur", "fastest", "slowest", "totaltime", "bpcount", "last"]
    };

    public string GameLogPath => Path.Combine(GamePath, "Game.log");
    public string LogBackupsPath => Path.Combine(GamePath, "logbackups");
    public string DataP4kPath => Path.Combine(GamePath, "Data.p4k");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
        }
        catch (JsonException) { /* výchozí hodnoty */ }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Options), Options)!;
}
