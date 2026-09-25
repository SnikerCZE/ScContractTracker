using System.Text.RegularExpressions;

namespace ScContractTracker.Core.Models;

/// <summary>Kategorie kontraktu pro zobrazení a seskupování (název se lokalizuje v aplikaci: „Type.&lt;hodnota&gt;“).</summary>
public enum ContractCategory
{
    Other,
    Hauling,
    Delivery,
    Refueling,
    Rescue,
    Bounty,
    Combat,
    Defense,
    Escort,
    Retrieval,
    Salvage,
    Mining,
    Investigation,
    Repair,
    Racing,
    Onboarding
}

/// <summary>
/// Odvozuje typ kontraktu a zadavatele z toho, co je k dispozici (log, OCR, název).
/// Počítá se za běhu – nic se neukládá, takže vylepšení pravidel se projeví i na starých kontraktech.
/// </summary>
public static partial class ContractClassifier
{
    // Pořadí je důležité: specifičtější pravidla dřív („Bulk Shipment“ je hauling, ne delivery).
    // Klíčová slova EN + CZ + DE (hra může běžet v libovolném jazyce), porovnává se malými písmeny.
    private static readonly (ContractCategory Category, string[] Keywords)[] Rules =
    {
        (ContractCategory.Onboarding, new[] { "welcome to the system", "arrive to system", "tutorial", "vítejte v systému", "willkommen im system" }),
        (ContractCategory.Hauling, new[] { "cargo haul", "cargo run", "haul", "bulk shipment", "freight", "supply run", "hauler",
            "náklad", "přeprav", "doprava", "fracht", "frachter", "transportauftrag" }),
        (ContractCategory.Refueling, new[] { "refuel", "fuel ", "tankov", "betank" }),
        (ContractCategory.Rescue, new[] { "distress", "rescue", "stranded", "medical", "nouzi", "záchran", "notruf", "rettung", "seenot" }),
        (ContractCategory.Delivery, new[] { "courier", "delivery", "deliver", "package", "parcel", "kurýr", "doruč", "zásilk", "kurier", "liefer", "paket" }),
        (ContractCategory.Bounty, new[] { "bounty", "wanted", "criminal", "eliminate", "kill ", "headhunt", "elimination",
            "odměna za", "hledan", "eliminuj", "kopfgeld", "gesucht" }),
        (ContractCategory.Escort, new[] { "escort", "protection detail", "protect ", "doprovod", "ochran", "eskort", "geleit" }),
        (ContractCategory.Defense, new[] { "defense", "defend", "ambush", "security", "patrol", "obran", "přepad", "hlídk", "verteidig", "hinterhalt", "patrouill" }),
        (ContractCategory.Retrieval, new[] { "retriev", "recover", "collect", "acquisition", "vyzvedn", "získej", "sběr", "berge ", "sammel" }),
        (ContractCategory.Salvage, new[] { "salvage", "wreck", "vrak", "wrack" }),
        (ContractCategory.Mining, new[] { "mining", "mine ", "ore ", "těžb", "vytěž", "bergbau", "abbau" }),
        (ContractCategory.Investigation, new[] { "investigat", "missing", "search ", "locate", "pátr", "vyšetř", "pohřeš", "ermittl", "vermisst", "suche" }),
        (ContractCategory.Repair, new[] { "repair", "maintenance", "restore power", "oprav", "údržb", "reparier", "wartung" }),
        (ContractCategory.Racing, new[] { "race", "racing", "time trial", "závod", "rennen" }),
        (ContractCategory.Combat, new[] { "combat", "assault", "attack", "mercenary", "clear ", "defeat", "hostile", "destroy", "retake",
            "boj", "útok", "znič", "poraz", "nepřátel", "kampf", "angriff", "zerstör", "feind" })
    };

    // Cíle typu „Go to …“ popisují jen místo – do klasifikace je nebereme (jinak „Asteroid Mining Base“ = těžba).
    private static readonly string[] TravelPrefixes =
    {
        "go to", "return to", "travel to", "head to", "fly to", "reach", "jděte", "leťte", "vraťte se", "doleťte", "přesuňte se",
        "gehe zu", "flieg", "kehre zurück", "begib dich"
    };

    // Organizace, které bývají na začátku názvu kontraktu (když je log ani mobiGlas neuvádí).
    private static readonly string[] KnownGivers =
    {
        "Dead Saints", "Ling Family", "Covalex", "Red Wind", "Shubin", "Crusader Security", "Hurston Security", "microTech",
        "ArcCorp", "Foxwell", "Northrock", "North Rock", "Bounty Hunters Guild", "BHG", "United Wayfarers Club", "UWC",
        "Headhunters", "Citizens for Prosperity", "Wikelo", "Recco Battaglia", "Vaughn", "Tecia Pacheco", "Klim", "Eckhart",
        "Adagio", "Rough & Ready", "Nine Tails", "XenoThreat", "Frontier Fighters", "Highpoint", "Gold Horizon", "Mercury"
    };

    // Obecné hodnoty z logu, které zadavatele neurčují.
    private static readonly string[] GenericGivers = { "Mission Giver", "MissionGiver", "Civilian", "Generic" };

    public static ContractCategory Category(Contract c)
    {
        if (c.CategoryOverride is { } manual) return manual;
        var raw = string.Join(" | ", new[]
        {
            c.Title, c.ContractRecord, c.GeneratorName, c.MissionType,
            c.MissionInfo.GetValueOrDefault("Mission Type")
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        // "ShipAmbush" → "ship ambush", "Hauling_Stanton" → "hauling stanton"
        var text = " " + CamelRx().Replace(raw, "$1 $2").Replace('_', ' ').ToLowerInvariant() + " ";

        if (Match(text) is { } byTitle) return byTitle;

        // Texty cílů z logu („Defeat Hostile Ships“, „Deliver 4 SCU…“)
        var objectives = " " + string.Join(" | ", c.Objectives.Select(o => o.Text.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0 && !TravelPrefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal)))) + " ";
        if (Match(objectives) is { } byObjectives) return byObjectives;

        // Podle typů značek cílů (pickup/dropoff = doručení)
        var kinds = c.Objectives.Select(o => o.Kind).Where(k => k is not null).ToList();
        if (kinds.Any(k => k!.Contains("pickup", StringComparison.OrdinalIgnoreCase))
            && kinds.Any(k => k!.Contains("dropoff", StringComparison.OrdinalIgnoreCase)))
            return ContractCategory.Delivery;
        return ContractCategory.Other;
    }

    private static ContractCategory? Match(string text)
    {
        foreach (var (category, keywords) in Rules)
            if (keywords.Any(k => text.Contains(k, StringComparison.Ordinal)))
                return category;
        return null;
    }

    /// <summary>Doplňující typ z logu/OCR („RBM Repeatable“, „Chain“…), pokud existuje a něco říká.</summary>
    public static string? SubType(Contract c)
    {
        var ocr = c.MissionInfo.GetValueOrDefault("Mission Type");
        if (!string.IsNullOrWhiteSpace(ocr)) return ocr;
        if (string.IsNullOrWhiteSpace(c.MissionType) || c.MissionType.Equals("Generic", StringComparison.OrdinalIgnoreCase))
            return null;
        return c.MissionType;
    }

    /// <summary>Zadavatel: organizace z logu → „Contracted By“ z mobiGlasu → konkrétní osoba → prefix názvu („DEAD SAINTS - …“).</summary>
    public static string? Giver(Contract c)
    {
        foreach (var s in new[] { c.Organization, c.Contractor, c.MissionGiver })
            if (!string.IsNullOrWhiteSpace(s) && !GenericGivers.Any(g => s.Trim().Equals(g, StringComparison.OrdinalIgnoreCase)))
                return s.Trim();

        var title = (c.Title ?? "").Trim();
        var known = KnownGivers.FirstOrDefault(k => title.StartsWith(k, StringComparison.OrdinalIgnoreCase)
                                                    && (title.Length == k.Length || !char.IsLetter(title[k.Length])));
        if (known is not null) return title[..known.Length];

        var m = TitlePrefixRx().Match(title);
        if (m.Success)
        {
            var prefix = m.Groups["p"].Value.Trim();
            var lower = " " + prefix.ToLowerInvariant() + " ";
            if (!Rules.Any(r => r.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal))))
                return prefix;
        }
        return null;
    }

    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex CamelRx();

    // „DEAD SAINTS - Rookie Rank - …“ / „DEAD SAINTS -- …“ (1–4 slova bez číslic před pomlčkou)
    [GeneratedRegex(@"^(?<p>[^\d\-–>:]{2,40}?)\s+[-–]{1,2}\s+")]
    private static partial Regex TitlePrefixRx();
}
