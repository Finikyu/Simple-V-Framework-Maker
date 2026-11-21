using System.Numerics;
using Svfm.Core.Formats;

namespace Svfm.Core.Rendering;

/// <summary>
/// Issue #128: cloth tails on a head (bandana tails, a ponytail) that the game hangs with a .sim. In the bind pose a
/// mod's tails can point straight back (AV Messy Head + Bandanas: 40 cm behind the head), which stretched the icon.
/// A tail chain is a chain of 2 or more bones under SKL_004_HEAD, each with at most one child, that points back
/// (see .docs/research/Bandana_Tail_Bones.md). <see cref="Hang"/> turns each chain down rigidly about its root joint
/// and marks the vertices it moves (<see cref="FmdlMesh.Tail"/>); <see cref="HeadIcon"/> leaves them out of the framing.
/// </summary>
public static class TailPose
{
    /// <summary>Where a hanging tail points: down and a little back (the vanilla bandana tails).</summary>
    public static readonly Vector3 Down = Vector3.Normalize(new Vector3(0, -1, -0.35f));

    /// <summary>The chains of tail bones of <paramref name="m"/>, each root first.</summary>
    public static List<int[]> Chains(FmdlModel m)
    {
        var bones = m.Bones;
        var head = m.Bone("SKL_004_HEAD");
        if (head == null) return [];
        var kids = new List<int>[bones.Count];
        for (int i = 0; i < bones.Count; i++) kids[i] = [];
        foreach (var b in bones) if (b.Parent >= 0 && b.Parent < bones.Count) kids[b.Parent].Add(b.Index);
        bool Under(int i) { for (int p = bones[i].Parent, n = 0; p >= 0 && p < bones.Count && n < 256; p = bones[p].Parent, n++) if (p == head.Index) return true; return false; }
        bool Link(int i) => i >= 0 && i < bones.Count && kids[i].Count <= 1 && Under(i);
        var chains = new List<int[]>();
        foreach (var b in bones)
        {
            if (!Link(b.Index) || Link(b.Parent)) continue;
            var c = new List<int> { b.Index };
            while (kids[c[^1]].Count == 1 && Link(kids[c[^1]][0])) c.Add(kids[c[^1]][0]);
            if (c.Count < 2) continue;
            var dir = bones[c[^1]].World - bones[c[0]].World;
            // Only chains that point back: hair strands that already hang down or lie on the face are left as they are.
            if (dir.Length() < 0.01f || Vector3.Normalize(dir).Z > -0.3f) continue;
            chains.Add([.. c]);
        }
        return chains;
    }

    /// <summary>
    /// Turns every tail chain of <paramref name="m"/> to <see cref="Down"/> about its root joint, in place (positions and
    /// normals, blended by the vertex weights), and marks the tail vertices: those whose largest weight is on a tail bone.
    /// Returns <paramref name="m"/>.
    /// </summary>
    public static FmdlModel Hang(FmdlModel m)
    {
        var chains = Chains(m);
        if (chains.Count == 0 || m.Meshes.Any(x => x.Tail != null)) return m; // already hung
        var turn = new Dictionary<int, (Quaternion q, Vector3 pivot)>();
        foreach (var c in chains)
        {
            Vector3 root = m.Bones[c[0]].World, dir = Vector3.Normalize(m.Bones[c[^1]].World - root);
            var q = Between(dir, Down);
            foreach (var i in c) turn[i] = (q, root);
        }
        foreach (var x in m.Meshes)
        {
            if (x.BoneIndices is not { } bi || x.BoneWeights is not { } bw) continue;
            bool[]? tail = null;
            for (int v = 0; v < x.Positions.Length; v++)
            {
                int best = -1; float bestW = 0, moved = 0;
                Vector3 p = Vector3.Zero, n = Vector3.Zero, p0 = x.Positions[v];
                Vector3 n0 = x.Normals is { } ns ? ns[v] : Vector3.Zero;
                for (int k = 0; k < 4; k++)
                {
                    int b = bi[v * 4 + k]; float w = bw[v * 4 + k];
                    if (w <= 0) continue;
                    if (w > bestW) { bestW = w; best = b; }
                    if (!turn.TryGetValue(b, out var t)) continue;
                    p += w * (Vector3.Transform(p0 - t.pivot, t.q) + t.pivot);
                    n += w * Vector3.Transform(n0, t.q);
                    moved += w;
                }
                if (moved == 0) continue;
                x.Positions[v] = p + (1 - moved) * p0;
                if (x.Normals is { } nn && (n + (1 - moved) * n0).LengthSquared() > 0) nn[v] = Vector3.Normalize(n + (1 - moved) * n0);
                if (turn.ContainsKey(best)) (tail ??= new bool[x.Positions.Length])[v] = true;
            }
            x.Tail = tail;
        }
        return m;
    }

    /// <summary>The rotation that turns unit vector <paramref name="a"/> to unit vector <paramref name="b"/>.</summary>
    private static Quaternion Between(Vector3 a, Vector3 b)
    {
        var axis = Vector3.Cross(a, b);
        float d = Math.Clamp(Vector3.Dot(a, b), -1, 1);
        if (axis.LengthSquared() < 1e-10f) return d > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(d));
    }
}
