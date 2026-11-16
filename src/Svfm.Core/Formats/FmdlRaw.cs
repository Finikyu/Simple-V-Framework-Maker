using System.Buffers.Binary;

namespace Svfm.Core.Formats;

/// <summary>
/// An .fmdl split into its raw feature and buffer arrays, and written back. <see cref="FmdlModel"/> reads a model for the
/// renderer and keeps no raw data; this class is the base of the model merge (part models into the receiver model,
/// <c>.docs/Research_ModelMerge.md</c>). Layout of all 336 vanilla chimera part models (v2.04): 56-byte header, the
/// feature table at 64, then the buffer table, the feature data at the next 16 bytes, then the buffer data. Each feature
/// starts at its own alignment (<see cref="Align"/>), and the feature data ends on 16 bytes. Pad bytes are zero.
/// </summary>
public sealed class FmdlRaw
{
    /// <summary>One feature: <see cref="Data"/> is its entries only, <c>Count * EntrySize(Type)</c> bytes, no pad.</summary>
    public sealed class Feature
    {
        public required int Type { get; init; }
        public required int Count { get; set; }
        public required byte[] Data { get; set; }
    }

    public sealed class Buffer
    {
        public required int Type { get; init; }
        public required byte[] Data { get; set; }
    }

    public float Version { get; set; }
    public List<Feature> Features { get; } = [];
    public List<Buffer> Buffers { get; } = [];

    /// <summary>Entry size per feature type (fmdl.bt; type 5 is 68 bytes in the game files, not 64).</summary>
    public static int EntrySize(int type) => type switch
    {
        0 => 48, 1 => 8, 2 => 32, 3 => 48, 4 => 16, 5 => 68, 6 => 4, 7 => 4, 8 => 4, 9 => 8, 10 => 8, 11 => 4,
        12 => 8, 13 => 32, 14 => 16, 16 => 16, 17 => 8, 18 => 8, 20 => 125, 21 => 8, 22 => 8,
        _ => throw new InvalidDataException($"The model has a feature of unknown type {type}."),
    };

    /// <summary>Start alignment per feature type, from the offsets in the vanilla files (the round-trip test checks it).</summary>
    public static int Align(int type) => type is 13 or 14 or 16 or 17 ? 16 : type is >= 6 and <= 11 ? 4 : 8;

    public Feature? Get(int type) => Features.FirstOrDefault(f => f.Type == type);
    public Buffer? GetBuffer(int type) => Buffers.FirstOrDefault(b => b.Type == type);

    public static FmdlRaw Read(byte[] d)
    {
        try { return ReadChecked(d); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("model", e); }
    }

    private static FmdlRaw ReadChecked(byte[] d)
    {
        if (!d.AsSpan(0, 4).SequenceEqual("FMDL"u8)) throw new InvalidDataException("Not an .fmdl file.");
        var r = new FmdlRaw { Version = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(4)) };
        int p = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(8)));
        int nf = I32(d, 0x20), nb = I32(d, 0x24), fo = I32(d, 0x28), bo = I32(d, 0x30);
        for (int i = 0; i < nf; i++, p += 8)
        {
            int type = d[p], count = d[p + 1] << 16 | BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2));
            int off = fo + I32(d, p + 4);
            r.Features.Add(new Feature { Type = type, Count = count, Data = d.AsSpan(off, count * EntrySize(type)).ToArray() });
        }
        for (int i = 0; i < nb; i++, p += 12)
            r.Buffers.Add(new Buffer { Type = I32(d, p), Data = d.AsSpan(bo + I32(d, p + 4), I32(d, p + 8)).ToArray() });
        return r;
    }

    public byte[] Write()
    {
        var feats = Features.OrderBy(f => f.Type).ToList();
        var bufs = Buffers.OrderBy(b => b.Type).ToList();
        int tableEnd = 64 + feats.Count * 8 + bufs.Count * 12;
        int fo = Up(tableEnd, 16);
        var featOffsets = new int[feats.Count];
        int fs = 0;
        for (int i = 0; i < feats.Count; i++)
        {
            if (feats[i].Data.Length != feats[i].Count * EntrySize(feats[i].Type))
                throw new InvalidOperationException($"Feature {feats[i].Type}: {feats[i].Data.Length} bytes for {feats[i].Count} entries.");
            fs = Up(fs, Align(feats[i].Type));
            featOffsets[i] = fs;
            fs += feats[i].Data.Length;
        }
        fs = Up(fs, 16);
        var bufOffsets = new int[bufs.Count];
        int bs = 0;
        for (int i = 0; i < bufs.Count; i++) { bs = Up(bs, 16); bufOffsets[i] = bs; bs += bufs[i].Data.Length; }
        int bo = fo + fs;

        var d = new byte[bo + bs];
        "FMDL"u8.CopyTo(d);
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(16), feats.Aggregate(0UL, (m, f) => m | 1UL << f.Type));
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(24), bufs.Aggregate(0UL, (m, b) => m | 1UL << b.Type));
        int[] head = [feats.Count, bufs.Count, fo, fs, bo, bs];
        for (int i = 0; i < head.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x20 + i * 4), head[i]);
        int p = 64;
        for (int i = 0; i < feats.Count; i++, p += 8)
        {
            var f = feats[i];
            if (f.Count > 0xFFFFFF) throw new InvalidOperationException($"Feature {f.Type} has too many entries.");
            d[p] = (byte)f.Type;
            d[p + 1] = (byte)(f.Count >> 16);
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(p + 2), (ushort)f.Count);
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(p + 4), featOffsets[i]);
            f.Data.CopyTo(d, fo + featOffsets[i]);
        }
        for (int i = 0; i < bufs.Count; i++, p += 12)
        {
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(p), bufs[i].Type);
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(p + 4), bufOffsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(p + 8), bufs[i].Data.Length);
            bufs[i].Data.CopyTo(d, bo + bufOffsets[i]);
        }
        return d;
    }

    private static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
    private static int Up(int v, int a) => (v + a - 1) / a * a;
}
