using System.Buffers.Binary;
using System.Text;
using Svfm.Core.Hashing;

namespace Svfm.Core.Formats;

/// <summary>
/// Minimal reader/editor for Fox Engine binary data sets ("\xF2box": <c>.parts</c>, <c>.fox2</c>).
/// Layout (FoxTool): 0x20-byte header (magic, version, entity count, string-table offset, data offset);
/// entities with a 0x40 header (class-name hash at +0x1C, static/dynamic property counts at +0x24);
/// each property = 8-byte name hash, type, container, count, value offset, total size; string and path
/// values are 48-bit string hashes resolved through the string table at the end. After the table: an 8-byte zero
/// terminator, padding to 16, two zero bytes, "end\0", padding to 16 (all vanilla .parts checked); the header
/// stores no file size, so the table can grow.
/// </summary>
public sealed class FoxDataSet
{
    /// <summary>
    /// The hash a data set stores for an empty string or path (as in every vanilla .parts, e.g. Raiden's
    /// facialSettingFile). Data sets hash names with their own 48-bit string hash, not StrCode64, so names
    /// are matched through the file's string table rather than by hashing.
    /// </summary>
    public const ulong EmptyStringHash = 0xB8A0BF169F98;

    private const uint Magic = 0x786F62F2;
    private const byte TypeString = 11, TypePath = 12, TypeFilePtr = 20;

    public byte[] Data { get; }
    public IReadOnlyDictionary<ulong, string> Strings => _strings;
    public IReadOnlyList<Entity> Entities => _entities;

    private readonly Dictionary<ulong, string> _strings = new();
    private readonly List<Entity> _entities = new();

    public sealed record Property(int Offset, ulong NameHash, byte Type, byte Container, int Count, int ValueOffset, int Size);
    public sealed record Entity(int Offset, ulong ClassHash, IReadOnlyList<Property> Properties);

    private FoxDataSet(byte[] data)
    {
        Data = data;
        var d = data.AsSpan();
        if (d.Length < 0x20 || BinaryPrimitives.ReadUInt32LittleEndian(d) != Magic)
            throw new InvalidDataException("Not a Fox binary data set.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
        int stringOffset = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        int o = BinaryPrimitives.ReadInt32LittleEndian(d[16..]);

        for (int s = stringOffset; s + 12 <= d.Length;)
        {
            ulong h = BinaryPrimitives.ReadUInt64LittleEndian(d[s..]);
            if (h == 0) break;
            int n = BinaryPrimitives.ReadInt32LittleEndian(d[(s + 8)..]);
            _strings[h] = Encoding.UTF8.GetString(d.Slice(s + 12, n));
            s += 12 + n;
        }

        for (int e = 0; e < count; e++)
        {
            int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
            ulong cls = BinaryPrimitives.ReadUInt64LittleEndian(d[(o + 0x1C)..]);
            int props = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 0x24)..]) + BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 0x26)..]);
            var list = new List<Property>(props);
            int p = o + headerSize;
            for (int k = 0; k < props; k++)
            {
                var prop = new Property(p, BinaryPrimitives.ReadUInt64LittleEndian(d[p..]), d[p + 8], d[p + 9],
                    BinaryPrimitives.ReadInt16LittleEndian(d[(p + 10)..]), BinaryPrimitives.ReadInt16LittleEndian(d[(p + 12)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(d[(p + 14)..]));
                if (prop.Size <= 0) throw new InvalidDataException($"Bad property size at 0x{p:X}.");
                list.Add(prop);
                p += prop.Size;
            }
            _entities.Add(new Entity(o, cls, list));
            o = p;
        }
    }

    /// <summary>Reads a copy of the data (edits never touch the caller's array).</summary>
    public static FoxDataSet Read(byte[] data)
    {
        try { return new((byte[])data.Clone()); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("data set (.parts)", e); }
    }

    /// <summary>The name of a hash, if the string table has it.</summary>
    public string? Name(ulong hash) => _strings.TryGetValue(hash, out var s) ? s : hash == EmptyStringHash ? "" : null;

    /// <summary>The model files that a .parts file names (ModelDescription.modelFile), in file order. A weapon receiver, a body and a buddy package name one.</summary>
    public IEnumerable<string?> ModelFiles => StringValues("ModelDescription", "modelFile").Select(v => v.value);

    /// <summary>The single string/path value of a property on every entity of a class.</summary>
    public IEnumerable<(Entity entity, string? value)> StringValues(string className, string property)
    {
        foreach (var (e, p) in Find(className, property))
            yield return (e, Name(BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(p.Offset + p.ValueOffset))));
    }

    /// <summary>
    /// Empties a single string/path/file property on every entity of a class (as vanilla files store an unset value).
    /// Returns how many values changed.
    /// </summary>
    public int ClearString(string className, string property)
    {
        int changed = 0;
        foreach (var (_, p) in Find(className, property))
        {
            var at = Data.AsSpan(p.Offset + p.ValueOffset);
            if (BinaryPrimitives.ReadUInt64LittleEndian(at) == EmptyStringHash) continue;
            BinaryPrimitives.WriteUInt64LittleEndian(at, EmptyStringHash);
            changed++;
        }
        return changed;
    }

    /// <summary>Single string/path/file values named <paramref name="property"/> on entities of a class.</summary>
    private IEnumerable<(Entity, Property)> Find(string className, string property)
    {
        var cls = _strings.Where(kv => kv.Value == className).Select(kv => kv.Key).ToHashSet();
        var name = _strings.Where(kv => kv.Value == property).Select(kv => kv.Key).ToHashSet();
        foreach (var e in _entities.Where(e => cls.Contains(e.ClassHash)))
            foreach (var p in e.Properties.Where(p => name.Contains(p.NameHash) && IsStringLike(p) && p.Count == 1 && p.Container == 0))
                yield return (e, p);
    }

    /// <summary>
    /// A copy with a single string/path/file property set on every entity of a class. The value's hash must be
    /// given (data sets use their own string hash; take it from a vanilla file that holds the same string), and the
    /// string is added to the table when missing.
    /// </summary>
    public FoxDataSet WithString(string className, string property, string value, ulong valueHash)
    {
        var data = (byte[])Data.Clone();
        foreach (var (_, p) in Find(className, property))
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(p.Offset + p.ValueOffset), valueHash);
        return WithStringInTable(data, value, valueHash);
    }

    /// <summary>
    /// A copy where every single string/path/file property holding <paramref name="oldValue"/>, on any entity, holds
    /// <paramref name="newValue"/> instead (data-set string hashes are StrCode64, checked against vanilla .parts and
    /// .fox2 files). <paramref name="changed"/> counts the values changed.
    /// </summary>
    public FoxDataSet Replacing(string oldValue, string newValue, out int changed)
    {
        ulong from = FoxHash.StrCode64(oldValue), to = FoxHash.StrCode64(newValue);
        var data = (byte[])Data.Clone();
        changed = 0;
        foreach (var e in _entities)
            foreach (var p in e.Properties.Where(p => IsStringLike(p) && p.Count == 1 && p.Container == 0))
            {
                var at = data.AsSpan(p.Offset + p.ValueOffset);
                if (BinaryPrimitives.ReadUInt64LittleEndian(at) != from) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(at, to);
                changed++;
            }
        return changed == 0 ? this : WithStringInTable(data, newValue, to);
    }

    /// <summary>
    /// A copy where every single string/path/file property holding <paramref name="value"/>, on any entity, is empty
    /// (as vanilla files store an unset value). <paramref name="changed"/> counts the values cleared.
    /// </summary>
    public FoxDataSet Clearing(string value, out int changed)
    {
        ulong h = FoxHash.StrCode64(value);
        var data = (byte[])Data.Clone();
        changed = 0;
        foreach (var e in _entities)
            foreach (var p in e.Properties.Where(p => IsStringLike(p) && p.Count == 1 && p.Container == 0))
            {
                var at = data.AsSpan(p.Offset + p.ValueOffset);
                if (BinaryPrimitives.ReadUInt64LittleEndian(at) != h) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(at, EmptyStringHash);
                changed++;
            }
        return changed == 0 ? this : new FoxDataSet(data);
    }

    /// <summary>
    /// A copy with entity <paramref name="e"/> of <paramref name="src"/> appended (a part's EffectDescription, for a gun
    /// whose part models are merged into the receiver): a new address, the name <paramref name="newName"/>, this file's
    /// DataSet as owner, its depends links on this file's ModelDescription (<paramref name="selfPath"/> is this file's
    /// path), an entry in the DataSet's dataList, and the strings of <paramref name="src"/>. Entity header (291 vanilla
    /// chimera .parts): +0x0A address (the EntityHandle value), +0x12 id, +0x2C and +0x30 entity size; a depends entry
    /// is package path, archive path (own path), name in archive, address.
    /// </summary>
    public FoxDataSet WithEntityCopy(FoxDataSet src, Entity e, string newName, string selfPath)
    {
        var d = Data;
        Entity Of(string cls) => _entities.First(x => Name(x.ClassHash) == cls);
        Property Prop(FoxDataSet f, Entity x, string name) => x.Properties.First(p => f.Name(p.NameHash) == name);
        uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
        void W64(byte[] b, int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), v);
        Entity ds = Of("DataSet"), md = Of("ModelDescription");
        var nameP = Prop(this, md, "name");
        ulong mdName = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(nameP.Offset + nameP.ValueOffset)), newHash = FoxHash.StrCode64(newName);
        uint dsAddr = U32(d, ds.Offset + 0x0A), mdAddr = U32(d, md.Offset + 0x0A);
        uint newAddr = _entities.Max(x => U32(d, x.Offset + 0x0A)) + 0x10, newId = _entities.Max(x => U32(d, x.Offset + 0x12)) + 1;

        int size = (int)U32(src.Data, e.Offset + 0x2C);
        if (size != e.Properties[^1].Offset + e.Properties[^1].Size - e.Offset) throw new InvalidDataException("Entity size does not match its properties.");
        var copy = src.Data.AsSpan(e.Offset, size).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(0x0A), newAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(0x12), newId);
        foreach (var p in e.Properties)
        {
            int v = p.Offset - e.Offset + p.ValueOffset;
            switch (src.Name(p.NameHash))
            {
                case "name": W64(copy, v, newHash); break;
                case "dataSet": W64(copy, v, dsAddr); break;
                case "depends":
                    for (int i = 0; i < p.Count; i++)
                    {
                        W64(copy, v + i * 32 + 8, FoxHash.StrCode64(selfPath));
                        W64(copy, v + i * 32 + 16, mdName);
                        W64(copy, v + i * 32 + 24, mdAddr);
                    }
                    break;
            }
        }

        var dl = Prop(this, ds, "dataList");
        if (dl.Type != 13 || dl.Container != 2) throw new InvalidDataException("DataSet dataList is not a string map.");
        int insertAt = dl.Offset + dl.Size, stringOffset = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        var entry = new byte[16];
        W64(entry, 0, newHash);
        W64(entry, 8, newAddr);
        var w = new MemoryStream();
        w.Write(d, 0, insertAt);
        w.Write(entry);
        w.Write(d, insertAt, stringOffset - insertAt);
        w.Write(copy);
        w.Write(d, stringOffset, d.Length - stringOffset);
        var nd = w.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(8), _entities.Count + 1);
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(12), stringOffset + 16 + copy.Length);
        BinaryPrimitives.WriteInt16LittleEndian(nd.AsSpan(dl.Offset + 10), (short)(dl.Count + 1));
        BinaryPrimitives.WriteInt16LittleEndian(nd.AsSpan(dl.Offset + 14), (short)(dl.Size + 16));
        foreach (int at in new[] { 0x2C, 0x30 })
            BinaryPrimitives.WriteUInt32LittleEndian(nd.AsSpan(ds.Offset + at), U32(nd, ds.Offset + at) + 16);
        return new FoxDataSet(nd).WithStringsInTable(src._strings.Append(new(newHash, newName)));
    }

    private FoxDataSet WithStringInTable(byte[] data, string value, ulong valueHash) =>
        new FoxDataSet(data).WithStringsInTable([new(valueHash, value)]);

    private FoxDataSet WithStringsInTable(IEnumerable<KeyValuePair<ulong, string>> add)
    {
        var missing = add.Where(kv => !_strings.ContainsKey(kv.Key) && kv.Key != EmptyStringHash).DistinctBy(kv => kv.Key).ToList();
        if (missing.Count == 0) return this;
        var data = Data;
        int stringOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        int end = stringOffset;
        while (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(end)) != 0)
            end += 12 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(end + 8));
        var w = new MemoryStream();
        w.Write(data, 0, end);
        foreach (var (valueHash, value) in missing)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var head = new byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(head, valueHash);
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(8), bytes.Length);
            w.Write(head);
            w.Write(bytes);
        }
        w.Write(new byte[8]);
        while (w.Length % 16 != 0) w.WriteByte(0);
        w.Write([0, 0, (byte)'e', (byte)'n', (byte)'d', 0]);
        while (w.Length % 16 != 0) w.WriteByte(0);
        return new FoxDataSet(w.ToArray());
    }

    private static bool IsStringLike(Property p) => p.Type is TypeString or TypePath or TypeFilePtr;
}
