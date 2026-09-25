using ScContractTracker.Core.Models;

namespace ScContractTracker.Core.Stats;

/// <summary>Souhrn za jeden typ kontraktu (stejný záznam v DataCore nebo stejný název).</summary>
public sealed record ContractTypeStats(
    string Key,
    string Title,
    int Accepted,
    int Completed,
    int Failed,
    long TotalReward,
    TimeSpan TotalDuration,
    TimeSpan? AverageDuration,
    double? RewardPerHour,
    long? AverageReward);

public sealed record OverallStats(
    int Accepted,
    int Active,
    int Completed,
    int Failed,
    long TotalReward,
    TimeSpan TotalDuration,
    double? RewardPerHour,
    long TotalRepMin = 0,
    int BlueprintsReceived = 0,
    double? SuccessRate = null,
    long? AverageReward = null,
    long? MedianReward = null,
    TimeSpan? AverageDuration = null,
    double? BestPerHour = null,
    string? BestPerHourTitle = null,
    long? MaxReward = null,
    long TotalFees = 0);

/// <summary>Jak seskupit kontrakty v historii a statistikách.</summary>
public enum StatsGrouping
{
    None,
    Title,
    Type,
    Giver
}

/// <summary>Statistika jedné skupiny kontraktů (podle názvu / typu / zadavatele).</summary>
public sealed record GroupStats(
    string Key,
    int Accepted,
    int Active,
    int Completed,
    int Failed,
    double? SuccessRate,
    long TotalReward,
    long? AverageReward,
    long? MedianReward,
    long? MinReward,
    long? MaxReward,
    TimeSpan TotalDuration,
    TimeSpan? AverageDuration,
    TimeSpan? MedianDuration,
    TimeSpan? Fastest,
    TimeSpan? Slowest,
    double? RewardPerHour,
    double? BestPerHour,
    long TotalRep,
    int Blueprints,
    DateTimeOffset? LastAccepted,
    IReadOnlyList<Contract> Contracts,
    long TotalFees = 0);

public static class StatisticsService
{
    /// <summary>
    /// Výtěžnost = součet odměn dokončených kontraktů s odměnou / součet jejich délek.
    /// Kontrakty bez odměny nebo bez času dokončení se do aUEC/h nezapočítají.
    /// </summary>
    public static OverallStats Overall(IEnumerable<Contract> contracts, DateTimeOffset? since = null)
    {
        var list = Filter(contracts, since).ToList();
        var paid = list.Where(IsPaidCompleted).ToList();
        var reward = paid.Sum(c => c.NetReward!.Value);
        var duration = Sum(paid.Select(c => c.Duration!.Value));
        return new OverallStats(
            Accepted: list.Count,
            Active: list.Count(c => c.Status == ContractStatus.Active),
            Completed: list.Count(c => c.Status == ContractStatus.Completed),
            Failed: list.Count(c => c.Status is ContractStatus.Failed or ContractStatus.Abandoned or ContractStatus.Withdrawn),
            TotalReward: reward,
            TotalDuration: duration,
            RewardPerHour: duration.TotalHours > 0 ? reward / duration.TotalHours : null,
            TotalRepMin: list.Where(c => c.Status == ContractStatus.Completed).Sum(c => (long)(c.RepMin ?? 0)),
            BlueprintsReceived: list.Sum(c => c.ReceivedBlueprints.Count),
            SuccessRate: SuccessRate(list),
            AverageReward: paid.Count > 0 ? reward / paid.Count : null,
            MedianReward: Median(paid.Select(c => c.NetReward!.Value)),
            AverageDuration: paid.Count > 0 ? duration / paid.Count : null,
            BestPerHour: paid.Count > 0 ? paid.Max(c => c.RewardPerHour) : null,
            BestPerHourTitle: paid.OrderByDescending(c => c.RewardPerHour).FirstOrDefault()?.Title,
            MaxReward: paid.Count > 0 ? paid.Max(c => c.NetReward) : null,
            TotalFees: list.Sum(c => c.Fee ?? 0));
    }

    /// <summary>
    /// Skupiny podle zvoleného klíče. <paramref name="keyOf"/> vrací zobrazovaný název skupiny
    /// (aplikace dodá lokalizovaný typ kontraktu, zadavatele…). Řazeno podle aUEC/h, pak počtu.
    /// </summary>
    public static IReadOnlyList<GroupStats> Group(IEnumerable<Contract> contracts, Func<Contract, string> keyOf,
        DateTimeOffset? since = null)
    {
        return Filter(contracts, since)
            .GroupBy(keyOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => Build(g.Key, g.ToList()))
            .OrderByDescending(s => s.RewardPerHour ?? -1)
            .ThenByDescending(s => s.Accepted)
            .ToList();
    }

    public static GroupStats Build(string key, IReadOnlyList<Contract> list)
    {
        var paid = list.Where(IsPaidCompleted).ToList();
        var completed = list.Where(c => c.Status == ContractStatus.Completed && c.Duration is { TotalSeconds: >= 1 }).ToList();
        var reward = paid.Sum(c => c.NetReward!.Value);
        var paidDuration = Sum(paid.Select(c => c.Duration!.Value));
        var durations = completed.Select(c => c.Duration!.Value).OrderBy(d => d).ToList();
        var allDuration = Sum(durations);
        return new GroupStats(
            Key: key,
            Accepted: list.Count,
            Active: list.Count(c => c.Status == ContractStatus.Active),
            Completed: list.Count(c => c.Status == ContractStatus.Completed),
            Failed: list.Count(c => c.Status is ContractStatus.Failed or ContractStatus.Abandoned or ContractStatus.Withdrawn),
            SuccessRate: SuccessRate(list),
            TotalReward: reward,
            AverageReward: paid.Count > 0 ? reward / paid.Count : null,
            MedianReward: Median(paid.Select(c => c.NetReward!.Value)),
            MinReward: paid.Count > 0 ? paid.Min(c => c.NetReward) : null,
            MaxReward: paid.Count > 0 ? paid.Max(c => c.NetReward) : null,
            TotalDuration: allDuration,
            AverageDuration: durations.Count > 0 ? allDuration / durations.Count : null,
            MedianDuration: durations.Count > 0 ? TimeSpan.FromTicks(Median(durations.Select(d => d.Ticks))!.Value) : null,
            Fastest: durations.Count > 0 ? durations[0] : null,
            Slowest: durations.Count > 0 ? durations[^1] : null,
            RewardPerHour: paidDuration.TotalHours > 0 ? reward / paidDuration.TotalHours : null,
            BestPerHour: paid.Count > 0 ? paid.Max(c => c.RewardPerHour) : null,
            TotalRep: list.Where(c => c.Status == ContractStatus.Completed).Sum(c => (long)(c.RepMin ?? 0)),
            Blueprints: list.Sum(c => c.ReceivedBlueprints.Count),
            LastAccepted: list.Count > 0 ? list.Max(c => c.AcceptedAt) : null,
            Contracts: list.OrderByDescending(c => c.AcceptedAt).ToList(),
            TotalFees: list.Sum(c => c.Fee ?? 0));
    }

    /// <summary>Úspěšnost = dokončené / ukončené (aktivní se nepočítají).</summary>
    private static double? SuccessRate(IReadOnlyCollection<Contract> list)
    {
        var finished = list.Count(c => c.Status != ContractStatus.Active);
        return finished > 0 ? (double)list.Count(c => c.Status == ContractStatus.Completed) / finished : null;
    }

    private static long? Median(IEnumerable<long> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    public static IReadOnlyList<ContractTypeStats> ByType(IEnumerable<Contract> contracts, DateTimeOffset? since = null)
    {
        return Filter(contracts, since)
            .GroupBy(c => c.TemplateKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var paid = g.Where(IsPaidCompleted).ToList();
                var completed = g.Where(c => c.Status == ContractStatus.Completed && c.Duration is not null).ToList();
                var reward = paid.Sum(c => c.NetReward!.Value);
                var paidDuration = Sum(paid.Select(c => c.Duration!.Value));
                var allDuration = Sum(completed.Select(c => c.Duration!.Value));
                var title = g.OrderByDescending(c => c.AcceptedAt).Select(c => c.Title)
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? g.Key;
                return new ContractTypeStats(
                    Key: g.Key,
                    Title: title,
                    Accepted: g.Count(),
                    Completed: g.Count(c => c.Status == ContractStatus.Completed),
                    Failed: g.Count(c => c.Status is ContractStatus.Failed or ContractStatus.Abandoned or ContractStatus.Withdrawn),
                    TotalReward: reward,
                    TotalDuration: allDuration,
                    AverageDuration: completed.Count > 0 ? allDuration / completed.Count : null,
                    RewardPerHour: paidDuration.TotalHours > 0 ? reward / paidDuration.TotalHours : null,
                    AverageReward: paid.Count > 0 ? reward / paid.Count : null);
            })
            .OrderByDescending(s => s.RewardPerHour ?? -1)
            .ThenByDescending(s => s.Accepted)
            .ToList();
    }

    private static bool IsPaidCompleted(Contract c) =>
        c.Status == ContractStatus.Completed && c.Reward is > 0 && c.Duration is { TotalSeconds: >= 1 };

    private static IEnumerable<Contract> Filter(IEnumerable<Contract> contracts, DateTimeOffset? since) =>
        since is null ? contracts : contracts.Where(c => c.AcceptedAt >= since);

    private static TimeSpan Sum(IEnumerable<TimeSpan> spans) =>
        spans.Aggregate(TimeSpan.Zero, (a, b) => a + b);
}
