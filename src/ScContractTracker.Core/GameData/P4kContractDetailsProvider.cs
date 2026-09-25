using System.Text;

namespace ScContractTracker.Core.GameData;

/// <summary>
/// Detaily kontraktů z herních souborů.
///
/// Aktuálně (MVP): lokalizace global.ini – z disku (komunitní překlady v LIVE\data\Localization)
/// nebo z Data.p4k. Název a popis se hledají podle interního jména kontraktu z Game.logu
/// a záložně podle názvu z notifikace.
///
/// Další krok (roadmapa): čtení DataCore (Data/Game2.dcb) – odměny, reputace, cíle a lokace
/// přímo ze záznamu ContractGenerator. Viz docs/ROADMAP.md.
/// </summary>
public sealed class P4kContractDetailsProvider : IContractDetailsProvider
{
    private readonly string _gamePath;
    private readonly string _localizationFolder;
    private GlobalIni? _ini;

    public P4kContractDetailsProvider(string gamePath, string localizationFolder)
    {
        _gamePath = gamePath;
        _localizationFolder = localizationFolder;
    }

    public bool IsReady => _ini is not null;
    public string? LoadedFrom { get; private set; }
    public int StringCount => _ini?.Count ?? 0;

    public Task InitializeAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        // 1) Přepis na disku (komunitní překlad, user.cfg g_language)
        foreach (var folder in new[] { _localizationFolder, "english" }.Distinct())
        {
            var disk = Path.Combine(_gamePath, "data", "Localization", folder, "global.ini");
            if (File.Exists(disk))
            {
                using var sr = new StreamReader(disk, Encoding.UTF8);
                _ini = GlobalIni.Parse(sr);
                LoadedFrom = disk;
                return;
            }
        }

        // 2) Data.p4k
        var p4kPath = Path.Combine(_gamePath, "Data.p4k");
        if (!File.Exists(p4kPath)) return;
        var wanted = new[] { _localizationFolder, "english" }
            .Select(f => $"Data/Localization/{f}/global.ini").ToList();
        using var p4k = P4kArchive.Open(p4kPath, n => n.EndsWith("global.ini", StringComparison.OrdinalIgnoreCase));
        ct.ThrowIfCancellationRequested();
        foreach (var w in wanted)
        {
            var entry = p4k.Find(w);
            if (entry is null) continue;
            using var sr = new StreamReader(p4k.OpenRead(entry), Encoding.UTF8);
            _ini = GlobalIni.Parse(sr);
            LoadedFrom = p4kPath + "!" + w;
            return;
        }
    }, ct);

    public ContractDetails? Lookup(string? contractRecord, string? generatorName, string? title)
    {
        if (_ini is null) return null;

        string? titleKey = null;
        if (!string.IsNullOrWhiteSpace(contractRecord))
        {
            titleKey = _ini.KeysContaining(contractRecord!)
                .Select(kv => kv.Key)
                .OrderBy(k => k.Length)
                .FirstOrDefault(k => k.Contains("title", StringComparison.OrdinalIgnoreCase));
        }
        if (titleKey is null && !string.IsNullOrWhiteSpace(title))
            titleKey = _ini.FindKeyByValue(title!);
        if (titleKey is null) return null;

        var resolvedTitle = _ini.Get(titleKey);
        string? desc = null;
        foreach (var candidate in DescriptionKeys(titleKey))
        {
            desc = _ini.Get(candidate);
            if (desc is not null) break;
        }

        var extra = new Dictionary<string, string> { ["titleKey"] = titleKey };
        if (generatorName is not null) extra["generator"] = generatorName;
        if (contractRecord is not null) extra["contract"] = contractRecord;
        return new ContractDetails(resolvedTitle, desc, null, Array.Empty<string>(), extra);
    }

    private static IEnumerable<string> DescriptionKeys(string titleKey)
    {
        foreach (var (a, b) in new[] { ("title", "desc"), ("Title", "Desc"), ("title", "description"), ("Title", "Description") })
        {
            var idx = titleKey.LastIndexOf(a, StringComparison.Ordinal);
            if (idx >= 0) yield return titleKey[..idx] + b + titleKey[(idx + a.Length)..];
        }
    }
}
