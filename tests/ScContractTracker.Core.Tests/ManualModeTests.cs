using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Logs;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Ocr;
using ScContractTracker.Core.Tracking;
using Xunit;

namespace ScContractTracker.Core.Tests;

public class ManualModeTests
{
    private const string Id = "5b2c7f10-1a2b-4c3d-8e9f-0123456789ab";
    private static readonly GameLogParser P = new(GameLanguagePack.BuiltIn.English);

    private static readonly string[] Log =
    [
        $"<2026-09-20T18:00:10.500Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Accepted: Covalex Local Delivery\" [1] to queue. MissionId: [{Id}]",
        $"<2026-09-20T18:30:10.000Z> [Notice] <MissionEnded> mission_id {Id} - mission_state MISSION_STATE_COMPLETED",
        "<2026-09-20T18:30:11.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Received Blueprint: S00 Hofstede: \" [5] to queue.",
        "<2026-09-20T19:00:00.000Z> [Notice] <Channel Disconnected> cause=30016 reason=\"Remote Disconnect - Player requested disconnect\" frame=1 isRemote=1",
    ];

    [Fact]
    public void Manual_mode_keeps_contract_active_and_remembers_log_end()
    {
        var t = new ContractTracker { DefaultCompletionMode = CompletionMode.Manual };
        t.ApplyMany(Log.Select(P.Parse).Where(e => e is not null)!);
        var c = t.Snapshot().Single();
        Assert.Equal(CompletionMode.Manual, c.CompletionMode);
        Assert.Equal(ContractStatus.Active, c.Status);
        Assert.Equal(ContractStatus.Completed, c.LogFinishStatus);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 18, 30, 10, TimeSpan.Zero), c.LogFinishedAt);
        Assert.Contains("S00 Hofstede", c.ReceivedBlueprints);
    }

    [Fact]
    public void Auto_mode_is_default_and_ends_contract()
    {
        var t = new ContractTracker();
        t.ApplyMany(Log.Select(P.Parse).Where(e => e is not null)!);
        Assert.Equal(ContractStatus.Completed, t.Snapshot().Single().Status);
    }

    [Fact]
    public void Manual_edits_are_not_overwritten_and_fee_reduces_net_reward()
    {
        var t = new ContractTracker();
        t.ApplyMany(Log.Take(1).Select(P.Parse).Where(e => e is not null)!);
        t.Update(Id, c =>
        {
            c.Title = "My name";
            c.ManualFields.Add("title");
            c.CategoryOverride = ContractCategory.Mining;
            c.Reward = 50_000; c.RewardSource = "manual";
            c.Fee = 10_000; c.FeeSource = "manual";
        });
        // log znovu ohlásí kontrakt (relog) – ruční název zůstane
        t.ApplyMany(Log.Take(2).Select(P.Parse).Where(e => e is not null)!);
        var c = t.Snapshot().Single();
        Assert.Equal("My name", c.Title);
        Assert.Equal(ContractCategory.Mining, ContractClassifier.Category(c));
        Assert.Equal(40_000, c.NetReward);
        Assert.True(Math.Abs(c.RewardPerHour!.Value - 80_000) < 100); // 40k za ~0,5 h
        var o = ScContractTracker.Core.Stats.StatisticsService.Overall(t.Snapshot());
        Assert.Equal(40_000, o.TotalReward);
        Assert.Equal(10_000, o.TotalFees);
    }

    [Fact]
    public void Manually_added_contract_gets_an_id()
    {
        var t = new ContractTracker();
        var c = t.AddManual(new Contract { Title = "Hand made", Reward = 1000 });
        Assert.StartsWith("manual:", c.Id);
        Assert.Equal(ContractStatus.Active, t.Snapshot().Single().Status);
    }

    [Fact]
    public void Fee_is_read_from_detail_header_but_not_from_description()
    {
        var lines = MobiGlasParserTests.LoadFixture("ocr_accepted_uwc_cursor_3440x1440.txt").ToList();
        // přidáme řádek „Buy-In ¤ 2,500“ pod „Contracted By“ (hlavička detailu)
        lines.Add(new OcrLine("Buy-In", 0.632, 0.245, 0.030, 0.015));
        lines.Add(new OcrLine("Ä 2,500", 0.775, 0.246, 0.025, 0.013));
        var snap = new MobiGlasParser(GameLanguagePack.BuiltIn.English).Parse(lines);
        Assert.Equal(2500, snap.Detail!.Fee);
        Assert.Equal(19750, snap.Detail.Reward);

        // „service fee listed above“ v popisu poplatek nevytvoří
        var plain = new MobiGlasParser(GameLanguagePack.BuiltIn.English)
            .Parse(MobiGlasParserTests.LoadFixture("ocr_accepted_uwc_cursor_3440x1440.txt"));
        Assert.Null(plain.Detail!.Fee);
        Assert.Contains("service fee", plain.Detail.Description!);
    }
}
