using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Logs;
using ScContractTracker.Core.Models;
using Xunit;

namespace ScContractTracker.Core.Tests;

public class GameLogParserTests
{
    private const string Id = "5b2c7f10-1a2b-4c3d-8e9f-0123456789ab";
    private readonly GameLogParser _en = new(GameLanguagePack.BuiltIn.English);

    [Fact]
    public void Parses_contract_accepted_notification_with_mission_id()
    {
        var line = $"<2026-09-20T18:01:02.345Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Accepted: Covalex Local Delivery: \" [12] to queue. New queue size: 1, MissionId: [{Id}], ObjectiveId: [] [Team_CoreGameplayFeatures][Missions][Comms]";
        var ev = _en.Parse(line);
        Assert.NotNull(ev);
        Assert.Equal(GameEventKind.ContractAccepted, ev!.Kind);
        Assert.Equal("Covalex Local Delivery", ev.Title);
        Assert.Equal(Id, ev.MissionId);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 18, 1, 2, 345, TimeSpan.Zero), ev.Time);
    }

    [Fact]
    public void Ignores_empty_guid_and_strips_markup()
    {
        var line = "<2026-09-20T18:01:02.345Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Complete: <EM4>Bounty</EM4> Hunt ~mission(Location)\" [3] to queue. MissionId: [00000000-0000-0000-0000-000000000000]";
        var ev = _en.Parse(line)!;
        Assert.Equal(GameEventKind.ContractCompleted, ev.Kind);
        Assert.Null(ev.MissionId);
        Assert.Equal("Bounty Hunt", ev.Title);
    }

    [Fact]
    public void Longest_prefix_wins_for_completed()
    {
        var ev = _en.Parse("<2026-09-20T18:01:02Z> Added notification \"Contract Completed: Box Delivery\"")!;
        Assert.Equal(GameEventKind.ContractCompleted, ev.Kind);
        Assert.Equal("Box Delivery", ev.Title);
    }

    [Fact]
    public void Parses_metadata_line()
    {
        var line = $"<2026-09-20T18:01:01.000Z> [Notice] <CContractGenerator::Accept> missionId [{Id}], generator name [Covalex_Delivery_Stanton], contract [Covalex_LocalDelivery_Easy_HUR]";
        var ev = _en.Parse(line)!;
        Assert.Equal(GameEventKind.ContractMetadata, ev.Kind);
        Assert.Equal("Covalex_Delivery_Stanton", ev.GeneratorName);
        Assert.Equal("Covalex_LocalDelivery_Easy_HUR", ev.ContractRecord);
    }

    [Theory]
    [InlineData("MISSION_STATE_COMPLETED", GameEventKind.ContractCompleted)]
    [InlineData("MISSION_STATE_FAILED", GameEventKind.ContractFailed)]
    [InlineData("MISSION_STATE_ABANDONED", GameEventKind.ContractAbandoned)]
    public void Parses_mission_ended(string state, GameEventKind expected)
    {
        var line = $"<2026-09-20T19:00:00.000Z> [Notice] <MissionEnded> Received MissionEnded push message for: mission_id {Id} - mission_state {state}";
        var ev = _en.Parse(line)!;
        Assert.Equal(expected, ev.Kind);
        Assert.Equal(Id, ev.MissionId);
    }

    [Fact]
    public void Parses_end_mission_completion_type()
    {
        var line = $"<2026-09-20T19:00:00.000Z> [Notice] <EndMission> Ending mission for player. MissionId[{Id}] Player[Someone] PlayerId[123] CompletionType[Complete] Reason[...]";
        Assert.Equal(GameEventKind.ContractCompleted, _en.Parse(line)!.Kind);
    }

    [Fact]
    public void Parses_objective_state()
    {
        var line = $"<2026-09-20T18:30:00.000Z> [Notice] <ObjectiveUpserted> mission_id {Id} - objective_id Deliver_1 - state MISSION_OBJECTIVE_STATE_COMPLETED";
        var ev = _en.Parse(line)!;
        Assert.Equal(GameEventKind.ObjectiveCompleted, ev.Kind);
        Assert.Equal("Deliver_1", ev.ObjectiveId);
    }

    [Fact]
    public void Parses_reward_amount()
    {
        var ev = _en.Parse("<2026-09-20T19:00:01.000Z> Added notification \"Awarded: 24,750 aUEC\" [5]")!;
        Assert.Equal(GameEventKind.RewardReceived, ev.Kind);
        Assert.Equal(24750, ev.Amount);
    }

    [Fact]
    public void Parses_location()
    {
        var ev = _en.Parse("<2026-09-20T18:00:00.000Z> [Notice] <RequestLocationInventory> Player[Me] requested inventory for Location[Stanton1_Lorville]")!;
        Assert.Equal(GameEventKind.LocationChanged, ev.Kind);
        Assert.Equal("Stanton1_Lorville", ev.Location);
    }

    [Fact]
    public void German_pack_recognises_german_notification()
    {
        var de = new GameLogParser(GameLanguagePack.BuiltIn.German.WithFallback(GameLanguagePack.BuiltIn.English));
        var ev = de.Parse("<2026-09-20T18:00:00.000Z> Added notification \"Auftrag angenommen: Lieferung nach Lorville\"")!;
        Assert.Equal(GameEventKind.ContractAccepted, ev.Kind);
        Assert.Equal("Lieferung nach Lorville", ev.Title);
        // záloha na angličtinu
        Assert.Equal(GameEventKind.ContractAccepted, de.Parse("<2026-09-20T18:00:00.000Z> Added notification \"Contract Accepted: X Y Z\"")!.Kind);
    }

    [Fact]
    public void Czech_pack_recognises_czech_notification()
    {
        var cs = new GameLogParser(GameLanguagePack.BuiltIn.Czech.WithFallback(GameLanguagePack.BuiltIn.English));
        var ev = cs.Parse("<2026-09-20T18:00:00.000Z> Added notification \"Kontrakt dokončen: Doručení zásilky\"")!;
        Assert.Equal(GameEventKind.ContractCompleted, ev.Kind);
        Assert.Equal("Doručení zásilky", ev.Title);
    }

    [Fact]
    public void Unrelated_lines_return_null()
    {
        Assert.Null(_en.Parse("no timestamp here"));
        Assert.Null(_en.Parse("<2026-09-20T18:00:00.000Z> [Notice] <Something> irrelevant"));
    }
}
