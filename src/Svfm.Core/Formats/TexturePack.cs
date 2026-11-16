using System.Buffers.Binary;
using Svfm.Core.Hashing;

namespace Svfm.Core.Formats;

/// <summary>One part of a packed texture: the .ftex header or one .N.ftexs file, keyed by its path code.</summary>
public sealed record TexturePart(ulong PathCode, byte[] Data)
{
    /// <summary>0 for the .ftex part, N for a .N.ftexs part, null if the extension is something else.</summary>
    public int? FileNumber => FoxHash.ExtensionOf(PathCode) switch
    {
        "ftex" => 0,
        "1.ftexs" => 1, "2.ftexs" => 2, "3.ftexs" => 3, "4.ftexs" => 4, "5.ftexs" => 5, "6.ftexs" => 6,
        _ => null,
    };
}

/// <summary>One texture in a pack. <see cref="PathCode"/> is the path code of its .ftex.</summary>
public sealed class PackedTexture
{
    public ulong PathCode { get; set; }
    public List<TexturePart> Parts { get; } = [];

    public FtexTexture Header => FtexTexture.Read(Parts.First(p => p.FileNumber == 0).Data);

    public Dictionary<int, byte[]> FtexsFiles() =>
        Parts.Where(p => p.FileNumber > 0).ToDictionary(p => p.FileNumber!.Value, p => p.Data);

    /// <summary>Decodes the largest mip held in the pack.</summary>
    public RgbaImage Decode() => Header.DecodeLargestAvailable(FtexsFiles());

    /// <summary>
    /// Gives the texture a new path: rewrites its code and every part code (keeping each part's
    /// extension), as needed when a texture at a vanilla path is moved to a mod's own path.
    /// </summary>
    public void Rename(string newFtexPath)
    {
        if (!newFtexPath.EndsWith(".ftex", StringComparison.Ordinal)) throw new ArgumentException("path must end in .ftex");
        ulong nameHash = FoxHash.PathCode64(newFtexPath) & ((1UL << FoxHash.ExtensionShift) - 1);
        PathCode = FoxHash.PathCode64(newFtexPath);
        for (int i = 0; i < Parts.Count; i++)
        {
            ulong ext = Parts[i].PathCode >> FoxHash.ExtensionShift;
            Parts[i] = Parts[i] with { PathCode = (ext << FoxHash.ExtensionShift) | nameHash };
        }
    }
}

/// <summary>
/// Texture pack (.pftxs): "PFTX" header, a "TEXL" list, then one "FTEX" block per texture holding
/// that texture's parts. SnakeBite never merges packs: a mod's pack replaces the vanilla one whole.
/// </summary>
public sealed class TexturePack
{
    public List<PackedTexture> Textures { get; } = [];

    public PackedTexture? Find(ulong ftexPathCode) => Textures.FirstOrDefault(t => t.PathCode == ftexPathCode);
    public PackedTexture? Find(string ftexPath) => Find(FoxHash.PathCode64(ftexPath));

    public static TexturePack Read(ReadOnlySpan<byte> d)
    {
        try { return ReadChecked(d); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("texture pack", e); }
    }

    private static TexturePack ReadChecked(ReadOnlySpan<byte> d)
    {
        if (!d[..4].SequenceEqual("PFTX"u8) || !d.Slice(0x10, 4).SequenceEqual("TEXL"u8))
            throw new InvalidDataException("Not a texture pack (.pftxs).");
        var pack = new TexturePack();
        int count = BinaryPrimitives.ReadInt32LittleEndian(d[0x18..]);
        int p = 0x20;
        for (int i = 0; i < count; i++)
        {
            if (!d.Slice(p, 4).SequenceEqual("FTEX"u8)) throw new InvalidDataException($"Texture block {i} has no FTEX tag.");
            int size = BinaryPrimitives.ReadInt32LittleEndian(d[(p + 4)..]);
            var block = d.Slice(p, size);
            var t = new PackedTexture { PathCode = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]) };
            int parts = BinaryPrimitives.ReadInt32LittleEndian(block[0x10..]);
            for (int k = 0; k < parts; k++)
            {
                var e = block.Slice(0x20 + k * 16, 16);
                ulong code = BinaryPrimitives.ReadUInt64LittleEndian(e);
                int off = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
                int len = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
                t.Parts.Add(new TexturePart(code, block.Slice(off, len).ToArray()));
            }
            pack.Textures.Add(t);
            p += size;
        }
        return pack;
    }

    /// <summary>
    /// Parts follow the part table directly, with no padding. Vanilla parts are all multiples of
    /// 16 bytes anyway; mod packs that work in game (True Black Ocelot, PW Battle Dress) have
    /// unpadded 8-byte-aligned parts. Both read back byte for byte.
    /// </summary>
    public byte[] Write()
    {
        var blocks = new List<byte[]>();
        foreach (var t in Textures)
        {
            int pos = 0x20 + t.Parts.Count * 16;
            var offsets = new List<int>();
            foreach (var part in t.Parts) { offsets.Add(pos); pos += part.Data.Length; }
            byte[] b = new byte[pos];
            "FTEX"u8.CopyTo(b);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), b.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), t.PathCode);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x10), t.Parts.Count);
            for (int k = 0; k < t.Parts.Count; k++)
            {
                var e = b.AsSpan(0x20 + k * 16, 16);
                BinaryPrimitives.WriteUInt64LittleEndian(e, t.Parts[k].PathCode);
                BinaryPrimitives.WriteInt32LittleEndian(e[8..], offsets[k]);
                BinaryPrimitives.WriteInt32LittleEndian(e[12..], t.Parts[k].Data.Length);
                t.Parts[k].Data.CopyTo(b, offsets[k]);
            }
            blocks.Add(b);
        }
        int total = 0x20 + blocks.Sum(b => b.Length);
        byte[] o = new byte[total];
        "PFTX"u8.CopyTo(o);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), 0x40000000); // 2.0f
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(8), 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(12), 1);
        "TEXL"u8.CopyTo(o.AsSpan(0x10));
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(0x14), total - 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(0x18), Textures.Count);
        int p = 0x20;
        foreach (var b in blocks) { b.CopyTo(o, p); p += b.Length; }
        return o;
    }

}
