using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Logs;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Stats;
using ScContractTracker.Core.Tracking;
using Xunit;

namespace ScContractTracker.Core.Tests;

public class ObjectiveProgressTests
{
    private const string Mission = "4a05dea7-2a74-4b31-9642-37d2828e2f2f";

    private static ContractTracker RunUntil(string? until = null)
    {
        var p = new GameLogParser(GameLanguagePack.BuiltIn.English);
        var t = new ContractTracker();
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gamelog_4x_objectives_ship_in_distress.txt"));
        if (until is not null) lines = lines.Where(l => string.CompareOrdinal(l, 1, until, 0, until.Length) < 0).ToArray();
        t.ApplyMany(lines.Select(p.Parse).Where(e => e is not null)!);
        return t;
    }

    [Fact]
    public void Current_objective_moves_to_next_when_previous_completes()
    {
        var before = RunUntil("2026-09-18T21:03:28").Snapshot().Single(c => c.Id == Mission);
        Assert.Equal("Goto ship in distress", before.GetCurrentObjective()?.Text);

        var after = RunUntil("2026-09-18T21:05:00").Snapshot().Single(c => c.Id == Mission);
        var done = after.Objectives.Single(o => o.Text == "Goto ship in distress");
        Assert.True(done.Completed);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 21, 3, 28, 796, TimeSpan.Zero), done.CompletedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 20, 53, 38, TimeSpan.Zero), done.AddedAt!.Value.AddTicks(-(done.AddedAt!.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal("Board The Drake Caterpillar", after.GetCurrentObjective()?.Text);

        var later = RunUntil("2026-09-18T21:27:00").Snapshot().Single(c => c.Id == Mission);
        Assert.Equal("Repair The Power Plant", later.GetCurrentObjective()?.Text);
    }

    [Fact]
    public void Completed_contract_has_all_objectives_done_with_times()
    {
        var c = RunUntil().Snapshot().Single(c => c.Id == Mission);
        Assert.Equal(ContractStatus.Completed, c.Status);
        Assert.Null(c.GetCurrentObjective());
        Assert.All(c.Objectives.Where(o => o.Text.Length > 0), o => Assert.NotNull(o.CompletedAt));
        Assert.Equal(ContractCategory.Rescue, ContractClassifier.Category(c));
        Assert.Equal("People Alliance", ContractClassifier.Giver(c));
        Assert.Equal("Recco Battaglia", c.MissionGiver);
    }
}

public class ClassifierTests
{
    [Theory]
    [InlineData("DEAD SAINTS - Rookie Rank - Medium Cargo Haul - Samson & Son's Salvage Center on Wala > Riker Memorial Spaceport", ContractCategory.Hauling)]
    [InlineData("Ling Family Rookie Cargo Haul - Small Scale - Levski > Grim HEX", ContractCategory.Hauling)]
    [InlineData("Rookie Hauler Needed for Direct Bulk Shipment - Megumi Refueling > Teasa", ContractCategory.Hauling)]
    [InlineData("ENTRY LVL. COURIER NEEDED IN STANTON", ContractCategory.Delivery)]
    [InlineData("UWC'S HIRING REFUELERS", ContractCategory.Refueling)]
    [InlineData("Ship In Distress", ContractCategory.Rescue)]
    [InlineData("Low Risk Protection Detail", ContractCategory.Escort)]
    [InlineData("New Defense Contract", ContractCategory.Defense)]
    [InlineData("NEW RETRIEVAL RUN AVAILABLE", ContractCategory.Retrieval)]
    [InlineData("Welcome to the System", ContractCategory.Onboarding)]
    public void Category_from_title(string title, ContractCategory expected) =>
        Assert.Equal(expected, ContractClassifier.Category(new Contract { Title = title }));

    [Fact]
    public void Category_from_log_mission_type_and_objective_kinds()
    {
        Assert.Equal(ContractCategory.Defense, ContractClassifier.Category(new Contract { Title = "Job", MissionType = "ShipAmbush" }));
        var c = new Contract { Title = "Orison Relief" };
        c.Objectives.Add(new ContractObjective { Id = "pickup_1", Kind = "pickup" });
        c.Objectives.Add(new ContractObjective { Id = "dropoff_1", Kind = "dropoff" });
        Assert.Equal(ContractCategory.Delivery, ContractClassifier.Category(c));

        var relief = new Contract { Title = "Orison Relief", MissionType = "Generic" };
        relief.Objectives.Add(new ContractObjective { Id = "a", Text = "Go to a debris field above Calliope" });
        relief.Objectives.Add(new ContractObjective { Id = "b", Text = "Defeat Hostile Ships" });
        Assert.Equal(ContractCategory.Combat, ContractClassifier.Category(relief));
    }

    [Fact]
    public void Giver_falls_back_to_title_prefix()
    {
        Assert.Equal("DEAD SAINTS", ContractClassifier.Giver(new Contract { Title = "DEAD SAINTS -- Rookie Rank - Small Scale Cargo Run" }));
        Assert.Equal("Ling Family", ContractClassifier.Giver(new Contract { Title = "Ling Family Rookie Cargo Haul - Small Scale - Levski > Orison" }));
        Assert.Null(ContractClassifier.Giver(new Contract { Title = "BOJOVÁ PROVĚRKA - SCÉNAŘ #5" }));
        Assert.Equal("Collector", ContractClassifier.Giver(new Contract { Title = "Wikelo Arrive to System", Organization = "Civilian", MissionGiver = "Collector" }));
        Assert.Equal("Foxwell Enforcement", ContractClassifier.Giver(new Contract { Title = "X", Organization = "Foxwell Enforcement", Contractor = "Other" }));
    }
}

public class GroupStatsTests
{
    private static Contract C(string title, long? reward, int minutes, ContractStatus status = ContractStatus.Completed, string? org = null)
    {
        var start = new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
        return new Contract
        {
            Id = Guid.NewGuid().ToString(), Title = title, Reward = reward, Status = status, Organization = org,
            AcceptedAt = start, FinishedAt = status == ContractStatus.Active ? null : start.AddMinutes(minutes)
        };
    }

    [Fact]
    public void Group_by_giver_computes_median_success_and_extremes()
    {
        var list = new[]
        {
            C("Haul A", 10_000, 30, org: "Dead Saints"),
            C("Haul B", 20_000, 60, org: "Dead Saints"),
            C("Haul C", 60_000, 90, org: "Dead Saints"),
            C("Haul D", null, 10, ContractStatus.Failed, org: "Dead Saints"),
            C("Courier", 5_000, 15, org: "Covalex"),
        };
        var groups = StatisticsService.Group(list, c => ContractClassifier.Giver(c) ?? "?");
        var ds = groups.Single(g => g.Key == "Dead Saints");
        Assert.Equal(4, ds.Accepted);
        Assert.Equal(3, ds.Completed);
        Assert.Equal(0.75, ds.SuccessRate!.Value, 3);
        Assert.Equal(90_000, ds.TotalReward);
        Assert.Equal(30_000, ds.AverageReward);
        Assert.Equal(20_000, ds.MedianReward);
        Assert.Equal(10_000, ds.MinReward);
        Assert.Equal(60_000, ds.MaxReward);
        Assert.Equal(TimeSpan.FromMinutes(30), ds.Fastest);
        Assert.Equal(TimeSpan.FromMinutes(90), ds.Slowest);
        Assert.Equal(TimeSpan.FromMinutes(60), ds.MedianDuration);
        Assert.Equal(30_000, ds.RewardPerHour!.Value, 1);   // 90k / 3 h
        Assert.Equal(40_000, ds.BestPerHour!.Value, 1);     // 60k / 1.5 h
        Assert.Equal(4, ds.Contracts.Count);

        var overall = StatisticsService.Overall(list);
        Assert.Equal(0.8, overall.SuccessRate!.Value, 3);
        Assert.Equal(15_000, overall.MedianReward);
        Assert.Equal("Haul C", overall.BestPerHourTitle);
    }
}
