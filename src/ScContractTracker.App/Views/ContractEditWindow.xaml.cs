using System.Globalization;
using System.Text.Json;
using System.Windows;
using ScContractTracker.App.Services;
using ScContractTracker.Core.Models;

namespace ScContractTracker.App.Views;

/// <summary>
/// Ruční založení nebo úprava celého kontraktu. Upravená pole se označí jako ruční (ManualFields),
/// takže je pozdější záznamy v logu ani OCR nepřepíšou.
/// </summary>
public partial class ContractEditWindow : Window
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly Contract _original;
    private readonly bool _isNew;

    /// <summary>Upravená kopie kontraktu (po „Uložit“).</summary>
    public Contract Result { get; private set; }

    public ContractEditWindow(Contract contract, bool isNew)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        _original = contract;
        _isNew = isNew;
        Result = Clone(contract);
        var L = Loc.Instance;
        if (isNew) Title = L["Edit.NewTitle"];

        TypeBox.ItemsSource = new[] { new KeyValuePair<string, string>("", L["Edit.TypeAuto"]) }
            .Concat(Enum.GetValues<ContractCategory>().Select(c => new KeyValuePair<string, string>(c.ToString(), L["Type." + c])))
            .ToList();
        StatusBox.ItemsSource = Enum.GetValues<ContractStatus>()
            .Select(s => new KeyValuePair<string, string>(s.ToString(), L["Status." + s])).ToList();
        ModeBox.ItemsSource = new[]
        {
            new KeyValuePair<string, string>(nameof(CompletionMode.Auto), L["Mode.Auto"]),
            new KeyValuePair<string, string>(nameof(CompletionMode.Manual), L["Mode.Manual"])
        };

        var c = contract;
        TitleBox.Text = c.Title;
        TypeBox.SelectedValue = c.CategoryOverride?.ToString() ?? "";
        StatusBox.SelectedValue = c.Status.ToString();
        ModeBox.SelectedValue = c.CompletionMode.ToString();
        GiverBox.Text = c.Organization ?? c.Contractor ?? "";
        PersonBox.Text = c.MissionGiver ?? "";
        AcceptedBox.Text = c.AcceptedAt == default ? "" : c.AcceptedAt.ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
        FinishedBox.Text = c.FinishedAt?.ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture) ?? "";
        RewardBox.Text = c.Reward?.ToString(CultureInfo.InvariantCulture) ?? "";
        FeeBox.Text = c.Fee?.ToString(CultureInfo.InvariantCulture) ?? "";
        RepMinBox.Text = c.RepMin?.ToString(CultureInfo.InvariantCulture) ?? "";
        RepMaxBox.Text = c.RepMax?.ToString(CultureInfo.InvariantCulture) ?? "";
        DescriptionBox.Text = c.Description ?? "";
        ObjectivesBox.Text = string.Join(Environment.NewLine, c.Objectives.Where(o => o.Text.Length > 0)
            .Select(o => (o.Completed ? "[x] " : o.Cancelled ? "[-] " : "") + o.Text));
        BlueprintsBox.Text = string.Join(Environment.NewLine, c.ReceivedBlueprints);
        ItemsBox.Text = string.Join(Environment.NewLine, c.ItemRewards);
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var L = Loc.Instance;
        ErrorText.Text = "";
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) { ErrorText.Text = L["Edit.ErrTitle"]; return; }
        if (!TryTime(AcceptedBox.Text, out var accepted) || accepted is null) { ErrorText.Text = L["Edit.ErrTime"]; return; }
        if (!TryTime(FinishedBox.Text, out var finished)) { ErrorText.Text = L["Edit.ErrTime"]; return; }
        if (!TryAmount(RewardBox.Text, out var reward) || !TryAmount(FeeBox.Text, out var fee)
            || !TryInt(RepMinBox.Text, out var repMin) || !TryInt(RepMaxBox.Text, out var repMax))
        {
            ErrorText.Text = L["Edit.ErrNumber"];
            return;
        }

        var r = Clone(_original);
        var o = _original;
        void Mark(string field, bool changed)
        {
            if (changed && !r.ManualFields.Contains(field, StringComparer.OrdinalIgnoreCase)) r.ManualFields.Add(field);
        }

        r.Title = TitleBox.Text.Trim();
        Mark("title", r.Title != o.Title || _isNew);

        r.CategoryOverride = Enum.TryParse<ContractCategory>(TypeBox.SelectedValue as string, out var cat) ? cat : null;

        var status = Enum.TryParse<ContractStatus>(StatusBox.SelectedValue as string, out var st) ? st : o.Status;
        if (status != o.Status && status != ContractStatus.Abandoned) r.EndReason = null;
        r.Status = status;
        r.CompletionMode = Enum.TryParse<CompletionMode>(ModeBox.SelectedValue as string, out var mode) ? mode : o.CompletionMode;

        var giver = Empty(GiverBox.Text);
        if (giver != (o.Organization ?? o.Contractor)) { r.Organization = giver; Mark("giver", true); }
        var person = Empty(PersonBox.Text);
        if (person != o.MissionGiver) { r.MissionGiver = person; Mark("person", true); }

        r.AcceptedAt = accepted.Value;
        r.FinishedAt = status == ContractStatus.Active ? null
            : finished ?? o.FinishedAt ?? DateTimeOffset.UtcNow;

        if (reward != o.Reward) { r.Reward = reward; r.RewardSource = reward is null ? null : "manual"; }
        if (fee != o.Fee) { r.Fee = fee; r.FeeSource = fee is null ? null : "manual"; }
        r.RepMin = repMin;
        r.RepMax = repMax ?? repMin;

        var description = Empty(DescriptionBox.Text);
        if (description != o.Description) { r.Description = description; r.DescriptionSource = "manual"; Mark("description", true); }

        r.Objectives = ParseObjectives(ObjectivesBox.Text, o.Objectives, r.FinishedAt);
        r.ReceivedBlueprints = Lines(BlueprintsBox.Text);
        r.ItemRewards = Lines(ItemsBox.Text);

        Result = r;
        DialogResult = true;
    }

    /// <summary>Řádky „[x] text“ = splněno, „[-] text“ = zrušeno; existující cíle si ponechají ID, časy a souřadnice.</summary>
    private static List<ContractObjective> ParseObjectives(string text, IReadOnlyList<ContractObjective> old, DateTimeOffset? finished)
    {
        var result = new List<ContractObjective>();
        // cíle bez textu (jen značky z logu) zachováme kvůli navigaci
        result.AddRange(old.Where(o => o.Text.Length == 0).Select(CloneObjective));
        var n = 0;
        foreach (var raw in text.Split('\n').Select(l => l.Trim('\r', ' ')).Where(l => l.Length > 0))
        {
            var done = raw.StartsWith("[x]", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("✔");
            var cancelled = raw.StartsWith("[-]");
            var line = done || cancelled ? raw[(raw.StartsWith("✔") ? 1 : 3)..].Trim() : raw;
            if (line.Length == 0) continue;
            var prev = old.FirstOrDefault(o => o.Text.Equals(line, StringComparison.OrdinalIgnoreCase));
            var obj = prev is not null ? CloneObjective(prev) : new ContractObjective { Id = "manual:" + n, Text = line, Source = "manual" };
            if (done && !obj.Completed) obj.CompletedAt ??= finished ?? DateTimeOffset.UtcNow;
            if (!done) obj.CompletedAt = null;
            obj.Completed = done;
            obj.Cancelled = cancelled;
            result.Add(obj);
            n++;
        }
        return result;
    }

    /// <summary>Přenese upravená pole do kontraktu v trackeru (ID a data z logu zůstávají).</summary>
    public static void CopyEditable(Contract src, Contract dst)
    {
        dst.Title = src.Title;
        dst.CategoryOverride = src.CategoryOverride;
        dst.Status = src.Status;
        dst.EndReason = src.EndReason;
        dst.CompletionMode = src.CompletionMode;
        dst.Organization = src.Organization;
        dst.MissionGiver = src.MissionGiver;
        dst.AcceptedAt = src.AcceptedAt;
        dst.FinishedAt = src.FinishedAt;
        dst.Reward = src.Reward;
        dst.RewardSource = src.RewardSource;
        dst.Fee = src.Fee;
        dst.FeeSource = src.FeeSource;
        dst.RepMin = src.RepMin;
        dst.RepMax = src.RepMax;
        dst.Description = src.Description;
        dst.DescriptionSource = src.DescriptionSource;
        dst.Objectives = src.Objectives;
        dst.ReceivedBlueprints = src.ReceivedBlueprints;
        dst.ItemRewards = src.ItemRewards;
        dst.ManualFields = src.ManualFields;
    }

    private static Contract Clone(Contract c) =>
        JsonSerializer.Deserialize<Contract>(JsonSerializer.Serialize(c))!;

    private static ContractObjective CloneObjective(ContractObjective o) =>
        JsonSerializer.Deserialize<ContractObjective>(JsonSerializer.Serialize(o))!;

    private static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<string> Lines(string s) => s.Split('\n', ',', ';')
        .Select(l => l.Trim('\r', ' ')).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool TryTime(string s, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(s)) return true;
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.CurrentCulture })
            if (DateTime.TryParse(s.Trim(), culture, DateTimeStyles.AssumeLocal, out var dt))
            {
                value = new DateTimeOffset(dt).ToUniversalTime();
                return true;
            }
        return false;
    }

    private static bool TryAmount(string s, out long? value)
    {
        value = null;
        var t = new string(s.Where(ch => char.IsDigit(ch) || ch == '-').ToArray());
        if (t.Length == 0) return string.IsNullOrWhiteSpace(s) || !s.Any(char.IsLetterOrDigit);
        if (!long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)) return false;
        value = v;
        return true;
    }

    private static bool TryInt(string s, out int? value)
    {
        value = null;
        if (!TryAmount(s, out var v)) return false;
        if (v is null) return true;
        if (v is < int.MinValue or > int.MaxValue) return false;
        value = (int)v.Value;
        return true;
    }
}
