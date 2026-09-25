using System.Collections.ObjectModel;
using System.Windows.Input;
using ScContractTracker.App.Services;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Navigation;
using ScContractTracker.Core.Tracking;

namespace ScContractTracker.App.ViewModels;

public sealed class NavTargetViewModel
{
    public NavTargetViewModel(NavigationTarget t)
    {
        Name = t.Location.Name;
        SystemName = t.Location.System.Name;
        Task = t.Task;
        Distance = t.DistanceMeters is not null ? t.DistanceText
            : t.Hops switch
            {
                0 => Loc.Instance["Nav.Here"],
                1 => Loc.Instance["Nav.Near"],
                >= 50 => Loc.Instance["Nav.OtherSystem"],
                _ => Loc.Instance.Format("Nav.Hops", t.Hops)
            };
    }

    public string Name { get; }
    public string SystemName { get; }
    public string Task { get; }
    public string Distance { get; }
    public bool HasTask => Task.Length > 0;
}

/// <summary>Jeden cíl kontraktu pro zobrazení (stav, místo, časy).</summary>
public sealed class ObjectiveItem
{
    public ObjectiveItem(ContractObjective o, bool isCurrent, string? location)
    {
        Text = o.Text;
        Completed = o.Completed;
        Cancelled = o.Cancelled;
        IsCurrent = isCurrent;
        Location = location;
        var L = Loc.Instance;
        if (o.Completed && o.CompletedAt is { } done)
        {
            var took = o.AddedAt is { } a && done > a ? done - a : (TimeSpan?)null;
            TimeText = took is null
                ? L.Format("Obj.DoneAt", Fmt.ShortTime(done))
                : L.Format("Obj.DoneAtTook", Fmt.ShortTime(done), Fmt.ShortDuration(took));
        }
        else if (!o.Completed && !o.Cancelled && o.AddedAt is { } added)
        {
            TimeText = L.Format("Obj.Since", Fmt.ShortTime(added));
        }
        else TimeText = "";
    }

    public string Text { get; }
    public bool Completed { get; }
    public bool Cancelled { get; }
    public bool IsCurrent { get; }
    public string? Location { get; }
    public bool HasLocation => !string.IsNullOrWhiteSpace(Location);
    public string TimeText { get; }
    public bool HasTime => TimeText.Length > 0;
}

public sealed class ContractViewModel : ObservableObject
{
    private readonly ContractTracker _tracker;
    private Contract _model;
    private NavigationService? _nav;

    public ContractViewModel(Contract model, ContractTracker tracker)
    {
        _model = model;
        _tracker = tracker;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string Id => _model.Id;
    public Contract Model => _model;
    public ICommand ToggleCommand { get; }

    private bool _isExpanded;
    /// <summary>Rozbalená karta / řádek (drží se po dobu běhu aplikace, i když se kontrakt přesune do historie).</summary>
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public string Title => string.IsNullOrWhiteSpace(_model.Title) ? (_model.ContractRecord ?? _model.Id) : _model.Title;
    public string? Contractor => _model.Contractor;
    public string? Description => _model.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(_model.Description);
    public ContractStatus Status => _model.Status;
    public bool IsActive => _model.Status == ContractStatus.Active;
    public bool IsFinished => !IsActive;
    public string StatusText => _model.Status == ContractStatus.Abandoned && _model.EndReason == "logout"
        ? Loc.Instance["Status.AbandonedLogout"] : Loc.Instance["Status." + _model.Status];
    public string Accepted => Fmt.Time(_model.AcceptedAt);
    public string AcceptedShort => Fmt.ShortTime(_model.AcceptedAt);
    public DateTimeOffset AcceptedAtValue => _model.AcceptedAt;
    public DateTimeOffset FinishedAtValue => _model.FinishedAt ?? DateTimeOffset.MinValue;
    public string Finished => Fmt.Time(_model.FinishedAt);
    public string Duration => Fmt.Duration(_model.Duration);
    public double SortDuration => _model.Duration?.TotalSeconds ?? -1;
    public string Elapsed => Loc.Instance.Format("Active.Elapsed", Fmt.Duration(_model.ElapsedAt(DateTimeOffset.UtcNow)));
    public string PerHour => Fmt.Money(_model.RewardPerHour);
    public double SortPerHour => _model.RewardPerHour ?? -1;
    public string RewardText => Fmt.Money(_model.Reward);
    /// <summary>Odměna jako v mobiGlasu: „¤ 19,750“ (přibližná hodnota ze seznamu s „~“).</summary>
    public string RewardDisplay => _model.Reward is null ? "–"
        : (_model.RewardSource == "ocr~" ? "~ " : "¤ ") + Fmt.Money(_model.Reward);
    // ---- Poplatek a čistý výdělek ----
    public string FeeDisplay => _model.Fee is { } f && f != 0 ? "¤ " + Fmt.Money(f) : "–";
    public bool HasFee => _model.Fee is { } f && f != 0;
    public long? Fee
    {
        get => _model.Fee;
        set
        {
            if (value == _model.Fee) return;
            _tracker.Update(Id, c => { c.Fee = value; c.FeeSource = value is null ? null : "manual"; });
        }
    }
    public string NetDisplay => _model.NetReward is { } n ? "¤ " + Fmt.Money(n) : "–";
    public long SortNet => _model.NetReward ?? long.MinValue;

    // ---- Režim dokončení ----
    public bool IsManualMode => _model.CompletionMode == CompletionMode.Manual;
    public string ModeText => IsManualMode ? "MANU" : "AUTO";
    /// <summary>Ruční režim: co o konci kontraktu napsal log („log: Dokončen 21:31“) – jen informace, do aUEC/h se nepočítá.</summary>
    public string LogEndHint => IsManualMode && _model.LogFinishedAt is { } t && _model.LogFinishStatus is { } s
        ? Loc.Instance.Format("Mode.LogEnd", Loc.Instance["Status." + s], Fmt.ShortTime(t)) : "";
    public string LogFinishedText => _model.LogFinishedAt is { } lt && _model.LogFinishStatus is { } ls
        ? $"{Loc.Instance["Status." + ls]} · {Fmt.Time(lt)}" : "";
    public bool HasLogFinished => LogFinishedText.Length > 0;
    public bool HasLogEndHint => LogEndHint.Length > 0;

    public string ContractorDisplay => string.IsNullOrWhiteSpace(_model.Contractor) ? "–" : _model.Contractor!;
    public string ElapsedShort => Fmt.Duration(_model.ElapsedAt(DateTimeOffset.UtcNow));
    /// <summary>Doba běhu (aktivní) nebo celková délka (ukončený).</summary>
    public string TimeLabel => IsActive ? Loc.Instance["Col.Elapsed"] : Loc.Instance["Col.Duration"];

    // ---- Typ kontraktu a zadavatel ----
    public ContractCategory Category => ContractClassifier.Category(_model);
    /// <summary>Lokalizovaný název kategorie – klíč pro seskupování.</summary>
    public string CategoryName => Loc.Instance["Type." + Category];
    public string? SubType => ContractClassifier.SubType(_model);
    public string TypeDisplay => SubType is { } sub && !sub.Equals(CategoryName, StringComparison.OrdinalIgnoreCase)
        ? $"{CategoryName} · {sub}" : CategoryName;
    /// <summary>Zadavatel pro zobrazení i seskupování („Neznámý“, když ho nic neuvádí).</summary>
    public string GiverName => ContractClassifier.Giver(_model) ?? Loc.Instance["Common.Unknown"];
    /// <summary>Konkrétní osoba, která kontrakt zadala (z logu, např. „Recco Battaglia“), pokud se liší od zadavatele.</summary>
    public string PersonDisplay => !string.IsNullOrWhiteSpace(_model.MissionGiver)
                                   && !_model.MissionGiver.Equals(GiverName, StringComparison.OrdinalIgnoreCase)
        ? _model.MissionGiver! : "";
    public bool HasPerson => PersonDisplay.Length > 0;
    public bool HasKnownGiver => ContractClassifier.Giver(_model) is not null;

    // ---- Aktuální úkol ----
    private ObjectiveItem? _current;
    public ObjectiveItem? Current => _current;
    public bool HasCurrent => _current is not null;
    public string CurrentText => _current?.Text
        ?? (Objectives.Count > 0 && Objectives.All(o => o.Completed || o.Cancelled)
            ? Loc.Instance["Active.AllDone"] : Loc.Instance["Active.NoObjective"]);
    public string? CurrentLocation => _current?.Location ?? Nearest.FirstOrDefault()?.Name;
    public bool HasCurrentLocation => !string.IsNullOrWhiteSpace(CurrentLocation);
    public string CurrentDistance => Nearest.FirstOrDefault() is { } n && n.Name == CurrentLocation ? n.Distance : "";
    /// <summary>„2/5“ – splněné / všechny viditelné cíle.</summary>
    public string Progress
    {
        get
        {
            var all = Objectives.Count(o => !o.Cancelled);
            return all == 0 ? "" : $"{Objectives.Count(o => o.Completed)}/{all}";
        }
    }
    public bool HasProgress => Progress.Length > 0;
    /// <summary>Další souběžné nesplněné cíle („+1“).</summary>
    public string MoreOpen
    {
        get
        {
            var open = Objectives.Count(o => !o.Completed && !o.Cancelled);
            return open > 1 ? $"+{open - 1}" : "";
        }
    }

    // ---- Další odměny a podrobnosti ----
    public string RepDisplay => _model.RepMin is not { } min ? "–"
        : _model.RepMax is { } max && max != min ? $"{min:N0}–{max:N0} Rep" : $"{min:N0} Rep";
    public bool HasRep => _model.RepMin is not null;
    public string BlueprintDisplay => _model.ReceivedBlueprints.Count > 0
        ? string.Join(", ", _model.ReceivedBlueprints)
        : _model.OffersBlueprint ? Loc.Instance["Rewards.BpPossible"] : "–";
    public bool HasReceivedBlueprints => _model.ReceivedBlueprints.Count > 0;
    /// <summary>Skutečně získané blueprinty (sloupec „Získané blueprinty“).</summary>
    public string ReceivedBlueprintsText => _model.ReceivedBlueprints.Count > 0 ? string.Join(", ", _model.ReceivedBlueprints) : "";
    /// <summary>Nabídka blueprintu: seznam možných z popisu, jinak „[BP]“ podle štítku.</summary>
    public string BlueprintOfferDisplay => _model.PotentialBlueprints.Count > 0 ? string.Join(", ", _model.PotentialBlueprints)
        : _model.OffersBlueprint ? "[BP]" : "";
    public string PotentialBlueprints => string.Join(", ", _model.PotentialBlueprints);
    public bool HasPotentialBlueprints => _model.PotentialBlueprints.Count > 0;
    public string ItemRewards => string.Join(", ", _model.ItemRewards);
    public bool HasItemRewards => _model.ItemRewards.Count > 0;
    public string GiverDisplay => string.Join(" · ", new[] { _model.Organization, _model.MissionGiver }
        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    public bool HasGiver => GiverDisplay.Length > 0;
    public string MissionTypeDisplay => _model.MissionInfo.TryGetValue("Mission Type", out var t) ? t : _model.MissionType ?? "";
    public bool HasMissionType => MissionTypeDisplay.Length > 0;
    public IReadOnlyList<KeyValuePair<string, string>> Difficulty => new[] { "Combat", "Complexity", "Risk", "Knowledge" }
        .Where(k => _model.MissionInfo.ContainsKey(k))
        .Select(k => new KeyValuePair<string, string>(k, _model.MissionInfo[k])).ToList();
    public bool HasDifficulty => Difficulty.Count > 0;
    public string OtherTags => string.Join(" ", _model.Tags
        .Where(t => !t.Equals("BP", StringComparison.OrdinalIgnoreCase) && !t.EndsWith("Rep", StringComparison.OrdinalIgnoreCase))
        .Select(t => $"[{t}]"));
    public bool HasOtherTags => OtherTags.Length > 0;
    public bool HasExtras => HasRep || _model.OffersBlueprint || HasReceivedBlueprints || HasPotentialBlueprints || HasItemRewards
                             || HasGiver || HasMissionType || HasDifficulty || HasOtherTags;
    public bool IsStale => _tracker.IsStale(_model);

    /// <summary>Editovatelná odměna (DataGrid). Ruční hodnota má přednost před OCR.</summary>
    public long? Reward
    {
        get => _model.Reward;
        set
        {
            if (value == _model.Reward) return;
            _tracker.Update(Id, c => { c.Reward = value; c.RewardSource = value is null ? null : "manual"; });
        }
    }

    private IReadOnlyList<ObjectiveItem> _objectives = Array.Empty<ObjectiveItem>();
    public IReadOnlyList<ObjectiveItem> Objectives => _objectives;
    public bool HasObjectives => _objectives.Count > 0;
    public bool HasNoObjectives => _objectives.Count == 0;

    public ObservableCollection<NavTargetViewModel> Nearest { get; } = new();
    public bool HasNearest => Nearest.Count > 0;
    public bool HasNoNearest => Nearest.Count == 0;

    public void Update(Contract model, NavigationService? nav, PlayerState player)
    {
        _model = model;
        _nav = nav;

        var current = model.GetCurrentObjective();
        _objectives = model.Objectives.Where(o => o.Text.Length > 0)
            .Select(o => new ObjectiveItem(o, ReferenceEquals(o, current), LocationName(o.Location)))
            .ToList();
        _current = _objectives.FirstOrDefault(o => o.IsCurrent);

        Nearest.Clear();
        if (nav is not null && model.Status == ContractStatus.Active)
            foreach (var t in nav.Nearest(model, player.LastLocation, player.LastPosition))
                Nearest.Add(new NavTargetViewModel(t));

        OnPropertyChanged(string.Empty); // všechny vlastnosti
        // Živé seskupování v historii reaguje jen na konkrétní názvy vlastností.
        OnPropertyChanged(nameof(CategoryName));
        OnPropertyChanged(nameof(GiverName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AcceptedAtValue));
    }

    private string? LocationName(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : _nav?.Database.Resolve(raw)?.Name ?? raw;

    public void Tick()
    {
        OnPropertyChanged(nameof(Elapsed));
        OnPropertyChanged(nameof(ElapsedShort));
    }
}
