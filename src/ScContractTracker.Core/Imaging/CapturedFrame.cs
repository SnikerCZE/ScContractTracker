namespace ScContractTracker.Core.Imaging;

/// <summary>Snímek v paměti ve formátu BGRA32, řádky shora dolů, bez zarovnání řádků.</summary>
public sealed class CapturedFrame
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Odkud snímek pochází (pro stavový řádek a diagnostiku).</summary>
    public string Source { get; init; } = "";

    /// <summary>Průměrný jas (0..1) v obdélníku zadaném normalizovanými souřadnicemi.</summary>
    public double Luminance(double nx, double ny, double nw, double nh)
    {
        int x0 = Math.Clamp((int)(nx * Width), 0, Width - 1);
        int y0 = Math.Clamp((int)(ny * Height), 0, Height - 1);
        int x1 = Math.Clamp((int)((nx + nw) * Width), x0 + 1, Width);
        int y1 = Math.Clamp((int)((ny + nh) * Height), y0 + 1, Height);
        long sum = 0, count = 0;
        var stepX = Math.Max(1, (x1 - x0) / 40);
        var stepY = Math.Max(1, (y1 - y0) / 10);
        for (var y = y0; y < y1; y += stepY)
        {
            var row = y * Width * 4;
            for (var x = x0; x < x1; x += stepX)
            {
                var i = row + x * 4;
                sum += Luma(i);
                count++;
            }
        }
        return count == 0 ? 0 : sum / (255.0 * count);
    }

    /// <summary>
    /// true = snímek je (téměř) jednobarevný – typicky černý obraz, když capture API
    /// nedostane obsah okna (exkluzivní fullscreen, chráněný obsah, minimalizované okno).
    /// </summary>
    public bool IsBlank(int threshold = 6)
    {
        if (Width == 0 || Height == 0) return true;
        int min = 255, max = 0;
        // Mřížka 64×36 vzorků stačí a je rychlá i pro 4K.
        for (var gy = 0; gy < 36; gy++)
        {
            var y = (int)((gy + 0.5) * Height / 36);
            for (var gx = 0; gx < 64; gx++)
            {
                var x = (int)((gx + 0.5) * Width / 64);
                var l = Luma((y * Width + x) * 4);
                if (l < min) min = l;
                if (l > max) max = l;
                if (max - min > threshold) return false;
            }
        }
        return true;
    }

    private int Luma(int i) => (Pixels[i] * 29 + Pixels[i + 1] * 150 + Pixels[i + 2] * 77) >> 8;

    /// <summary>Zmenšení celočíselným faktorem (průměr bloku) kvůli limitu velikosti Windows OCR.</summary>
    public CapturedFrame Downscale(int factor)
    {
        if (factor <= 1) return this;
        int w = Width / factor, h = Height / factor;
        var dst = new byte[w * h * 4];
        var area = factor * factor;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int b = 0, g = 0, r = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = ((y * factor + dy) * Width + x * factor) * 4;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var i = row + dx * 4;
                        b += Pixels[i]; g += Pixels[i + 1]; r += Pixels[i + 2];
                    }
                }
                var o = (y * w + x) * 4;
                dst[o] = (byte)(b / area); dst[o + 1] = (byte)(g / area); dst[o + 2] = (byte)(r / area); dst[o + 3] = 255;
            }
        return new CapturedFrame { Pixels = dst, Width = w, Height = h, Source = Source };
    }

    /// <summary>Výřez v normalizovaných souřadnicích (0..1). Mimo snímek se ořízne.</summary>
    public CapturedFrame Crop(double nx, double ny, double nw, double nh)
    {
        int x0 = Math.Clamp((int)Math.Floor(nx * Width), 0, Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(ny * Height), 0, Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((nx + nw) * Width), x0 + 1, Width);
        int y1 = Math.Clamp((int)Math.Ceiling((ny + nh) * Height), y0 + 1, Height);
        int w = x1 - x0, h = y1 - y0;
        var dst = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Pixels.AsSpan(((y0 + y) * Width + x0) * 4, w * 4).CopyTo(dst.AsSpan(y * w * 4, w * 4));
        return new CapturedFrame { Pixels = dst, Width = w, Height = h, Source = Source };
    }

    /// <summary>Malý otisk snímku (mřížka 64×36 jasů) – pro rychlé zjištění, že se obraz nezměnil.</summary>
    public byte[] Signature(int cols = 64, int rows = 36)
    {
        var sig = new byte[cols * rows];
        for (var gy = 0; gy < rows; gy++)
            for (var gx = 0; gx < cols; gx++)
            {
                int sx0 = gx * Width / cols, sx1 = Math.Max(sx0 + 1, (gx + 1) * Width / cols);
                int sy0 = gy * Height / rows, sy1 = Math.Max(sy0 + 1, (gy + 1) * Height / rows);
                long sum = 0; var n = 0;
                // řídké vzorkování (každý 4. pixel) – stačí a je rychlé i pro 4K
                for (var y = sy0; y < sy1; y += 4)
                    for (var x = sx0; x < sx1; x += 4) { sum += Luma((y * Width + x) * 4); n++; }
                sig[gy * cols + gx] = (byte)(n == 0 ? 0 : sum / n);
            }
        return sig;
    }

    /// <summary>Průměrný rozdíl dvou otisků (0–255). Pod ~1,5 je obraz prakticky stejný (jen kurzor, blikání).</summary>
    public static double SignatureDistance(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) return double.MaxValue;
        long d = 0;
        for (var i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
        return (double)d / a.Length;
    }

    /// <summary>Zkopíruje data s libovolným krokem řádku (stride) do těsně zarovnaného snímku.</summary>
    public static CapturedFrame FromBgra(ReadOnlySpan<byte> data, int width, int height, int stride, string source = "")
    {
        var pixels = new byte[width * height * 4];
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
            data.Slice(y * stride, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        return new CapturedFrame { Pixels = pixels, Width = width, Height = height, Source = source };
    }
}
