using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ScContractTracker.Core.GameData;

/// <summary>
/// Čtení archivu Data.p4k (jen pro čtení). Je to ZIP64 s úpravami CIG:
/// komprese 100 = Zstandard, některé soubory šifrované AES-128-CBC s veřejně známým klíčem
/// a lokální hlavička může mít signaturu PK\x03\x14. Popis formátu vychází z projektu unp4k (dolkensp).
/// Archiv má přes milion souborů, proto se při otevření drží v paměti jen položky vyhovující filtru.
/// </summary>
public sealed class P4kArchive : IDisposable
{
    private static readonly byte[] AesKey =
        [0x5E, 0x7A, 0x20, 0x02, 0x30, 0x2E, 0xEB, 0x1A, 0x3B, 0xB6, 0x17, 0xC3, 0x0F, 0xDE, 0x1E, 0x47];

    /// <summary>
    /// Továrna na zstd dekompresní stream. .NET zstd nemá, nastaví ji aplikace (ZstdSharp).
    /// </summary>
    public static Func<Stream, Stream>? ZstdFactory { get; set; }

    private readonly FileStream _fs;
    private readonly Dictionary<string, P4kEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<P4kEntry> Entries => _entries.Values;

    private P4kArchive(FileStream fs) => _fs = fs;

    public static P4kArchive Open(string path, Func<string, bool>? filter = null)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        var a = new P4kArchive(fs);
        try { a.ReadCentralDirectory(filter); }
        catch { fs.Dispose(); throw; }
        return a;
    }

    public P4kEntry? Find(string name) => _entries.GetValueOrDefault(Normalize(name));

    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');

    private void ReadCentralDirectory(Func<string, bool>? filter)
    {
        var br = new BinaryReader(_fs, Encoding.UTF8, leaveOpen: true);
        var eocd = FindEndOfCentralDirectory();
        _fs.Seek(eocd + 10, SeekOrigin.Begin);
        long total = br.ReadUInt16();
        long cdSize = br.ReadUInt32();
        long cdOffset = br.ReadUInt32();

        if (total == 0xFFFF || cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF)
        {
            // ZIP64 locator leží těsně před EOCD
            _fs.Seek(eocd - 20, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x07064b50) throw new InvalidDataException("Chybí ZIP64 locator.");
            br.ReadUInt32();
            var z64 = br.ReadInt64();
            _fs.Seek(z64, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x06064b50) throw new InvalidDataException("Chybí ZIP64 EOCD.");
            _fs.Seek(z64 + 32, SeekOrigin.Begin);
            total = br.ReadInt64();
            cdSize = br.ReadInt64();
            cdOffset = br.ReadInt64();
        }

        _fs.Seek(cdOffset, SeekOrigin.Begin);
        var bs = new BufferedStream(_fs, 1 << 20);
        var r = new BinaryReader(bs, Encoding.UTF8, leaveOpen: true);
        for (long i = 0; i < total; i++)
        {
            if (r.ReadUInt32() != 0x02014b50) throw new InvalidDataException($"Neplatná položka centrálního adresáře #{i}.");
            r.ReadUInt16(); r.ReadUInt16();              // version made / needed
            var flags = r.ReadUInt16();
            var method = r.ReadUInt16();
            r.ReadUInt32();                              // čas + datum
            r.ReadUInt32();                              // crc
            long comp = r.ReadUInt32();
            long uncomp = r.ReadUInt32();
            var nameLen = r.ReadUInt16();
            var extraLen = r.ReadUInt16();
            var commentLen = r.ReadUInt16();
            r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32(); // disk, int attr, ext attr
            long offset = r.ReadUInt32();
            var nameBytes = r.ReadBytes(nameLen);
            var extra = r.ReadBytes(extraLen);
            if (commentLen > 0) r.ReadBytes(commentLen);

            var name = Normalize(Encoding.UTF8.GetString(nameBytes));
            if (filter is not null && !filter(name)) continue;

            ApplyZip64(extra, ref uncomp, ref comp, ref offset);
            // unp4k: šifrování je signalizováno bajtem 168 v extra poli
            var encrypted = extra.Length > 168 && extra[168] != 0;
            _entries[name] = new P4kEntry(name, method, comp, uncomp, offset, encrypted, (flags & 1) != 0);
        }
    }

    private static void ApplyZip64(byte[] extra, ref long uncomp, ref long comp, ref long offset)
    {
        var i = 0;
        while (i + 4 <= extra.Length)
        {
            var id = BitConverter.ToUInt16(extra, i);
            var size = BitConverter.ToUInt16(extra, i + 2);
            var p = i + 4;
            if (id == 0x0001)
            {
                if (uncomp == 0xFFFFFFFF && p + 8 <= extra.Length) { uncomp = BitConverter.ToInt64(extra, p); p += 8; }
                if (comp == 0xFFFFFFFF && p + 8 <= extra.Length) { comp = BitConverter.ToInt64(extra, p); p += 8; }
                if (offset == 0xFFFFFFFF && p + 8 <= extra.Length) { offset = BitConverter.ToInt64(extra, p); }
                return;
            }
            i = p + size;
        }
    }

    private long FindEndOfCentralDirectory()
    {
        var len = _fs.Length;
        var scan = (int)Math.Min(len, 65557);
        var buf = new byte[scan];
        _fs.Seek(len - scan, SeekOrigin.Begin);
        _fs.ReadExactly(buf);
        for (var i = scan - 22; i >= 0; i--)
            if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
                return len - scan + i;
        throw new InvalidDataException("Soubor není platný p4k/zip archiv (chybí EOCD).");
    }

    /// <summary>Načte a rozbalí celý soubor do paměti.</summary>
    public byte[] ReadAllBytes(P4kEntry entry)
    {
        byte[] raw;
        lock (_fs)
        {
            _fs.Seek(entry.LocalHeaderOffset, SeekOrigin.Begin);
            Span<byte> hdr = stackalloc byte[30];
            _fs.ReadExactly(hdr);
            var sig = BitConverter.ToUInt32(hdr);
            if (sig != 0x04034b50 && sig != 0x14034b50)
                throw new InvalidDataException($"Neplatná lokální hlavička u {entry.Name}.");
            var nameLen = BitConverter.ToUInt16(hdr[26..]);
            var extraLen = BitConverter.ToUInt16(hdr[28..]);
            _fs.Seek(nameLen + extraLen, SeekOrigin.Current);
            raw = new byte[entry.CompressedSize];
            _fs.ReadExactly(raw);
        }

        if (entry.Encrypted) raw = Decrypt(raw);

        switch (entry.Method)
        {
            case 0:
                return raw;
            case 8:
            {
                using var ds = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
                return ReadToEnd(ds, entry.UncompressedSize);
            }
            case 100:
            {
                var factory = ZstdFactory ?? throw new NotSupportedException("Zstd dekompresor není nastaven (P4kArchive.ZstdFactory).");
                using var zs = factory(new MemoryStream(raw));
                return ReadToEnd(zs, entry.UncompressedSize);
            }
            default:
                throw new NotSupportedException($"Nepodporovaná komprese {entry.Method} u {entry.Name}.");
        }
    }

    public Stream OpenRead(P4kEntry entry) => new MemoryStream(ReadAllBytes(entry), writable: false);

    private static byte[] Decrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = AesKey;
        var plain = aes.DecryptCbc(data, new byte[16], PaddingMode.None);
        var end = plain.Length;
        while (end > 0 && plain[end - 1] == 0) end--;
        return end == plain.Length ? plain : plain[..end];
    }

    private static byte[] ReadToEnd(Stream s, long expected)
    {
        using var ms = new MemoryStream(expected is > 0 and < int.MaxValue ? (int)expected : 0);
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose() => _fs.Dispose();
}

public sealed record P4kEntry(string Name, ushort Method, long CompressedSize, long UncompressedSize, long LocalHeaderOffset, bool Encrypted, bool ZipCrypto);
