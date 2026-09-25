using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Logs;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Ocr;
using ScContractTracker.Core.Tracking;
using Xunit;

namespace ScContractTracker.Core.Tests;

/// <summary>Testy nad skutečnými daty ze Star Citizenu 4.x (anonymizovaný Game.log, diagnostika OCR).</summary>
public class RealDataTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static ContractTracker LoadLog(string name, Func<string, IEnumerable<string>>? mentions = null, bool withoutEnd = false)
    {
        var parser = new GameLogParser(GameLanguagePack.BuiltIn.English);
        var t = new ContractTracker(findLocationMentions: mentions);
        t.ApplyMany(File.ReadAllLines(FixturePath(name))
            .Where(l => !withoutEnd || !l.Contains("<EndMission>") && !l.Contains("<PlayerLeft>"))
            .Select(parser.Parse).Where(e => e is not null)!);
        return t;
    }

    [Fact]
    public void Abandon_button_ends_contracts_and_titles_are_clean()
    {
        var t = LoadLog("gamelog_4x_accepted_then_logout.txt");
        var all = t.Snapshot();
        Assert.Equal(2, all.Count);
        // „EndMission … CompletionType[Abandon] Reason[Player left]“ = tlačítko ABANDON ve hře
        Assert.All(all, c => Assert.Equal(ContractStatus.Abandoned, c.Status));
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 10, 58, 40, 418, TimeSpan.Zero),
            all.Single(c => c.Id == "1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06").FinishedAt);

        var refuel = all.Single(c => c.Id == "dc88a6f8-7913-4e91-9956-0930481ae7a9");
        Assert.Equal("UWC'S HIRING REFUELERS", refuel.Title);
        Assert.Equal("Ship_Refueling_Intro_Stanton", refuel.ContractRecord);
        Assert.Equal("66f31bca-1961-47f7-a4af-f7392271b15c", refuel.ContractDefinitionId);

        var courier = all.Single(c => c.Id == "1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06");
        Assert.Equal("ENTRY LVL. COURIER NEEDED IN STANTON", courier.Title);
        Assert.Equal("FTL_Courier_Stanton_Food_Rank0", courier.ContractRecord);
    }

    [Fact]
    public void Objective_text_and_marker_come_from_log()
    {
        var t = LoadLog("gamelog_4x_accepted_then_logout.txt");
        var refuel = t.Snapshot().Single(c => c.Id == "dc88a6f8-7913-4e91-9956-0930481ae7a9");

        // Skryté interní fáze (phase_…, flags=Hidden) se jako cíle neobjeví
        var o = Assert.Single(refuel.Objectives);
        Assert.Equal("17de6488-b26b-0543-b328-405540f1d280", o.Id);
        Assert.Equal("Go to a Hurston Weapons Testing Site above Arial", o.Text);
        Assert.Equal("log", o.Source);
        Assert.Equal("832905918342", o.ZoneHostId);
        Assert.Equal(-516447.664772, o.Position![0], 3);

        var courier = t.Snapshot().Single(c => c.Id == "1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06");
        Assert.Equal(new[] { "pickup", "dropoff" }, courier.Objectives.Select(x => x.Kind));
    }

    [Fact]
    public void Ocr_objectives_merge_wrapped_lines_and_log_text_wins()
    {
        var snap = new MobiGlasParser(GameLanguagePack.BuiltIn.English)
            .Parse(MobiGlasParserTests.LoadFixture("ocr_accepted_courier_3440x1440.txt"));
        Assert.True(snap.IsAcceptedTab);

        var d = snap.Detail!;
        Assert.Equal(37250, d.Reward);
        Assert.Equal("Ftl Courier", d.Contractor);
        Assert.Equal(new[]
        {
            "Deliver Food Supplies to HDMS-Edmond.",
            "Collect Food Supplies from Covalex Distribution Center SIDC06 on Hurston."
        }, d.Objectives);

        var entry = Assert.Single(snap.Entries);
        Assert.Equal("ENTRY LVL. COURIER NEEDED IN STANTON", entry.Title);
        Assert.Equal(37000, entry.Reward);

        // Spojení s logem: odměna a cíle se přiřadí ke správnému (ještě aktivnímu) kontraktu
        var t = LoadLog("gamelog_4x_accepted_then_logout.txt", withoutEnd: true);
        t.ApplyMobiGlas(snap);
        var courier = t.Snapshot().Single(c => c.Id == "1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06");
        Assert.Equal(37250, courier.Reward);
        Assert.Contains(courier.Objectives, o => o.Text.StartsWith("Collect Food Supplies") && o.Source == "ocr");

        // Pozdější „New Objective“ z logu nahradí text z OCR (S1DC06 místo SIDC06)
        var parser = new GameLogParser(GameLanguagePack.BuiltIn.English);
        t.Apply(parser.Parse("<2026-09-24T11:05:00.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"New Objective: Collect Food Supplies from Covalex Distribution Center S1DC06 on Hurston: \" [9] to queue. New queue size: 1, MissionId: [1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06], ObjectiveId: [pickup_f1fc9593-e30c-4303-8a4e-7e1509141107_0] [Team_CoreGameplayFeatures][Missions][Comms]")!);
        courier = t.Snapshot().Single(c => c.Id == "1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06");
        var pickup = courier.Objectives.Single(o => o.Id.StartsWith("pickup_"));
        Assert.Equal("Collect Food Supplies from Covalex Distribution Center S1DC06 on Hurston", pickup.Text);
        Assert.Equal("log", pickup.Source);
        Assert.Equal(1, courier.Objectives.Count(o => o.Text.StartsWith("Collect Food Supplies")));
    }

    [Theory]
    [InlineData("ENTRY LVL. COURIER NEEDED IN STANTON noo-soo REPI", "ENTRY LVL. COURIER NEEDED IN STANTON")]
    [InlineData("ENTRY LVL. COURIER NEEDED IN STANTON [BP] [100-500 REP]", "ENTRY LVL. COURIER NEEDED IN STANTON")]
    [InlineData("UWC•S HIRING REFUELERS (200 REPI", "UWC'S HIRING REFUELERS")]
    public void Ocr_title_cleanup(string raw, string expected) =>
        Assert.Equal(expected, MobiGlasParser.CleanTitle(raw));
}

public class OcrCursorTests
{
    [Fact]
    public void Accepted_tab_is_detected_when_the_cursor_covers_the_tab_counter()
    {
        // Reálná diagnostika 24. 9.: kurzor myši přes záložku → OCR přečetlo „ACCEPTED (1/1“
        var snap = new MobiGlasParser(GameLanguagePack.BuiltIn.English)
            .Parse(MobiGlasParserTests.LoadFixture("ocr_accepted_uwc_cursor_3440x1440.txt"));
        Assert.True(snap.IsAcceptedTab);
        Assert.Equal(19750, snap.Detail!.Reward);
        Assert.Equal("United Wayfarers Club", snap.Detail.Contractor);
        Assert.StartsWith("Hey!", snap.Detail.Description);
        Assert.Contains(snap.Detail.Objectives, o => o.Contains("Hurston Weapons Testing Site above Magda"));
        Assert.Equal("UWC'S HIRING REFUELERS", Assert.Single(snap.Entries).Title);
    }

    [Fact]
    public void Abandon_button_with_other_tabs_is_enough_when_accepted_label_is_hidden()
    {
        var lines = MobiGlasParserTests.LoadFixture("ocr_accepted_uwc_cursor_3440x1440.txt")
            .Where(l => !l.Text.StartsWith("ACCEPTED")).ToList();
        var snap = new MobiGlasParser(GameLanguagePack.BuiltIn.English).Parse(lines);
        Assert.True(snap.IsAcceptedTab);
        Assert.Equal(19750, snap.Detail!.Reward);
    }
}

public class AbandonTests
{
    [Fact]
    public void Abandon_in_game_is_abandoned_but_quitting_the_game_keeps_contract_active()
    {
        var p = new GameLogParser(GameLanguagePack.BuiltIn.English);
        var t = new ContractTracker();
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gamelog_4x_abandon_then_quit.txt"));
        t.ApplyMany(lines.Select(p.Parse).Where(e => e is not null)!);
        var all = t.Snapshot();

        var refuel = all.Single(c => c.Id == "46c91224-f0d8-44b4-9c77-4f15c76225a4");
        Assert.Equal(ContractStatus.Abandoned, refuel.Status);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 12, 21, 48, 163, TimeSpan.Zero), refuel.FinishedAt);
        Assert.Equal(TimeSpan.FromSeconds(29.448), refuel.Duration!.Value);
        Assert.Null(refuel.RewardPerHour);

        Assert.Null(refuel.EndReason);

        // Odhlášení / ukončení hry: EndMission se nepíše, ale hra zruší všechny přijaté kontrakty
        var courier = all.Single(c => c.Title == "ENTRY LVL. COURIER NEEDED IN STANTON");
        Assert.Equal(ContractStatus.Abandoned, courier.Status);
        Assert.Equal("logout", courier.EndReason);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 12, 25, 44, 744, TimeSpan.Zero), courier.FinishedAt);
    }

    [Fact]
    public void Crash_keeps_contracts_and_game_start_disconnect_is_not_a_logout()
    {
        var p = new GameLogParser(GameLanguagePack.BuiltIn.English);
        var t = new ContractTracker();
        string[] lines =
        [
            "<2026-09-18T16:52:20.922Z> Log started on Fri Sep 18 16:52:20 2026",
            "<2026-09-18T16:55:00.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Accepted:  Ship In Distress <EM4>[300 Rep]</EM4>: \" [116] to queue. New queue size: 1, MissionId: [4a05dea7-2a74-4b31-9642-37d2828e2f2f], ObjectiveId: []",
            // log končí náhle (pád) – další relace začíná „Nub destroyed“ odpojením z úvodní obrazovky
            "<2026-09-18T19:07:13.467Z> Log started on Fri Sep 18 19:07:13 2026",
            "<2026-09-18T19:07:39.000Z> [Notice] <Channel Disconnected> cause=30010 reason=\"Nub destroyed\" frame=929 isRemote=0 viewState=eCVS_InGame map=\"megamap\" gamerules=\"SC_Frontend\" hostType=\"Replicant\"",
        ];
        t.ApplyMany(lines.Select(p.Parse).Where(e => e is not null)!);
        Assert.Equal(ContractStatus.Active, t.Snapshot().Single().Status);

        // Zavření okna („User closed the application“ bez odpojení) kontrakt neruší
        Assert.Null(p.Parse("<2026-09-18T21:00:00.000Z> [Notice] <SystemQuit> CSystem::Quit invoked with - cause=30016, reason=User closed the application, exitCode=0"));

        // Odhlášení z menu ano
        t.Apply(p.Parse("<2026-09-18T21:10:00.000Z> [Notice] <Channel Disconnected> cause=30016 reason=\"Remote Disconnect - Player requested disconnect\" frame=28407 isRemote=1")!);
        var c = t.Snapshot().Single();
        Assert.Equal(ContractStatus.Abandoned, c.Status);
        Assert.Equal("logout", c.EndReason);

        // …a kdyby ho hra po přihlášení přesto znovu ohlásila, vrátí se mezi aktivní
        t.Apply(p.Parse("<2026-09-18T21:30:00.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Accepted:  Ship In Distress <EM4>[300 Rep]</EM4>: \" [5] to queue. New queue size: 1, MissionId: [4a05dea7-2a74-4b31-9642-37d2828e2f2f], ObjectiveId: []")!);
        c = t.Snapshot().Single();
        Assert.Equal(ContractStatus.Active, c.Status);
        Assert.Null(c.EndReason);
    }
}

public class ReactivationTests
{
    [Fact]
    public void Contract_wrongly_abandoned_on_logout_is_reactivated_by_later_marker()
    {
        var parser = new GameLogParser(GameLanguagePack.BuiltIn.English);
        var t = new ContractTracker();
        t.Apply(parser.Parse("<2026-09-24T10:56:03.956Z> Added notification \"Contract Accepted:  COURIER JOB: \" [5] to queue. MissionId: [1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06], ObjectiveId: []")!);
        // simulace stavu uloženého starší verzí aplikace
        t.Update("1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06", c => { c.Status = ContractStatus.Abandoned; c.FinishedAt = new DateTimeOffset(2026, 9, 24, 10, 58, 40, TimeSpan.Zero); });

        t.Apply(parser.Parse("<2026-09-24T12:00:00.000Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: missionId [1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06], generator name [FTL_Courier], contract [FTL_Courier_Stanton_Food_Rank0], contractDefinitionId[8419c7de-5b76-4e15-af19-6e1e54f13989], objectiveId [pickup_x_0], markerEntityId [1], zoneHostId [2], position [x: 1.0, y: 2.0, z: 3.0]")!);

        var c = t.Snapshot().Single();
        Assert.Equal(ContractStatus.Active, c.Status);
        Assert.Null(c.FinishedAt);
    }
}

public class RewardsFromLogTests
{
    private static ContractTracker Load()
    {
        // Parser v angličtině musí zvládnout i české notifikace (jazyk hry se mezi relacemi měnil)
        var parser = new GameLogParser(GameLanguagePack.BuiltIn.English);
        var t = new ContractTracker();
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gamelog_4x_rewards_en_cs.txt");
        t.ApplyMany(File.ReadAllLines(path).Select(parser.Parse).Where(e => e is not null)!);
        return t;
    }

    [Fact]
    public void Tags_reputation_blueprint_items_and_giver_are_read_from_log()
    {
        var c = Load().Snapshot().Single(x => x.Id == "4a05dea7-2a74-4b31-9642-37d2828e2f2f");
        Assert.Equal("Ship In Distress", c.Title);
        Assert.Equal(ContractStatus.Completed, c.Status);
        Assert.True(c.OffersBlueprint);
        Assert.Equal(300, c.RepMin);
        Assert.Equal(300, c.RepMax);
        Assert.Equal(new[] { "S00 Hofstede" }, c.ReceivedBlueprints);
        Assert.Contains("Falston Jumpsuit", c.ItemRewards);
        Assert.Contains("People's Alliance Hat", c.ItemRewards);
        Assert.Equal("People Alliance", c.Organization);
        Assert.Equal("Recco Battaglia", c.MissionGiver);
        Assert.Equal("RBM Repeatable", c.MissionType);
        Assert.Equal("Battaglia_BoardShip_Intro", c.ContractRecord);
        // opětovné „Contract Accepted“ po znovupřipojení nesmí posunout čas přijetí
        Assert.True(Math.Abs((c.AcceptedAt - new DateTimeOffset(2026, 9, 18, 20, 53, 38, TimeSpan.Zero)).TotalSeconds) < 1);
        Assert.Contains(c.Objectives, o => o.Text == "Repair The Power Plant" && o.Source == "log");
    }

    [Fact]
    public void Reward_log_keeps_every_blueprint_and_item_with_its_contract_and_survives_replay()
    {
        var parser = new GameLogParser(GameLanguagePack.BuiltIn.English);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gamelog_4x_rewards_en_cs.txt");
        var t = new ContractTracker();
        var events = File.ReadAllLines(path).Select(parser.Parse).Where(e => e is not null).ToList();
        t.ApplyMany(events!);
        t.ApplyMany(events!); // opakované čtení stejného logu nesmí vytvořit duplicity

        var rewards = t.Rewards();
        var hofstede = rewards.Single(r => r.Kind == RewardKind.Blueprint && r.Name == "S00 Hofstede");
        Assert.Equal("4a05dea7-2a74-4b31-9642-37d2828e2f2f", hofstede.ContractId);
        Assert.Contains(rewards, r => r.Kind == RewardKind.Item && r.Name == "Falston Jumpsuit");
        Assert.Equal(rewards.Count, rewards.Select(r => (r.Kind, r.Name, r.Time)).Distinct().Count());

        // uložení a načtení (contracts.json verze 2)
        var file = Path.Combine(Path.GetTempPath(), "sct-rewards-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new ScContractTracker.Core.Storage.JsonContractStore(file);
            store.Save(t.Snapshot(), t.Rewards());
            var t2 = new ContractTracker(store);
            Assert.Equal(rewards.Count, t2.Rewards().Count);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Czech_notifications_are_recognised_with_english_game_language()
    {
        var c = Load().Snapshot().Single(x => x.Id == "76394816-bae2-43b6-8284-41b27c73a6b8");
        Assert.Equal("DEAD SAINTS – HODNOST Zacatecnik – TRANSPORT NÁKLADU Medium", c.Title);
        Assert.Equal(ContractStatus.Completed, c.Status);
        Assert.Equal(3, c.Objectives.Count(o => o.Text.StartsWith("Dovezte") && o.Source == "log"));
        Assert.All(c.Objectives.Where(o => o.Id.StartsWith("dropoff_")), o => Assert.Equal("dropoff", o.Kind));
    }

    [Theory]
    [InlineData("300 Rep", 300, 300)]
    [InlineData("100–500 Rep", 100, 500)]
    [InlineData("2,000 Rep", 2000, 2000)]
    public void Parses_rep_tags(string tag, int min, int max) =>
        Assert.Equal((min, max), GameLogParser.ParseRep([tag])!.Value);
}

public class OcrExtrasTests
{
    [Fact]
    public void Potential_blueprints_and_mission_details_from_description()
    {
        var snap = new MobiGlasParser(GameLanguagePack.BuiltIn.English)
            .Parse(MobiGlasParserTests.LoadFixture("ocr_accepted_courier_3440x1440.txt"));
        var d = snap.Detail!;
        Assert.Equal(new[] { "Defiant", "Colossus", "Endurance", "Huracan", "Sedulity", "Agni" }, d.PotentialBlueprints);
        Assert.Equal("Chain", d.MissionInfo!["Mission Type"]);
        Assert.Equal("2/7", d.MissionInfo["Combat"]);
        Assert.Equal("3/7", d.MissionInfo["Complexity"]);
        Assert.Equal("2/7", d.MissionInfo["Risk"]);
        Assert.Equal("3/7", d.MissionInfo["Knowledge"]);
    }
}

public class SessionTests
{
    [Fact]
    public void Same_contract_accepted_again_in_new_session_retires_the_old_one()
    {
        var p = new GameLogParser(GameLanguagePack.BuiltIn.English);
        var t = new ContractTracker();
        string[] lines =
        [
            "<2026-09-24T10:32:50.698Z> Log started on Thu Sep 24 10:32:50 2026",
            "<2026-09-24T10:34:14.028Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Accepted:  UWC'S HIRING REFUELERS <EM4>[200 Rep]</EM4>: \" [3] to queue. New queue size: 1, MissionId: [89e86935-785f-4f64-90fa-3f1bb53742e9], ObjectiveId: []",
            "<2026-09-24T10:51:00.000Z> Log started on Thu Sep 24 10:51:00 2026",
            "<2026-09-24T10:55:32.835Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Accepted:  UWC'S HIRING REFUELERS <EM4>[200 Rep]</EM4>: \" [3] to queue. New queue size: 1, MissionId: [dc88a6f8-7913-4e91-9956-0930481ae7a9], ObjectiveId: []",
            "<2026-09-24T10:56:03.956Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Contract Accepted:  OTHER JOB: \" [5] to queue. New queue size: 1, MissionId: [1c0a1ff7-9e43-4813-ba9b-f6be93ea3a06], ObjectiveId: []",
        ];
        t.ApplyMany(lines.Select(p.Parse).Where(e => e is not null)!);

        var all = t.Snapshot();
        Assert.Equal(ContractStatus.Abandoned, all.Single(c => c.Id.StartsWith("89e86935")).Status);
        Assert.Equal(ContractStatus.Active, all.Single(c => c.Id.StartsWith("dc88a6f8")).Status);
        Assert.False(t.IsStale(all.Single(c => c.Id.StartsWith("dc88a6f8"))));
    }
}
