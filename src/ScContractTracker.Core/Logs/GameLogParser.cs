using System.Globalization;
using System.Text.RegularExpressions;
using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Models;

namespace ScContractTracker.Core.Logs;

/// <summary>
/// Převádí řádky Game.logu na <see cref="GameEvent"/>. Technické značky (MissionEnded,
/// EndMission, objective_id…) jsou nezávislé na jazyce, texty notifikací bere z jazykového balíčku.
/// Formát logu se mezi patchi mění, proto jsou výrazy záměrně tolerantní.
/// </summary>
public sealed partial class GameLogParser
{
    private GameLanguagePack _pack;

    public GameLogParser(GameLanguagePack pack) => _pack = WithAllLanguages(pack);

    public void SetLanguage(GameLanguagePack pack) => _pack = WithAllLanguages(pack);

    /// <summary>
    /// Jazyk hry se dá přepnout mezi relacemi (import starších logů), proto log čteme se všemi
    /// vestavěnými jazyky najednou – zvolený jazyk má jen přednost.
    /// </summary>
    private static GameLanguagePack WithAllLanguages(GameLanguagePack pack) =>
        GameLanguagePack.BuiltIn.All.Aggregate(pack, (acc, other) => acc.WithFallback(other));

    [GeneratedRegex(@"^<(?<ts>\d{4}-\d{2}-\d{2}T[\d:.]+Z?)>")]
    private static partial Regex TimestampRx();

    // Text může obsahovat uvozovky (You've earned: Jumpsuit "Edition"), konec poznáme podle „" [n] to queue“.
    [GeneratedRegex(@"Added notification ""(?<text>.*?)"" \[\d+\]")]
    private static partial Regex NotificationRx();

    [GeneratedRegex(@"Added notification ""(?<text>[^""]*)""")]
    private static partial Regex NotificationSimpleRx();

    // Text s koncem řádku uvnitř (hra ho rozdělí na více řádků logu)
    [GeneratedRegex(@"Added notification ""(?<text>[^""]+)$")]
    private static partial Regex NotificationOpenRx();

    [GeneratedRegex(@"\[(?<tag>[^\[\]]{1,24})\]")]
    private static partial Regex BracketTagRx();

    [GeneratedRegex(@"(?<a>\d[\d,.]*)\s*(?:[–-]\s*(?<b>\d[\d,.]*))?\s*Rep\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepTagRx();

    // SendCommsNotification +Missions.Organization.X,Missions.Organization.X.Giver,Missions.MissionType.T,… - Mission: [id]
    [GeneratedRegex(@"SendCommsNotification \+(?<tokens>\S+).*?Mission: \[(?<id>[0-9a-fA-F-]{36})\]")]
    private static partial Regex CommsRx();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelRx();

    [GeneratedRegex(@"MissionId:\s*\[(?<id>[0-9a-fA-F-]{36})\]")]
    private static partial Regex NotificationMissionIdRx();

    [GeneratedRegex(@"missionId \[(?<id>[0-9a-fA-F-]{36})\], generator name \[(?<gen>[^\]]+)\], contract \[(?<con>[^\]]+)\]")]
    private static partial Regex MetadataRx();

    // <CLocalMissionPhaseMarker::CreateMarker> … contractDefinitionId[..], objectiveId [..], markerEntityId [..], zoneHostId [..], position [x: .., y: .., z: ..]
    [GeneratedRegex(@"contractDefinitionId\[(?<def>[0-9a-fA-F-]{36})\]")]
    private static partial Regex ContractDefinitionRx();

    [GeneratedRegex(@"objectiveId \[(?<obj>[^\]]+)\].*?zoneHostId \[(?<zone>\d+)\], position \[x: (?<x>-?[\d.]+), y: (?<y>-?[\d.]+), z: (?<z>-?[\d.]+)\]")]
    private static partial Regex MarkerRx();

    [GeneratedRegex(@"ObjectiveId: \[(?<obj>[^\]]*)\]")]
    private static partial Regex NotificationObjectiveIdRx();

    // Štítky v názvu: [200 Rep], [100–500 Rep], [BP], [ACE]
    [GeneratedRegex(@"\[[^\[\]]{1,24}\]")]
    private static partial Regex TitleTagRx();

    [GeneratedRegex(@"<MissionEnded>.*?mission_id (?<id>[0-9a-fA-F-]{36}).*?mission_state (?<state>MISSION_STATE_\w+)")]
    private static partial Regex MissionEndedRx();

    [GeneratedRegex(@"<EndMission>.*?MissionId\[(?<id>[0-9a-fA-F-]{36})\].*?CompletionType\[(?<type>[^\]]+)\]")]
    private static partial Regex EndMissionRx();

    [GeneratedRegex(@"<PlayerLeft>.*?mission_id (?<id>[0-9a-fA-F-]{36})")]
    private static partial Regex PlayerLeftRx();

    [GeneratedRegex(@"mission_id (?<id>[0-9a-fA-F-]{36}) - objective_id (?<obj>\S+) - state (?<state>MISSION_OBJECTIVE_STATE_\w+)")]
    private static partial Regex ObjectiveRx();

    [GeneratedRegex(@"requested inventory for Location\[(?<loc>[^\]]+)\]")]
    private static partial Regex InventoryLocationRx();

    [GeneratedRegex(@"(?:Successfully calculated route to|selected point) (?<dest>[^\s,\]]+)")]
    private static partial Regex QuantumRx();

    [GeneratedRegex(@"(?<amt>\d{1,3}(?:[.,  ]\d{3})+|\d+)\s*a?UEC", RegexOptions.IgnoreCase)]
    private static partial Regex AmountRx();

    [GeneratedRegex(@"<[^>]*>|~mission\([^)]*\)")]
    private static partial Regex MarkupRx();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRx();

    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    public GameEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var time = ParseTimestamp(line);
        if (time is null) return null;
        var t = time.Value;

        // 0) Začátek relace
        if (line.Contains("> Log started on ", StringComparison.Ordinal))
            return new GameEvent(GameEventKind.SessionStarted, t) { RawLine = line };

        // 0b) Řádné odhlášení nebo ukončení hry z menu → hra zruší všechny přijaté kontrakty.
        //     Při pádu hry tyto řádky chybí (log končí náhle) a kontrakty zůstávají přijaté.
        //     Samotné „<SystemQuit> … User closed the application“ (zavření okna bez odpojení) se chová jako pád –
        //     v reálném logu 18. 9. hra po dalším přihlášení stejný kontrakt znovu ohlásila.
        //     Pozor: „Channel Disconnected cause=30010 Nub destroyed“ při startu hry NENÍ odhlášení.
        if ((line.Contains("<Channel Disconnected>", StringComparison.Ordinal) && line.Contains("Player requested disconnect", StringComparison.Ordinal))
            || line.Contains("CGame::OnDisconnected Client quitting game", StringComparison.Ordinal))
            return new GameEvent(GameEventKind.SessionEnded, t) { RawLine = line };

        // 1) Metadata kontraktu (propojení na DataCore)
        if (line.Contains("generator name [", StringComparison.Ordinal))
        {
            var m = MetadataRx().Match(line);
            if (m.Success)
            {
                var def = ContractDefinitionRx().Match(line);
                var marker = MarkerRx().Match(line);
                return new GameEvent(GameEventKind.ContractMetadata, t)
                {
                    MissionId = m.Groups["id"].Value.ToLowerInvariant(),
                    GeneratorName = m.Groups["gen"].Value,
                    ContractRecord = m.Groups["con"].Value,
                    ContractDefinitionId = def.Success ? def.Groups["def"].Value.ToLowerInvariant() : null,
                    ObjectiveId = marker.Success ? marker.Groups["obj"].Value : null,
                    ZoneHostId = marker.Success ? marker.Groups["zone"].Value : null,
                    Position = marker.Success
                        ? [ParseDouble(marker.Groups["x"].Value), ParseDouble(marker.Groups["y"].Value), ParseDouble(marker.Groups["z"].Value)]
                        : null,
                    RawLine = line
                };
            }
        }

        // 2) Konec mise (autoritativní stav)
        if (line.Contains("<MissionEnded>", StringComparison.Ordinal))
        {
            var m = MissionEndedRx().Match(line);
            if (m.Success)
            {
                var kind = MapMissionState(m.Groups["state"].Value);
                if (kind is not null)
                    return new GameEvent(kind.Value, t) { MissionId = m.Groups["id"].Value.ToLowerInvariant(), RawLine = line };
            }
        }
        // „<PlayerLeft> Received PlayerLeft push message for: mission_id X“ – hráč misi opustil (Abandon)
        if (line.Contains("<PlayerLeft>", StringComparison.Ordinal))
        {
            var m = PlayerLeftRx().Match(line);
            if (m.Success)
                return new GameEvent(GameEventKind.ContractAbandoned, t) { MissionId = m.Groups["id"].Value.ToLowerInvariant(), RawLine = line };
        }
        if (line.Contains("<EndMission>", StringComparison.Ordinal))
        {
            // Tlačítko ABANDON ve hře zapíše „CompletionType[Abandon] Reason[Player left]“ (ověřeno na reálném logu).
            // Obyčejné odhlášení / ukončení hry EndMission NEzapisuje – kontrakt zůstává přijatý.
            var m = EndMissionRx().Match(line);
            if (m.Success)
            {
                var kind = MapCompletionType(m.Groups["type"].Value);
                if (kind is not null)
                    return new GameEvent(kind.Value, t) { MissionId = m.Groups["id"].Value.ToLowerInvariant(), RawLine = line };
            }
        }

        // 3) Cíle mise
        if (line.Contains("objective_id", StringComparison.Ordinal))
        {
            var m = ObjectiveRx().Match(line);
            // Skryté interní fáze mise (flags=Hidden…) nejsou cíle pro hráče.
            if (m.Success && line.Contains("flags=Hidden", StringComparison.Ordinal)) return null;
            if (m.Success)
            {
                var state = m.Groups["state"].Value;
                var kind = state.EndsWith("COMPLETED", StringComparison.Ordinal)
                    ? GameEventKind.ObjectiveCompleted
                    : GameEventKind.ObjectiveUpdated;
                return new GameEvent(kind, t)
                {
                    MissionId = m.Groups["id"].Value.ToLowerInvariant(),
                    ObjectiveId = m.Groups["obj"].Value,
                    RawLine = line
                };
            }
        }

        // 3b) Zadavatel a typ mise (MissionAccept comms)
        if (line.Contains("SendCommsNotification +", StringComparison.Ordinal))
        {
            var ev = ParseComms(line, t);
            if (ev is not null) return ev;
        }

        // 4) Notifikace HUDu (jazykově závislé)
        if (line.Contains("Added notification", StringComparison.Ordinal))
        {
            var ev = ParseNotification(line, t);
            if (ev is not null) return ev;
        }

        // 5) Poloha hráče
        if (line.Contains("requested inventory for Location", StringComparison.Ordinal))
        {
            var m = InventoryLocationRx().Match(line);
            if (m.Success)
                return new GameEvent(GameEventKind.LocationChanged, t) { Location = m.Groups["loc"].Value, RawLine = line };
        }
        if (line.Contains("calculated route to", StringComparison.Ordinal) || line.Contains("as their destination", StringComparison.Ordinal))
        {
            var m = QuantumRx().Match(line);
            if (m.Success)
                return new GameEvent(GameEventKind.QuantumDestination, t) { Location = m.Groups["dest"].Value, RawLine = line };
        }

        return null;
    }

    private GameEvent? ParseNotification(string line, DateTimeOffset t)
    {
        var m = NotificationRx().Match(line);
        if (!m.Success) m = NotificationSimpleRx().Match(line);
        if (!m.Success) m = NotificationOpenRx().Match(line);
        if (!m.Success) return null;

        var text = Clean(m.Groups["text"].Value);
        var idMatch = NotificationMissionIdRx().Match(line);
        string? missionId = idMatch.Success && idMatch.Groups["id"].Value != EmptyGuid
            ? idMatch.Groups["id"].Value.ToLowerInvariant()
            : null;
        var objMatch = NotificationObjectiveIdRx().Match(line);
        string? objectiveId = objMatch.Success && objMatch.Groups["obj"].Value.Length > 0 ? objMatch.Groups["obj"].Value : null;

        var n = _pack.Notifications;
        (GameEventKind kind, List<string> prefixes)[] map =
        [
            (GameEventKind.ContractAccepted, n.Accepted),
            (GameEventKind.ContractCompleted, n.Completed),
            (GameEventKind.ContractFailed, n.Failed),
            (GameEventKind.ContractAbandoned, n.Abandoned),
            (GameEventKind.ContractWithdrawn, n.Withdrawn),
            (GameEventKind.ObjectiveUpdated, n.NewObjective),
            (GameEventKind.ObjectiveCompleted, n.ObjectiveCompleted),
            (GameEventKind.ObjectiveCancelled, n.ObjectiveCancelled),
            (GameEventKind.BlueprintReceived, n.BlueprintReceived),
            (GameEventKind.ItemEarned, n.ItemEarned),
        ];

        foreach (var (kind, prefixes) in map)
        {
            // Nejdelší prefix první, aby "Contract Completed" nevyhrálo jako "Contract Complete" + "d".
            foreach (var p in prefixes.OrderByDescending(p => p.Length))
            {
                if (!text.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
                var title = text[p.Length..].Trim(' ', ':', '-', '–', '.');
                var isContract = kind is GameEventKind.ContractAccepted or GameEventKind.ContractCompleted
                    or GameEventKind.ContractFailed or GameEventKind.ContractAbandoned or GameEventKind.ContractWithdrawn;
                var tags = isContract ? ExtractTags(title) : null;
                if (isContract) title = CleanTitle(title);
                if (kind is GameEventKind.BlueprintReceived or GameEventKind.ItemEarned) title = title.Trim(' ', '"', ':');
                var isObjective = kind is GameEventKind.ObjectiveUpdated or GameEventKind.ObjectiveCompleted or GameEventKind.ObjectiveCancelled;
                return new GameEvent(kind, t)
                {
                    MissionId = missionId,
                    Title = title,
                    Tags = tags,
                    ObjectiveId = isObjective ? objectiveId : null,
                    RawLine = line
                };
            }
        }

        if (n.Reward.Any(r => text.Contains(r, StringComparison.OrdinalIgnoreCase)))
        {
            var a = AmountRx().Match(text);
            if (a.Success && TryParseAmount(a.Groups["amt"].Value, out var amount))
                return new GameEvent(GameEventKind.RewardReceived, t) { MissionId = missionId, Amount = amount, Title = text, RawLine = line };
        }

        return null;
    }

    internal static string Clean(string s)
    {
        s = MarkupRx().Replace(s, " ");
        return MultiSpaceRx().Replace(s, " ").Trim();
    }

    /// <summary>Štítky z názvu: „[300 Rep] [BP]“ → ["300 Rep", "BP"].</summary>
    public static IReadOnlyList<string> ExtractTags(string title) =>
        BracketTagRx().Matches(title).Select(x => x.Groups["tag"].Value.Trim()).Where(x => x.Length > 0).ToList();

    /// <summary>Reputace ze štítku: „300 Rep“ → (300, 300), „100–500 Rep“ → (100, 500), „2,000 Rep“ → (2000, 2000).</summary>
    public static (int Min, int Max)? ParseRep(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            var m = RepTagRx().Match(tag);
            if (!m.Success) continue;
            if (!TryParseAmount(m.Groups["a"].Value, out var a)) continue;
            var b = m.Groups["b"].Success && TryParseAmount(m.Groups["b"].Value, out var bb) ? bb : a;
            return ((int)a, (int)b);
        }
        return null;
    }

    /// <summary>„UnitedWayfarersClub“ → „United Wayfarers Club“.</summary>
    public static string Humanize(string s) => CamelRx().Replace(s.Replace('_', ' ').Replace('.', ' '), " ").Trim();

    private GameEvent? ParseComms(string line, DateTimeOffset t)
    {
        var m = CommsRx().Match(line);
        if (!m.Success) return null;
        string? org = null, giver = null, type = null, faction = null;
        foreach (var token in m.Groups["tokens"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('.');
            if (token.StartsWith("Missions.Organization.MissionGiver.", StringComparison.Ordinal) && parts.Length >= 4)
                giver ??= Humanize(parts[3]);
            else if (token.StartsWith("Missions.Organization.", StringComparison.Ordinal) && parts.Length >= 3)
            {
                org ??= Humanize(parts[2]);
                if (parts.Length >= 4) giver ??= Humanize(parts[3]);
            }
            else if (token.StartsWith("Missions.MissionType.", StringComparison.Ordinal) && parts.Length >= 3)
            {
                // nejkonkrétnější typ (RBM.Repeatable má přednost před RBM)
                var candidate = string.Join(' ', parts.Skip(2).Select(Humanize));
                if (type is null || candidate.Length > type.Length) type = candidate;
            }
            else if (token.StartsWith("AI.Faction.", StringComparison.Ordinal) && parts.Length >= 3)
                faction ??= Humanize(parts[2]);
        }
        if (org is null && giver is null && type is null && faction is null) return null;
        return new GameEvent(GameEventKind.ContractGiver, t)
        {
            MissionId = m.Groups["id"].Value.ToLowerInvariant(),
            Organization = org ?? faction,
            MissionGiver = giver,
            MissionType = type,
            RawLine = line
        };
    }

    /// <summary>Název kontraktu bez štítků „[200 Rep]“, „[BP]“.</summary>
    public static string CleanTitle(string title) =>
        MultiSpaceRx().Replace(TitleTagRx().Replace(title, " "), " ").Trim(' ', ':', '-', '–');

    private static double ParseDouble(string s) =>
        double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static bool TryParseAmount(string raw, out long amount)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out amount);
    }

    internal static DateTimeOffset? ParseTimestamp(string line)
    {
        var m = TimestampRx().Match(line);
        if (!m.Success) return null;
        var raw = m.Groups["ts"].Value;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto
            : null;
    }

    private static GameEventKind? MapMissionState(string state) => state switch
    {
        "MISSION_STATE_COMPLETED" or "MISSION_STATE_SUCCEEDED" => GameEventKind.ContractCompleted,
        "MISSION_STATE_FAILED" => GameEventKind.ContractFailed,
        "MISSION_STATE_ABANDONED" => GameEventKind.ContractAbandoned,
        "MISSION_STATE_WITHDRAWN" or "MISSION_STATE_CANCELLED" => GameEventKind.ContractWithdrawn,
        _ => null
    };

    private static GameEventKind? MapCompletionType(string type)
    {
        var t = type.ToLowerInvariant();
        if (t.Contains("complete") || t.Contains("success")) return GameEventKind.ContractCompleted;
        if (t.Contains("fail")) return GameEventKind.ContractFailed;
        if (t.Contains("abandon")) return GameEventKind.ContractAbandoned;
        if (t.Contains("withdraw") || t.Contains("cancel")) return GameEventKind.ContractWithdrawn;
        return null;
    }
}
