using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Models;
using ScContractTracker.Core.Navigation;
using ScContractTracker.Core.Ocr;
using ScContractTracker.Core.Text;
using Xunit;

namespace ScContractTracker.Core.Tests;

public class NavigationTests
{
    private static readonly LocationDatabase Db =
        LocationDatabase.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "locations.json"));

    [Theory]
    [InlineData("Stanton1_Lorville", "Lorville")]
    [InlineData("Everus Harbor", "Everus Harbor")]
    [InlineData("Stanton2c", "Yela")]
    [InlineData("HUR-L1", "HUR-L1 Green Glade Station")]
    [InlineData("Port Tressler", "Port Tressler")]
    public void Resolves_names(string raw, string expected) =>
        Assert.Equal(expected, Db.Resolve(raw)?.Name);

    [Fact]
    public void Finds_mentions_and_prefers_specific_places()
    {
        var found = Db.FindMentions("Collect the package at Lorville on Hurston and deliver it to Port Tressler.").ToList();
        Assert.Contains("Lorville", found);
        Assert.Contains("Port Tressler", found);
        Assert.DoesNotContain("Hurston", found);
    }

    [Fact]
    public void Nearest_orders_by_tree_distance_without_coordinates()
    {
        var nav = new NavigationService(Db);
        var c = new Contract
        {
            Title = "Test",
            Objectives =
            [
                new ContractObjective { Text = "Go to Port Tressler", Location = "Port Tressler" },
                new ContractObjective { Text = "Collect box at Everus Harbor", Location = "Everus Harbor" },
                new ContractObjective { Text = "Visit Arial", Location = "Arial" },
                new ContractObjective { Text = "Visit Ruin Station", Location = "Ruin Station" },
            ]
        };
        var result = nav.Nearest(c, "Stanton1_Lorville", null);
        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "Everus Harbor", "Arial", "Port Tressler" }, result.Select(r => r.Location.Name));
        Assert.Equal("Collect box at Everus Harbor", result[0].Task);
    }

    [Fact]
    public void Nearest_uses_coordinates_when_available()
    {
        var db = new LocationDatabase(
        [
            new LocationNode { Id = "sys", Name = "Sys", Type = "system" },
            new LocationNode { Id = "a", Name = "Alpha Station", Parent = "sys", Position = [0, 0, 0] },
            new LocationNode { Id = "b", Name = "Beta Station", Parent = "sys", Position = [1000, 0, 0] },
        ]);
        var nav = new NavigationService(db);
        var c = new Contract { LocationHints = ["Alpha Station", "Beta Station"] };
        var r = nav.Nearest(c, null, new Vector3d(900, 0, 0));
        Assert.Equal("Beta Station", r[0].Location.Name);
        Assert.Equal(100, r[0].DistanceMeters!.Value, 3);
    }

    [Fact]
    public void Parses_showlocation_clipboard()
    {
        Assert.True(ShowLocationParser.TryParse("Coordinates: x:-18930539540.3 y:-2610158765.2 z:12.5", out var p));
        Assert.Equal(-18930539540.3, p.X, 1);
        Assert.Equal(12.5, p.Z, 3);
        Assert.False(ShowLocationParser.TryParse("hello", out _));
    }

    [Fact]
    public void Fuzzy_similarity_tolerates_ocr_noise()
    {
        Assert.True(Fuzzy.Similarity("Covalex Local Delivery", "Cova1ex Local De1ivery") > 0.85);
        Assert.True(Fuzzy.Similarity("Doručení zásilky", "Doruceni zasilky") == 1);
        Assert.True(Fuzzy.Similarity("Bounty Hunt", "Salvage Contract") < 0.5);
    }
}

public class MobiGlasParserTests
{
    private static OcrLine L(string text, double x, double y) => new(text, x, y, 0.2, 0.02);

    /// <summary>Načte diagnostiku OCR uloženou aplikací (řádky „x; y; w; h | text“).</summary>
    internal static List<OcrLine> LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var list = new List<OcrLine>();
        foreach (var line in File.ReadAllLines(path))
        {
            var bar = line.IndexOf(" | ", StringComparison.Ordinal);
            if (bar < 0) continue;
            var nums = line[..bar].Split(';', StringSplitOptions.TrimEntries);
            if (nums.Length != 4) continue;
            var v = new double[4];
            var ok = true;
            for (var i = 0; i < 4; i++)
                ok &= double.TryParse(nums[i].Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v[i]);
            if (!ok) continue; // hlavička sekce
            list.Add(new OcrLine(line[(bar + 3)..], v[0], v[1], v[2], v[3]));
        }
        return list;
    }

    [Fact]
    public void Real_4x_accepted_screen_3440x1440_is_parsed()
    {
        // Skutečný snímek: UWC's Hiring Refuelers, odměna ¤ 19,750 (OCR: „Ä 19.750“), v seznamu „19k“
        var lines = LoadFixture("ocr_accepted_uwc_3440x1440.txt");
        var p = new MobiGlasParser(GameLanguagePack.BuiltIn.English);
        var snap = p.Parse(lines);

        Assert.True(snap.IsContractsApp);
        Assert.True(snap.IsAcceptedTab); // tlačítko ABANDON

        var entry = Assert.Single(snap.Entries);
        Assert.Equal("UWC'S HIRING REFUELERS", entry.Title);
        Assert.Equal(19000, entry.Reward);
        Assert.True(entry.Approximate);

        var d = snap.Detail!;
        Assert.Equal(19750, d.Reward);
        Assert.Equal("United Wayfarers Club", d.Contractor);
        Assert.Equal("UWC'S HIRING REFUELE", d.Title); // konec názvu zakryl odlesk
        Assert.Equal(new[] { "Go to Asteroid Mining Base" }, d.Objectives);
        Assert.StartsWith("Hey! DD from the United Wayfarers Club here.", d.Description);
        Assert.DoesNotContain("PRIMARY", d.Description);
    }

    [Fact]
    public void Real_screen_updates_tracked_contract_with_exact_reward()
    {
        var t = new ScContractTracker.Core.Tracking.ContractTracker();
        var parser = new ScContractTracker.Core.Logs.GameLogParser(GameLanguagePack.BuiltIn.English);
        t.Apply(parser.Parse("<2026-09-24T10:30:00Z> Added notification \"Contract Accepted: UWC's Hiring Refuelers [200 Rep]\"")!);

        var snap = new MobiGlasParser(GameLanguagePack.BuiltIn.English).Parse(LoadFixture("ocr_accepted_uwc_3440x1440.txt"));
        t.ApplyMobiGlas(snap);

        var c = t.Active().Single();
        Assert.Equal(19750, c.Reward);
        Assert.Equal("ocr", c.RewardSource);
        Assert.Equal("United Wayfarers Club", c.Contractor);
    }

    [Fact]
    public void Background_of_active_tab_is_detected_without_abandon_button()
    {
        var lines = LoadFixture("ocr_accepted_uwc_3440x1440.txt").Where(l => l.Text != "ABANDON").ToList();
        var p = new MobiGlasParser(GameLanguagePack.BuiltIn.English);
        // Zelená výplň aktivní záložky ACCEPTED (x 0.428–0.506), bílý křížek vpravo nesmí vadit
        double Lum(OcrLine l) => l.Y > 0.09 && l.Y < 0.13 && l.X > 0.425 && l.X < 0.51 ? 0.35
            : l.X > 0.79 && l.X < 0.82 ? 0.9 : 0.08;
        Assert.True(p.Parse(lines, Lum).IsAcceptedTab);

        // Aktivní je OFFERS → nejde o přijaté kontrakty
        double LumOffers(OcrLine l) => l.Y > 0.09 && l.Y < 0.13 && l.X > 0.37 && l.X < 0.425 ? 0.35 : 0.08;
        Assert.False(p.Parse(lines, LumOffers).IsAcceptedTab);
    }

    [Theory]
    [InlineData("Ä 19.750", 19750, false)]
    [InlineData("¤ 19,750", 19750, false)]
    [InlineData("19k", 19000, true)]
    [InlineData("1.5M", 1500000, true)]
    [InlineData("12 500", 12500, false)]
    public void Parses_amount_variants(string text, long expected, bool approx)
    {
        Assert.True(MobiGlasParser.TryParseAmount(text, out var v, out var a, out _));
        Assert.Equal(expected, v);
        Assert.Equal(approx, a);
    }

    [Fact]
    public void Clean_title_removes_rep_tag_but_keeps_words_ending_with_rep()
    {
        Assert.Equal("UWC'S HIRING REFUELERS", MobiGlasParser.CleanTitle("UWC•S HIRING REFUELERS (200 REPI"));
        Assert.Equal("Kill Pirates", MobiGlasParser.CleanTitle("Kill Pirates [300 Rep]"));
        Assert.Equal("Cargo Prep", MobiGlasParser.CleanTitle("Cargo Prep"));
    }

    [Fact]
    public void Synthetic_16x9_layout_with_list_rewards_on_title_row()
    {
        var p = new MobiGlasParser(GameLanguagePack.BuiltIn.English);
        List<OcrLine> lines =
        [
            L("CONTRACTS", 0.05, 0.95),
            L("OFFERS", 0.40, 0.10), L("ACCEPTED (2/10)", 0.48, 0.10), L("HISTORY", 0.60, 0.10),
            L("Covalex Local Delivery", 0.10, 0.20), new("¤ 12,500", 0.30, 0.20, 0.05, 0.02),
            L("Kill Pirates near Yela", 0.10, 0.28), new("45k", 0.30, 0.28, 0.03, 0.02),
            new("Covalex Local Delivery", 0.42, 0.18, 0.3, 0.04),
            new("Reward", 0.75, 0.18, 0.05, 0.02), new("¤ 12,500", 0.88, 0.18, 0.06, 0.02),
            new("Contracted By", 0.75, 0.22, 0.08, 0.02), new("Covalex Shipping", 0.86, 0.22, 0.1, 0.02),
            new("DETAILS", 0.42, 0.30, 0.1, 0.02), new("PRIMARY OBJECTIVES", 0.70, 0.30, 0.15, 0.02),
            new("We need a package delivered quickly.", 0.42, 0.34, 0.25, 0.02),
            new("◇ Collect package at Lorville", 0.70, 0.34, 0.2, 0.02),
            new("O Deliver package to Everus Harbor", 0.70, 0.37, 0.2, 0.02),
            new("ABANDON", 0.42, 0.80, 0.06, 0.02), new("TRACK", 0.85, 0.80, 0.05, 0.02),
        ];
        var snap = p.Parse(lines);
        Assert.True(snap.IsAcceptedTab);
        Assert.Contains(snap.Entries, e => e.Title == "Covalex Local Delivery" && e.Reward == 12500 && !e.Approximate);
        Assert.Contains(snap.Entries, e => e.Title == "Kill Pirates near Yela" && e.Reward == 45000 && e.Approximate);
        var d = snap.Detail!;
        Assert.Equal("Covalex Local Delivery", d.Title);
        Assert.Equal("Covalex Shipping", d.Contractor);
        Assert.Equal(12500, d.Reward);
        Assert.Equal("We need a package delivered quickly.", d.Description);
        Assert.Equal(new[] { "Collect package at Lorville", "Deliver package to Everus Harbor" }, d.Objectives);
    }

    [Fact]
    public void Non_mobiglas_screen_is_empty()
    {
        var p = new MobiGlasParser(GameLanguagePack.BuiltIn.English);
        var snap = p.Parse([L("Quantum drive spooling", 0.4, 0.5)]);
        Assert.False(snap.IsContractsApp);
    }
}
