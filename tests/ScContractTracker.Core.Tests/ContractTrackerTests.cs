using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Logs;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Ocr;
using ScContractTracker.Core.Stats;
using ScContractTracker.Core.Storage;
using ScContractTracker.Core.Tracking;
using Xunit;

namespace ScContractTracker.Core.Tests;

public class ContractTrackerTests
{
    private const string Id = "5b2c7f10-1a2b-4c3d-8e9f-0123456789ab";
    private static readonly GameLogParser Parser = new(GameLanguagePack.BuiltIn.English);

    private static readonly string[] SessionLog =
    [
        $"<2026-09-20T18:00:00.000Z> [Notice] <RequestLocationInventory> Player[Me] requested inventory for Location[Stanton1_Lorville]",
        $"<2026-09-20T18:00:10.000Z> [Notice] <CContractGenerator> missionId [{Id}], generator name [Covalex_Gen], contract [Covalex_Local]",
        $"<2026-09-20T18:00:10.500Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Accepted: Covalex Local Delivery\" [1] to queue. MissionId: [{Id}]",
        $"<2026-09-20T18:10:00.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"New Objective: Deliver package to Everus Harbor\" [2] to queue. MissionId: [{Id}]",
        $"<2026-09-20T18:30:10.000Z> [Notice] <MissionEnded> mission_id {Id} - mission_state MISSION_STATE_COMPLETED",
        $"<2026-09-20T18:30:11.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Complete: Covalex Local Delivery\" [3]",
        $"<2026-09-20T18:30:12.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Awarded: 15,000 aUEC\" [4]",
    ];

    private static ContractTracker Run(IEnumerable<string> lines, ContractTracker? t = null)
    {
        t ??= new ContractTracker();
        t.ApplyMany(lines.Select(Parser.Parse).Where(e => e is not null)!);
        return t;
    }

    [Fact]
    public void Full_lifecycle_produces_one_completed_contract_with_duration_and_reward()
    {
        var t = Run(SessionLog);
        var c = Assert.Single(t.Snapshot());
        Assert.Equal(Id, c.Id);
        Assert.Equal("Covalex Local Delivery", c.Title);
        Assert.Equal("Covalex_Local", c.ContractRecord);
        Assert.Equal(ContractStatus.Completed, c.Status);
        Assert.Equal(TimeSpan.FromMinutes(30), c.Duration);
        Assert.Equal(15000, c.Reward);
        Assert.Equal("log", c.RewardSource);
        Assert.Equal(30000, c.RewardPerHour!.Value, 3);
        Assert.Single(c.Objectives);
        Assert.Equal("Stanton1_Lorville", t.Player.LastLocation);
    }

    [Fact]
    public void Replaying_the_same_log_is_idempotent()
    {
        var t = Run(SessionLog);
        Run(SessionLog, t);
        var c = Assert.Single(t.Snapshot());
        Assert.Equal(TimeSpan.FromMinutes(30), c.Duration);
    }

    [Fact]
    public void Contract_without_mission_id_is_completed_by_title()
    {
        var t = Run([
            "<2026-09-20T18:00:00Z> Added notification \"Contract Accepted: Kill the Pirates Near Yela\"",
            "<2026-09-20T18:45:00Z> Added notification \"Contract Complete: Kill the Pirates near Yela\"",
        ]);
        var c = Assert.Single(t.Snapshot());
        Assert.Equal(ContractStatus.Completed, c.Status);
        Assert.Equal(TimeSpan.FromMinutes(45), c.Duration);
    }

    [Fact]
    public void Metadata_after_notification_without_id_merges_into_same_contract()
    {
        var t = Run([
            "<2026-09-20T18:00:00Z> Added notification \"Contract Accepted: Salvage Job\"",
            $"<2026-09-20T18:00:02Z> missionId [{Id}], generator name [Gen], contract [Salvage_Rec]",
            $"<2026-09-20T18:20:00Z> <MissionEnded> mission_id {Id} - mission_state MISSION_STATE_FAILED",
        ]);
        var c = Assert.Single(t.Snapshot());
        Assert.Equal(Id, c.Id);
        Assert.Equal("Salvage Job", c.Title);
        Assert.Equal(ContractStatus.Failed, c.Status);
    }

    [Fact]
    public void Ocr_snapshot_fills_reward_and_objectives_for_active_contract()
    {
        var t = new ContractTracker(findLocationMentions: text => text.Contains("Everus Harbor") ? ["Everus Harbor"] : []);
        Run(["<2026-09-20T18:00:00Z> Added notification \"Contract Accepted: Covalex Local Delivery\""], t);

        var snap = new MobiGlasSnapshot(true, true,
            [new MobiGlasEntry("Covalex Local Delivery", 12500)],
            new MobiGlasDetail("Covalex Local Delivery", "Covalex Shipping", 12500, "Pick up a package.",
                ["Collect package at Lorville", "Deliver package to Everus Harbor"]));
        Assert.Equal(1, t.ApplyMobiGlas(snap));

        var c = t.Active().Single();
        Assert.Equal(12500, c.Reward);
        Assert.Equal("ocr", c.RewardSource);
        Assert.Equal("Covalex Shipping", c.Contractor);
        Assert.Equal(2, c.Objectives.Count);
        Assert.Contains("Everus Harbor", c.LocationHints);
    }

    [Fact]
    public void Log_reward_is_not_overwritten_by_ocr()
    {
        var t = Run(SessionLog);
        t.Update(Id, c => c.Status = ContractStatus.Active); // simulace
        t.ApplyMobiGlas(new MobiGlasSnapshot(true, true, [new MobiGlasEntry("Covalex Local Delivery", 999)], null));
        Assert.Equal(15000, t.Snapshot().Single().Reward);
    }

    [Fact]
    public void Store_round_trip_preserves_contracts()
    {
        var path = Path.Combine(Path.GetTempPath(), "sct-test-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonContractStore(path);
            Run(SessionLog, new ContractTracker(store));
            var reloaded = new ContractTracker(new JsonContractStore(path));
            var c = Assert.Single(reloaded.Snapshot());
            Assert.Equal(ContractStatus.Completed, c.Status);
            Assert.Equal(15000, c.Reward);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Statistics_compute_reward_per_hour_per_type()
    {
        var baseTime = new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
        var list = new List<Contract>
        {
            new() { Id = "a", Title = "Delivery", ContractRecord = "Del", AcceptedAt = baseTime, FinishedAt = baseTime.AddMinutes(30), Status = ContractStatus.Completed, Reward = 10000 },
            new() { Id = "b", Title = "Delivery", ContractRecord = "Del", AcceptedAt = baseTime, FinishedAt = baseTime.AddMinutes(90), Status = ContractStatus.Completed, Reward = 30000 },
            new() { Id = "c", Title = "Delivery", ContractRecord = "Del", AcceptedAt = baseTime, FinishedAt = baseTime.AddMinutes(5), Status = ContractStatus.Failed },
            new() { Id = "d", Title = "Bounty", AcceptedAt = baseTime, Status = ContractStatus.Active },
        };
        var byType = StatisticsService.ByType(list);
        var del = byType.Single(s => s.Key == "Del");
        Assert.Equal(3, del.Accepted);
        Assert.Equal(2, del.Completed);
        Assert.Equal(1, del.Failed);
        Assert.Equal(40000, del.TotalReward);
        Assert.Equal(20000, del.RewardPerHour!.Value, 3);   // 40k / 2 h
        Assert.Equal(TimeSpan.FromMinutes(60), del.AverageDuration);

        var overall = StatisticsService.Overall(list);
        Assert.Equal(4, overall.Accepted);
        Assert.Equal(1, overall.Active);
        Assert.Equal(20000, overall.RewardPerHour!.Value, 3);
    }
}
