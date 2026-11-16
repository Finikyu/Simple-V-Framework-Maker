using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Svfm.Core.Formats;

/// <summary>
/// Connect points of a weapon part (.fcnp, FoxData; layout from fcnp.bt, checked on vanilla receiver, barrel, muzzle
/// and option files). The node name has no <c>CNP_</c> prefix. The transform is relative to the Parent bone, in metres,
/// +Z to the muzzle. Rotation is a quaternion (x, y, z, w).
/// </summary>
public static class Fcnp
{
    public sealed record Point(string Name, string Parent, Vector3 Translation, Quaternion Rotation, Vector3 Scale);

    public static IReadOnlyList<Point> Read(byte[] d)
    {
        try { return ReadChecked(d); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("connect point file", e); }
    }

    private static IReadOnlyList<Point> ReadChecked(byte[] d)
    {
        var points = new List<Point>();
        int node = (int)U32(d, 4);
        if (node == 0) return points;
        for (int guard = 0; guard < 1000; guard++)
        {
            uint so = U32(d, node + 4);
            string name = so != 0 ? CStr(d, node + (int)so) : "";
            if (name.StartsWith("CNP_", StringComparison.Ordinal)) name = name[4..];
            int dataOff = (int)U32(d, node + 12), dataSize = (int)U32(d, node + 16);
            int next = (int)U32(d, node + 32), parOff = (int)U32(d, node + 36);
            string parent = "";
            if (parOff != 0) { int p = node + parOff; parent = CStr(d, p + 12 + (int)U32(d, p + 16)); }
            if (dataOff != 0 && dataSize >= 48)
            {
                int o = node + dataOff;
                points.Add(new Point(name, parent, new Vector3(F(d, o), F(d, o + 4), F(d, o + 8)),
                    new Quaternion(F(d, o + 16), F(d, o + 20), F(d, o + 24), F(d, o + 28)),
                    new Vector3(F(d, o + 32), F(d, o + 36), F(d, o + 40))));
            }
            if (next == 0) return points;
            node += next;
        }
        throw new InvalidDataException("The connect point list does not end.");
    }

    /// <summary>The file name in the header: the model name, for example <c>hg09_main0_def</c>.</summary>
    public static string FileName(byte[] d) => CStr(d, 12 + (int)U32(d, 16));

    /// <summary>
    /// Writes a .fcnp with the layout of the vanilla files: a 32-byte header, one 96-byte node per point (48 bytes of
    /// node, then the transform), one 20-byte Parent parameter per point, then the strings, each one time. All offsets
    /// are relative to the field or the node that holds them. Each string has a 32-bit hash that is not StrCode32, FNV
    /// or Murmur; it comes from <paramref name="hashes"/> (see <see cref="Hashes"/>), so every string must be in a source file.
    /// </summary>
    public static byte[] Write(string fileName, IReadOnlyList<Point> points, IReadOnlyDictionary<string, uint> hashes)
    {
        uint H(string s) => hashes.TryGetValue(s, out uint h) ? h : throw new KeyNotFoundException($"No hash for \"{s}\" in the source connect point files.");
        int n = points.Count, pars = 32 + n * 96, strs = pars + n * 20;
        var text = new List<byte>();
        var at = new Dictionary<string, int>();
        int Str(string s)
        {
            if (!at.TryGetValue(s, out int o)) { at[s] = o = strs + text.Count; text.AddRange(Encoding.Latin1.GetBytes(s)); text.Add(0); }
            return o;
        }
        int fileStr = Str(fileName);
        var names = points.Select(p => (Name: Str("CNP_" + p.Name), Key: Str("Parent"), Parent: Str(p.Parent))).ToList();
        var d = new byte[strs + text.Count];
        text.CopyTo(d, strs);
        void W(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
        void Wf(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o), v);
        W(4, 32); W(8, (uint)d.Length); W(12, H(fileName)); W(16, (uint)(fileStr - 12));
        for (int i = 0; i < n; i++)
        {
            var pt = points[i];
            int o = 32 + i * 96, p = pars + i * 20;
            W(o, H("CNP_" + pt.Name)); W(o + 4, (uint)(names[i].Name - o)); W(o + 12, 48); W(o + 16, 48);
            if (i > 0) W(o + 28, unchecked((uint)-96));
            if (i < n - 1) W(o + 32, 96);
            W(o + 36, (uint)(p - o));
            float[] v = [pt.Translation.X, pt.Translation.Y, pt.Translation.Z, 1, pt.Rotation.X, pt.Rotation.Y, pt.Rotation.Z, pt.Rotation.W, pt.Scale.X, pt.Scale.Y, pt.Scale.Z, 1];
            for (int k = 0; k < 12; k++) Wf(o + 48 + k * 4, v[k]);
            W(p, 1); W(p + 4, H("Parent")); W(p + 8, (uint)(names[i].Key - (p + 4)));
            W(p + 12, H(pt.Parent)); W(p + 16, (uint)(names[i].Parent - (p + 12)));
        }
        return d;
    }

    /// <summary>Every string of the file with its stored hash: the file name, the node names (with <c>CNP_</c>), "Parent" and the bone names.</summary>
    public static Dictionary<string, uint> Hashes(byte[] d)
    {
        var h = new Dictionary<string, uint> { [FileName(d)] = U32(d, 12) };
        int node = (int)U32(d, 4);
        for (int guard = 0; node != 0 && guard < 1000; guard++)
        {
            if (U32(d, node + 4) is var so and not 0) h[CStr(d, node + (int)so)] = U32(d, node);
            if (U32(d, node + 36) is var po and not 0)
            {
                int p = node + (int)po;
                h[CStr(d, p + 4 + (int)U32(d, p + 8))] = U32(d, p + 4);
                h[CStr(d, p + 12 + (int)U32(d, p + 16))] = U32(d, p + 12);
            }
            if (U32(d, node + 32) is var next and not 0) node += (int)next; else break;
        }
        return h;
    }

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    private static float F(byte[] d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));
    private static string CStr(byte[] d, int o) { int e = Array.IndexOf(d, (byte)0, o); return Encoding.Latin1.GetString(d, o, e - o); }
}
