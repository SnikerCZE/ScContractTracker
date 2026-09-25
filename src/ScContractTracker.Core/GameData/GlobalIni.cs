using ScContractTracker.Core.Text;

namespace ScContractTracker.Core.GameData;

/// <summary>
/// Lokalizační tabulka hry (Data/Localization/&lt;jazyk&gt;/global.ini): řádky "klíč=hodnota",
/// klíč může mít příponu ",P". Obsahuje názvy a popisy kontraktů.
/// </summary>
public sealed class GlobalIni
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _map.Count;

    public static GlobalIni Parse(TextReader reader)
    {
        var ini = new GlobalIni();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().TrimStart('﻿');
            var comma = key.IndexOf(',');
            if (comma > 0) key = key[..comma];
            ini._map[key] = line[(eq + 1)..].Replace("\\n", "\n").Trim();
        }
        return ini;
    }

    public string? Get(string key) => _map.GetValueOrDefault(key.TrimStart('@'));

    /// <summary>Záznamy, jejichž klíč obsahuje daný fragment (např. interní jméno kontraktu).</summary>
    public IEnumerable<KeyValuePair<string, string>> KeysContaining(string fragment) =>
        _map.Where(kv => kv.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Najde klíč, jehož hodnota nejlépe odpovídá textu (např. názvu kontraktu z notifikace).</summary>
    public string? FindKeyByValue(string value, double threshold = 0.92)
    {
        var norm = Fuzzy.Normalize(value);
        if (norm.Length < 6) return null;
        string? best = null;
        var bestScore = threshold;
        foreach (var (k, v) in _map)
        {
            if (Math.Abs(v.Length - value.Length) > 12) continue;
            var s = Fuzzy.Similarity(v, value);
            if (s >= bestScore) { bestScore = s; best = k; }
        }
        return best;
    }
}
