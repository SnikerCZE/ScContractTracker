using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScContractTracker.Core.Localization;

/// <summary>
/// Texty, podle kterých aplikace poznává události v jazyce hry: notifikace v Game.logu
/// a popisky v mobiGlasu pro OCR. Každý jazyk je jeden JSON soubor v Languages/game/,
/// takže nový jazyk jde přidat bez překladu aplikace.
/// </summary>
public sealed class GameLanguagePack
{
    public string Code { get; set; } = "en";
    public string Name { get; set; } = "English";

    /// <summary>Podsložka Data/Localization/&lt;folder&gt;/global.ini v Data.p4k.</summary>
    public string LocalizationFolder { get; set; } = "english";

    /// <summary>BCP-47 tag pro Windows OCR (např. en-US, de-DE, cs-CZ).</summary>
    public string OcrLanguageTag { get; set; } = "en-US";

    public NotificationTexts Notifications { get; set; } = new();
    public MobiGlasTexts MobiGlas { get; set; } = new();

    public sealed class NotificationTexts
    {
        public List<string> Accepted { get; set; } = new();
        public List<string> Completed { get; set; } = new();
        public List<string> Failed { get; set; } = new();
        public List<string> Abandoned { get; set; } = new();
        public List<string> Withdrawn { get; set; } = new();
        public List<string> NewObjective { get; set; } = new();
        public List<string> ObjectiveCompleted { get; set; } = new();
        public List<string> ObjectiveCancelled { get; set; } = new();
        public List<string> Reward { get; set; } = new();
        /// <summary>„Received Blueprint: …“ – blueprint získaný za kontrakt.</summary>
        public List<string> BlueprintReceived { get; set; } = new();
        /// <summary>„You've earned: …“ – předmět získaný za kontrakt.</summary>
        public List<string> ItemEarned { get; set; } = new();
    }

    public sealed class MobiGlasTexts
    {
        /// <summary>Název aplikace Contracts / Contract Manager v mobiGlasu.</summary>
        public List<string> ContractsApp { get; set; } = new();
        /// <summary>Název záložky s přijatými kontrakty.</summary>
        public List<string> AcceptedTab { get; set; } = new();
        public List<string> RewardLabel { get; set; } = new();
        public List<string> ContractorLabel { get; set; } = new();
        public List<string> ObjectivesLabel { get; set; } = new();
        public List<string> DeadlineLabel { get; set; } = new();
        /// <summary>Poplatek za přijetí kontraktu v hlavičce detailu (Buy-In / Fee…).</summary>
        public List<string> FeeLabel { get; set; } = new();
        /// <summary>Ostatní záložky aplikace Contracts (OFFERS, HISTORY…) – kvůli poloze a aktivní záložce.</summary>
        public List<string> OtherTabs { get; set; } = new();
        /// <summary>Tlačítko, které je jen na záložce Accepted (ABANDON) – spolehlivý znak přijatého kontraktu.</summary>
        public List<string> AbandonButton { get; set; } = new();
        public List<string> DetailsLabel { get; set; } = new();
        /// <summary>Tlačítka pod detailem (SHARE, TRACK…) – spodní hranice obsahu.</summary>
        public List<string> BottomButtons { get; set; } = new();
        /// <summary>Nadpis seznamu možných blueprintů v popisu kontraktu.</summary>
        public List<string> PotentialBlueprintsLabel { get; set; } = new();
    }

    /// <summary>Sloučí texty jiného balíčku (typicky angličtiny jako zálohy).</summary>
    public GameLanguagePack WithFallback(GameLanguagePack fallback)
    {
        if (ReferenceEquals(this, fallback)) return this;
        static List<string> M(List<string> a, List<string> b) =>
            a.Concat(b).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new GameLanguagePack
        {
            Code = Code,
            Name = Name,
            LocalizationFolder = LocalizationFolder,
            OcrLanguageTag = OcrLanguageTag,
            Notifications = new NotificationTexts
            {
                Accepted = M(Notifications.Accepted, fallback.Notifications.Accepted),
                Completed = M(Notifications.Completed, fallback.Notifications.Completed),
                Failed = M(Notifications.Failed, fallback.Notifications.Failed),
                Abandoned = M(Notifications.Abandoned, fallback.Notifications.Abandoned),
                Withdrawn = M(Notifications.Withdrawn, fallback.Notifications.Withdrawn),
                NewObjective = M(Notifications.NewObjective, fallback.Notifications.NewObjective),
                ObjectiveCompleted = M(Notifications.ObjectiveCompleted, fallback.Notifications.ObjectiveCompleted),
                ObjectiveCancelled = M(Notifications.ObjectiveCancelled, fallback.Notifications.ObjectiveCancelled),
                BlueprintReceived = M(Notifications.BlueprintReceived, fallback.Notifications.BlueprintReceived),
                ItemEarned = M(Notifications.ItemEarned, fallback.Notifications.ItemEarned),
                Reward = M(Notifications.Reward, fallback.Notifications.Reward),
            },
            MobiGlas = new MobiGlasTexts
            {
                ContractsApp = M(MobiGlas.ContractsApp, fallback.MobiGlas.ContractsApp),
                AcceptedTab = M(MobiGlas.AcceptedTab, fallback.MobiGlas.AcceptedTab),
                RewardLabel = M(MobiGlas.RewardLabel, fallback.MobiGlas.RewardLabel),
                ContractorLabel = M(MobiGlas.ContractorLabel, fallback.MobiGlas.ContractorLabel),
                ObjectivesLabel = M(MobiGlas.ObjectivesLabel, fallback.MobiGlas.ObjectivesLabel),
                DeadlineLabel = M(MobiGlas.DeadlineLabel, fallback.MobiGlas.DeadlineLabel),
                FeeLabel = M(MobiGlas.FeeLabel, fallback.MobiGlas.FeeLabel),
                OtherTabs = M(MobiGlas.OtherTabs, fallback.MobiGlas.OtherTabs),
                AbandonButton = M(MobiGlas.AbandonButton, fallback.MobiGlas.AbandonButton),
                DetailsLabel = M(MobiGlas.DetailsLabel, fallback.MobiGlas.DetailsLabel),
                BottomButtons = M(MobiGlas.BottomButtons, fallback.MobiGlas.BottomButtons),
                PotentialBlueprintsLabel = M(MobiGlas.PotentialBlueprintsLabel, fallback.MobiGlas.PotentialBlueprintsLabel),
            }
        };
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static GameLanguagePack FromJson(string json) =>
        JsonSerializer.Deserialize<GameLanguagePack>(json, JsonOptions)
        ?? throw new InvalidDataException("Prázdný jazykový balíček.");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Načte všechny *.json ze složky; vestavěné jazyky se použijí, pokud soubor chybí.
    /// </summary>
    public static IReadOnlyDictionary<string, GameLanguagePack> LoadAll(string? folder, bool includeBuiltIn = true)
    {
        var result = includeBuiltIn
            ? BuiltIn.All.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, GameLanguagePack>(StringComparer.OrdinalIgnoreCase);
        if (folder is null || !Directory.Exists(folder)) return result;

        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var pack = FromJson(File.ReadAllText(file));
                if (string.IsNullOrWhiteSpace(pack.Code)) continue;
                // Starší soubor bez nových položek doplníme z vestavěné verze stejného jazyka.
                var builtIn = BuiltIn.All.FirstOrDefault(p => p.Code.Equals(pack.Code, StringComparison.OrdinalIgnoreCase));
                result[pack.Code] = builtIn is null ? pack : pack.WithFallback(builtIn);
            }
            catch (Exception)
            {
                // Poškozený soubor ignorujeme, zůstane vestavěná verze.
            }
        }
        return result;
    }

    /// <summary>Vestavěné balíčky EN / CS / DE.</summary>
    public static class BuiltIn
    {
        public static GameLanguagePack English { get; } = new()
        {
            Code = "en",
            Name = "English",
            LocalizationFolder = "english",
            OcrLanguageTag = "en-US",
            Notifications = new()
            {
                Accepted = ["Contract Accepted"],
                Completed = ["Contract Complete", "Contract Completed"],
                Failed = ["Contract Failed"],
                Abandoned = ["Contract Abandoned"],
                Withdrawn = ["Contract Withdrawn", "Contract Cancelled", "Contract Canceled"],
                NewObjective = ["New Objective", "Objective Updated"],
                ObjectiveCompleted = ["Objective Complete", "Objective Completed"],
                ObjectiveCancelled = ["Objective Withdrawn", "Objective Cancelled", "Objective Failed"],
                BlueprintReceived = ["Received Blueprint"],
                ItemEarned = ["You've earned", "You have earned"],
                Reward = ["Awarded", "Payment Received", "Reward", "aUEC received"]
            },
            MobiGlas = new()
            {
                ContractsApp = ["CONTRACTS", "CONTRACT MANAGER"],
                AcceptedTab = ["ACCEPTED"],
                RewardLabel = ["REWARD"],
                ContractorLabel = ["CONTRACTED BY", "CONTRACTOR"],
                ObjectivesLabel = ["PRIMARY OBJECTIVES", "OBJECTIVES"],
                DeadlineLabel = ["CONTRACT DEADLINE", "DEADLINE", "TIME LEFT"],
                FeeLabel = ["BUY-IN", "BUY IN", "BUYIN", "FEE", "ENTRY FEE", "CONTRACT FEE", "ACCEPTANCE FEE", "DEPOSIT", "COLLATERAL"],
                OtherTabs = ["OFFERS", "HISTORY", "BEACONS"],
                AbandonButton = ["ABANDON"],
                DetailsLabel = ["DETAILS"],
                BottomButtons = ["ABANDON", "SHARE", "TRACK", "UNTRACK", "ACCEPT"],
                PotentialBlueprintsLabel = ["POTENTIAL BLUEPRINTS"]
            }
        };

        public static GameLanguagePack German { get; } = new()
        {
            Code = "de",
            Name = "Deutsch",
            LocalizationFolder = "german_(germany)",
            OcrLanguageTag = "de-DE",
            Notifications = new()
            {
                Accepted = ["Auftrag angenommen", "Vertrag angenommen"],
                Completed = ["Auftrag abgeschlossen", "Vertrag abgeschlossen"],
                Failed = ["Auftrag fehlgeschlagen", "Vertrag fehlgeschlagen"],
                Abandoned = ["Auftrag abgebrochen", "Auftrag aufgegeben"],
                Withdrawn = ["Auftrag zurückgezogen", "Auftrag storniert"],
                NewObjective = ["Neues Ziel", "Ziel aktualisiert"],
                ObjectiveCompleted = ["Ziel abgeschlossen", "Ziel erreicht"],
                ObjectiveCancelled = ["Ziel abgebrochen", "Ziel fehlgeschlagen"],
                BlueprintReceived = ["Blaupause erhalten"],
                ItemEarned = ["Du hast erhalten"],
                Reward = ["Belohnung", "Missions-Belohnung", "Zahlung erhalten", "aUEC erhalten"]
            },
            MobiGlas = new()
            {
                ContractsApp = ["AUFTRÄGE", "AUFTRAGSMANAGER", "VERTRÄGE"],
                AcceptedTab = ["ANGENOMMEN"],
                RewardLabel = ["BELOHNUNG"],
                ContractorLabel = ["AUFTRAGGEBER"],
                ObjectivesLabel = ["HAUPTZIELE", "ZIELE"],
                DeadlineLabel = ["AUFTRAGSFRIST", "FRIST", "ZEITLIMIT"],
                FeeLabel = ["GEBÜHR", "EINSATZ", "TEILNAHMEGEBÜHR", "KAUTION", "ANZAHLUNG"],
                OtherTabs = ["ANGEBOTE", "VERLAUF", "HISTORIE", "BAKEN", "NOTRUF-BAKEN"],
                AbandonButton = ["ABBRECHEN", "AUFGEBEN"],
                DetailsLabel = ["DETAILS"],
                BottomButtons = ["ABBRECHEN", "AUFGEBEN", "TEILEN", "VERFOLGEN", "NICHT VERFOLGEN", "ANNEHMEN"],
                PotentialBlueprintsLabel = ["MÖGLICHE BLAUPAUSEN", "POTENZIELLE BLAUPAUSEN"]
            }
        };

        // Čeština v Star Citizenu je komunitní překlad; texty se dají upravit v Languages/game/cs.json.
        public static GameLanguagePack Czech { get; } = new()
        {
            Code = "cs",
            Name = "Čeština",
            LocalizationFolder = "czech_(czech_republic)",
            OcrLanguageTag = "cs-CZ",
            Notifications = new()
            {
                Accepted = ["Kontrakt přijat", "Zakázka přijata"],
                Completed = ["Kontrakt dokončen", "Zakázka dokončena"],
                Failed = ["Kontrakt selhal", "Zakázka selhala"],
                Abandoned = ["Kontrakt opuštěn", "Kontrakt zrušen", "Zakázka opuštěna"],
                Withdrawn = ["Kontrakt stažen", "Zakázka stažena"],
                NewObjective = ["Nový úkol", "Nový cíl", "Úkol aktualizován"],
                ObjectiveCompleted = ["Úkol splněn", "Cíl splněn"],
                ObjectiveCancelled = ["Úkol zrušen", "Úkol selhal"],
                BlueprintReceived = ["Získán plán", "Obdržen plán"],
                ItemEarned = ["Získali jste", "Získal jste"],
                Reward = ["Odměna", "Platba přijata", "aUEC přijato"]
            },
            MobiGlas = new()
            {
                ContractsApp = ["KONTRAKTY", "ZAKÁZKY", "SPRÁVCE KONTRAKTŮ"],
                AcceptedTab = ["PŘIJATÉ", "PŘIJATO"],
                RewardLabel = ["ODMĚNA"],
                ContractorLabel = ["ZADAVATEL", "KONTRAKTOR"],
                ObjectivesLabel = ["HLAVNÍ CÍLE", "CÍLE"],
                DeadlineLabel = ["TERMÍN KONTRAKTU", "TERMÍN", "ZBÝVÁ"],
                FeeLabel = ["POPLATEK", "VSTUPNÍ POPLATEK", "ZÁLOHA", "KAUCE"],
                OtherTabs = ["NABÍDKY", "HISTORIE", "MAJÁKY"],
                AbandonButton = ["OPUSTIT", "VZDÁT"],
                DetailsLabel = ["PODROBNOSTI", "DETAILY"],
                BottomButtons = ["OPUSTIT", "VZDÁT", "SDÍLET", "SLEDOVAT", "NESLEDOVAT", "PŘIJMOUT"],
                PotentialBlueprintsLabel = ["MOŽNÉ PLÁNY", "POTENCIÁLNÍ PLÁNY", "MOŽNÉ BLUEPRINTY"]
            }
        };

        public static IReadOnlyList<GameLanguagePack> All { get; } = [English, Czech, German];
    }
}
