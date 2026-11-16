using System.Buffers.Binary;
using Svfm.Core.Hashing;

namespace Svfm.Core.Formats;

/// <summary>
/// Form variation file (.fv2, "FOV2win"): how a look changes a model (texture swaps, meshes
/// hidden, models or physics attached). Only the part the converter needs is decoded: every .fv2
/// ends in a table of 64-bit path codes of the files it uses, starting at the u16 offset at 0x0A
/// (checked on all 356 vanilla and mod .fv2 files seen: camos, faces, heads, arms). A camo port
/// copies the vanilla .fv2 and points its texture codes at the new textures; everything else is
/// kept byte for byte.
/// </summary>
public sealed class Fv2File
{
    private readonly byte[] _data;

    public Fv2File(byte[] data)
    {
        if (data.Length < 0x10 || !data.AsSpan(0, 7).SequenceEqual("FOV2win"u8))
            throw new InvalidDataException("Not an .fv2 file.");
        int off = FileTableOffset(data);
        if (off > data.Length || (data.Length - off) % 8 != 0)
            throw new InvalidDataException(".fv2 file table does not fit the file.");
        _data = (byte[])data.Clone();
    }

    private static int FileTableOffset(ReadOnlySpan<byte> d) => BinaryPrimitives.ReadUInt16LittleEndian(d[0x0A..]);

    public int FileCount => (_data.Length - FileTableOffset(_data)) / 8;

    /// <summary>Path codes of the files this .fv2 uses (textures, models, .sim), in table order.</summary>
    public IReadOnlyList<ulong> Files
    {
        get
        {
            int off = FileTableOffset(_data);
            var list = new ulong[FileCount];
            for (int i = 0; i < list.Length; i++) list[i] = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(off + i * 8));
            return list;
        }
    }

    public IEnumerable<ulong> Textures => Files.Where(c => FoxHash.ExtensionOf(c) == "ftex");

    public void SetFile(int index, ulong pathCode)
    {
        if ((uint)index >= (uint)FileCount) throw new ArgumentOutOfRangeException(nameof(index));
        BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(FileTableOffset(_data) + index * 8), pathCode);
    }

    /// <summary>Replaces every reference to one file with another. Returns how many were replaced.</summary>
    public int Replace(ulong oldCode, ulong newCode)
    {
        var files = Files;
        int n = 0;
        for (int i = 0; i < files.Count; i++)
            if (files[i] == oldCode) { SetFile(i, newCode); n++; }
        return n;
    }

    public int Replace(string oldPath, string newPath) => Replace(FoxHash.PathCode64(oldPath), FoxHash.PathCode64(newPath));

    public byte[] Write() => (byte[])_data.Clone();

    /// <summary>An .fv2 that changes nothing (#85): the 48 bytes of the vanilla hyu0_main0_v00.fv2 and oce0_main0_v00.fv2
    /// (no groups, no files; 0x08, 0x0A and 0x10 = 0x30).</summary>
    public static Fv2File Empty()
    {
        var d = new byte[0x30];
        "FOV2win"u8.CopyTo(d);
        d[7] = 1;
        d[0x08] = d[0x0A] = d[0x10] = 0x30;
        return new Fv2File(d);
    }

    /// <summary>One model attached by an attach-only .fv2: the model and, optionally, its cloth physics (.sim).</summary>
    public sealed record ModelAttach(ulong Model, ulong? Sim = null);

    /// <summary>
    /// Builds an attach-only .fv2 (no texture swaps, no meshes hidden), like the vanilla Snake face, bandana face, arm
    /// and gas mask files. Layout read from those (Oct 24): the block at 0x20 opens with 8 count bytes; byte 4 is the
    /// number of attached models. Each attachment is 6 × u16 file-table indices (model, frdv, two unknown, sim, unknown;
    /// 0xFFFF = none; the bandana face puts its .sim in the fifth, the arm its .frdv in the second), then a u32 0 closes
    /// the block. 0x08 = end of the block (section table, empty), 0x0A and 0x10 = file table (16-aligned), 0x0E = file
    /// count, 0x18 = texture count (0). Vanilla files only attach one model; more than one follows the same layout
    /// and needs an in-game check.
    /// </summary>
    public static Fv2File Attach(IReadOnlyList<ModelAttach> models)
    {
        if (models.Count is 0 or > 255) throw new ArgumentOutOfRangeException(nameof(models));
        var files = new List<ulong>();
        ushort Index(ulong code) { int i = files.IndexOf(code); if (i < 0) { files.Add(code); i = files.Count - 1; } return (ushort)i; }
        var entries = models.Select(m => (model: Index(m.Model), sim: m.Sim is { } s ? Index(s) : (ushort)0xFFFF)).ToList();
        int end = 0x28 + entries.Count * 12 + 4;
        int table = (end + 15) & ~15;
        var d = new byte[table + files.Count * 8];
        "FOV2win"u8.CopyTo(d);
        d[7] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x08), (ushort)end);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x0A), (ushort)table);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x0E), (ushort)files.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x10), (uint)table);
        d[0x24] = (byte)entries.Count;
        for (int i = 0; i < entries.Count; i++)
        {
            int o = 0x28 + i * 12;
            d.AsSpan(o, 12).Fill(0xFF);
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), entries[i].model);
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 8), entries[i].sim);
        }
        for (int i = 0; i < files.Count; i++) BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(table + i * 8), files[i]);
        return new Fv2File(d);
    }

    /// <summary>The attachments of an attach-only .fv2 (see <see cref="Attach"/>); empty if it attaches nothing.</summary>
    public IReadOnlyList<ModelAttach> Attachments()
    {
        if (_data.Length < 0x28) return [];
        int n = _data[0x24];
        if (_data[0x20] != 0 || _data[0x21] != 0 || _data[0x22] != 0 || 0x28 + n * 12 > _data.Length) return [];
        var files = Files;
        var list = new List<ModelAttach>();
        for (int i = 0; i < n; i++)
        {
            int o = 0x28 + i * 12;
            ushort m = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(o)), s = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(o + 8));
            if (m >= files.Count) return [];
            list.Add(new ModelAttach(files[m], s < files.Count ? files[s] : null));
        }
        return list;
    }

    /// <summary>One texture swap: on the material whose StrCode32 name hash is <see cref="Material"/>, slot
    /// <see cref="Slot"/> (StrCode32 of e.g. "Base_Tex_SRGB") gets the texture <see cref="Texture"/>.</summary>
    public sealed record TextureSwap(int Group, uint Material, uint Slot, ulong Texture);

    /// <summary>
    /// The texture-swap groups. Layout (decoded on the Snake camo, naked camo and DD camo files): the first
    /// group starts at 0x20, a second one (if any) at the u16 offset at 0x08. A group is: u16 id, u16 count n,
    /// 4 bytes, then n × u32 material name hash (StrCode32 = low 32 bits of the model's StrCode64 name),
    /// n × u32 slot name hash, n × u16 texture index into the file table, n × u16 (0xFFFF in camos).
    /// Groups that don't fit this layout are skipped.
    /// </summary>
    public IReadOnlyList<TextureSwap> TextureSwaps()
    {
        var list = new List<TextureSwap>();
        var files = Files;
        int table = FileTableOffset(_data);
        var starts = new List<int> { 0x20 };
        int second = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x08));
        if (second > 0x20 && second < table) starts.Add(second);
        foreach (int g in starts)
        {
            if (g + 8 > table) continue;
            int id = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(g));
            int n = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(g + 2));
            if (n == 0 || g + 8 + n * 12 > table) continue;
            int mat = g + 8, slot = mat + n * 4, tex = slot + n * 4;
            for (int i = 0; i < n; i++)
            {
                int ti = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(tex + i * 2));
                if (ti >= files.Count) continue;
                list.Add(new TextureSwap(id, BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(mat + i * 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(slot + i * 4)), files[ti]));
            }
        }
        return list;
    }
}
