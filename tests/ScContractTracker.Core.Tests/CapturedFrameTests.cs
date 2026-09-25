using ScContractTracker.Core.Imaging;
using Xunit;

namespace ScContractTracker.Core.Tests;

public class CapturedFrameTests
{
    private static CapturedFrame Solid(int w, int h, byte v)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255; }
        return new CapturedFrame { Pixels = px, Width = w, Height = h };
    }

    [Fact]
    public void Black_frame_is_blank_and_frame_with_content_is_not()
    {
        var black = Solid(640, 360, 0);
        Assert.True(black.IsBlank());

        var withText = Solid(640, 360, 0);
        // Světlý obdélník (např. text mobiGlasu) přes část obrazu
        for (var y = 100; y < 200; y++)
            for (var x = 100; x < 400; x++)
            {
                var i = (y * 640 + x) * 4;
                withText.Pixels[i] = withText.Pixels[i + 1] = withText.Pixels[i + 2] = 220;
            }
        Assert.False(withText.IsBlank());
    }

    [Fact]
    public void Luminance_and_downscale()
    {
        var f = Solid(100, 100, 255);
        Assert.Equal(1.0, f.Luminance(0.1, 0.1, 0.5, 0.5), 2);

        var small = Solid(400, 200, 128).Downscale(4);
        Assert.Equal(100, small.Width);
        Assert.Equal(50, small.Height);
        Assert.Equal((byte)128, small.Pixels[0]);
    }

    [Fact]
    public void FromBgra_removes_row_padding()
    {
        // 2×2 pixely, stride 12 bajtů (4 bajty zarovnání na řádek)
        var data = new byte[]
        {
            1, 1, 1, 255, 2, 2, 2, 255, 9, 9, 9, 9,
            3, 3, 3, 255, 4, 4, 4, 255, 9, 9, 9, 9,
        };
        var f = CapturedFrame.FromBgra(data, 2, 2, 12, "test");
        Assert.Equal(16, f.Pixels.Length);
        Assert.Equal((byte)3, f.Pixels[8]);
        Assert.Equal("test", f.Source);
    }
}

public class OcrSavingTests
{
    private static ScContractTracker.Core.Imaging.CapturedFrame Frame(int w, int h, Func<int, int, byte> lum)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = lum(x, y); var i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
            }
        return new ScContractTracker.Core.Imaging.CapturedFrame { Pixels = px, Width = w, Height = h };
    }

    [Fact]
    public void Crop_takes_the_right_pixels_and_signature_detects_changes()
    {
        var f = Frame(400, 200, (x, y) => (byte)(x < 200 ? 10 : 200));
        var c = f.Crop(0.5, 0.25, 0.25, 0.5);
        Assert.Equal(100, c.Width);
        Assert.Equal(100, c.Height);
        Assert.Equal(200, c.Pixels[0]);

        var same = Frame(400, 200, (x, y) => (byte)(x < 200 ? 10 : 200));
        var cursor = Frame(400, 200, (x, y) => (byte)(x is > 50 and < 58 && y is > 50 and < 58 ? 255 : x < 200 ? 10 : 200));
        var other = Frame(400, 200, (x, y) => (byte)(y < 100 ? 10 : 200));
        Assert.Equal(0, ScContractTracker.Core.Imaging.CapturedFrame.SignatureDistance(f.Signature(), same.Signature()));
        Assert.True(ScContractTracker.Core.Imaging.CapturedFrame.SignatureDistance(f.Signature(), cursor.Signature()) < 1.5);
        Assert.True(ScContractTracker.Core.Imaging.CapturedFrame.SignatureDistance(f.Signature(), other.Signature()) > 20);
    }

    [Fact]
    public void Panel_bounds_cover_the_mobiglas_and_cropped_lines_parse_the_same()
    {
        var parser = new ScContractTracker.Core.Ocr.MobiGlasParser(ScContractTracker.Core.Localization.GameLanguagePack.BuiltIn.English);
        var lines = MobiGlasParserTests.LoadFixture("ocr_accepted_uwc_cursor_3440x1440.txt");
        var b = parser.PanelBounds(lines);
        Assert.NotNull(b);
        var (x, y, w, h) = b!.Value;
        Assert.True(w < 0.75 && h < 0.95); // výřez menší než celý snímek

        var inside = lines.Where(l => l.X >= x && l.Right <= x + w && l.Y >= y && l.Y + l.Height <= y + h).ToList();
        var full = parser.Parse(lines);
        var cropped = parser.Parse(inside);
        Assert.True(cropped.IsAcceptedTab);
        Assert.Equal(full.Detail!.Reward, cropped.Detail!.Reward);
        Assert.Equal(full.Detail.Description, cropped.Detail.Description);
        Assert.Equal(full.Detail.Objectives, cropped.Detail.Objectives);
        Assert.Equal(full.Entries.Count, cropped.Entries.Count);

        // souřadnice z výřezu se převedou zpět do celého snímku
        var l0 = new ScContractTracker.Core.Ocr.OcrLine("A", 0.5, 0.5, 0.1, 0.1).FromCrop(0.2, 0.1, 0.5, 0.8);
        Assert.Equal(0.45, l0.X, 6);
        Assert.Equal(0.5, l0.Y, 6);
    }

    [Fact]
    public void Complete_contract_does_not_need_ocr()
    {
        var c = new ScContractTracker.Core.Models.Contract { Title = "X", Reward = 1000, RewardSource = "ocr", Contractor = "UWC", Description = "d" };
        Assert.True(ScContractTracker.Core.Tracking.ContractTracker.NeedsOcr(c));
        c.Objectives.Add(new ScContractTracker.Core.Models.ContractObjective { Id = "a", Text = "Go" });
        Assert.False(ScContractTracker.Core.Tracking.ContractTracker.NeedsOcr(c));
        c.RewardSource = "ocr~";
        Assert.True(ScContractTracker.Core.Tracking.ContractTracker.NeedsOcr(c));
    }
}
