using System.Globalization;
using System.Text.RegularExpressions;
using ScContractTracker.Core.Localization;
using ScContractTracker.Core.Logs;
using ScContractTracker.Core.Text;

namespace ScContractTracker.Core.Ocr;

/// <summary>Řádek textu z OCR. Souřadnice jsou normalizované 0..1 vůči zachycenému oknu.</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height)
{
    public double CenterY => Y + Height / 2;
    public double Right => X + Width;

    /// <summary>Převod souřadnic z výřezu (cx, cy, cw, ch) zpět do celého snímku.</summary>
    public OcrLine FromCrop(double cx, double cy, double cw, double ch) =>
        this with { X = cx + X * cw, Y = cy + Y * ch, Width = Width * cw, Height = Height * ch };
}

/// <param name="Approximate">true = zkrácená hodnota ze seznamu („19k“), použije se jen když přesná chybí.</param>
public sealed record MobiGlasEntry(string Title, long? Reward, bool Approximate = false);

public sealed record MobiGlasDetail(string Title, string? Contractor, long? Reward, string? Description, IReadOnlyList<string> Objectives,
    IReadOnlyList<string>? PotentialBlueprints = null, IReadOnlyDictionary<string, string>? MissionInfo = null, long? Fee = null);

public sealed record MobiGlasSnapshot(bool IsContractsApp, bool IsAcceptedTab, IReadOnlyList<MobiGlasEntry> Entries, MobiGlasDetail? Detail)
{
    public static MobiGlasSnapshot Empty { get; } = new(false, false, Array.Empty<MobiGlasEntry>(), null);
}

/// <summary>
/// Rozpozná, zda je v mobiGlasu otevřená aplikace Contracts se záložkou Accepted,
/// a vytáhne seznam kontraktů (vlevo) a detail vybraného kontraktu (vpravo).
///
/// Rozložení (4.x): vlevo seznam s kategoriemi a zkrácenou odměnou („19k“), vpravo lišta záložek
/// OFFERS / ACCEPTED (n/m) / HISTORY / BEACONS, pod ní název kontraktu, blok Reward / Contract Deadline /
/// Contracted By (hodnoty vpravo od popisků), sloupce DETAILS a PRIMARY OBJECTIVES a dole ABANDON / SHARE / TRACK.
/// Polohy se odvozují od nalezených popisků, takže fungují pro různé poměry stran (16:9, 21:9, 32:9).
/// </summary>
public sealed partial class MobiGlasParser
{
    private GameLanguagePack _pack;

    public MobiGlasParser(GameLanguagePack pack) => _pack = pack;

    public void SetLanguage(GameLanguagePack pack) => _pack = pack;

    /// <summary>Záložní hranice seznam/detail, když se nenajde lišta záložek (podíl šířky okna).</summary>
    public double ListColumnSplit { get; set; } = 0.42;

    // Symbol aUEC „¤“ čte OCR různě (Ä, ¤, a, 8…), proto před číslem povolíme jeden libovolný nečíselný znak.
    [GeneratedRegex(@"(?<num>\d{1,3}(?:[ ,. ]\d{3})+|\d{4,9})\s*(?:aUEC|UEC)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingAmountRx();

    [GeneratedRegex(@"(?<num>\d{1,4}(?:[.,]\d{1,2})?)\s*(?<unit>[kKmM])\s*$")]
    private static partial Regex ShortAmountRx();

    // Počítadlo na záložce „(1/10)“ – i useknuté/zkomolené („(1/1“ když přes ně svítí kurzor, „1/10)“, „(1/1O)“)
    //  Závorka aspoň na jedné straně – jinak by se useklo i „Knowledge 3/7“.
    [GeneratedRegex(@"\s*[\(\[{]\s*\d+\s*[/\\|lI]\s*[\dOo]*\s*[\)\]}]?\s*$|\s*\d+\s*[/\\|lI]\s*[\dOo]*\s*[\)\]}]\s*$|\s*[\(\[{]\s*\d*\s*$")]
    private static partial Regex CountRx();

    // Odrážky cílů: ◇ čte OCR často jako „O“, „0“ nebo „o“ následované mezerou.
    [GeneratedRegex(@"^(?:[\s\-–—•·◇◆○●■□▪>*]+|[Oo0]\s+)")]
    private static partial Regex BulletRx();

    // Štítky „[200 Rep]“, „[100-500 Rep]“, „[BP]“ (OCR: „(200 REPI“, „[IND-S2-Bl“…)
    [GeneratedRegex(@"[\[\(\{|][^\]\)\}]*?\b(?:REP|BP)\w?[\]\)\}|]?", RegexOptions.IgnoreCase)]
    private static partial Regex RepTagRx();

    // Zkomolený štítek na konci bez závorky: „noo-soo REPI“, „200 REP“
    [GeneratedRegex(@"(?:\s+\S*[\d\-–]\S*)?\s+REP[I1l\]]?\]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingRepRx();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRx();

    /// <param name="backgroundLuminance">
    /// Volitelně jas (0..1) oblasti obrazu – aktivní záložka má barevně vyplněné pozadí.
    /// </param>
    public MobiGlasSnapshot Parse(IReadOnlyList<OcrLine> lines, Func<OcrLine, double>? backgroundLuminance = null)
    {
        if (lines.Count == 0) return MobiGlasSnapshot.Empty;
        var mg = _pack.MobiGlas;

        var isContracts = lines.Any(l => ContainsAny(l.Text, mg.ContractsApp));
        var acceptedLabels = lines.Where(l => IsTabLabel(l.Text, mg.AcceptedTab)).ToList();
        if (!isContracts && acceptedLabels.Count == 0) return MobiGlasSnapshot.Empty;

        var otherTabs = lines.Where(l => IsTabLabel(l.Text, mg.OtherTabs)).ToList();
        var hasAbandon = lines.Any(l => EqualsAny(l.Text, mg.AbandonButton));

        // 1) Tlačítko ABANDON je jen u přijatých kontraktů. 2) Jinak podle zvýrazněného pozadí záložky.
        //    ABANDON stačí i bez čitelného popisku ACCEPTED (může ho zakrývat kurzor myši), pokud jsou vidět ostatní záložky.
        var isAccepted = (acceptedLabels.Count > 0 || otherTabs.Count > 0) &&
                         (hasAbandon || (backgroundLuminance is not null &&
                                         acceptedLabels.Any(l => IsActiveTab(l, otherTabs, backgroundLuminance))));
        if (!isAccepted) return new MobiGlasSnapshot(true, false, Array.Empty<MobiGlasEntry>(), null);

        var anchor = acceptedLabels.Count > 0 ? acceptedLabels[0] : otherTabs.OrderBy(t => t.Y).First();
        var tabBar = acceptedLabels.Concat(otherTabs).Where(t => Math.Abs(t.CenterY - anchor.CenterY) < anchor.Height).ToList();
        var tabBottom = tabBar.Max(l => l.Y + l.Height);
        // Lišta záložek začíná nad detailem → její levý okraj je hranice mezi seznamem a detailem.
        var split = tabBar.Count > 0 ? tabBar.Min(t => t.X) - 0.005 : ListColumnSplit;

        var buttons = lines.Where(l => l.Y > tabBottom && EqualsAny(l.Text, mg.BottomButtons)).ToList();
        var contentBottom = buttons.Count > 0 ? buttons.Min(b => b.Y) - 0.002 : 0.88;

        var content = lines.Where(l => l.Y > tabBottom && l.Y < contentBottom)
            .OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
        var left = content.Where(l => l.X < split).ToList();
        var right = content.Where(l => l.X >= split).ToList();

        return new MobiGlasSnapshot(true, true, ParseList(left), ParseDetail(right));
    }

    /// <summary>
    /// Oblast aplikace Contracts ve snímku (normalizovaně, s okrajem) – další snímky se pak OCR čtou jen v tomto výřezu.
    /// Zahrnuje i spodní lištu mobiGlasu (text „CONTRACTS“ slouží k rozpoznání aplikace). null = mobiGlas nenalezen.
    /// </summary>
    public (double X, double Y, double W, double H)? PanelBounds(IReadOnlyList<OcrLine> lines, double margin = 0.03)
    {
        var mg = _pack.MobiGlas;
        var anchors = lines.Where(l => IsTabLabel(l.Text, mg.AcceptedTab) || IsTabLabel(l.Text, mg.OtherTabs)
                                       || ContainsAny(l.Text, mg.ContractsApp)).ToList();
        if (anchors.Count < 2) return null;
        var top = anchors.Min(l => l.Y);
        var bottom = anchors.Max(l => l.Y + l.Height);
        // obsah mobiGlasu: vše mezi lištou záložek a spodní lištou
        var inside = lines.Where(l => l.Y >= top - 0.05 && l.Y + l.Height <= bottom + 0.05).ToList();
        double x0 = inside.Min(l => l.X) - margin, x1 = inside.Max(l => l.Right) + margin;
        double y0 = top - margin, y1 = bottom + margin;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(1, x1); y1 = Math.Min(1, y1);
        return x1 - x0 < 0.2 || y1 - y0 < 0.2 ? null : (x0, y0, x1 - x0, y1 - y0);
    }

    private static bool IsActiveTab(OcrLine label, List<OcrLine> otherTabs, Func<OcrLine, double> lum)
    {
        var tabs = otherTabs.Where(t => Math.Abs(t.CenterY - label.CenterY) < label.Height).ToList();
        if (tabs.Count == 0) return false;
        var mine = Background(label, lum);
        return tabs.All(t => mine > Background(t, lum) + 0.03);
    }

    /// <summary>Jas pozadí vedle textu (vlevo a vpravo od něj), ne textu samotného.</summary>
    private static double Background(OcrLine l, Func<OcrLine, double> lum)
    {
        var pad = l.Height * 0.25;
        var w = l.Height * 0.6;
        var leftStrip = new OcrLine("", Math.Max(0, l.X - pad - w), l.Y, w, l.Height);
        var rightStrip = new OcrLine("", Math.Min(1 - w, l.Right + pad), l.Y, w, l.Height);
        return (lum(leftStrip) + lum(rightStrip)) / 2;
    }

    private IReadOnlyList<MobiGlasEntry> ParseList(List<OcrLine> left)
    {
        var entries = new List<MobiGlasEntry>();
        var used = new HashSet<OcrLine>();

        foreach (var a in left)
        {
            if (!TryParseAmount(a.Text, out var reward, out var approx, out var index)) continue;
            var before = a.Text[..index].Trim(' ', '-', ':', '¤');
            if (Letters(before) >= 6)
            {
                // Název i odměna na jednom řádku
                entries.Add(new MobiGlasEntry(CleanTitle(before), reward, approx));
                used.Add(a);
                continue;
            }
            // Název je vlevo na stejném řádku (seznam: název … 19k)
            var title = left
                .Where(t => !ReferenceEquals(t, a) && !used.Contains(t) && t.X < a.X
                            && Math.Abs(t.CenterY - a.CenterY) < Math.Max(t.Height, a.Height) * 1.2
                            && Letters(t.Text) >= 6 && !IsLabel(t.Text) && !TryParseAmount(t.Text, out _, out _, out _))
                .OrderBy(t => Math.Abs(t.CenterY - a.CenterY))
                .FirstOrDefault();
            if (title is null) continue;
            used.Add(title);
            // Dlouhý název je v seznamu zalomený – další řádek pod ním stejným písmem (zadavatel je menším písmem).
            var text = title.Text.Trim();
            var last = title;
            while (true)
            {
                var next = left.FirstOrDefault(n => !used.Contains(n) && !ReferenceEquals(n, a)
                    && Math.Abs(n.X - title.X) < 0.006 && n.Y > last.Y
                    && n.Y - (last.Y + last.Height) < last.Height * 0.8
                    && n.Height >= title.Height * 0.85 && Letters(n.Text) >= 3
                    && !TryParseAmount(n.Text, out _, out _, out _) && !IsLabel(n.Text));
                if (next is null) break;
                used.Add(next);
                text += " " + next.Text.Trim();
                last = next;
            }
            entries.Add(new MobiGlasEntry(CleanTitle(text), reward, approx));
        }

        return entries
            .Where(e => e.Title.Length >= 4)
            .GroupBy(e => Fuzzy.Normalize(e.Title))
            .Select(g => g.OrderBy(e => e.Approximate).First())
            .ToList();
    }

    private MobiGlasDetail? ParseDetail(List<OcrLine> right)
    {
        var mg = _pack.MobiGlas;
        if (right.Count == 0) return null;

        var detailsLabel = right.FirstOrDefault(l => StartsWithAny(l.Text, mg.DetailsLabel));
        var objLabel = right.FirstOrDefault(l => StartsWithAny(l.Text, mg.ObjectivesLabel));
        var headerBottom = new[] { detailsLabel?.Y, objLabel?.Y }.Where(v => v is not null).Select(v => v!.Value)
            .DefaultIfEmpty(double.MaxValue).Min();

        var used = new HashSet<OcrLine>();
        var contractor = ValueOf(right, mg.ContractorLabel, used);
        long? reward = null;
        var rewardText = ValueOf(right, mg.RewardLabel, used);
        if (rewardText is not null && TryParseAmount(rewardText, out var r, out var approx, out _) && !approx) reward = r;
        ValueOf(right, mg.DeadlineLabel, used);
        long? fee = null;
        // jen hlavička detailu (nad DETAILS) – slova „fee“/„deposit“ bývají i v popisu
        var feeText = ValueOf(right.Where(l => l.Y < headerBottom).ToList(), mg.FeeLabel, used);
        if (feeText is not null && TryParseAmount(feeText, out var f, out var feeApprox, out _) && !feeApprox) fee = f;

        // Název: největší písmo v hlavičce (nad DETAILS / PRIMARY OBJECTIVES)
        var titleLine = right
            .Where(l => l.Y < headerBottom && !used.Contains(l) && Letters(l.Text) >= 6 && !IsLabel(l.Text)
                        && !TryParseAmount(l.Text, out _, out _, out _))
            .OrderByDescending(l => l.Height)
            .ThenBy(l => l.Y)
            .FirstOrDefault();
        if (titleLine is null) return null;
        var title = CleanTitle(titleLine.Text);

        var objectives = new List<string>();
        if (objLabel is not null)
        {
            OcrLine? prev = null;
            foreach (var l in right.Where(l => l.Y > objLabel.Y + objLabel.Height * 0.5 && l.X >= objLabel.X - 0.02).OrderBy(l => l.Y))
            {
                if (IsLabel(l.Text)) break;
                var raw = l.Text.Trim();
                var hasBullet = BulletRx().Match(raw) is { Success: true, Length: > 0 };
                var clean = BulletRx().Replace(raw, "").Trim();
                if (clean.Length == 0 || Letters(clean) < 2) continue;
                // Zalomený řádek cíle: bez odrážky, těsně pod předchozím řádkem
                if (!hasBullet && prev is not null && objectives.Count > 0
                    && l.Y - (prev.Y + prev.Height) < prev.Height * 0.9)
                    objectives[^1] = objectives[^1] + " " + clean;
                else if (clean.Length >= 4 && Letters(clean) >= 3)
                    objectives.Add(clean);
                prev = l;
            }
        }

        string? description = null;
        List<string> rows = new();
        if (detailsLabel is not null)
        {
            var maxX = objLabel is not null && objLabel.X > detailsLabel.X ? objLabel.X - 0.01 : double.MaxValue;
            rows = JoinRows(right
                .Where(l => l.Y > detailsLabel.Y + detailsLabel.Height * 0.5 && l.X >= detailsLabel.X - 0.02 && l.X < maxX
                            && !IsLabel(l.Text)));
            var text = string.Join(' ', rows);
            if (text.Length > 0) description = text;
        }

        var (blueprints, info) = ParseDescriptionExtras(rows);
        return new MobiGlasDetail(title, contractor, reward, description, objectives, blueprints, info, fee);
    }

    /// <summary>Spojí fragmenty OCR ležící na stejném řádku (zleva doprava) do řádků textu.</summary>
    private static List<string> JoinRows(IEnumerable<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var l in lines.OrderBy(l => l.CenterY))
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row[0].CenterY - l.CenterY) < Math.Min(row[0].Height, l.Height) * 0.6)
                row.Add(l);
            else
                rows.Add([l]);
        }
        return rows.Select(r => string.Join(' ', r.OrderBy(x => x.X).Select(x => x.Text.Trim()))).ToList();
    }

    [GeneratedRegex(@"^[\[\(\{C]?[A-Z0-9]{2,4}-[A-Z0-9]{1,3}(?:-[A-Z0-9]{1,2})?[\]\)\}lI1]?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex BlueprintCodeRx();

    [GeneratedRegex(@"Mission\s+Type\s*:\s*(?<v>[A-Za-z][\w -]{1,30}?)(?=\s{2,}|\s+Difficulty|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MissionTypeRx();

    [GeneratedRegex(@"(?<k>Combat|Complexity|Risk|Knowledge)\s+(?<v>\d)\s*/\s*7", RegexOptions.IgnoreCase)]
    private static partial Regex DifficultyRx();

    /// <summary>
    /// Z popisu vytáhne „POTENTIAL BLUEPRINTS“ (řádky „- [IND-S1-B] Colossus“) a „MISSION DETAILS“
    /// (Mission Type, Combat/Complexity/Risk/Knowledge x/7).
    /// </summary>
    private (IReadOnlyList<string>? Blueprints, IReadOnlyDictionary<string, string>? Info) ParseDescriptionExtras(List<string> rows)
    {
        if (rows.Count == 0) return (null, null);
        var mg = _pack.MobiGlas;
        var blueprints = new List<string>();
        var start = rows.FindIndex(r => StartsWithAny(r, mg.PotentialBlueprintsLabel));
        if (start >= 0)
        {
            for (var i = start + 1; i < rows.Count; i++)
            {
                var r = rows[i].Trim();
                if (!r.StartsWith('-') && !r.StartsWith('–') && !r.StartsWith('•')) break;
                var name = r.TrimStart('-', '–', '•', ' ');
                var code = BlueprintCodeRx().Match(name);
                var item = code.Success ? name[code.Length..].Trim() : name;
                if (Letters(item) >= 2 && !blueprints.Contains(item, StringComparer.OrdinalIgnoreCase)) blueprints.Add(item);
            }
        }

        var info = new Dictionary<string, string>();
        var all = string.Join("  ", rows);
        var mt = MissionTypeRx().Match(all);
        if (mt.Success) info["Mission Type"] = mt.Groups["v"].Value.Trim();
        foreach (Match d in DifficultyRx().Matches(all))
        {
            var key = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(d.Groups["k"].Value.ToLowerInvariant());
            info[key] = d.Groups["v"].Value + "/7";
        }
        return (blueprints.Count > 0 ? blueprints : null, info.Count > 0 ? info : null);
    }

    /// <summary>
    /// Hodnota u popisku: text za popiskem na stejném řádku, jinak nejbližší řádek vpravo
    /// ve stejné výšce (blok Reward / Contracted By), jinak řádek těsně pod popiskem.
    /// </summary>
    private static string? ValueOf(List<OcrLine> lines, List<string> labels, HashSet<OcrLine> used)
    {
        foreach (var l in lines)
        {
            if (used.Contains(l)) continue;
            var text = l.Text.Trim();
            var label = labels.OrderByDescending(x => x.Length)
                .FirstOrDefault(lb => text.StartsWith(lb, StringComparison.OrdinalIgnoreCase));
            if (label is null) continue;
            used.Add(l);
            var rest = text[label.Length..].Trim(' ', ':', '-');
            if (rest.Length > 0) return rest;

            var sameRow = lines
                .Where(o => !used.Contains(o) && o.X > l.Right && Math.Abs(o.CenterY - l.CenterY) < l.Height * 0.8)
                .OrderBy(o => o.X)
                .FirstOrDefault();
            if (sameRow is not null) { used.Add(sameRow); return sameRow.Text.Trim(); }

            var below = lines
                .Where(o => !used.Contains(o) && o.Y > l.Y && o.Y - l.Y < Math.Max(l.Height, 0.005) * 3.5
                            && Math.Abs(o.X - l.X) < Math.Max(l.Width, 0.05))
                .OrderBy(o => o.Y).ThenBy(o => Math.Abs(o.X - l.X))
                .FirstOrDefault();
            if (below is not null) { used.Add(below); return below.Text.Trim(); }
            return null;
        }
        return null;
    }

    /// <summary>Částka na konci textu: přesná („¤ 19,750“, „Ä 19.750“) nebo zkrácená („19k“).</summary>
    public static bool TryParseAmount(string text, out long value, out bool approximate, out int index)
    {
        value = 0; approximate = false; index = text.Length;
        var t = text.TrimEnd();
        var m = TrailingAmountRx().Match(t);
        if (m.Success && GameLogParser.TryParseAmount(m.Groups["num"].Value, out value) && value > 0)
        {
            index = m.Index;
            return true;
        }
        var k = ShortAmountRx().Match(t);
        if (k.Success && double.TryParse(k.Groups["num"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            var mult = k.Groups["unit"].Value is "m" or "M" ? 1_000_000 : 1_000;
            value = (long)Math.Round(d * mult);
            approximate = true;
            index = k.Index;
            return value > 0;
        }
        return false;
    }

    /// <summary>Odstraní reputační štítek a šum z názvu kontraktu.</summary>
    public static string CleanTitle(string title)
    {
        var t = RepTagRx().Replace(title.Trim(), " ");
        t = TrailingRepRx().Replace(t, "");
        t = t.Replace('•', '\'').Replace('’', '\'');
        return MultiSpaceRx().Replace(t, " ").Trim(' ', '-', ':', '[', '(');
    }

    private bool IsLabel(string text)
    {
        var mg = _pack.MobiGlas;
        var n = Fuzzy.Normalize(CountRx().Replace(text, ""));
        if (n.Length == 0) return true;
        return mg.ContractsApp.Concat(mg.AcceptedTab).Concat(mg.OtherTabs).Concat(mg.RewardLabel)
            .Concat(mg.ContractorLabel).Concat(mg.ObjectivesLabel).Concat(mg.DeadlineLabel).Concat(mg.FeeLabel)
            .Concat(mg.DetailsLabel).Concat(mg.BottomButtons).Concat(mg.AbandonButton)
            .Any(lb => Fuzzy.Normalize(lb) == n);
    }

    /// <summary>Krátký řádek, který je jen název záložky (případně s počtem „(1/10)“).</summary>
    private static bool IsTabLabel(string text, IEnumerable<string> names)
    {
        var n = Fuzzy.Normalize(CountRx().Replace(text, ""));
        return n.Length > 0 && names.Any(w => Fuzzy.Normalize(w) == n);
    }

    private static bool EqualsAny(string text, IEnumerable<string> words)
    {
        var n = Fuzzy.Normalize(text);
        return n.Length > 0 && words.Any(w => Fuzzy.Normalize(w) == n);
    }

    private static bool ContainsAny(string text, IEnumerable<string> words)
    {
        var padded = " " + Fuzzy.Normalize(text) + " ";
        return words.Any(w => padded.Contains(" " + Fuzzy.Normalize(w) + " ", StringComparison.Ordinal));
    }

    private static bool StartsWithAny(string text, IEnumerable<string> words)
    {
        var n = Fuzzy.Normalize(text);
        return words.Any(w => n.StartsWith(Fuzzy.Normalize(w), StringComparison.Ordinal));
    }

    private static int Letters(string s) => s.Count(char.IsLetter);
}
