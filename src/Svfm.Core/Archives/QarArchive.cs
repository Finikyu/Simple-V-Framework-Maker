using System.Buffers.Binary;
using System.IO.Compression;
using Svfm.Core.Hashing;

namespace Svfm.Core.Archives;

/// <summary>One file inside a QAR archive. <see cref="Hash"/> is the full 64-bit path code.</summary>
public sealed record QarEntry(
    ulong Hash,
    long HeaderOffset,
    uint UncompressedSize,
    uint StoredSize,
    byte[] Md5)
{
    public bool Compressed => UncompressedSize != StoredSize;
    public long DataOffset => HeaderOffset + 32;
}

/// <summary>
/// Read-only access to a Fox Engine QAR archive (chunk*.dat, texture*.dat, 00.dat, 01.dat).
/// Single files are read straight from the archive, nothing is unpacked to disk.
/// Format and obfuscation follow GzsTool 0.5.3 (the version known to round-trip correctly).
/// </summary>
public sealed class QarArchive : IDisposable
{
    private const uint Magic = 0x52415153; // "SQAR"
    private readonly Stream _stream;
    private readonly Dictionary<ulong, QarEntry> _byHash;
    private readonly object _lock = new();

    public string? Path { get; }
    public uint Flags { get; }
    public uint Version { get; }
    public IReadOnlyList<QarEntry> Entries { get; }

    private QarArchive(Stream stream, string? path)
    {
        _stream = stream;
        Path = path;
        Span<byte> header = stackalloc byte[32];
        stream.Position = 0;
        stream.ReadExactly(header);
        var x = QarCrypto.HeaderXor;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != Magic) throw new InvalidDataException("Not a QAR archive (no SQAR header).");
        Flags = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) ^ x[0];
        uint fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) ^ x[1];
        uint unknownCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) ^ x[2];
        Version = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) ^ x[0];
        _ = unknownCount;

        int shift = (Flags & 0x800) != 0 ? 12 : 10;
        if (fileCount > (stream.Length - 32) / 8) throw new InvalidDataException("The archive (.dat) file is damaged or cut off: its file table is longer than the file.");
        byte[] table = new byte[(int)fileCount * 8];
        stream.ReadExactly(table);
        ulong[] sections = QarCrypto.DecryptSectionList(table, (int)fileCount, Version);

        var entries = new List<QarEntry>((int)fileCount);
        _byHash = new Dictionary<ulong, QarEntry>((int)fileCount);
        byte[] eh = new byte[32];
        foreach (ulong section in sections)
        {
            long offset = (long)(section >> 40) << shift;
            stream.Position = offset;
            stream.ReadExactly(eh);
            uint hashLow = BinaryPrimitives.ReadUInt32LittleEndian(eh) ^ x[0];
            uint hashHigh = BinaryPrimitives.ReadUInt32LittleEndian(eh.AsSpan(4)) ^ x[0];
            uint size1 = BinaryPrimitives.ReadUInt32LittleEndian(eh.AsSpan(8)) ^ x[1];
            uint size2 = BinaryPrimitives.ReadUInt32LittleEndian(eh.AsSpan(12)) ^ x[2];
            byte[] md5 = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(md5, BinaryPrimitives.ReadUInt32LittleEndian(eh.AsSpan(16)) ^ x[3]);
            BinaryPrimitives.WriteUInt32LittleEndian(md5.AsSpan(4), BinaryPrimitives.ReadUInt32LittleEndian(eh.AsSpan(20)) ^ x[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(md5.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(eh.AsSpan(24)) ^ x[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(md5.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(eh.AsSpan(28)) ^ x[1]);
            ulong hash = (ulong)hashHigh << 32 | hashLow;
            // TPP (version 1) archives store the on-disk size first and the unpacked size second
            // (checked on the 1.0.15 update archive: every compressed entry inflates from exactly
            // the first size to the second). GzsTool (Atvaark master and BobDoleOwndU 0.5.3.1b) names
            // them the other way round; its extraction still works because zlib stops at the end of
            // the stream, but a reader that trusts the names cuts compressed files short.
            uint stored = Version != 2 ? size1 : size2;
            uint uncompressed = Version != 2 ? size2 : size1;
            var e = new QarEntry(hash, offset, uncompressed, stored, md5);
            entries.Add(e);
            _byHash.TryAdd(hash, e);
        }
        Entries = entries;
    }

    public static QarArchive Open(string path)
    {
        // The app holds the installed game's archives for the whole session, and the mod managers must still write them:
        // SnakeBite replaces 00.dat/01.dat (delete, then move) to install, copies over them in place to switch mods off
        // or on, and copies over *.original at setup; DMM deletes *.original at "Restore to retail". GameData.Changed
        // tells the app to open the game again after such a write.
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        try { return new(fs, path); }
        catch { fs.Dispose(); throw; } // a damaged archive must not keep the file open
    }

    public static QarArchive Open(Stream stream) => new(stream, null);

    public bool TryGetEntry(ulong pathCode, out QarEntry entry) => _byHash.TryGetValue(pathCode, out entry!);

    public bool TryGetEntry(string gamePath, out QarEntry entry) => TryGetEntry(FoxHash.PathCode64(gamePath), out entry);

    public bool Contains(string gamePath) => _byHash.ContainsKey(FoxHash.PathCode64(gamePath));

    /// <summary>Returns the file's contents (both layers removed, decompressed).</summary>
    public byte[] Read(QarEntry entry)
    {
        byte[] data = new byte[entry.StoredSize];
        lock (_lock)
        {
            _stream.Position = entry.DataOffset;
            _stream.ReadExactly(data);
        }
        QarCrypto.Layer1(data, Version, entry.Md5, (uint)entry.Hash);

        int start = 0;
        if (data.Length >= 8)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
            int hs = QarCrypto.Layer2HeaderSize(magic);
            if (hs > 0)
            {
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                QarCrypto.Layer2(data.AsSpan(hs), key);
                start = hs;
            }
        }

        if (!entry.Compressed)
            return start == 0 ? data : data[start..];

        using var input = new MemoryStream(data, start, data.Length - start, writable: false);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        byte[] output = new byte[entry.UncompressedSize];
        z.ReadExactly(output);
        if (z.ReadByte() != -1)
            throw new InvalidDataException($"Entry {entry.Hash:x16} inflates past its recorded size {entry.UncompressedSize}.");
        return output;
    }

    public byte[] Read(string gamePath) =>
        TryGetEntry(gamePath, out var e) ? Read(e) : throw new FileNotFoundException($"{gamePath} is not in {Path ?? "the archive"}.");

    public void Dispose() => _stream.Dispose();
}
