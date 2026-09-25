namespace ScContractTracker.Core.Models;

/// <summary>Auto = konec kontraktu podle Game.logu; Manual = konec jen tlačítkem v aplikaci (log se jen poznamená).</summary>
public enum CompletionMode
{
    Auto,
    Manual
}

public enum ContractStatus
{
    Active,
    Completed,
    Failed,
    Abandoned,
    Withdrawn
}

/// <summary>
/// Jeden přijatý kontrakt (jedna instance mise). Identita je MissionId z Game.logu,
/// pokud ho log neposkytne, použije se syntetické ID z názvu a času přijetí.
/// </summary>
public sealed class Contract
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Interní název záznamu v DataCore (např. "Hauling_Stanton_..."), pokud ho log uvádí.</summary>
    public string? ContractRecord { get; set; }
    public string? GeneratorName { get; set; }
    /// <summary>GUID definice kontraktu v DataCore (contractDefinitionId z logu).</summary>
    public string? ContractDefinitionId { get; set; }

    public string? Contractor { get; set; }
    public string? Description { get; set; }
    /// <summary>Odkud je popis: "ocr" (skutečný text z mobiGlasu) nebo "p4k" (šablona z global.ini v Data.p4k).</summary>
    public string? DescriptionSource { get; set; }

    // ---- Další odměny a informace ----

    /// <summary>Štítky z názvu (bez závorek): „300 Rep“, „100–500 Rep“, „BP“, „ACE“.</summary>
    public List<string> Tags { get; set; } = new();
    /// <summary>Reputace podle štítku v názvu (min–max; u pevné hodnoty stejné).</summary>
    public int? RepMin { get; set; }
    public int? RepMax { get; set; }
    /// <summary>Štítek [BP] – kontrakt může odměnit blueprintem.</summary>
    public bool OffersBlueprint { get; set; }
    /// <summary>Možné blueprinty ze seznamu „POTENTIAL BLUEPRINTS“ v popisu (OCR).</summary>
    public List<string> PotentialBlueprints { get; set; } = new();
    /// <summary>Skutečně získané blueprinty („Received Blueprint“ v logu).</summary>
    public List<string> ReceivedBlueprints { get; set; } = new();
    /// <summary>Získané předměty („You've earned“ v logu).</summary>
    public List<string> ItemRewards { get; set; } = new();

    /// <summary>Organizace / konkrétní zadavatel / typ mise ze zprávy MissionAccept v logu.</summary>
    public string? Organization { get; set; }
    public string? MissionGiver { get; set; }
    public string? MissionType { get; set; }

    /// <summary>Údaje z části „MISSION DETAILS“ popisu: Mission Type, Combat 2/7, Complexity 3/7…</summary>
    public Dictionary<string, string> MissionInfo { get; set; } = new();

    /// <summary>Odměna v aUEC. Zdroj: OCR mobiGlasu, Game.log nebo ruční zadání.</summary>
    public long? Reward { get; set; }
    public string? RewardSource { get; set; }

    /// <summary>Poplatek za přijetí kontraktu (aUEC). Zdroj: OCR („Buy-In“, „Fee“…) nebo ruční zadání.</summary>
    public long? Fee { get; set; }
    public string? FeeSource { get; set; }

    /// <summary>Čistý výdělek = odměna − poplatek.</summary>
    public long? NetReward => Reward is { } r ? r - (Fee ?? 0) : null;

    /// <summary>Režim dokončení (zkopíruje se z výchozího nastavení při založení kontraktu).</summary>
    public CompletionMode CompletionMode { get; set; } = CompletionMode.Auto;

    /// <summary>V ručním režimu: kdy a jak kontrakt podle logu skončil (použije se jako čas při ručním dokončení).</summary>
    public DateTimeOffset? LogFinishedAt { get; set; }
    public ContractStatus? LogFinishStatus { get; set; }

    /// <summary>Ručně nastavený typ (null = odhad z názvu, logu a cílů).</summary>
    public ContractCategory? CategoryOverride { get; set; }

    /// <summary>Ručně upravená pole ("title", "description", "contractor", "giver", "fee"…) – log ani OCR je nepřepíšou.</summary>
    public List<string> ManualFields { get; set; } = new();

    public bool IsManual(string field) => ManualFields.Contains(field, StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset AcceptedAt { get; set; }
    /// <summary>Poslední událost v logu, která se kontraktu týkala.</summary>
    public DateTimeOffset? LastSeen { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Active;
    /// <summary>Proč kontrakt skončil, pokud to není vidět ze stavu: "logout" = hra ho zrušila při odhlášení/ukončení.</summary>
    public string? EndReason { get; set; }

    public List<ContractObjective> Objectives { get; set; } = new();

    /// <summary>Názvy míst, která se u kontraktu objevila (z logu, OCR nebo p4k).</summary>
    public List<string> LocationHints { get; set; } = new();

    public TimeSpan? Duration => FinishedAt is { } f ? f - AcceptedAt : null;

    public TimeSpan ElapsedAt(DateTimeOffset now) => (FinishedAt ?? now) - AcceptedAt;

    /// <summary>Výtěžnost aUEC/h pro dokončený kontrakt s odměnou.</summary>
    public double? RewardPerHour
    {
        get
        {
            if (Status != ContractStatus.Completed || NetReward is not { } r || Duration is not { } d) return null;
            if (d.TotalSeconds < 1) return null;
            return r / d.TotalHours;
        }
    }

    /// <summary>Klíč pro seskupování ve statistikách (stejný typ kontraktu).</summary>
    public string TemplateKey =>
        !string.IsNullOrWhiteSpace(ContractRecord) ? ContractRecord! : Normalize(Title);

    /// <summary>
    /// Aktuální úkol: první cíl s textem, který ještě není splněný ani zrušený.
    /// (Metoda, ne vlastnost – aby se neukládala do JSON.)
    /// </summary>
    public ContractObjective? GetCurrentObjective() =>
        Objectives.FirstOrDefault(o => o.Text.Length > 0 && !o.Completed && !o.Cancelled);

    internal static string Normalize(string s) =>
        string.Join(' ', s.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed class ContractObjective
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Location { get; set; }
    public bool Completed { get; set; }
    /// <summary>Čas, kdy se cíl poprvé objevil v logu.</summary>
    public DateTimeOffset? AddedAt { get; set; }
    /// <summary>Čas splnění cíle (z logu; u ručně/OCR označených může chybět).</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Cíl zrušen („Objective Withdrawn“ / „ÚKOL ZRUŠEN“).</summary>
    public bool Cancelled { get; set; }

    /// <summary>Odkud text pochází: "log" (přesný, z Game.logu) nebo "ocr" (z mobiGlasu).</summary>
    public string? Source { get; set; }

    /// <summary>Typ cíle podle ID značky v logu (pickup / dropoff / …).</summary>
    public string? Kind { get; set; }

    /// <summary>Souřadnice značky cíle z logu (metry, relativně k tělesu <see cref="ZoneHostId"/>).</summary>
    public double[]? Position { get; set; }
    public string? ZoneHostId { get; set; }
}
