using System.IO.Compression;
using System.Text;
using ScContractTracker.Core.GameData;
using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Logs;
using Xunit;

namespace ScContractTracker.Core.Tests;

public class GameDataTests
{
    [Fact]
    public void P4k_reader_reads_standard_zip_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), "sct-" + Guid.NewGuid() + ".p4k");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var e1 = zip.CreateEntry("Data/Localization/english/global.ini", CompressionLevel.Optimal);
                using (var w = new StreamWriter(e1.Open(), new UTF8Encoding(true)))
                {
                    w.WriteLine("Covalex_Local_title=Covalex Local Delivery");
                    w.WriteLine("Covalex_Local_desc=Deliver the package.\\nQuickly.");
                    w.WriteLine("other_key,P=Other value");
                }
                var e2 = zip.CreateEntry("Data/Game2.dcb", CompressionLevel.NoCompression);
                using (var s = e2.Open()) s.Write(new byte[] { 1, 2, 3, 4 });
            }

            using var p4k = P4kArchive.Open(path, n => n.EndsWith(".ini") || n.EndsWith(".dcb"));
            Assert.Equal(2, p4k.Entries.Count);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, p4k.ReadAllBytes(p4k.Find("Data\\Game2.dcb")!));

            using var sr = new StreamReader(p4k.OpenRead(p4k.Find("Data/Localization/english/global.ini")!));
            var ini = GlobalIni.Parse(sr);
            Assert.Equal("Covalex Local Delivery", ini.Get("Covalex_Local_title"));
            Assert.Equal("Other value", ini.Get("@other_key"));
            Assert.Equal("Covalex_Local_title", ini.FindKeyByValue("Covalex Local Delivery"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Details_provider_uses_disk_override_and_derives_description()
    {
        var root = Path.Combine(Path.GetTempPath(), "sct-game-" + Guid.NewGuid());
        var locDir = Path.Combine(root, "data", "Localization", "english");
        Directory.CreateDirectory(locDir);
        File.WriteAllText(Path.Combine(locDir, "global.ini"),
            "Covalex_Local_title=Covalex Local Delivery\nCovalex_Local_desc=Deliver the package.\n");
        try
        {
            var provider = new P4kContractDetailsProvider(root, "english");
            await provider.InitializeAsync();
            Assert.True(provider.IsReady);

            var byRecord = provider.Lookup("Covalex_Local", null, null)!;
            Assert.Equal("Covalex Local Delivery", byRecord.Title);
            Assert.Equal("Deliver the package.", byRecord.Description);

            var byTitle = provider.Lookup(null, null, "Covalex Local Delivery")!;
            Assert.Equal("Deliver the package.", byTitle.Description);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Language_packs_round_trip_through_json_and_load_from_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sct-lang-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var custom = GameLanguagePack.FromJson(GameLanguagePack.BuiltIn.English.ToJson());
            custom.Code = "fr";
            custom.Name = "Français";
            custom.Notifications.Accepted = ["Contrat accepté"];
            File.WriteAllText(Path.Combine(dir, "fr.json"), custom.ToJson());

            var all = GameLanguagePack.LoadAll(dir);
            Assert.True(all.ContainsKey("fr"));
            Assert.True(all.ContainsKey("cs"));
            Assert.Equal("Contrat accepté", all["fr"].Notifications.Accepted[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Shipped_language_files_are_valid()
    {
        var gameDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Languages", "game");
        var packs = GameLanguagePack.LoadAll(gameDir);
        Assert.Contains("en", packs.Keys);
        Assert.Contains("cs", packs.Keys);
        Assert.Contains("de", packs.Keys);

        var app = AppStrings.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Assets", "Languages", "app"));
        app.SetLanguage("cs");
        Assert.Equal("Jazyk hry", app["Menu.GameLanguage"]);
        app.SetLanguage("de");
        Assert.Equal("Sprache des Spiels", app["Menu.GameLanguage"]);
        Assert.Equal("Missing.Key", app["Missing.Key"]);
    }

    [Fact]
    public void Watcher_reads_appended_lines_and_detects_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "sct-log-" + Guid.NewGuid() + ".log");
        File.WriteAllText(path, "line1\nline2\npart");
        try
        {
            var lines = new List<string>();
            var restarted = 0;
            using var w = new GameLogWatcher(path);
            w.LineRead += lines.Add;
            w.LogRestarted += () => restarted++;

            w.Poll();
            Assert.Equal(new[] { "line1", "line2" }, lines);

            File.AppendAllText(path, "ial\r\nline4\n");
            w.Poll();
            Assert.Equal(new[] { "line1", "line2", "partial", "line4" }, lines);

            File.WriteAllText(path, "new\n");
            w.Poll();
            Assert.Equal(1, restarted);
            Assert.Equal("new", lines[^1]);
        }
        finally { File.Delete(path); }
    }
}
