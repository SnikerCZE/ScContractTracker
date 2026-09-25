using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ScContractTracker.App.Services;
using ScContractTracker.App.Views;
using ScContractTracker.Core.GameData;
using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Logs;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Navigation;
using ScContractTracker.Core.Ocr;
using ScContractTracker.Core.Settings;
using ScContractTracker.Core.Stats;
using ScContractTracker.Core.Storage;
using ScContractTracker.Core.Tracking;

namespace ScContractTracker.App.ViewModels;

public sealed class LanguageOption : ObservableObject
{
    private bool _isSelected;
    public LanguageOption(string code, string name) { Code = code; Name = name; }
    public string Code { get; }
    public string Name { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed record StatTile(string Label, string Value, string? Sub = null)
{
    public bool HasSub => !string.IsNullOrWhiteSpace(Sub);
}

/// <summary>Jeden získaný blueprint / předmět (seskupeno podle názvu) pro záložky Blueprinty a Předměty.</summary>
public sealed record RewardRow(string Name, int Count, string FirstReceived, DateTimeOffset FirstValue,
    string LastReceived, DateTimeOffset LastValue, string Contracts, string Types, string Givers);

/// <summary>Řádek statistiky jedné skupiny (typ / zadavatel / název); rozbalením se ukážou její kontrakty.</summary>
public sealed class GroupStatsRow : ObservableObject
{
    public GroupStatsRow(GroupStats s, IReadOnlyList<ContractViewModel> contracts, bool expanded)
    {
        Stats = s;
        Contracts = contracts;
        _isExpanded = expanded;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public GroupStats Stats { get; }
    public IReadOnlyList<ContractViewModel> Contracts { get; }
    public ICommand ToggleCommand { get; }

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public string Name => Stats.Key;
    public int Accepted => Stats.Accepted;
    public int Active => Stats.Active;
    public int Completed => Stats.Completed;
    public int Failed => Stats.Failed;
    public string SuccessRate => Fmt.Percent(Stats.SuccessRate);
    public double SortSuccessRate => Stats.SuccessRate ?? -1;
    public string TotalReward => Fmt.Money(Stats.TotalReward);
    public long SortTotalReward => Stats.TotalReward;
    public string AverageReward => Fmt.Money(Stats.AverageReward);
    public long SortAverageReward => Stats.AverageReward ?? -1;
    public string MedianReward => Fmt.Money(Stats.MedianReward);
    public long SortMedianReward => Stats.MedianReward ?? -1;
    public string RewardRange => Stats.MinReward is null ? "–" : $"{Fmt.Money(Stats.MinReward)} – {Fmt.Money(Stats.MaxReward)}";
    public string AverageDuration => Fmt.ShortDuration(Stats.AverageDuration);
    public double SortAverageDuration => Stats.AverageDuration?.TotalSeconds ?? -1;
    public string MedianDuration => Fmt.ShortDuration(Stats.MedianDuration);
    public string Fastest => Fmt.ShortDuration(Stats.Fastest);
    public string Slowest => Fmt.ShortDuration(Stats.Slowest);
    public string TotalDuration => Fmt.ShortDuration(Stats.TotalDuration);
    public string PerHour => Fmt.Money(Stats.RewardPerHour);
    public double SortPerHour => Stats.RewardPerHour ?? -1;
    public string BestPerHour => Fmt.Money(Stats.BestPerHour);
    public double SortBestPerHour => Stats.BestPerHour ?? -1;
    public string Rep => Stats.TotalRep > 0 ? Fmt.Money(Stats.TotalRep) : "–";
    public int Blueprints => Stats.Blueprints;
    public string Fees => Stats.TotalFees > 0 ? Fmt.Money(Stats.TotalFees) : "–";
    public long SortFees => Stats.TotalFees;
    public string BlueprintNames => string.Join(", ", Stats.Contracts.SelectMany(c => c.ReceivedBlueprints)
        .Distinct(StringComparer.OrdinalIgnoreCase));
    public string LastAccepted => Fmt.ShortTime(Stats.LastAccepted);
}

/// <summary>Hlavní view model – skládá dohromady všechny služby aplikace.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly ContractTracker _tracker;
    private readonly GameLogParser _parser;
    private readonly MobiGlasParser _mobiParser;
    private readonly WindowsOcrService _ocr = new();
    private readonly MobiGlasMonitor _monitor;
    private readonly ClipboardWatcher _clipboard = new();
    private readonly CaptureService _capture = new();
    private readonly GameScreenshotWatcher _screenshots = new();
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _enrichAttempted = new();
    /// <summary>Jeden view model na kontrakt – sdílený aktivní kartou, historií i statistikami (drží stav rozbalení).</summary>
    private readonly Dictionary<string, ContractViewModel> _vms = new();
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings;
    private IReadOnlyDictionary<string, GameLanguagePack> _gamePacks = new Dictionary<string, GameLanguagePack>();
    private GameLogWatcher? _watcher;
    private NavigationService? _nav;
    private P4kContractDetailsProvider? _details;
    private volatile bool _dirty = true;

    public MainViewModel()
    {
        AppPaths.EnsureCreated();
        _settings = AppSettings.Load(AppPaths.SettingsFile);

        Loc.Instance.Load(_settings.AppLanguage);
        LoadGamePacks();
        var pack = CurrentGamePack();

        try { _nav = new NavigationService(LocationDatabase.Load(AppPaths.LocationsFile)); }
        catch (Exception ex) { AppPaths.LogError(ex); }

        _parser = new GameLogParser(pack);
        _mobiParser = new MobiGlasParser(pack) { ListColumnSplit = _settings.ListColumnSplit };
        _tracker = new ContractTracker(new JsonContractStore(AppPaths.ContractsFile),
            text => _nav?.Database.FindMentions(text) ?? Enumerable.Empty<string>());
        _tracker.Changed += () => _dirty = true;
        _tracker.DefaultCompletionMode = _settings.DefaultCompletionMode.Equals("Manual", StringComparison.OrdinalIgnoreCase)
            ? CompletionMode.Manual : CompletionMode.Auto;

        _ocr.SetLanguage(pack.OcrLanguageTag);
        _capture.Method = ParseCaptureMethod(_settings.CaptureMethod);
        _monitor = new MobiGlasMonitor(_ocr, _mobiParser, _tracker, _capture);
        _screenshots.ScreenshotCreated += path => _ = _monitor.ProcessScreenshotAsync(path);
        _monitor.StateChanged += _ => _ui.InvokeAsync(UpdateMobiStatus);

        _clipboard.TextCopied += OnClipboardText;

        Loc.Instance.LanguageChanged += () => { _dirty = true; OnPropertyChanged(nameof(StatsGroupHeader)); RebuildLanguageMenus(); UpdateAllStatus(); };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();

        OpenSettingsCommand = new RelayCommand(OpenSettings);
        InstallOcrCommand = new RelayCommand(async () => await EnsureOcrLanguageAsync(userRequested: true));
        ImportHistoryCommand = new RelayCommand(ImportHistory);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.DataDir));
        ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
        CheckUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync(interactive: true));
        AboutCommand = new RelayCommand(() => new AboutWindow { Owner = Application.Current.MainWindow }.ShowDialog());
        SetAppLanguageCommand = new RelayCommand(p => { if (p is string code) SetAppLanguage(code); });
        SetGameLanguageCommand = new RelayCommand(p => { if (p is string code) SetGameLanguage(code); });
        MarkCompletedCommand = new RelayCommand(p => MarkFinished(p, ContractStatus.Completed));
        MarkFailedCommand = new RelayCommand(p => MarkFinished(p, ContractStatus.Failed));
        DeleteCommand = new RelayCommand(DeleteContract);
        DataGridColumnChooser.Store = key =>
        {
            if (!_settings.HiddenColumns.TryGetValue(key, out var list))
                _settings.HiddenColumns[key] = list = new List<string>();
            return list;
        };
        DataGridColumnChooser.WidthStore = key =>
        {
            if (!_settings.ColumnWidths.TryGetValue(key, out var d))
                _settings.ColumnWidths[key] = d = new Dictionary<string, double>();
            return d;
        };
        DataGridColumnChooser.OrderStore = key =>
        {
            if (!_settings.ColumnOrder.TryGetValue(key, out var l))
                _settings.ColumnOrder[key] = l = new List<string>();
            return l;
        };
        DataGridColumnChooser.Changed = () => _settings.Save(AppPaths.SettingsFile);
        ReadMobiGlasNowCommand = new RelayCommand(() => _monitor.Trigger(force: true));
        SaveOcrDiagnosticsCommand = new RelayCommand(SaveOcrDiagnostics);
        NewContractCommand = new RelayCommand(NewContract);
        EditContractCommand = new RelayCommand(EditContract);
        ToggleModeCommand = new RelayCommand(ToggleMode);
        CompleteAllCommand = new RelayCommand(() => FinishAll(ContractStatus.Completed));
        AbandonAllCommand = new RelayCommand(() => FinishAll(ContractStatus.Abandoned));
        DeleteAllHistoryCommand = new RelayCommand(DeleteAllHistory);
        ExpandAllCommand = new RelayCommand(p => SetExpanded(p, true));
        CollapseAllCommand = new RelayCommand(p => SetExpanded(p, false));

        HistoryView = (ListCollectionView)CollectionViewSource.GetDefaultView(HistoryContracts);
        HistoryView.SortDescriptions.Add(new SortDescription(nameof(ContractViewModel.AcceptedAtValue), ListSortDirection.Descending));
        HistoryView.IsLiveSorting = true;
        HistoryView.IsLiveGrouping = true;
        HistoryView.LiveGroupingProperties.Add(nameof(ContractViewModel.CategoryName));
        HistoryView.LiveGroupingProperties.Add(nameof(ContractViewModel.GiverName));
        HistoryView.LiveGroupingProperties.Add(nameof(ContractViewModel.Title));
        HistoryView.LiveSortingProperties.Add(nameof(ContractViewModel.AcceptedAtValue));
        HistoryView.Filter = o => o is ContractViewModel vm && (PeriodStart() is not { } since || vm.AcceptedAtValue >= since);
    }

    // ---------- Kolekce a vlastnosti pro UI ----------

    public ObservableCollection<ContractViewModel> ActiveContracts { get; } = new();
    public ObservableCollection<ContractViewModel> HistoryContracts { get; } = new();
    /// <summary>Historie se seskupením a filtrem období (DataGrid se váže sem).</summary>
    public ListCollectionView HistoryView { get; }
    public ObservableCollection<GroupStatsRow> GroupStats { get; } = new();
    public ObservableCollection<RewardRow> BlueprintRows { get; } = new();
    public ObservableCollection<RewardRow> ItemRows { get; } = new();

    private string _rewardFilter = "";
    /// <summary>Hledání v záložkách Blueprinty / Předměty (název, kontrakt, zadavatel).</summary>
    public string RewardFilter
    {
        get => _rewardFilter;
        set { if (Set(ref _rewardFilter, value ?? "")) RefreshRewards(_tracker.Snapshot()); }
    }

    private string _blueprintSummary = "";
    public string BlueprintSummary { get => _blueprintSummary; private set => Set(ref _blueprintSummary, value); }
    private string _itemSummary = "";
    public string ItemSummary { get => _itemSummary; private set => Set(ref _itemSummary, value); }
    public ObservableCollection<StatTile> OverallTiles { get; } = new();
    public ObservableCollection<LanguageOption> AppLanguages { get; } = new();
    public ObservableCollection<LanguageOption> GameLanguages { get; } = new();

    public bool HasNoActive => ActiveContracts.Count == 0;

    /// <summary>Výchozí režim dokončení nově založených kontraktů (přepínač AUTO / MANU v Aktivních kontraktech).</summary>
    public bool DefaultManualMode
    {
        get => _tracker.DefaultCompletionMode == CompletionMode.Manual;
        set
        {
            _tracker.DefaultCompletionMode = value ? CompletionMode.Manual : CompletionMode.Auto;
            _settings.DefaultCompletionMode = value ? "Manual" : "Auto";
            _settings.Save(AppPaths.SettingsFile);
            OnPropertyChanged();
        }
    }

    private int _periodIndex = 3;
    public int PeriodIndex
    {
        get => _periodIndex;
        set
        {
            if (!Set(ref _periodIndex, value)) return;
            RefreshStats();
            RefreshHistoryView();
        }
    }

    private int _historyGroupingIndex;
    /// <summary>0 = bez seskupení, 1 = typ, 2 = zadavatel, 3 = název.</summary>
    public int HistoryGroupingIndex
    {
        get => _historyGroupingIndex;
        set
        {
            if (!Set(ref _historyGroupingIndex, value)) return;
            HistoryView.GroupDescriptions.Clear();
            var prop = value switch
            {
                1 => nameof(ContractViewModel.CategoryName),
                2 => nameof(ContractViewModel.GiverName),
                3 => nameof(ContractViewModel.Title),
                _ => null
            };
            if (prop is not null) HistoryView.GroupDescriptions.Add(new PropertyGroupDescription(prop));
        }
    }

    /// <summary>Hlavička prvního sloupce statistik podle seskupení: Typ kontraktu / Zadavatel / Název.</summary>
    public string StatsGroupHeader => Loc.Instance[StatsGroupingIndex switch
    {
        1 => "Group.Giver",
        2 => "Col.Name",
        _ => "Group.Type"
    }];

    private int _statsGroupingIndex;
    /// <summary>0 = typ, 1 = zadavatel, 2 = název.</summary>
    public int StatsGroupingIndex
    {
        get => _statsGroupingIndex;
        set
        {
            if (!Set(ref _statsGroupingIndex, value)) return;
            OnPropertyChanged(nameof(StatsGroupHeader));
            _expandedGroups.Clear();
            RefreshStats();
        }
    }

    public bool AlwaysOnTop
    {
        get => _settings.AlwaysOnTop;
        set
        {
            _settings.AlwaysOnTop = value;
            _settings.Save(AppPaths.SettingsFile);
            OnPropertyChanged();
        }
    }

    private string _logStatus = "";
    public string LogStatus { get => _logStatus; private set => Set(ref _logStatus, value); }
    private string _mobiStatus = "";
    public string MobiStatus { get => _mobiStatus; private set => Set(ref _mobiStatus, value); }
    private string _locationStatus = "";
    public string LocationStatus { get => _locationStatus; private set => Set(ref _locationStatus, value); }
    private string _gameDataStatus = "";
    public string GameDataStatus { get => _gameDataStatus; private set => Set(ref _gameDataStatus, value); }
    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    public string WindowTitle => $"{Loc.Instance["App.Title"]} {UpdateService.CurrentVersion}";

    public ICommand OpenSettingsCommand { get; }
    public ICommand InstallOcrCommand { get; }
    public ICommand ImportHistoryCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand SetAppLanguageCommand { get; }
    public ICommand SetGameLanguageCommand { get; }
    public ICommand MarkCompletedCommand { get; }
    public ICommand MarkFailedCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ReadMobiGlasNowCommand { get; }
    public ICommand SaveOcrDiagnosticsCommand { get; }
    public ICommand NewContractCommand { get; }
    public ICommand EditContractCommand { get; }
    public ICommand ToggleModeCommand { get; }
    public ICommand CompleteAllCommand { get; }
    public ICommand AbandonAllCommand { get; }
    public ICommand DeleteAllHistoryCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }

    // ---------- Životní cyklus ----------

    /// <summary>Volá MainWindow po načtení (potřebuje handle okna pro hook a schránku).</summary>
    public void Start(Window window)
    {
        RebuildLanguageMenus();
        ApplyHooks(window);
        StartLogWatcher();
        StartScreenshotWatcher();
        _ = LoadGameDataAsync();
        _timer.Start();
        UpdateAllStatus();
        Refresh();
        _ = EnsureOcrLanguageAsync(userRequested: false);
        if (_settings.CheckUpdatesOnStartup) _ = CheckUpdatesAsync(interactive: false);
    }

    private void ApplyHooks(Window window)
    {
        _monitor.Enabled = _settings.OcrEnabled;
        _monitor.WatchDuration = TimeSpan.FromSeconds(Math.Clamp(_settings.OcrWatchSeconds, 5, 300));
        _monitor.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.OcrIntervalMs, 300, 10000));

        if (_settings.WatchClipboard)
        {
            try { _clipboard.Attach(window); }
            catch (Exception ex) { AppPaths.LogError(ex); }
        }
    }

    private static CaptureMethod ParseCaptureMethod(string? value) =>
        Enum.TryParse<CaptureMethod>(value, ignoreCase: true, out var m) ? m : CaptureMethod.Auto;

    private void StartScreenshotWatcher()
    {
        _screenshots.Stop();
        if (!_settings.WatchGameScreenshots) return;
        try { _screenshots.Start(_settings.GamePath); }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    private void StartLogWatcher()
    {
        _watcher?.Dispose();
        _watcher = new GameLogWatcher(_settings.GameLogPath);
        _watcher.LineRead += line =>
        {
            var ev = _parser.Parse(line);
            if (ev is null) return;
            _tracker.Apply(ev);
            if (TriggersMobiGlasCapture(ev)) _monitor.Trigger();
        };
        _watcher.Error += ex => AppPaths.LogError(ex);
        _watcher.Start(fromStart: true);
    }

    /// <summary>
    /// Snímkování mobiGlasu se spouští jen při ŽIVÉM záznamu o kontraktu v logu (přijetí, dokončení,
    /// opuštění, selhání, nový/splněný cíl). Řádky čtené zpětně při startu aplikace (starší než 2 min) ho nespouští.
    /// </summary>
    private static bool TriggersMobiGlasCapture(GameEvent ev) =>
        (ev.Kind is GameEventKind.ContractAccepted or GameEventKind.ContractCompleted or GameEventKind.ContractFailed
            or GameEventKind.ContractAbandoned or GameEventKind.ContractWithdrawn
            or GameEventKind.ObjectiveUpdated or GameEventKind.ObjectiveCompleted or GameEventKind.ObjectiveCancelled)
        && (DateTimeOffset.UtcNow - ev.Time).Duration() < TimeSpan.FromMinutes(2);

    private async Task LoadGameDataAsync()
    {
        GameDataStatus = Loc.Instance["Bar.GameDataLoading"];
        var provider = new P4kContractDetailsProvider(_settings.GamePath, CurrentGamePack().LocalizationFolder);
        try
        {
            await provider.InitializeAsync();
        }
        catch (Exception ex) { AppPaths.LogError(ex); }
        _details = provider.IsReady ? provider : null;
        _enrichAttempted.Clear();
        _dirty = true;
        UpdateGameDataStatus();
    }

    private void OnTick()
    {
        if (_dirty)
        {
            _dirty = false;
            Refresh();
        }
        foreach (var c in ActiveContracts) c.Tick();
        UpdateLogStatus();
        UpdateLocationStatus();
    }

    // ---------- Obnova dat ----------

    private void Refresh()
    {
        var all = _tracker.Snapshot();
        Enrich(all);

        Sync(ActiveContracts, all.Where(c => c.Status == ContractStatus.Active).OrderByDescending(c => c.AcceptedAt).ToList());
        Sync(HistoryContracts, all.Where(c => c.Status != ContractStatus.Active).OrderByDescending(c => c.AcceptedAt).ToList());
        foreach (var gone in _vms.Keys.Except(all.Select(c => c.Id)).ToList()) _vms.Remove(gone);
        OnPropertyChanged(nameof(HasNoActive));
        RefreshStats(all);
        RefreshRewards(all);
    }

    private ContractViewModel VmFor(Contract model)
    {
        if (!_vms.TryGetValue(model.Id, out var vm))
            _vms[model.Id] = vm = new ContractViewModel(model, _tracker);
        return vm;
    }

    private void Sync(ObservableCollection<ContractViewModel> target, IReadOnlyList<Contract> source)
    {
        var ids = source.Select(s => s.Id).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!ids.Contains(target[i].Id)) target.RemoveAt(i);

        for (var i = 0; i < source.Count; i++)
        {
            var model = source[i];
            var vm = VmFor(model);
            var idx = target.IndexOf(vm);
            if (idx < 0) target.Insert(Math.Min(i, target.Count), vm);
            else if (idx != i && i < target.Count) target.Move(idx, i);
            vm.Update(model, _nav, _tracker.Player);
        }
    }

    private void SetExpanded(object? scope, bool expanded)
    {
        IEnumerable<ContractViewModel> items = (scope as string) switch
        {
            "history" => HistoryContracts,
            _ => ActiveContracts
        };
        foreach (var vm in items) vm.IsExpanded = expanded;
    }

    private void RefreshHistoryView()
    {
        if (HistoryView.IsEditingItem || HistoryView.IsAddingNew) return;
        HistoryView.Refresh();
    }

    private DateTimeOffset? PeriodStart() => PeriodIndex switch
    {
        0 => new DateTimeOffset(DateTime.Today),
        1 => DateTimeOffset.Now.AddDays(-7),
        2 => DateTimeOffset.Now.AddDays(-30),
        _ => null
    };

    private void Enrich(IReadOnlyList<Contract> all)
    {
        if (_details is null) return;
        foreach (var c in all.Where(c => c.Description is null && _enrichAttempted.Add(c.Id)))
        {
            var d = _details.Lookup(c.ContractRecord, c.GeneratorName, c.Title);
            if (d is null) continue;
            var locations = d.Description is not null && _nav is not null
                ? _nav.Database.FindMentions(d.Description).Concat(d.Locations)
                : d.Locations;
            _tracker.Enrich(c.Id, d.Title, d.Description, locations);
        }
    }

    private void RefreshRewards(IReadOnlyList<Contract> all)
    {
        var byId = all.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var rewards = _tracker.Rewards();
        var filter = RewardFilter.Trim();
        var L = Loc.Instance;

        IEnumerable<RewardRow> Rows(RewardKind kind) => rewards
            .Where(r => r.Kind == kind)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var contracts = g.Select(r => r.ContractId is { } id && byId.TryGetValue(id, out var c) ? c : null)
                    .Where(c => c is not null).Cast<Contract>().ToList();
                return new RewardRow(
                    g.First().Name, g.Count(),
                    Fmt.Time(g.Min(r => r.Time)), g.Min(r => r.Time),
                    Fmt.Time(g.Max(r => r.Time)), g.Max(r => r.Time),
                    string.Join(", ", contracts.Select(c => c.Title).Where(t => t.Length > 0).Distinct()),
                    string.Join(", ", contracts.Select(c => L["Type." + ContractClassifier.Category(c)]).Distinct()),
                    string.Join(", ", contracts.Select(c => ContractClassifier.Giver(c)).Where(x => x is not null).Distinct()));
            })
            .Where(r => filter.Length == 0
                        || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.Contracts.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.Givers.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || r.Types.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.LastValue);

        BlueprintRows.Clear();
        foreach (var r in Rows(RewardKind.Blueprint)) BlueprintRows.Add(r);
        ItemRows.Clear();
        foreach (var r in Rows(RewardKind.Item)) ItemRows.Add(r);
        BlueprintSummary = L.Format("Rewards.Summary", BlueprintRows.Count, BlueprintRows.Sum(r => r.Count));
        ItemSummary = L.Format("Rewards.Summary", ItemRows.Count, ItemRows.Sum(r => r.Count));
    }

    private void RefreshStats() => RefreshStats(_tracker.Snapshot());

    private void RefreshStats(IReadOnlyList<Contract> all)
    {
        var since = PeriodStart();
        var o = StatisticsService.Overall(all, since);
        var L = Loc.Instance;
        OverallTiles.Clear();
        OverallTiles.Add(new StatTile(L["Stats.Accepted"], o.Accepted.ToString()));
        OverallTiles.Add(new StatTile(L["Stats.Completed"], o.Completed.ToString()));
        OverallTiles.Add(new StatTile(L["Stats.Failed"], o.Failed.ToString()));
        OverallTiles.Add(new StatTile(L["Stats.TotalReward"], Fmt.Money(o.TotalReward) + " aUEC"));
        OverallTiles.Add(new StatTile(L["Stats.TotalTime"], Fmt.Duration(o.TotalDuration)));
        OverallTiles.Add(new StatTile(L["Stats.PerHour"], Fmt.Money(o.RewardPerHour) + " aUEC/h"));
        OverallTiles.Add(new StatTile(L["Stats.Rep"], Fmt.Money(o.TotalRepMin) + " Rep"));
        OverallTiles.Add(new StatTile(L["Stats.Blueprints"], o.BlueprintsReceived.ToString()));
        OverallTiles.Add(new StatTile(L["Stats.SuccessRate"], Fmt.Percent(o.SuccessRate)));
        OverallTiles.Add(new StatTile(L["Stats.AvgReward"], Fmt.Money(o.AverageReward) + " aUEC"));
        OverallTiles.Add(new StatTile(L["Stats.MedianReward"], Fmt.Money(o.MedianReward) + " aUEC"));
        OverallTiles.Add(new StatTile(L["Stats.AvgDuration"], Fmt.ShortDuration(o.AverageDuration)));
        OverallTiles.Add(new StatTile(L["Stats.BestPerHour"], Fmt.Money(o.BestPerHour) + " aUEC/h", o.BestPerHourTitle));
        OverallTiles.Add(new StatTile(L["Stats.MaxReward"], Fmt.Money(o.MaxReward) + " aUEC"));
        OverallTiles.Add(new StatTile(L["Stats.Active"], o.Active.ToString()));
        OverallTiles.Add(new StatTile(L["Stats.Fees"], Fmt.Money(o.TotalFees) + " aUEC"));

        // Skupiny (typ / zadavatel / název) – rozbalení se drží přes obnovy
        foreach (var row in GroupStats.Where(r => r.IsExpanded)) _expandedGroups.Add(row.Name);
        foreach (var row in GroupStats.Where(r => !r.IsExpanded)) _expandedGroups.Remove(row.Name);
        Func<Contract, string> key = StatsGroupingIndex switch
        {
            1 => c => ContractClassifier.Giver(c) ?? L["Common.Unknown"],
            2 => c => string.IsNullOrWhiteSpace(c.Title) ? (c.ContractRecord ?? c.Id) : c.Title,
            _ => c => L["Type." + ContractClassifier.Category(c)]
        };
        var groups = StatisticsService.Group(all, key, since);
        GroupStats.Clear();
        foreach (var g in groups)
            GroupStats.Add(new GroupStatsRow(g, g.Contracts.Select(VmFor).ToList(), _expandedGroups.Contains(g.Key)));
    }

    // ---------- Stavový řádek ----------

    private void UpdateAllStatus()
    {
        UpdateLogStatus();
        UpdateMobiStatus();
        UpdateLocationStatus();
        UpdateGameDataStatus();
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void UpdateLogStatus() =>
        LogStatus = File.Exists(_settings.GameLogPath) ? Loc.Instance["Bar.LogOk"] : Loc.Instance["Bar.LogMissing"];

    private void UpdateMobiStatus()
    {
        var L = Loc.Instance;
        if (!_settings.OcrEnabled) { MobiStatus = L["Bar.MobiOcrOff"]; return; }
        if (_ocrInstallStatus is not null) { MobiStatus = _ocrInstallStatus; return; }
        if (!_ocr.IsAvailable) { MobiStatus = L.Format("Bar.MobiOcrMissing", _ocr.RequestedLanguage ?? "?"); return; }
        var s = _monitor.State switch
        {
            MobiGlasState.Watching => L["Bar.MobiWatching"],
            MobiGlasState.AcceptedRead => L.Format("Bar.MobiAccepted", _monitor.LastEntryCount, _monitor.LastSource ?? "?"),
            MobiGlasState.CaptureBlank => L["Bar.MobiBlank"],
            MobiGlasState.ScreenshotIgnored => L["Bar.MobiScreenshotIgnored"],
            _ => L["Bar.MobiIdle"]
        };
        if (_ocr.IsFallback) s += " · " + L.Format("Bar.MobiOcrMissing", _ocr.RequestedLanguage ?? "?");
        else if (_ocr.ActiveLanguage is { } active && !active.Equals(_ocr.RequestedLanguage, StringComparison.OrdinalIgnoreCase))
            s += $" · OCR {active}";
        MobiStatus = s;
    }

    private void UpdateLocationStatus()
    {
        var p = _tracker.Player;
        var name = _nav?.Database.Resolve(p.LastLocation)?.Name ?? p.LastLocation;
        if (p.LastPosition is { } pos && (name is null || p.LastPositionTime > p.LastLocationTime))
            name = (name is null ? "" : name + " · ") + $"x {pos.X / 1000:N0} km, y {pos.Y / 1000:N0} km, z {pos.Z / 1000:N0} km";
        LocationStatus = name is null ? Loc.Instance["Bar.LocationUnknown"] : Loc.Instance.Format("Bar.Location", name);
    }

    private void UpdateGameDataStatus() =>
        GameDataStatus = _details is { } d
            ? Loc.Instance.Format("Bar.GameDataOk", d.StringCount.ToString("N0"))
            : Loc.Instance["Bar.GameDataMissing"];

    private void OnClipboardText(string text)
    {
        if (!ShowLocationParser.TryParse(text, out var pos)) return;
        _tracker.Player.LastPosition = pos;
        _tracker.Player.LastPositionTime = DateTimeOffset.UtcNow;
        _dirty = true;
    }

    // ---------- Jazyky ----------

    private void LoadGamePacks()
    {
        // Pořadí: vestavěné → Assets aplikace → %AppData% (uživatelské úpravy mají přednost)
        var merged = new Dictionary<string, GameLanguagePack>(StringComparer.OrdinalIgnoreCase);
        var first = true;
        foreach (var dir in AppPaths.LanguageDirs("game"))
        {
            foreach (var kv in GameLanguagePack.LoadAll(dir, includeBuiltIn: first))
                merged[kv.Key] = kv.Value;
            first = false;
        }
        _gamePacks = merged;
    }

    private GameLanguagePack CurrentGamePack()
    {
        var en = _gamePacks.GetValueOrDefault("en") ?? GameLanguagePack.BuiltIn.English;
        var pack = _gamePacks.GetValueOrDefault(_settings.GameLanguage) ?? en;
        // Technické anglické texty necháme jako zálohu – hra je v logu občas nepřekládá.
        return pack.WithFallback(en);
    }

    private void RebuildLanguageMenus()
    {
        AppLanguages.Clear();
        foreach (var (code, name) in Loc.Instance.Available)
            AppLanguages.Add(new LanguageOption(code, name) { IsSelected = code == _settings.AppLanguage });
        GameLanguages.Clear();
        foreach (var p in _gamePacks.Values.OrderBy(p => p.Name))
            GameLanguages.Add(new LanguageOption(p.Code, p.Name) { IsSelected = p.Code == _settings.GameLanguage });
    }

    public IReadOnlyList<LanguageOption> GameLanguageList => _gamePacks.Values.OrderBy(p => p.Name)
        .Select(p => new LanguageOption(p.Code, p.Name)).ToList();

    private void SetAppLanguage(string code)
    {
        _settings.AppLanguage = code;
        _settings.Save(AppPaths.SettingsFile);
        Loc.Instance.SetLanguage(code);
    }

    private void SetGameLanguage(string code)
    {
        _settings.GameLanguage = code;
        _settings.Save(AppPaths.SettingsFile);
        var pack = CurrentGamePack();
        _parser.SetLanguage(pack);
        _mobiParser.SetLanguage(pack);
        _ocr.SetLanguage(pack.OcrLanguageTag);
        RebuildLanguageMenus();
        UpdateMobiStatus();
        _ = LoadGameDataAsync();
        _ = EnsureOcrLanguageAsync(userRequested: false);
    }

    private bool _ocrInstallRunning;
    private string? _ocrInstallStatus;

    /// <summary>
    /// Když Windows chybí OCR pro jazyk hry, doinstaluje ho (jeden dotaz UAC).
    /// Automaticky při startu a při změně jazyka hry; pokud uživatel dotaz odmítne,
    /// automaticky se pro ten jazyk už neptáme – jde to spustit z menu Nastavení.
    /// </summary>
    private async Task EnsureOcrLanguageAsync(bool userRequested)
    {
        if (_ocrInstallRunning || !_settings.OcrEnabled) return;
        var tag = CurrentGamePack().OcrLanguageTag;
        var missing = !_ocr.IsAvailable || _ocr.IsFallback;
        var L = Loc.Instance;
        if (!missing)
        {
            if (userRequested)
                MessageBox.Show(L.Format("Ocr.AlreadyInstalled", _ocr.ActiveLanguage ?? tag), L["App.Title"]);
            return;
        }
        if (!userRequested && _settings.OcrInstallDeclined.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;

        _ocrInstallRunning = true;
        _ocrInstallStatus = L.Format("Bar.OcrInstalling", tag);
        UpdateMobiStatus();
        try
        {
            var result = await OcrLanguageInstaller.InstallAsync(tag);
            switch (result.Status)
            {
                case OcrInstallStatus.Installed:
                    _settings.OcrInstallDeclined.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
                    _settings.Save(AppPaths.SettingsFile);
                    _ocr.SetLanguage(tag);
                    if (!_ocr.IsAvailable || _ocr.IsFallback)
                        MessageBox.Show(L.Format("Ocr.RestartNeeded", tag), L["App.Title"], MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case OcrInstallStatus.Cancelled:
                    if (!_settings.OcrInstallDeclined.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        _settings.OcrInstallDeclined.Add(tag);
                    _settings.Save(AppPaths.SettingsFile);
                    break;
                default:
                    MessageBox.Show(L.Format("Ocr.InstallFailed", tag, result.Error ?? result.ExitCode.ToString(),
                            OcrLanguageInstaller.CapabilityName(tag)),
                        L["App.Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }
        finally
        {
            _ocrInstallRunning = false;
            _ocrInstallStatus = null;
            UpdateMobiStatus();
        }
    }

    // ---------- Příkazy ----------

    private void OpenSettings()
    {
        var copy = _settings.Clone();
        var dlg = new SettingsWindow(copy, Loc.Instance.Available, GameLanguageList) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var pathChanged = !string.Equals(copy.GamePath, _settings.GamePath, StringComparison.OrdinalIgnoreCase);
        var gameLangChanged = copy.GameLanguage != _settings.GameLanguage;
        var appLangChanged = copy.AppLanguage != _settings.AppLanguage;
        _settings = copy;
        _settings.Save(AppPaths.SettingsFile);

        _monitor.Enabled = _settings.OcrEnabled;
        if (!_settings.OcrEnabled) _monitor.Stop();
        _monitor.WatchDuration = TimeSpan.FromSeconds(Math.Clamp(_settings.OcrWatchSeconds, 5, 300));
        _mobiParser.ListColumnSplit = _settings.ListColumnSplit;
        _capture.Method = ParseCaptureMethod(_settings.CaptureMethod);

        if (appLangChanged) Loc.Instance.SetLanguage(_settings.AppLanguage);
        if (gameLangChanged) SetGameLanguage(_settings.GameLanguage);
        StartScreenshotWatcher();
        if (pathChanged)
        {
            StartLogWatcher();
            if (!gameLangChanged) _ = LoadGameDataAsync();
        }
        RebuildLanguageMenus();
        UpdateAllStatus();
    }

    private void ImportHistory()
    {
        var dir = _settings.LogBackupsPath;
        if (!Directory.Exists(dir))
        {
            MessageBox.Show(Loc.Instance["Import.NoFolder"], Loc.Instance["App.Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Mouse.OverrideCursor = Cursors.Wait;
        var mode = _tracker.DefaultCompletionMode;
        try
        {
            var files = Directory.EnumerateFiles(dir, "*.log").OrderBy(f => File.GetLastWriteTimeUtc(f)).ToList();
            // Staré kontrakty z archivu se ukončují podle logu, i když je výchozí režim ruční.
            _tracker.DefaultCompletionMode = CompletionMode.Auto;
            foreach (var f in files)
            {
                var events = GameLogWatcher.ReadAllLines(f).Select(_parser.Parse).Where(e => e is not null).Cast<GameEvent>();
                _tracker.ApplyMany(events);
            }
            Refresh();
            MessageBox.Show(Loc.Instance.Format("Import.Done", files.Count, _tracker.Snapshot().Count),
                Loc.Instance["App.Title"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppPaths.LogError(ex);
            MessageBox.Show(ex.Message, Loc.Instance["App.Title"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _tracker.DefaultCompletionMode = mode;
            Mouse.OverrideCursor = null;
        }
    }

    private async Task CheckUpdatesAsync(bool interactive)
    {
        var L = Loc.Instance;
        UpdateStatus = L["Update.Checking"];
        var result = await _updates.CheckAsync(_settings.IncludePrereleases);
        switch (result)
        {
            case UpdateCheckResult.Available:
                UpdateStatus = _updates.AvailableVersion ?? "";
                if (MessageBox.Show(L.Format("Update.Available", _updates.AvailableVersion ?? "?"), L["App.Title"],
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    try { await _updates.DownloadAndRestartAsync(p => _ui.InvokeAsync(() => UpdateStatus = $"{p} %")); }
                    catch (Exception ex)
                    {
                        AppPaths.LogError(ex);
                        MessageBox.Show(L.Format("Update.Failed", ex.Message), L["App.Title"]);
                    }
                }
                break;
            case UpdateCheckResult.UpToDate:
                UpdateStatus = "";
                if (interactive) MessageBox.Show(L["Update.None"], L["App.Title"]);
                break;
            case UpdateCheckResult.NotInstalled:
                UpdateStatus = "";
                if (interactive) MessageBox.Show(L["Update.NotInstalled"], L["App.Title"]);
                break;
            default:
                UpdateStatus = "";
                if (interactive) MessageBox.Show(L.Format("Update.Failed", _updates.LastError ?? "?"), L["App.Title"]);
                break;
        }
    }

    private void MarkFinished(object? p, ContractStatus status)
    {
        if (p is not ContractViewModel vm) return;
        _tracker.Update(vm.Id, c => Finish(c, status));
    }

    /// <summary>
    /// Ruční ukončení tlačítkem: čas konce = TEĎ (i v režimu MANU – aUEC/h počítá čas od přijetí do ručního dokončení).
    /// Čas konce z logu zůstává jen jako poznámka (LogFinishedAt).
    /// </summary>
    private static void Finish(Contract c, ContractStatus status)
    {
        {
            c.Status = status;
            c.FinishedAt = DateTimeOffset.UtcNow;
            if (status == ContractStatus.Completed)
                foreach (var o in c.Objectives.Where(o => o.Text.Length > 0 && !o.Completed && !o.Cancelled))
                {
                    o.Completed = true;
                    o.CompletedAt = c.FinishedAt;
                }
        }
    }

    private void FinishAll(ContractStatus status)
    {
        var L = Loc.Instance;
        var ids = ActiveContracts.Select(v => v.Id).ToList();
        if (ids.Count == 0) return;
        var key = status == ContractStatus.Completed ? "Confirm.CompleteAll" : "Confirm.AbandonAll";
        if (MessageBox.Show(L.Format(key, ids.Count), L["App.Title"], MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        _tracker.UpdateMany(ids, c => Finish(c, status));
    }

    private void DeleteAllHistory()
    {
        var L = Loc.Instance;
        // jen to, co je v historii právě vidět (filtr období)
        var ids = HistoryView.Cast<ContractViewModel>().Select(v => v.Id).ToList();
        if (ids.Count == 0) return;
        if (MessageBox.Show(L.Format("Confirm.DeleteAll", ids.Count), L["App.Title"], MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        _tracker.RemoveMany(ids);
    }

    private void ToggleMode(object? p)
    {
        if (p is not ContractViewModel vm) return;
        _tracker.Update(vm.Id, c => c.CompletionMode = c.CompletionMode == CompletionMode.Auto ? CompletionMode.Manual : CompletionMode.Auto);
    }

    private void NewContract()
    {
        var c = new Contract
        {
            AcceptedAt = DateTimeOffset.UtcNow,
            Status = ContractStatus.Active,
            CompletionMode = _tracker.DefaultCompletionMode
        };
        var dlg = new ContractEditWindow(c, isNew: true) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true) _tracker.AddManual(dlg.Result);
    }

    private void EditContract(object? p)
    {
        if (p is not ContractViewModel vm) return;
        var dlg = new ContractEditWindow(vm.Model, isNew: false) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var edited = dlg.Result;
        _tracker.Update(vm.Id, c => ContractEditWindow.CopyEditable(edited, c));
    }

    private void DeleteContract(object? p)
    {
        if (p is not ContractViewModel vm) return;
        if (MessageBox.Show(Loc.Instance.Format("Confirm.Delete", vm.Title), Loc.Instance["App.Title"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _tracker.Remove(vm.Id);
    }

    private void SaveOcrDiagnostics()
    {
        var L = Loc.Instance;
        if (_monitor.LastDiagnostic is not { } d)
        {
            MessageBox.Show(L["Diag.Nothing"], L["App.Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var dir = Path.Combine(AppPaths.DataDir, "diagnostics", d.Time.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);

            // Snímek, který OCR dostalo
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(d.Frame.Width, d.Frame.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, d.Frame.Pixels, d.Frame.Width * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(dir, "frame.png"))) encoder.Save(fs);

            // Rozpoznaný text se souřadnicemi a výsledek parseru
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Čas: {d.Time:O}");
            sb.AppendLine($"Zdroj snímku: {d.Frame.Source} ({d.Frame.Width}×{d.Frame.Height})");
            sb.AppendLine($"OCR jazyk: {d.OcrLanguage} (požadovaný {_ocr.RequestedLanguage})");
            sb.AppendLine($"Jazyk hry: {_settings.GameLanguage}, ListColumnSplit: {_settings.ListColumnSplit}");
            sb.AppendLine($"Contracts: {d.Snapshot.IsContractsApp}, Accepted: {d.Snapshot.IsAcceptedTab}");
            sb.AppendLine();
            sb.AppendLine("== Seznam (vlevo) ==");
            foreach (var e in d.Snapshot.Entries) sb.AppendLine($"  {e.Title} | {e.Reward?.ToString() ?? "-"}");
            if (d.Snapshot.Detail is { } det)
            {
                sb.AppendLine("== Detail (vpravo) ==");
                sb.AppendLine($"  Název: {det.Title}");
                sb.AppendLine($"  Zadavatel: {det.Contractor}");
                sb.AppendLine($"  Odměna: {det.Reward}");
                foreach (var o in det.Objectives) sb.AppendLine($"  Cíl: {o}");
            }
            sb.AppendLine();
            sb.AppendLine("== OCR řádky (x; y; šířka; výška | text) ==");
            foreach (var l in d.Lines.OrderBy(l => l.Y).ThenBy(l => l.X))
                sb.AppendLine(FormattableString.Invariant($"{l.X:0.000}; {l.Y:0.000}; {l.Width:0.000}; {l.Height:0.000} | {l.Text}"));
            File.WriteAllText(Path.Combine(dir, "ocr.txt"), sb.ToString(), System.Text.Encoding.UTF8);

            OpenFolder(dir);
        }
        catch (Exception ex)
        {
            AppPaths.LogError(ex);
            MessageBox.Show(ex.Message, L["App.Title"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    public void Dispose()
    {
        _timer.Stop();
        _watcher?.Dispose();
        _monitor.Dispose();
        _screenshots.Dispose();
        _capture.Dispose();
        _clipboard.Dispose();
    }
}
