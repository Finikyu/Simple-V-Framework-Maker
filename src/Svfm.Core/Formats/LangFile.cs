using System.Buffers.Binary;
using System.Text;
using Svfm.Core.Hashing;

namespace Svfm.Core.Formats;

/// <summary>One text entry: key = StrCode32 of the language ID (e.g. "name_st_19033").</summary>
public sealed record LangEntry(uint Key, string Text, ushort Color = 1);

/// <summary>
/// Language file (.lng2). Read in either byte order; written big-endian version 3, the way
/// FoxEngine.TranslationTool (LangTool) writes them, which the game and every checked mod use.
/// </summary>
public sealed class LangFile
{
    public uint Version { get; set; } = 3;
    public List<LangEntry> Entries { get; } = [];

    public string? this[string langId] => Entries.FirstOrDefault(e => e.Key == FoxHash.StrCode32(langId))?.Text;
    public string? Get(uint key) => Entries.FirstOrDefault(e => e.Key == key)?.Text;

    public void Set(string langId, string text, ushort color = 1) => Set(FoxHash.StrCode32(langId), text, color);

    public void Set(uint key, string text, ushort color = 1)
    {
        int i = Entries.FindIndex(e => e.Key == key);
        var entry = new LangEntry(key, text, color);
        if (i >= 0) Entries[i] = entry; else Entries.Add(entry);
    }

    public static LangFile Read(byte[] b)
    {
        try { return ReadChecked(b); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("text (.lng2)", e); }
    }

    private static LangFile ReadChecked(byte[] b)
    {
        if (!b.AsSpan(0, 4).SequenceEqual("LANG"u8)) throw new InvalidDataException("Not a .lng2 file.");
        bool le = b[8] == (byte)'L';
        uint U32(int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        var f = new LangFile { Version = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)) };
        int count = (int)U32(12), values = (int)U32(16), keys = (int)U32(20);
        // Keys are stored sorted; values in the order the author wrote them. Entries keep the value
        // order so that writing the file again gives the same bytes.
        var rows = new List<(int offset, uint key)>(); // no capacity: a damaged count would ask for gigabytes
        for (int i = 0; i < count; i++)
            rows.Add(((int)U32(keys + i * 8 + 4), U32(keys + i * 8)));
        foreach (var (offset, key) in rows.OrderBy(r => r.offset))
        {
            int p = values + offset;
            ushort color = le ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
            int end = b.AsSpan(p + 2).IndexOf((byte)0);
            f.Entries.Add(new LangEntry(key, Encoding.UTF8.GetString(b, p + 2, end), color));
        }
        return f;
    }

    /// <summary>
    /// Big-endian v3. Values in entry order, then keys sorted ascending. The colour is written as
    /// LangTool does: a value below 256 goes in the high byte. The value block always gets 1-4 pad bytes.
    /// </summary>
    public byte[] Write()
    {
        var values = new MemoryStream();
        var offsets = new List<uint>();
        Span<byte> c = stackalloc byte[2];
        foreach (var e in Entries)
        {
            offsets.Add((uint)values.Length);
            BinaryPrimitives.WriteUInt16BigEndian(c, e.Color < 256 ? (ushort)(e.Color << 8) : e.Color);
            values.Write(c);
            // A NUL ends the value, so one in pasted text would cut it short: it is dropped.
            values.Write(Encoding.UTF8.GetBytes(e.Text.Replace("\0", "")));
            values.WriteByte(0);
        }
        int pad = 4 - (int)(values.Length % 4);
        values.Write(new byte[pad]);

        var keyed = Entries.Select((e, i) => (e.Key, offsets[i])).OrderBy(k => k.Key).ToList();
        byte[] o = new byte[0x18 + values.Length + keyed.Count * 8];
        "LANG"u8.CopyTo(o);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(4), Version);
        "BE"u8.CopyTo(o.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(12), (uint)Entries.Count);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(16), 0x18);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(20), (uint)(0x18 + values.Length));
        values.ToArray().CopyTo(o, 0x18);
        int k = 0x18 + (int)values.Length;
        foreach (var (key, off) in keyed)
        {
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(k), key);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(k + 4), off);
            k += 8;
        }
        return o;
    }
}
