using ScContractTracker.Core.Logs;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Ocr;
using ScContractTracker.Core.Storage;
using ScContractTracker.Core.Text;

namespace ScContractTracker.Core.Tracking;

/// <summary>
/// Stav všech kontraktů. Skládá dohromady události z Game.logu (časy, stav, ID)
/// a snímky mobiGlasu z OCR (odměna, zadavatel, cíle). Operace jsou idempotentní,
/// takže opakované přečtení stejného logu nevytvoří duplicity.
/// </summary>
public sealed class ContractTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Contract> _contracts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RewardEntry> _rewards = new();
    private readonly IContractStore? _store;
    private readonly Func<string, IEnumerable<string>>? _findLocations;

    public const double TitleMatchThreshold = 0.8;

    public ContractTracker(IContractStore? store = null, Func<string, IEnumerable<string>>? findLocationMentions = null)
    {
        _store = store;
        _findLocations = findLocationMentions;
        if (_store is not null)
        {
            foreach (var c in _store.Load())
                _contracts[c.Id] = c;
            _rewards.AddRange(_store.LoadRewards());
        }
    }

    public PlayerState Player { get; } = new();

    /// <summary>Režim dokončení pro nově založené kontrakty (výchozí přepínač v aplikaci).</summary>
    public CompletionMode DefaultCompletionMode { get; set; } = CompletionMode.Auto;

    /// <summary>Ručně založený kontrakt (bez záznamu v logu).</summary>
    public Contract AddManual(Contract c)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || _contracts.ContainsKey(c.Id)) c.Id = "manual:" + Guid.NewGuid().ToString("N");
            if (c.AcceptedAt == default) c.AcceptedAt = DateTimeOffset.UtcNow;
            _contracts[c.Id] = c;
        }
        Persist();
        return c;
    }

    /// <summary>Vyvolá se po každé změně (z libovolného vlákna).</summary>
    public event Action? Changed;

    /// <summary>Všechny získané blueprinty a předměty (nejnovější první).</summary>
    public IReadOnlyList<RewardEntry> Rewards()
    {
        lock (_lock) return _rewards.OrderByDescending(r => r.Time).ToList();
    }

    public IReadOnlyList<Contract> Snapshot()
    {
        lock (_lock) return _contracts.Values.OrderByDescending(c => c.AcceptedAt).ToList();
    }

    public IReadOnlyList<Contract> Active()
    {
        lock (_lock) return _contracts.Values.Where(c => c.Status == ContractStatus.Active)
            .OrderByDescending(c => c.AcceptedAt).ToList();
    }

    public void Apply(GameEvent ev)
    {
        bool changed;
        lock (_lock) changed = ApplyCore(ev);
        if (changed) Persist();
    }

    public void ApplyMany(IEnumerable<GameEvent> events)
    {
        var changed = false;
        lock (_lock)
            foreach (var ev in events)
                changed |= ApplyCore(ev);
        if (changed) Persist();
    }

    private bool ApplyCore(GameEvent ev)
    {
        var changed = ApplyCoreInner(ev);
        if (ev.MissionId is not null && _contracts.TryGetValue(ev.MissionId, out var seen)
            && (seen.LastSeen is null || ev.Time > seen.LastSeen))
            seen.LastSeen = ev.Time;
        return changed;
    }

    private bool ApplyCoreInner(GameEvent ev)
    {
        switch (ev.Kind)
        {
            case GameEventKind.ContractMetadata:
                return OnMetadata(ev);
            case GameEventKind.ContractAccepted:
                return OnAccepted(ev);
            case GameEventKind.ContractCompleted:
                return OnFinished(ev, ContractStatus.Completed);
            case GameEventKind.ContractFailed:
                return OnFinished(ev, ContractStatus.Failed);
            case GameEventKind.ContractAbandoned:
                return OnFinished(ev, ContractStatus.Abandoned);
            case GameEventKind.ContractWithdrawn:
                return OnFinished(ev, ContractStatus.Withdrawn);
            case GameEventKind.ObjectiveUpdated:
            case GameEventKind.ObjectiveCompleted:
                return OnObjective(ev);
            case GameEventKind.ObjectiveCancelled:
                return OnObjectiveCancelled(ev);
            case GameEventKind.BlueprintReceived:
            case GameEventKind.ItemEarned:
                return OnItemReward(ev);
            case GameEventKind.ContractGiver:
                return OnGiver(ev);
            case GameEventKind.SessionStarted:
                Player.SessionStart = ev.Time;
                return false;
            case GameEventKind.SessionEnded:
                return OnSessionEnded(ev);
            case GameEventKind.RewardReceived:
                return OnReward(ev);
            case GameEventKind.LocationChanged:
                Player.LastLocation = ev.Location;
                Player.LastLocationTime = ev.Time;
                return false;
            case GameEventKind.QuantumDestination:
                Player.LastQuantumDestination = ev.Location;
                return false;
            default:
                return false;
        }
    }

    private bool OnMetadata(GameEvent ev)
    {
        if (ev.MissionId is null) return false;
        if (!_contracts.TryGetValue(ev.MissionId, out var c))
        {
            // Metadata může přijít před notifikací – zkusíme spárovat s čerstvým kontraktem bez ID.
            c = _contracts.Values
                .Where(x => x.Id.StartsWith("t:", StringComparison.Ordinal) && x.ContractRecord is null
                            && Math.Abs((x.AcceptedAt - ev.Time).TotalSeconds) <= 15)
                .OrderBy(x => Math.Abs((x.AcceptedAt - ev.Time).TotalSeconds))
                .FirstOrDefault();
            if (c is not null)
            {
                _contracts.Remove(c.Id);
                c.Id = ev.MissionId;
            }
            else
            {
                c = new Contract { Id = ev.MissionId, AcceptedAt = ev.Time, Title = ev.ContractRecord ?? "", CompletionMode = DefaultCompletionMode };
            }
            _contracts[c.Id] = c;
        }
        var changed = Reactivate(c, ev.Time);
        changed |= ApplyPendingGiver(c);
        changed |= c.ContractRecord != ev.ContractRecord || c.GeneratorName != ev.GeneratorName;
        c.ContractRecord = ev.ContractRecord;
        c.GeneratorName = ev.GeneratorName;
        if (ev.ContractDefinitionId is not null && c.ContractDefinitionId != ev.ContractDefinitionId)
        {
            c.ContractDefinitionId = ev.ContractDefinitionId;
            changed = true;
        }

        // Značka cíle (CreateMarker): ID cíle, typ a souřadnice
        if (ev.ObjectiveId is { } objId)
        {
            var o = c.Objectives.FirstOrDefault(x => x.Id == objId);
            if (o is null)
            {
                o = new ContractObjective { Id = objId };
                c.Objectives.Add(o);
                changed = true;
            }
            o.AddedAt ??= ev.Time;
            var kind = KindFromId(objId);
            if (kind is not null && o.Kind != kind) { o.Kind = kind; changed = true; }
            if (ev.Position is not null) { o.Position = ev.Position; o.ZoneHostId = ev.ZoneHostId; }
        }
        return changed;
    }

    private bool OnAccepted(GameEvent ev)
    {
        var title = ev.Title ?? "";
        Contract? c = null;

        if (ev.MissionId is not null)
            _contracts.TryGetValue(ev.MissionId, out c);

        if (c is null)
        {
            // Kontrakt založený z metadat (titul = interní jméno) do 15 s
            c = _contracts.Values
                .Where(x => x.Status == ContractStatus.Active && Math.Abs((x.AcceptedAt - ev.Time).TotalSeconds) <= 15
                            && (x.Title.Length == 0 || x.Title == x.ContractRecord))
                .OrderBy(x => Math.Abs((x.AcceptedAt - ev.Time).TotalSeconds))
                .FirstOrDefault();
        }

        var syntheticId = "t:" + Fuzzy.Normalize(title) + "@" + ev.Time.ToUnixTimeSeconds();
        if (c is null && _contracts.TryGetValue(syntheticId, out var existing))
            c = existing;

        if (c is null)
        {
            c = new Contract
            {
                Id = ev.MissionId ?? syntheticId,
                Title = title,
                AcceptedAt = ev.Time,
                Status = ContractStatus.Active,
                CompletionMode = DefaultCompletionMode
            };
            _contracts[c.Id] = c;
            ApplyTags(c, ev.Tags);
            ApplyPendingGiver(c);
            RetireReplaced(c, ev.Time);
            return true;
        }

        var changed = ApplyTags(c, ev.Tags);
        // Hra kontrakt po přihlášení znovu ohlásila → nebyl zrušen (pád, zavření okna)
        changed |= ev.MissionId is not null && c.Id == ev.MissionId && Reactivate(c, ev.Time);
        changed |= ApplyPendingGiver(c);
        if (!string.IsNullOrWhiteSpace(title) && c.Title != title && !c.IsManual("title")) { c.Title = title; changed = true; }
        if (ev.MissionId is not null && c.Id != ev.MissionId)
        {
            _contracts.Remove(c.Id);
            c.Id = ev.MissionId;
            _contracts[c.Id] = c;
            changed = true;
        }
        if (ev.Time < c.AcceptedAt) { c.AcceptedAt = ev.Time; changed = true; }
        changed |= RetireReplaced(c, ev.Time);
        return changed;
    }

    private bool OnFinished(GameEvent ev, ContractStatus status)
    {
        var c = Find(ev.MissionId, ev.Title, ev.Time);
        if (c is null) return false;
        var tagged = ApplyTags(c, ev.Tags);
        if (c.Status != ContractStatus.Active) return tagged; // první konec vyhrává (notifikace vs. MissionEnded)
        if (c.CompletionMode == CompletionMode.Manual)
        {
            // Ruční režim: stav nemění, jen si poznamená, kdy a jak kontrakt podle logu skončil.
            if (c.LogFinishedAt is not null) return tagged;
            c.LogFinishedAt = ev.Time;
            c.LogFinishStatus = status;
            if (status == ContractStatus.Completed) AssignRecentRewards(c, ev.Time);
            return true;
        }
        c.Status = status;
        c.FinishedAt = ev.Time;
        // Dokončený kontrakt = splněné všechny cíle (poslední cíl hra často jako splněný nezapíše).
        if (status == ContractStatus.Completed)
        {
            foreach (var o in c.Objectives.Where(o => o.Text.Length > 0 && !o.Completed && !o.Cancelled))
            {
                o.Completed = true;
                o.CompletedAt = ev.Time;
            }
            AssignRecentRewards(c, ev.Time);
        }
        return true;
    }

    /// <summary>Odměna zapsaná těsně PŘED dokončením (do 3 s) zůstala nepřiřazená → přiřadíme ji teď.</summary>
    private void AssignRecentRewards(Contract c, DateTimeOffset finished)
    {
        foreach (var r in _rewards.Where(r => r.ContractId is null && (r.Time - finished).TotalSeconds is >= -3 and <= 20))
        {
            r.ContractId = c.Id;
            var list = r.Kind == RewardKind.Blueprint ? c.ReceivedBlueprints : c.ItemRewards;
            if (!list.Contains(r.Name, StringComparer.OrdinalIgnoreCase)) list.Add(r.Name);
        }
    }

    /// <summary>Odhlášení / ukončení hry: všechny ještě aktivní kontrakty hra zruší.</summary>
    private bool OnSessionEnded(GameEvent ev)
    {
        var changed = false;
        foreach (var c in _contracts.Values.Where(c => c.Status == ContractStatus.Active && c.AcceptedAt <= ev.Time))
        {
            if (c.CompletionMode == CompletionMode.Manual)
            {
                if (c.LogFinishedAt is null) { c.LogFinishedAt = ev.Time; c.LogFinishStatus = ContractStatus.Abandoned; changed = true; }
                continue;
            }
            // Kontrakt z dřívější relace, který hra v této relaci znovu neohlásila, zmizel už dřív.
            var stale = IsStale(c);
            c.Status = ContractStatus.Abandoned;
            c.FinishedAt = stale ? c.LastSeen ?? c.AcceptedAt : ev.Time;
            c.EndReason = "logout";
            changed = true;
        }
        return changed;
    }

    private bool OnObjective(GameEvent ev)
    {
        var c = Find(ev.MissionId, null, ev.Time, activeFallback: true);
        if (c is null) return false;
        var reactivated = ev.MissionId is not null && Reactivate(c, ev.Time);

        // „New Objective: …“ s ObjectiveId – přesný text cíle z logu
        if (ev.ObjectiveId is { } textObjId && !string.IsNullOrWhiteSpace(ev.Title) && ev.Kind == GameEventKind.ObjectiveUpdated)
        {
            var textSet = SetLogObjectiveText(c, textObjId, ev.Title!);
            c.Objectives.First(x => x.Id == textObjId).AddedAt ??= ev.Time;
            return textSet || reactivated;
        }

        // „Objective Complete: …“ s ObjectiveId – text + splněno
        if (ev.ObjectiveId is { } doneId && !string.IsNullOrWhiteSpace(ev.Title) && ev.Kind == GameEventKind.ObjectiveCompleted)
        {
            var textChanged = SetLogObjectiveText(c, doneId, ev.Title!);
            var obj = c.Objectives.First(x => x.Id == doneId);
            obj.AddedAt ??= ev.Time;
            if (obj.Completed) return textChanged || reactivated;
            obj.Completed = true;
            obj.CompletedAt = ev.Time;
            return true;
        }

        if (ev.ObjectiveId is { } objId)
        {
            var o = c.Objectives.FirstOrDefault(x => x.Id == objId);
            if (o is null)
            {
                o = new ContractObjective { Id = objId };
                c.Objectives.Add(o);
            }
            o.AddedAt ??= ev.Time;
            var done = ev.Kind == GameEventKind.ObjectiveCompleted;
            if (o.Completed == done) return reactivated;
            o.Completed = done;
            o.CompletedAt = done ? ev.Time : null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ev.Title))
        {
            var added = AddObjectiveText(c, ev.Title!, "log");
            var o = c.Objectives.LastOrDefault(x => Fuzzy.Similarity(x.Text, ev.Title!) >= 0.8);
            if (o is not null)
            {
                o.AddedAt ??= ev.Time;
                if (ev.Kind == GameEventKind.ObjectiveCompleted && !o.Completed)
                {
                    o.Completed = true;
                    o.CompletedAt = ev.Time;
                    added = true;
                }
            }
            return added || reactivated;
        }
        return reactivated;
    }

    /// <summary>Text cíle z logu má přednost – nahradí případný stejný cíl přečtený přes OCR.</summary>
    /// <summary>
    /// Kontrakt z dřívější relace, o kterém už log nic nenapsal (hra spadla / relace skončila bez EndMission),
    /// a hráč přijal stejný kontrakt znovu → starý uzavřeme jako opuštěný.
    /// </summary>
    private bool RetireReplaced(Contract fresh, DateTimeOffset time)
    {
        var sessionStart = Player.SessionStart ?? DateTimeOffset.MinValue;
        var changed = false;
        foreach (var old in _contracts.Values.Where(x => !ReferenceEquals(x, fresh) && x.Status == ContractStatus.Active
                     && x.CompletionMode == CompletionMode.Auto
                     && (x.LastSeen ?? x.AcceptedAt) < sessionStart && time >= sessionStart
                     && (Fuzzy.Normalize(x.Title) == Fuzzy.Normalize(fresh.Title)
                         || (x.ContractRecord is not null && x.ContractRecord == fresh.ContractRecord))).ToList())
        {
            old.Status = ContractStatus.Abandoned;
            old.FinishedAt = old.LastSeen ?? old.AcceptedAt;
            changed = true;
        }
        return changed;
    }

    /// <summary>true = aktivní kontrakt, o kterém v aktuální relaci log nic nenapsal.</summary>
    /// <summary>
    /// Má smysl číst mobiGlas? true, když některý aktivní kontrakt ještě nemá přesnou odměnu, zadavatele,
    /// popis nebo cíle (to, co umí dodat jen OCR). Kompletní kontrakty se znovu nečtou.
    /// </summary>
    public bool AnyActiveNeedsOcr()
    {
        lock (_lock)
            return _contracts.Values.Any(c => c.Status == ContractStatus.Active && !IsStale(c) && NeedsOcr(c));
    }

    public static bool NeedsOcr(Contract c) =>
        c.Reward is null || c.RewardSource == "ocr~"
        || string.IsNullOrWhiteSpace(c.Contractor)
        || string.IsNullOrWhiteSpace(c.Description) || c.DescriptionSource == "p4k"
        || !c.Objectives.Any(o => o.Text.Length > 0);

    public bool IsStale(Contract c) =>
        c.Status == ContractStatus.Active && Player.SessionStart is { } s && (c.LastSeen ?? c.AcceptedAt) < s;

    /// <summary>„pickup_40f0…_1“ → „pickup“; GUID bez prefixu → null.</summary>
    private static string? KindFromId(string objectiveId)
    {
        var i = objectiveId.IndexOf('_');
        return i is > 0 and <= 16 ? objectiveId[..i] : null;
    }

    /// <summary>Štítky z názvu → reputace, [BP], ostatní (ACE…).</summary>
    private static bool ApplyTags(Contract c, IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0) return false;
        var changed = false;
        foreach (var t in tags)
            if (!c.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) { c.Tags.Add(t); changed = true; }
        if (GameLogParser.ParseRep(tags) is { } rep && (c.RepMin != rep.Min || c.RepMax != rep.Max))
        {
            c.RepMin = rep.Min;
            c.RepMax = rep.Max;
            changed = true;
        }
        if (!c.OffersBlueprint && tags.Any(t => t.Equals("BP", StringComparison.OrdinalIgnoreCase)))
        {
            c.OffersBlueprint = true;
            changed = true;
        }
        return changed;
    }

    // MissionAccept comms přichází v logu těsně PŘED vznikem kontraktu – podržíme ho, dokud kontrakt nevznikne.
    private readonly Dictionary<string, GameEvent> _pendingGivers = new(StringComparer.OrdinalIgnoreCase);

    private bool ApplyPendingGiver(Contract c)
    {
        if (!_pendingGivers.Remove(c.Id, out var ev)) return false;
        return OnGiver(ev);
    }

    private bool OnGiver(GameEvent ev)
    {
        if (ev.MissionId is null) return false;
        if (!_contracts.TryGetValue(ev.MissionId, out var c))
        {
            _pendingGivers[ev.MissionId] = ev;
            return false;
        }
        var changed = false;
        if (ev.Organization is { } org && c.Organization != org && !c.IsManual("giver")) { c.Organization = org; changed = true; }
        if (ev.MissionGiver is { } giver && c.MissionGiver != giver && !c.IsManual("person")) { c.MissionGiver = giver; changed = true; }
        if (ev.MissionType is { } type && c.MissionType != type) { c.MissionType = type; changed = true; }
        if (string.IsNullOrWhiteSpace(c.Contractor) && (ev.Organization ?? ev.MissionGiver) is { } who)
        {
            c.Contractor = who;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// „Received Blueprint“ / „You've earned“ nemají MissionId – hra je zapíše do pár sekund
    /// po „Contract Complete“, takže je přiřadíme k právě dokončenému kontraktu.
    /// </summary>
    private bool OnItemReward(GameEvent ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Title)) return false;
        var name = ev.Title!.Trim();
        var kind = ev.Kind == GameEventKind.BlueprintReceived ? RewardKind.Blueprint : RewardKind.Item;
        var c = _contracts.Values
            .Select(x => (x, end: x.Status == ContractStatus.Completed ? x.FinishedAt
                              : x.LogFinishStatus == ContractStatus.Completed ? x.LogFinishedAt : null))
            .Where(p => p.end is { } f && (ev.Time - f).TotalSeconds is >= -3 and <= 20)
            .OrderByDescending(p => p.end)
            .Select(p => p.x)
            .FirstOrDefault();

        // Samostatný záznam odměny (i nepřiřazené). Stejná odměna ve stejné vteřině = opakovaně přečtený log.
        var changed = false;
        var existing = _rewards.FirstOrDefault(r => r.Kind == kind && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                    && Math.Abs((r.Time - ev.Time).TotalSeconds) < 2);
        if (existing is null)
        {
            _rewards.Add(new RewardEntry { Kind = kind, Name = name, Time = ev.Time, ContractId = c?.Id });
            changed = true;
        }
        else if (existing.ContractId is null && c is not null)
        {
            existing.ContractId = c.Id;
            changed = true;
        }

        if (c is null) return changed;
        var list = kind == RewardKind.Blueprint ? c.ReceivedBlueprints : c.ItemRewards;
        if (list.Contains(name, StringComparer.OrdinalIgnoreCase)) return changed;
        list.Add(name);
        return true;
    }

    private bool OnObjectiveCancelled(GameEvent ev)
    {
        if (ev.MissionId is null || ev.ObjectiveId is null || !_contracts.TryGetValue(ev.MissionId, out var c)) return false;
        var o = c.Objectives.FirstOrDefault(x => x.Id == ev.ObjectiveId);
        if (o is null)
        {
            o = new ContractObjective { Id = ev.ObjectiveId, Text = ev.Title ?? "", Source = "log" };
            c.Objectives.Add(o);
        }
        if (o.Cancelled) return false;
        o.Cancelled = true;
        return true;
    }

    /// <summary>
    /// Kontrakt označený jako opuštěný (ručně, odhadem po pádu hry…), o kterém log později znovu píše
    /// (nový cíl, značka), je ve skutečnosti aktivní. Řádky do minuty po opuštění (úklid značek) se ignorují.
    /// </summary>
    private static bool Reactivate(Contract c, DateTimeOffset time)
    {
        if (c.Status != ContractStatus.Abandoned || c.FinishedAt is not { } f || time <= f.AddMinutes(1)) return false;
        c.Status = ContractStatus.Active;
        c.FinishedAt = null;
        c.EndReason = null;
        return true;
    }

    private bool SetLogObjectiveText(Contract c, string objectiveId, string text)
    {
        var o = c.Objectives.FirstOrDefault(x => x.Id == objectiveId);
        var ocrTwin = c.Objectives.FirstOrDefault(x => x.Source == "ocr" && Fuzzy.Similarity(x.Text, text) >= 0.8);
        if (o is null)
        {
            o = ocrTwin ?? new ContractObjective();
            if (ocrTwin is null) c.Objectives.Add(o);
            o.Id = objectiveId;
        }
        else if (ocrTwin is not null && !ReferenceEquals(ocrTwin, o))
        {
            c.Objectives.Remove(ocrTwin);
        }
        o.Kind ??= KindFromId(objectiveId);
        if (o.Text == text && o.Source == "log") return false;
        o.Text = text;
        o.Source = "log";
        ApplyLocations(c, o);
        return true;
    }

    private void ApplyLocations(Contract c, ContractObjective obj)
    {
        if (_findLocations is null) return;
        var locs = _findLocations(obj.Text).ToList();
        obj.Location = locs.FirstOrDefault() ?? obj.Location;
        foreach (var l in locs)
            if (!c.LocationHints.Contains(l, StringComparer.OrdinalIgnoreCase))
                c.LocationHints.Add(l);
    }

    private bool AddObjectiveText(Contract c, string text, string source = "ocr")
    {
        var twin = c.Objectives.FirstOrDefault(o => Fuzzy.Similarity(o.Text, text) >= 0.8);
        if (twin is not null)
        {
            // Text z logu je přesnější než OCR (OCR plete např. 1/I, O/0).
            if (source == "log" && twin.Source != "log")
            {
                twin.Text = text;
                twin.Source = "log";
                ApplyLocations(c, twin);
                return true;
            }
            return false;
        }
        var obj = new ContractObjective { Id = "txt:" + c.Objectives.Count, Text = text, Source = source };
        ApplyLocations(c, obj);
        c.Objectives.Add(obj);
        return true;
    }

    private bool OnReward(GameEvent ev)
    {
        if (ev.Amount is not { } amount) return false;
        Contract? c = null;
        if (ev.MissionId is not null) _contracts.TryGetValue(ev.MissionId, out c);
        // Bez ID: poslední dokončený kontrakt do 2 minut, který ještě nemá výplatu z logu.
        c ??= _contracts.Values
            .Where(x => x.Status == ContractStatus.Completed && x.FinishedAt is { } f
                        && (ev.Time - f).TotalSeconds is >= -5 and <= 120 && x.RewardSource != "log")
            .OrderByDescending(x => x.FinishedAt)
            .FirstOrDefault();
        if (c is null) return false;
        c.Reward = amount;
        c.RewardSource = "log";
        return true;
    }

    private Contract? Find(string? missionId, string? title, DateTimeOffset time, bool activeFallback = false)
    {
        if (missionId is not null && _contracts.TryGetValue(missionId, out var byId)) return byId;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var best = _contracts.Values
                .Where(c => c.Status == ContractStatus.Active && c.AcceptedAt <= time.AddSeconds(5))
                .Select(c => (c, score: Fuzzy.Similarity(c.Title, title)))
                .Where(x => x.score >= TitleMatchThreshold)
                .OrderByDescending(x => x.score).ThenByDescending(x => x.c.AcceptedAt)
                .FirstOrDefault();
            if (best.c is not null) return best.c;
        }

        if (activeFallback)
            return _contracts.Values.Where(c => c.Status == ContractStatus.Active && c.AcceptedAt <= time.AddSeconds(5))
                .OrderByDescending(c => c.AcceptedAt).FirstOrDefault();
        return null;
    }

    /// <summary>Doplní údaje z OCR mobiGlasu k aktivním kontraktům.</summary>
    public int ApplyMobiGlas(MobiGlasSnapshot snap)
    {
        if (!snap.IsAcceptedTab) return 0;
        var touched = new HashSet<Contract>();
        lock (_lock)
        {
            var active = _contracts.Values.Where(c => c.Status == ContractStatus.Active).ToList();

            foreach (var entry in snap.Entries)
            {
                var c = BestMatch(active, entry.Title);
                if (c is null) continue;
                // Zkrácená hodnota ze seznamu („19k“) jen když zatím žádnou odměnu nemáme.
                if (entry.Approximate && c.Reward is not null) continue;
                if (entry.Reward is { } r && c.RewardSource is null or "ocr" or "ocr~" && c.Reward != r)
                {
                    c.Reward = r;
                    c.RewardSource = entry.Approximate ? "ocr~" : "ocr";
                    touched.Add(c);
                }
            }

            if (snap.Detail is { } d)
            {
                var c = BestMatch(active, d.Title);
                if (c is not null)
                {
                    var changed = false;
                    if (d.Contractor is { Length: > 0 } && c.Contractor != d.Contractor && !c.IsManual("giver")) { c.Contractor = d.Contractor; changed = true; }
                    if (d.Fee is { } fee && c.FeeSource != "manual" && c.Fee != fee) { c.Fee = fee; c.FeeSource = "ocr"; changed = true; }
                    // Skutečný text z mobiGlasu má přednost před šablonou z Data.p4k (ta má místo hodnot ~mission(...) proměnné).
                    if (d.Description is { Length: > 0 } && (c.Description is null || c.DescriptionSource == "p4k") && !c.IsManual("description"))
                    {
                        c.Description = d.Description;
                        c.DescriptionSource = "ocr";
                        changed = true;
                    }
                    if (d.Reward is { } r && c.RewardSource is null or "ocr" or "ocr~" && c.Reward != r) { c.Reward = r; c.RewardSource = "ocr"; changed = true; }
                    foreach (var o in d.Objectives) changed |= AddObjectiveText(c, o);
                    if (d.PotentialBlueprints is { Count: > 0 } bps && bps.Count >= c.PotentialBlueprints.Count
                        && !bps.SequenceEqual(c.PotentialBlueprints))
                    {
                        c.PotentialBlueprints = bps.ToList();
                        changed = true;
                    }
                    if (d.MissionInfo is { Count: > 0 } info)
                        foreach (var (k, v) in info)
                            if (!c.MissionInfo.TryGetValue(k, out var old) || old != v) { c.MissionInfo[k] = v; changed = true; }
                    if (d.Description is { Length: > 0 } && _findLocations is not null)
                        foreach (var l in _findLocations(d.Description))
                            if (!c.LocationHints.Contains(l, StringComparer.OrdinalIgnoreCase)) { c.LocationHints.Add(l); changed = true; }
                    if (changed) touched.Add(c);
                }
            }
        }
        if (touched.Count > 0) Persist();
        return touched.Count;
    }

    private static Contract? BestMatch(IEnumerable<Contract> candidates, string title) =>
        candidates.Select(c => (c, s: Fuzzy.Similarity(c.Title, title)))
            .Where(x => x.s >= TitleMatchThreshold)
            .OrderByDescending(x => x.s)
            .Select(x => x.c)
            .FirstOrDefault();

    /// <summary>Ruční úprava (odměna, stav) z UI.</summary>
    public void Update(string id, Action<Contract> edit)
    {
        lock (_lock)
        {
            if (!_contracts.TryGetValue(id, out var c)) return;
            edit(c);
        }
        Persist();
    }

    public void Remove(string id)
    {
        lock (_lock) _contracts.Remove(id);
        Persist();
    }

    public void RemoveMany(IEnumerable<string> ids)
    {
        lock (_lock)
            foreach (var id in ids) _contracts.Remove(id);
        Persist();
    }

    /// <summary>Hromadná úprava více kontraktů s jedním uložením.</summary>
    public void UpdateMany(IEnumerable<string> ids, Action<Contract> edit)
    {
        lock (_lock)
            foreach (var id in ids)
                if (_contracts.TryGetValue(id, out var c)) edit(c);
        Persist();
    }

    /// <summary>Doplní popis/detaily z herních dat (Data.p4k).</summary>
    public void Enrich(string id, string? title, string? description, IEnumerable<string>? locations)
    {
        Update(id, c =>
        {
            if (!string.IsNullOrWhiteSpace(title) && (c.Title.Length == 0 || c.Title == c.ContractRecord) && !c.IsManual("title")) c.Title = title!;
            if (!string.IsNullOrWhiteSpace(description) && c.Description is null && !c.IsManual("description"))
            {
                c.Description = description;
                c.DescriptionSource = "p4k";
            }
            if (locations is not null)
                foreach (var l in locations)
                    if (!c.LocationHints.Contains(l, StringComparer.OrdinalIgnoreCase)) c.LocationHints.Add(l);
        });
    }

    private void Persist()
    {
        if (_store is not null)
        {
            List<Contract> copy;
            List<RewardEntry> rewards;
            lock (_lock)
            {
                copy = _contracts.Values.ToList();
                rewards = _rewards.ToList();
            }
            _store.Save(copy, rewards);
        }
        Changed?.Invoke();
    }
}

public sealed class PlayerState
{
    public string? LastLocation { get; set; }
    public DateTimeOffset? LastLocationTime { get; set; }
    public string? LastQuantumDestination { get; set; }
    /// <summary>Souřadnice z /showlocation (schránka), v metrech.</summary>
    public Navigation.Vector3d? LastPosition { get; set; }
    public DateTimeOffset? LastPositionTime { get; set; }
    /// <summary>Začátek aktuální herní relace (první řádek Game.logu).</summary>
    public DateTimeOffset? SessionStart { get; set; }
}
