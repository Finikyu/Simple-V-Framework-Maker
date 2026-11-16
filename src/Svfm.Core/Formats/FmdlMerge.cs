using System.Buffers.Binary;
using System.Numerics;
using Svfm.Core.Hashing;

namespace Svfm.Core.Formats;

/// <summary>
/// Puts a weapon part model (barrel, magazine, sight, laser ...) into the receiver model. V Framework 1.7 draws only
/// the receiver model of a custom gun (<c>.docs/Research_ModelMerge.md</c>), so a part shows only when it is in that
/// model. The part's vertices are moved to the connect point (bone position + rotation * scale * vertex + translation),
/// and its meshes are skinned to the connect point's parent bone, so a part on the slide moves with the slide. No bones
/// are added: a part with bones of its own (a folding stock, a scope cover) is skinned whole to the parent bone, so it
/// shows in its rest pose and its own bones do not move. Receiver LOD settings stay; each part mesh gets one index slice
/// per receiver LOD.
/// </summary>
public static class FmdlMerge
{
    public sealed class NotMergeableException(string message) : Exception(message);

    /// <summary>A copy of <paramref name="receiver"/> with <paramref name="part"/> in it. The inputs do not change. A null <paramref name="parentBone"/> puts the part at the model origin on bone 0.</summary>
    public static FmdlRaw Merge(FmdlRaw receiver, FmdlRaw part, string? parentBone, Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        var r = Clone(receiver);
        foreach (var x in new[] { r, part })
        {
            if (x.Get(12) is { Count: > 0 } || x.GetBuffer(3) != null) throw new NotMergeableException("The model uses name strings, not name hashes.");
            var fb = F(x, 14);
            if (fb.Count != 3 || U16(fb.Data, 0) != 0 || U16(fb.Data, 16) != 0 || U16(fb.Data, 32) != 1)
                throw new NotMergeableException("The model does not have the 3 usual mesh buffers.");
        }

        // Receiver counts before the append.
        int nGrp = F(r, 1).Count, nMesh = F(r, 3).Count, nMat = F(r, 4).Count, nBoneGrp = F(r, 5).Count, nTex = F(r, 6).Count,
            nParam = F(r, 7).Count, nShader = F(r, 8).Count, nLayout = F(r, 9).Count, nBufHdr = F(r, 10).Count,
            nElem = F(r, 11).Count, nBox = F(r, 13).Count, nPath = F(r, 21).Count, nName = F(r, 22).Count;
        int vec16 = (r.GetBuffer(0)?.Data.Length ?? 0) / 16;
        int lods = Lods(r), partLods = Lods(part);

        var names = F(r, 22);
        int pb = parentBone == null ? 0 : -1;
        ulong boneHash = parentBone == null ? 0 : FoxHash.StrCode64(parentBone);
        for (int i = 0; i < F(r, 0).Count && pb < 0; i++)
            if (U64(names.Data, U16(F(r, 0).Data, i * 48) * 8) == boneHash) pb = i;
        if (pb < 0) throw new NotMergeableException($"The receiver has no bone {parentBone}.");
        var bonePos = parentBone == null ? Vector3.Zero : Vec3(F(r, 0).Data, pb * 48 + 32);
        Vector3 Place(Vector3 v) => bonePos + Vector3.Transform(v * scale, rotation) + translation;

        // Names and path codes.
        Append(names, F(part, 22).Data, F(part, 22).Count);
        Append(F(r, 21), F(part, 21).Data, F(part, 21).Count);

        // Mesh groups: a part group with a receiver group's name (MESH_ROOT) gets a new name. The part root hangs under
        // the receiver root.
        var taken = new HashSet<ulong>();
        for (int i = 0; i < nGrp; i++) taken.Add(U64(names.Data, U16(F(r, 1).Data, i * 8) * 8));
        var grp = Copy(F(part, 1));
        for (int i = 0; i < F(part, 1).Count; i++)
        {
            int o = i * 8;
            int ni = U16(grp, o) + nName;
            if (taken.Contains(U64(names.Data, ni * 8)))
            {
                Append(names, BitConverter.GetBytes(FoxHash.StrCode64($"SVFM_MERGE_{nGrp + i}")), 1);
                ni = names.Count - 1;
            }
            W16(grp, o, ni);
            short parent = (short)U16(grp, o + 4);
            W16(grp, o + 4, parent < 0 ? 0 : parent + nGrp);
            if ((short)U16(grp, o + 6) != -1) throw new NotMergeableException("The part has a mesh group with vectors.");
        }
        Append(F(r, 1), grp, F(part, 1).Count);

        // Index slices: one per receiver LOD for each part mesh.
        var pMesh = F(part, 3).Data;
        var slices = F(r, 17);
        var meshSlice = new int[F(part, 3).Count];
        int rIndexCount = I32(F(r, 14).Data, 32 + 4) / 2;
        for (int m = 0; m < meshSlice.Length; m++)
        {
            meshSlice[m] = slices.Count;
            int first = I32(pMesh, m * 48 + 24);
            for (int k = 0; k < lods; k++)
                Append(slices, F(part, 17).Data.AsSpan((first + Math.Min(k, partLods - 1)) * 8, 8).ToArray(), 1);
        }

        // Mesh group defs.
        var defs = Copy(F(part, 2));
        for (int i = 0; i < F(part, 2).Count; i++)
        {
            int o = i * 32;
            int firstMesh = U16(defs, o + 8);
            W16(defs, o + 4, U16(defs, o + 4) + nGrp);
            W16(defs, o + 8, firstMesh + nMesh);
            W16(defs, o + 10, U16(defs, o + 10) + nBox);
            W16(defs, o + 16, firstMesh < meshSlice.Length ? meshSlice[firstMesh] : 0);
        }
        Append(F(r, 2), defs, F(part, 2).Count);

        // Meshes.
        var mesh = Copy(F(part, 3));
        for (int m = 0; m < meshSlice.Length; m++)
        {
            int o = m * 48;
            W16(mesh, o + 4, U16(mesh, o + 4) + nMat);
            W16(mesh, o + 6, U16(mesh, o + 6) + nBoneGrp);
            W16(mesh, o + 8, U16(mesh, o + 8) + nLayout);
            W32(mesh, o + 16, I32(mesh, o + 16) + rIndexCount);
            W32(mesh, o + 24, meshSlice[m]);
        }
        Append(F(r, 3), mesh, meshSlice.Length);

        // Materials, shaders, parameters, textures.
        var mats = Copy(F(part, 4));
        var texParam = new HashSet<int>();
        var vecParam = new HashSet<int>();
        for (int i = 0; i < F(part, 4).Count; i++)
        {
            int o = i * 16;
            for (int k = 0; k < mats[o + 6]; k++) texParam.Add(U16(mats, o + 8) + k);
            for (int k = 0; k < mats[o + 7]; k++) vecParam.Add(U16(mats, o + 10) + k);
            W16(mats, o, U16(mats, o) + nName);
            W16(mats, o + 4, U16(mats, o + 4) + nShader);
            W16(mats, o + 8, U16(mats, o + 8) + nParam);
            W16(mats, o + 10, U16(mats, o + 10) + nParam);
        }
        Append(F(r, 4), mats, F(part, 4).Count);
        var pars = Copy(F(part, 7));
        for (int i = 0; i < F(part, 7).Count; i++)
        {
            W16(pars, i * 4, U16(pars, i * 4) + nName);
            if (texParam.Contains(i)) W16(pars, i * 4 + 2, U16(pars, i * 4 + 2) + nTex);
            else if (vecParam.Contains(i)) W16(pars, i * 4 + 2, U16(pars, i * 4 + 2) + vec16);
        }
        Append(F(r, 7), pars, F(part, 7).Count);
        var shaders = Copy(F(part, 8));
        for (int i = 0; i < F(part, 8).Count * 2; i++) W16(shaders, i * 2, U16(shaders, i * 2) + nName);
        Append(F(r, 8), shaders, F(part, 8).Count);
        var tex = Copy(F(part, 6));
        for (int i = 0; i < F(part, 6).Count; i++) { W16(tex, i * 4, U16(tex, i * 4) + nName); W16(tex, i * 4 + 2, U16(tex, i * 4 + 2) + nPath); }
        Append(F(r, 6), tex, F(part, 6).Count);
        var b0 = r.GetBuffer(0);
        if (part.GetBuffer(0) is { } pb0)
        {
            if (b0 == null) r.Buffers.Add(b0 = new FmdlRaw.Buffer { Type = 0, Data = [] });
            b0.Data = [.. b0.Data, .. pb0.Data];
        }

        // Bone groups: the part root becomes the parent bone.
        var bg = Copy(F(part, 5));
        for (int i = 0; i < F(part, 5).Count; i++)
            for (int k = 0; k < U16(bg, i * 68 + 2); k++) W16(bg, i * 68 + 4 + k * 2, pb);
        Append(F(r, 5), bg, F(part, 5).Count);

        // Vertex layouts and buffers. The part's vertex data goes after the receiver's in each mesh buffer.
        var rf = F(r, 14).Data; var pf = F(part, 14).Data;
        var rb2 = r.GetBuffer(2)!.Data; var pb2 = part.GetBuffer(2)!.Data;
        byte[] Sub(byte[] b2, byte[] fb, int i) => b2.AsSpan(I32(fb, i * 16 + 8), I32(fb, i * 16 + 4)).ToArray();
        var pv = new[] { Sub(pb2, pf, 0), Sub(pb2, pf, 1) };
        var rSize = new[] { Up16(I32(rf, 4)), Up16(I32(rf, 20)) };
        var lay = Copy(F(part, 9));
        for (int i = 0; i < F(part, 9).Count; i++) { W16(lay, i * 8 + 4, U16(lay, i * 8 + 4) + nBufHdr); W16(lay, i * 8 + 6, U16(lay, i * 8 + 6) + nElem); }
        Append(F(r, 9), lay, F(part, 9).Count);
        var hdr = Copy(F(part, 10));
        var pHdr = F(part, 10).Data; var pEl = F(part, 11).Data; var pLay = F(part, 9).Data;
        for (int m = 0; m < meshSlice.Length; m++)
        {
            int l = U16(pMesh, m * 48 + 8), vcount = U16(pMesh, m * 48 + 10);
            int h0 = U16(pLay, l * 8 + 4), e = U16(pLay, l * 8 + 6);
            for (int h = h0; h < h0 + pLay[l * 8]; h++)
            {
                int fbi = pHdr[h * 8], stride = pHdr[h * 8 + 2], off = I32(pHdr, h * 8 + 4);
                for (int k = 0; k < pHdr[h * 8 + 1]; k++, e++)
                {
                    int usage = pEl[e * 4], type = pEl[e * 4 + 1], eo = U16(pEl, e * 4 + 2);
                    if (fbi > 1) continue;
                    var buf = pv[fbi];
                    for (int v = 0; v < vcount; v++)
                    {
                        int q = off + v * stride + eo;
                        if (usage == 0 && type == 1) WVec3(buf, q, Place(Vec3(buf, q)));
                        else if (usage is 2 or 14 && type == 6) WHalf3(buf, q, Vector3.Transform(Half3(buf, q), rotation));
                        else if (usage is 2 or 14 && rotation != Quaternion.Identity) throw new NotMergeableException($"The part has normals of type {type}.");
                    }
                }
            }
        }
        for (int i = 0; i < F(part, 10).Count; i++) if (hdr[i * 8] <= 1) W32(hdr, i * 8 + 4, I32(hdr, i * 8 + 4) + rSize[hdr[i * 8]]);
        Append(F(r, 10), hdr, F(part, 10).Count);
        Append(F(r, 11), pEl, F(part, 11).Count);

        var fbs = new[]
        {
            Pad16([.. Sub(rb2, rf, 0), .. new byte[rSize[0] - I32(rf, 4)], .. pv[0]]),
            Pad16([.. Sub(rb2, rf, 1), .. new byte[rSize[1] - I32(rf, 20)], .. pv[1]]),
            Pad16([.. Sub(rb2, rf, 2), .. Sub(pb2, pf, 2)]),
        };
        var newFb = (byte[])rf.Clone();
        int at = 0;
        for (int i = 0; i < 3; i++) { W32(newFb, i * 16 + 4, fbs[i].Length); W32(newFb, i * 16 + 8, at); at += fbs[i].Length; }
        F(r, 14).Data = newFb;
        r.GetBuffer(2)!.Data = [.. fbs[0], .. fbs[1], .. fbs[2]];

        // Bounding boxes: the part's, moved; the model box and the parent bone's box grow to hold the part.
        var boxes = Copy(F(part, 13));
        for (int i = 0; i < F(part, 13).Count; i++) MoveBox(boxes, i * 32, Place);
        Append(F(r, 13), boxes, F(part, 13).Count);
        if (F(part, 13).Count > 0)
        {
            var all = F(r, 13).Data;
            Grow(all, 0, boxes, 0);
            var bones = F(r, 0).Data;
            if ((U16(bones, pb * 48 + 6) & 1) != 0) Grow(all, U16(bones, pb * 48 + 4) * 32, boxes, 0);
        }
        return r;
    }

    /// <summary>Clears the hidden flag of the mesh groups whose name hash (low 48 bits) is in <paramref name="groups"/>. Returns how many.</summary>
    public static int ShowGroups(FmdlRaw m, IReadOnlySet<ulong> groups)
    {
        var grp = F(m, 1);
        var names = F(m, 22);
        int n = 0;
        for (int i = 0; i < grp.Count; i++)
            if (groups.Contains(U64(names.Data, U16(grp.Data, i * 8) * 8) & 0xFFFF_FFFF_FFFF)) { W16(grp.Data, i * 8 + 2, 0); n++; }
        return n;
    }

    private static int Lods(FmdlRaw x) => x.Get(16) is { Count: > 0 } f ? Math.Max(1, (int)U16(f.Data, 0)) : 1;

    private static FmdlRaw Clone(FmdlRaw x)
    {
        var c = new FmdlRaw { Version = x.Version };
        foreach (var f in x.Features) c.Features.Add(new FmdlRaw.Feature { Type = f.Type, Count = f.Count, Data = (byte[])f.Data.Clone() });
        foreach (var b in x.Buffers) c.Buffers.Add(new FmdlRaw.Buffer { Type = b.Type, Data = (byte[])b.Data.Clone() });
        return c;
    }

    /// <summary>The feature of a type; an empty one is added when the model has none.</summary>
    private static FmdlRaw.Feature F(FmdlRaw x, int type)
    {
        var f = x.Get(type);
        if (f == null) x.Features.Add(f = new FmdlRaw.Feature { Type = type, Count = 0, Data = [] });
        return f;
    }

    private static byte[] Copy(FmdlRaw.Feature f) => (byte[])f.Data.Clone();
    private static void Append(FmdlRaw.Feature f, byte[] data, int count) { f.Data = [.. f.Data, .. data]; f.Count += count; }

    private static void MoveBox(byte[] b, int o, Func<Vector3, Vector3> place)
    {
        Vector3 hi = Vec3(b, o), lo = Vec3(b, o + 16);
        Vector3 nhi = new(float.MinValue), nlo = new(float.MaxValue);
        for (int c = 0; c < 8; c++)
        {
            var p = place(new Vector3((c & 1) != 0 ? hi.X : lo.X, (c & 2) != 0 ? hi.Y : lo.Y, (c & 4) != 0 ? hi.Z : lo.Z));
            nhi = Vector3.Max(nhi, p); nlo = Vector3.Min(nlo, p);
        }
        WVec3(b, o, nhi); WVec3(b, o + 16, nlo);
    }

    private static void Grow(byte[] b, int o, byte[] src, int so)
    {
        WVec3(b, o, Vector3.Max(Vec3(b, o), Vec3(src, so)));
        WVec3(b, o + 16, Vector3.Min(Vec3(b, o + 16), Vec3(src, so + 16)));
    }

    private static int Up16(int v) => (v + 15) & ~15;
    private static byte[] Pad16(byte[] b) => b.Length % 16 == 0 ? b : [.. b, .. new byte[16 - b.Length % 16]];
    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
    private static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
    private static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));
    private static void W16(byte[] d, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), checked((ushort)v));
    private static void W32(byte[] d, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o), v);
    private static Vector3 Vec3(byte[] d, int o) => new(BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 4)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 8)));
    private static void WVec3(byte[] d, int o, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(o + 8), v.Z);
    }
    private static Vector3 Half3(byte[] d, int o) => new((float)BinaryPrimitives.ReadHalfLittleEndian(d.AsSpan(o)),
        (float)BinaryPrimitives.ReadHalfLittleEndian(d.AsSpan(o + 2)), (float)BinaryPrimitives.ReadHalfLittleEndian(d.AsSpan(o + 4)));
    private static void WHalf3(byte[] d, int o, Vector3 v)
    {
        BinaryPrimitives.WriteHalfLittleEndian(d.AsSpan(o), (Half)v.X);
        BinaryPrimitives.WriteHalfLittleEndian(d.AsSpan(o + 2), (Half)v.Y);
        BinaryPrimitives.WriteHalfLittleEndian(d.AsSpan(o + 4), (Half)v.Z);
    }
}
