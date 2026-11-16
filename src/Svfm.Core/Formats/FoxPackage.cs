using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Svfm.Core.Formats;

/// <summary>A file inside a Fox package.</summary>
public sealed class PackageFile
{
    /// <summary>Game path, e.g. /Assets/tpp/fova/chara/sna/sna0_main1_c13.fv2. For an obfuscated name, the stored text.</summary>
    public string Path { get; }

    /// <summary>The name exactly as stored (vanilla packages obfuscate a few names; those are kept byte for byte).</summary>
    public byte[] StoredName { get; }

    /// <summary>MD5 of the real path text, as stored in the entry.</summary>
    public byte[] NameMd5 { get; }

    /// <summary>File contents as stored. Packed lua can be obfuscated (first byte 0x1B/0x1C); it is never changed here.</summary>
    public byte[] Data { get; set; }

    /// <summary>True when the stored name does not hash to <see cref="NameMd5"/> (an obfuscated vanilla name).</summary>
    public bool NameObfuscated { get; }

    public PackageFile(string path, byte[] data)
    {
        Path = path;
        StoredName = Encoding.Latin1.GetBytes(path);
        NameMd5 = MD5.HashData(StoredName);
        Data = data;
    }

    internal PackageFile(byte[] storedName, byte[] md5, byte[] data)
    {
        StoredName = storedName;
        NameMd5 = md5;
        Data = data;
        Path = Encoding.Latin1.GetString(storedName);
        NameObfuscated = !MD5.HashData(storedName).AsSpan().SequenceEqual(md5);
    }

    public string Extension
    {
        get
        {
            int slash = Path.LastIndexOf('/');
            int dot = Path.LastIndexOf('.');
            return dot > slash ? Path[(dot + 1)..] : "";
        }
    }

    public override string ToString() => Path;
}

/// <summary>
/// Fox Engine package (.fpk / .fpkd). Layout as written by GzsTool 0.5.3 and SnakeBite:
/// 48-byte header, 48-byte entries, 16-byte references, names, then 16-aligned data.
/// Reading and writing a vanilla package gives back the same bytes.
/// </summary>
public sealed class FoxPackage
{
    public bool IsFpkd { get; set; }
    public List<PackageFile> Files { get; } = [];

    /// <summary>Other packages this one depends on (kept as stored).</summary>
    public List<byte[]> References { get; } = [];

    public PackageFile? Find(string path) => Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));

    public static FoxPackage Read(ReadOnlySpan<byte> d)
    {
        try { return ReadChecked(d); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("package", e); }
    }

    private static FoxPackage ReadChecked(ReadOnlySpan<byte> d)
    {
        if (d.Length < 0x30 || !d[..6].SequenceEqual("foxfpk"u8))
            throw new InvalidDataException("Not a Fox package (no foxfpk header).");
        var p = new FoxPackage { IsFpkd = d[6] == (byte)'d' };
        int count = BinaryPrimitives.ReadInt32LittleEndian(d[0x24..]);
        int refs = BinaryPrimitives.ReadInt32LittleEndian(d[0x28..]);
        for (int i = 0; i < count; i++)
        {
            var e = d.Slice(0x30 + i * 0x30, 0x30);
            int dataOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(e));
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
            int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(e[16..]);
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(e[24..]);
            p.Files.Add(new PackageFile(
                d.Slice(nameOffset, nameLength).ToArray(),
                e.Slice(32, 16).ToArray(),
                d.Slice(dataOffset, dataSize).ToArray()));
        }
        int refBase = 0x30 + count * 0x30;
        for (int i = 0; i < refs; i++)
        {
            var r = d.Slice(refBase + i * 16, 16);
            int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(r);
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(r[8..]);
            p.References.Add(d.Slice(nameOffset, nameLength).ToArray());
        }
        return p;
    }

    public static FoxPackage Read(string file) => Read(File.ReadAllBytes(file));

    /// <summary>Writes the files in their current order (see <see cref="PackageOrder"/> for SnakeBite's order).</summary>
    public byte[] Write()
    {
        int head = 0x30 + Files.Count * 0x30 + References.Count * 16;
        var names = new MemoryStream();
        var nameOffsets = new List<int>();
        foreach (var f in Files) { nameOffsets.Add(head + (int)names.Length); names.Write(f.StoredName); names.WriteByte(0); }
        var refOffsets = new List<int>();
        foreach (var r in References) { refOffsets.Add(head + (int)names.Length); names.Write(r); names.WriteByte(0); }

        int pos = Align16(head + (int)names.Length);
        var dataOffsets = new List<int>();
        foreach (var f in Files) { dataOffsets.Add(pos); pos = Align16(pos + f.Data.Length); }

        byte[] o = new byte[pos];
        "foxfpk"u8.CopyTo(o);
        o[6] = IsFpkd ? (byte)'d' : (byte)0;
        "win"u8.CopyTo(o.AsSpan(7));
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(0x0A), (uint)o.Length);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(0x20), 2);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(0x24), Files.Count);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(0x28), References.Count);
        for (int i = 0; i < Files.Count; i++)
        {
            var e = o.AsSpan(0x30 + i * 0x30, 0x30);
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)dataOffsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(e[8..], Files[i].Data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(e[16..], nameOffsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(e[24..], Files[i].StoredName.Length);
            Files[i].NameMd5.CopyTo(e[32..]);
        }
        int refBase = 0x30 + Files.Count * 0x30;
        for (int i = 0; i < References.Count; i++)
        {
            var r = o.AsSpan(refBase + i * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(r, refOffsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(r[8..], References[i].Length);
        }
        names.ToArray().CopyTo(o, head);
        for (int i = 0; i < Files.Count; i++) Files[i].Data.CopyTo(o, dataOffsets[i]);
        return o;
    }

    private static int Align16(int v) => (v + 15) & ~15;
}
