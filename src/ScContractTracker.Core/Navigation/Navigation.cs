using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Text;

namespace ScContractTracker.Core.Navigation;

public readonly record struct Vector3d(double X, double Y, double Z)
{
    public double DistanceTo(Vector3d o)
    {
        double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>Uzel stromu míst: systém → planeta → měsíc → stanice/město/outpost.</summary>
public sealed class LocationNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>system, star, planet, moon, station, city, outpost, lagrange, poi</summary>
    public string Type { get; set; } = "poi";
    public string? Parent { get; set; }
    public List<string> Aliases { get; set; } = new();
    /// <summary>Globální souřadnice v metrech (stejný systém jako /showlocation). Nepovinné.</summary>
    public double[]? Position { get; set; }

    [JsonIgnore] public LocationNode? ParentNode { get; internal set; }
    [JsonIgnore] public Vector3d? Vector => Position is { Length: 3 } p ? new Vector3d(p[0], p[1], p[2]) : null;

    public IEnumerable<LocationNode> Ancestors()
    {
        for (var n = ParentNode; n is not null; n = n.ParentNode) yield return n;
    }

    /// <summary>Nejbližší předek s pozicí (stanice bez souřadnic zdědí polohu planety).</summary>
    public Vector3d? EffectivePosition() => Vector ?? Ancestors().Select(a => a.Vector).FirstOrDefault(v => v is not null);

    public LocationNode System => Ancestors().LastOrDefault() ?? this;

    public override string ToString() => Name;
}

/// <summary>Databáze míst z data/locations.json – uživatel ji může rozšiřovat.</summary>
public sealed class LocationDatabase
{
    private readonly Dictionary<string, LocationNode> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string alias, LocationNode node)> _aliases = new();

    public IReadOnlyCollection<LocationNode> All => _byId.Values;

    public LocationDatabase(IEnumerable<LocationNode> nodes)
    {
        foreach (var n in nodes) _byId[n.Id] = n;
        foreach (var n in _byId.Values)
        {
            if (n.Parent is not null && _byId.TryGetValue(n.Parent, out var p)) n.ParentNode = p;
            foreach (var a in n.Aliases.Append(n.Name).Append(n.Id))
            {
                var key = Fuzzy.Normalize(a);
                if (key.Length >= 3) _aliases.Add((key, n));
            }
        }
        // Delší aliasy první, aby "Port Tressler" vyhrálo nad "Tressler".
        _aliases.Sort((x, y) => y.alias.Length.CompareTo(x.alias.Length));
    }

    public static LocationDatabase Load(string path)
    {
        var json = File.ReadAllText(path);
        var nodes = JsonSerializer.Deserialize<List<LocationNode>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? new();
        return new LocationDatabase(nodes);
    }

    public LocationNode? Get(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Najde místo podle názvu z logu/OCR. Zvládá i interní názvy typu "Stanton1_Lorville"
    /// nebo "RR_HUR_LEO" (pokud jsou uvedeny v aliasech).
    /// </summary>
    public LocationNode? Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Interní názvy: podtržítka a CamelCase → mezery
        var spaced = Regex.Replace(raw.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
        var key = Fuzzy.Normalize(spaced);
        if (key.Length == 0) return null;

        foreach (var (alias, node) in _aliases)
            if (alias == key) return node;

        var padded = " " + key + " ";
        foreach (var (alias, node) in _aliases)
            if (padded.Contains(" " + alias + " ", StringComparison.Ordinal)) return node;

        return null;
    }

    /// <summary>Všechna místa zmíněná v textu (popis kontraktu, cíl mise).</summary>
    public IEnumerable<string> FindMentions(string text)
    {
        var padded = " " + Fuzzy.Normalize(text) + " ";
        var found = new List<LocationNode>();
        foreach (var (alias, node) in _aliases)
        {
            if (alias.Length < 4) continue;
            if (!padded.Contains(" " + alias + " ", StringComparison.Ordinal)) continue;
            if (found.Contains(node)) continue;
            // Pokud už máme potomka (Lorville), nepřidáváme rodiče (Hurston) zmíněného stejným textem.
            if (found.Any(f => f.Ancestors().Contains(node))) continue;
            found.RemoveAll(f => node.Ancestors().Contains(f));
            found.Add(node);
        }
        return found.Select(f => f.Name);
    }
}

public sealed record NavigationTarget(LocationNode Location, string Task, double? DistanceMeters, int Hops)
{
    public string DistanceText => DistanceMeters switch
    {
        null => "", // UI zobrazí lokalizovaný text podle Hops
        < 1_000 => $"{DistanceMeters:0} m",
        < 1_000_000 => $"{DistanceMeters / 1000:0.0} km",
        < 1_000_000_000 => $"{DistanceMeters / 1_000_000:0.0} Mm",
        _ => $"{DistanceMeters / 1_000_000_000:0.00} Gm"
    };
}

public sealed class NavigationService
{
    private readonly LocationDatabase _db;
    public NavigationService(LocationDatabase db) => _db = db;

    public LocationDatabase Database => _db;

    /// <summary>
    /// Vrátí nejvýše <paramref name="count"/> nejbližších míst kontraktu vzhledem k hráči.
    /// Primárně podle souřadnic, pokud chybí, podle vzdálenosti ve stromu míst (stejný měsíc &lt; stejná planeta &lt; stejný systém).
    /// </summary>
    public IReadOnlyList<NavigationTarget> Nearest(Contract contract, string? playerLocation, Vector3d? playerPosition, int count = 3)
    {
        var here = _db.Resolve(playerLocation);
        var herePos = playerPosition ?? here?.EffectivePosition();

        var candidates = new List<(LocationNode node, string task)>();
        foreach (var o in contract.Objectives.Where(o => !o.Completed))
        {
            var n = _db.Resolve(o.Location) ?? _db.FindMentions(o.Text).Select(_db.Resolve).FirstOrDefault(x => x is not null);
            if (n is not null) candidates.Add((n, o.Text));
        }
        foreach (var hint in contract.LocationHints)
        {
            var n = _db.Resolve(hint);
            if (n is not null && candidates.All(c => c.node != n))
                candidates.Add((n, contract.Objectives.FirstOrDefault(o => o.Text.Contains(n.Name, StringComparison.OrdinalIgnoreCase))?.Text ?? ""));
        }

        return candidates
            .GroupBy(c => c.node)
            .Select(g =>
            {
                var node = g.Key;
                var task = string.Join(" · ", g.Select(x => x.task).Where(t => t.Length > 0).Distinct());
                var pos = node.EffectivePosition();
                double? dist = herePos is { } hp && pos is { } np ? hp.DistanceTo(np) : null;
                return new NavigationTarget(node, task, dist, TreeHops(here, node));
            })
            .OrderBy(t => t.DistanceMeters ?? double.MaxValue)
            .ThenBy(t => t.Hops)
            .Take(count)
            .ToList();
    }

    /// <summary>Počet hran mezi dvěma uzly přes nejbližšího společného předka; neznámá poloha = 99.</summary>
    public static int TreeHops(LocationNode? a, LocationNode b)
    {
        if (a is null) return 99;
        if (a == b) return 0;
        var pathA = new List<LocationNode> { a };
        pathA.AddRange(a.Ancestors());
        var pathB = new List<LocationNode> { b };
        pathB.AddRange(b.Ancestors());
        for (var i = 0; i < pathA.Count; i++)
        {
            var j = pathB.IndexOf(pathA[i]);
            if (j >= 0) return i + j;
        }
        return 50 + pathA.Count + pathB.Count; // jiný systém
    }
}

/// <summary>Parsuje výstup příkazu /showlocation, který hra zkopíruje do schránky.</summary>
public static partial class ShowLocationParser
{
    [GeneratedRegex(@"x:\s*(?<x>-?\d+(?:\.\d+)?)\s*[,;]?\s*y:\s*(?<y>-?\d+(?:\.\d+)?)\s*[,;]?\s*z:\s*(?<z>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CoordRx();

    public static bool TryParse(string? text, out Vector3d position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = CoordRx().Match(text);
        if (!m.Success) return false;
        position = new Vector3d(
            double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups["z"].Value, CultureInfo.InvariantCulture));
        return true;
    }
}
