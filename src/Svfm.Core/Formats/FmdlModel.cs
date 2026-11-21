using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Svfm.Core.Hashing;

namespace Svfm.Core.Formats;

public sealed record FmdlBone(int Index, string? Name, ulong NameHash, int Parent, Vector3 World);

/// <summary>A texture slot of a material: role (e.g. Base_Tex_SRGB) and the texture's path code.</summary>
public sealed record FmdlTextureSlot(string? Role, ulong RoleHash, string? FileName, ulong PathCode);

public sealed record FmdlMaterial(int Index, string? Name, ulong NameHash, IReadOnlyList<FmdlTextureSlot> Textures)
{
    public FmdlTextureSlot? Slot(string role) =>
        Textures.FirstOrDefault(t => t.Role == role || t.RoleHash == FoxHash.StrCode64(role));
    /// <summary>The material's shader (e.g. fox3DDF_Blin), when the model stores names as strings.</summary>
    public string? Shader { get; init; }
    /// <summary>
    /// The shader's StrCode64 hash, when the model stores hashes instead of names (every vanilla model does, and so do
    /// most mods). <see cref="Shader"/> is then the hash in hex, so a name test against it can never match - which is why
    /// the layer-multiply shaders were never recognised and their camo layer was never drawn.
    /// </summary>
    public ulong ShaderHash { get; init; }
    /// <summary>
    /// True when the shader is one of the layer-multiply family (fox3DDF_Blin_LayerMul and its _UV, _UV2, _Alpha and
    /// _AlphaTest forms). Those draw their colour from a tiled layer map multiplied into the base, so a mesh using one
    /// needs its layer and mask textures or it comes out flat.
    /// </summary>
    public bool IsLayerMultiply => ShaderHash != 0
        ? LayerMulShaders.Contains(ShaderHash)
        : Shader?.Contains("LayerMul", StringComparison.OrdinalIgnoreCase) == true;

    // Declared first: LayerMulShaders below includes it, and static fields start in order.
    /// <summary>
    /// The weapon layer-multiply shaders (hg09, ar02, hg02). Decoded from TppShaders_dx11.fsop: the camo layer is read at
    /// (UV1 + shift) * repeat, the mask R on UV0, and the blend is lerp(base, base*layer, mask*layer.a).
    /// </summary>
    public static readonly IReadOnlySet<ulong> WeaponLayerMulShaders = new HashSet<ulong>
    {
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_LNM"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_2MT_LNM"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_3MT_LNM"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_4MT_LNM"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_4MT"),
    };
    /// <summary>The layer-multiply shader hashes, so a model that stores hashes can still be recognised.</summary>
    public static readonly IReadOnlySet<ulong> LayerMulShaders = new HashSet<ulong>
    {
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_UV"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_UV2"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_Alpha"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_AlphaTest"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_UV_Alpha"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_UV2_Alpha"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_UV_AlphaTest"),
        FoxHash.StrCode64("fox3DDF_Blin_LayerMul_UV2_AlphaTest"),
    }.Concat(WeaponLayerMulShaders).ToHashSet();

    /// <summary>Vector parameters by name (e.g. MatParamIndex_0, URoughness).</summary>
    public IReadOnlyDictionary<string, Vector4> Parameters { get; init; } = new Dictionary<string, Vector4>();

    /// <summary>
    /// A parameter's X by name, or <paramref name="fallback"/> when it is missing or 0. A model that stores hashes keys
    /// its parameters by the hash in hex (every vanilla gun: URepeat_UV is there as a hash), so both are tried.
    /// </summary>
    public float Param(string name, float fallback) =>
        (Parameters.TryGetValue(name, out var v) || Parameters.TryGetValue(FoxHash.StrCode64(name).ToString("x"), out v)) && v.X != 0 ? v.X : fallback;
}

public sealed class FmdlMesh
{
    public required int Index { get; init; }
    public required int MaterialIndex { get; init; }
    public required int MeshGroup { get; init; }
    public required Vector3[] Positions { get; init; }
    /// <summary>Where the mesh's index list starts in the model file (bytes).</summary>
    public int? IndexOffset { get; init; }
    public Vector3[]? Normals { get; init; }
    /// <summary>Tangents (xyz) with the handedness in w, when the mesh has them.</summary>
    public Vector4[]? Tangents { get; init; }
    public Vector2[]? Uv { get; init; }
    /// <summary>The second UV set (vertex usage 9), when the mesh has one (D-Dog's fur: the coat colour map's layout).</summary>
    public Vector2[]? Uv1 { get; init; }
    public required ushort[] Indices { get; init; }
    /// <summary>Four model bone indices per vertex (the mesh's bone group already applied), when the mesh is skinned.</summary>
    public int[]? BoneIndices { get; init; }
    /// <summary>Four bone weights per vertex (0 to 1), in the order of <see cref="BoneIndices"/>.</summary>
    public float[]? BoneWeights { get; init; }
    /// <summary>Per vertex: true for the vertices a hung tail chain moves (set by <see cref="Rendering.TailPose.Hang"/>).</summary>
    public bool[]? Tail { get; set; }
}

/// <summary>
/// Fox model (.fmdl), read-only: bones, mesh groups, meshes (LOD 0), materials with their texture
/// slots. Layout as documented by FMDL Studio v2 (BobDoleOwndU, MIT). Vanilla models store names as
/// StrCode64 hashes and texture paths as path codes; mod models may use strings instead. Both work.
/// </summary>
public sealed class FmdlModel
{
    public IReadOnlyList<FmdlBone> Bones { get; private set; } = [];
    public IReadOnlyList<FmdlMaterial> Materials { get; private set; } = [];
    public IReadOnlyList<FmdlMesh> Meshes { get; private set; } = [];
    /// <summary>Mesh group names (hash when unnamed) and whether the group is hidden by default.</summary>
    public IReadOnlyList<(string? name, ulong hash, bool hidden)> MeshGroups { get; private set; } = [];

    public FmdlBone? Bone(string name)
    {
        ulong h = FoxHash.StrCode64(name);
        return Bones.FirstOrDefault(b => b.Name == name || b.NameHash == h);
    }

    /// <summary>
    /// A copy of the model with only the meshes <paramref name="keep"/> accepts, for drawing a weapon without its
    /// suppressor, light or magazine. Bones, materials and mesh groups are shared, not copied.
    /// </summary>
    /// <param name="keep">Which meshes to keep.</param>
    /// <returns>The copy.</returns>
    public FmdlModel WithMeshes(Func<FmdlMesh, bool> keep) => new()
    {
        Bones = Bones, Materials = Materials, MeshGroups = MeshGroups,
        Meshes = Meshes.Where(keep).ToList(),
    };

    /// <summary>A copy of the model with <paramref name="extra"/> meshes added after its own. Bones, materials and mesh groups are shared.</summary>
    public FmdlModel WithMeshesAdded(IEnumerable<FmdlMesh> extra) => new()
    {
        Bones = Bones, Materials = Materials, MeshGroups = MeshGroups,
        Meshes = Meshes.Concat(extra).ToList(),
    };

    /// <summary>
    /// A copy of the model with every mesh group shown, i.e. with the hidden flag cleared on all of them. Bones, materials
    /// and meshes are shared, not copied.
    /// </summary>
    /// <remarks>
    /// A head package hides most of its groups on purpose: in game a look's .fv2 shows them, and the game's face system
    /// picks which. The preview has no look to do that, and both renderers skip a hidden group, so a vanilla DD soldier
    /// face (cm_dds5/dds6_head_z_eye0) came out as a pair of eyes with no head around them - the owner's report. This is
    /// only ever applied to a head model, never to a body: a body hides parts for a reason (a folded hood, an alternate
    /// mesh) and showing them would draw the wrong thing.
    /// </remarks>
    /// <returns>The copy.</returns>
    public FmdlModel WithAllGroupsShown() => MeshGroups.All(g => !g.hidden) ? this : new()
    {
        Bones = Bones, Materials = Materials, Meshes = Meshes,
        MeshGroups = MeshGroups.Select(g => (g.name, g.hash, false)).ToList(),
    };

    /// <summary>A copy of the model that shows only the mesh groups <paramref name="shown"/> accepts (by index).</summary>
    public FmdlModel WithGroupsShown(Func<int, bool> shown) => new()
    {
        Bones = Bones, Materials = Materials, Meshes = Meshes,
        MeshGroups = MeshGroups.Select((g, i) => (g.name, g.hash, !shown(i))).ToList(),
    };

    /// <summary>
    /// A copy of a head model with its hidden groups shown, except a hidden group that repeats a shown one: the same
    /// number of meshes with the same vertex counts. That is a second copy of a part (a visor up and a visor down), and
    /// drawing both puts one on top of the other.
    /// </summary>
    /// <remarks>
    /// <see cref="WithAllGroupsShown"/> drew the Peace Walker battle dress head with two visors: its group 2 (54 and 162
    /// vertices, hidden) repeats group 3 (54 and 162, shown). The vanilla soldier faces hide groups that repeat nothing
    /// (the balaclava cloth), so those are still shown. In game the head's .fv2 decides; the tool does not read that part
    /// of an .fv2, so this is the preview's rule, not the game's.
    /// </remarks>
    /// <returns>The copy.</returns>
    public FmdlModel WithHeadGroupsShown()
    {
        if (MeshGroups.All(g => !g.hidden)) return this;
        string Shape(int group) => string.Join(",", Meshes.Where(m => m.MeshGroup == group).Select(m => m.Positions.Length).Order());
        var shown = Enumerable.Range(0, MeshGroups.Count).Where(i => !MeshGroups[i].hidden).Select(Shape).Where(s => s.Length > 0).ToHashSet();
        return new()
        {
            Bones = Bones, Materials = Materials, Meshes = Meshes,
            MeshGroups = MeshGroups.Select((g, i) => (g.name, g.hash, g.hidden && shown.Contains(Shape(i)))).ToList(),
        };
    }

    private static readonly uint BaseSlot = (uint)FoxHash.StrCode64("Base_Tex_SRGB");

    /// <summary>
    /// A copy of a model with some material texture slots pointed at other textures, one material and slot at a time, as an
    /// .fv2 texture swap does in game (<see cref="Fv2File.TextureSwap"/>: material and slot by their StrCode32 name
    /// hashes, the texture by its path code). Only slots whose current texture <paramref name="replaceable"/> accepts are
    /// changed (e.g. the flat placeholders a weapon model leaves for its paint). Textures the model doesn't list yet are
    /// added to its texture table (block 6, with their path codes in block 21 and name hashes in block 22), and section 0 is
    /// laid out again the way the game's exporter lays it out (no gaps; see Section0Layout); section 1 follows it.
    /// Geometry is untouched. Null when the model names its textures with strings (no block 21). With
    /// <paramref name="ownsBase"/>, only materials whose Base_Tex_SRGB texture it accepts are changed.
    /// </summary>
    public static byte[]? ApplyTextureSwaps(byte[] data, IReadOnlyList<Fv2File.TextureSwap> swaps, Func<ulong, bool> replaceable, out int changed, Func<ulong, bool>? ownsBase = null)
    {
        changed = 0;
        var d = data;
        int infoOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(8));
        int n0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x20));
        int s0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x28));
        int size0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x2C));
        int s1 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x30));
        var info = new Dictionary<int, int>();   // block type → info entry position
        for (int i = 0, p = infoOffset; i < n0; i++, p += 8) info[BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p))] = p;
        if (!info.ContainsKey(21) || !info.ContainsKey(22) || !info.ContainsKey(6) || !info.ContainsKey(7) || !info.ContainsKey(4)) return null;
        int Count(int t) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(info[t] + 2));
        int Off(int t) => s0 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(info[t] + 4));
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
        ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));

        var hashes = Enumerable.Range(0, Count(22)).Select(i => U64(Off(22) + i * 8)).ToList();
        var paths = Enumerable.Range(0, Count(21)).Select(i => U64(Off(21) + i * 8)).ToList();
        var texEntries = Enumerable.Range(0, Count(6)).Select(i => (name: U16(Off(6) + i * 4), path: U16(Off(6) + i * 4 + 2))).ToList();
        uint Low(int nameIndex) => nameIndex < hashes.Count ? (uint)hashes[nameIndex] : 0;

        var newHashes = new List<ulong>();
        var newPaths = new List<ulong>();
        var newTex = new List<(ushort name, ushort path)>();
        int TextureIndex(ulong code)
        {
            int at = texEntries.FindIndex(t => t.path < paths.Count && paths[t.path] == code);
            if (at >= 0) return at;
            at = newTex.FindIndex(t => t.path >= paths.Count && newPaths[t.path - paths.Count] == code);
            if (at >= 0) return texEntries.Count + at;
            // The texture's name (vanilla: StrCode64 of its source file name, e.g. "hg07_main4_def_c00_bsm.tga").
            string? file = Archives.PathDictionary.Bundled.TryGetPath(code) is { } path ? System.IO.Path.GetFileNameWithoutExtension(path) + ".tga" : null;
            ulong nameHash = file != null ? FoxHash.StrCode64(file) : code;
            int ni = hashes.IndexOf(nameHash);
            if (ni < 0) { ni = hashes.Count + newHashes.Count; newHashes.Add(nameHash); }
            newPaths.Add(code);
            newTex.Add(((ushort)ni, (ushort)(paths.Count + newPaths.Count - 1)));
            return texEntries.Count + newTex.Count - 1;
        }

        var edits = new List<(int at, int texture)>();
        for (int m = 0; m < Count(4); m++)
        {
            int o = Off(4) + m * 0x10;
            uint matHash = Low(U16(o));
            int texCount = d[o + 6], first = U16(o + 8);
            ulong TexAt(int k) { int ti = U16(Off(7) + (first + k) * 4 + 2); return ti < texEntries.Count && texEntries[ti].path < paths.Count ? paths[texEntries[ti].path] : 0; }
            // Weapon parts share material names (a merged model holds several), so a paint keyed by name alone leaks onto other parts.
            if (ownsBase != null && !Enumerable.Range(0, texCount).Any(k => Low(U16(Off(7) + (first + k) * 4)) == BaseSlot && ownsBase(TexAt(k)))) continue;
            for (int k = 0; k < texCount; k++)
            {
                int po = Off(7) + (first + k) * 4;
                uint slot = Low(U16(po));
                int ti = U16(po + 2);
                ulong current = TexAt(k);
                var swap = swaps.LastOrDefault(s => s.Material == matHash && s.Slot == slot);
                if (swap == null || swap.Texture == current || !replaceable(current)) continue;
                edits.Add((po + 2, TextureIndex(swap.Texture)));
            }
        }
        if (edits.Count == 0) return (byte[])data.Clone();

        // Section 0 laid out again as the game's exporter does (checked on 140 vanilla models: every block offset
        // reproduced): blocks in their original order, packed, each starting at its type's alignment (Section0Layout).
        // Blocks 6, 21 and 22 get their new entries at the end.
        var work = (byte[])d.Clone();
        foreach (var (at, tex) in edits) BinaryPrimitives.WriteUInt16LittleEndian(work.AsSpan(at), (ushort)tex);
        var extra = new Dictionary<int, byte[]>
        {
            [6] = newTex.SelectMany(t => { var b = new byte[4]; BinaryPrimitives.WriteUInt16LittleEndian(b, t.name); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), t.path); return b; }).ToArray(),
            [21] = newPaths.SelectMany(c => BitConverter.GetBytes(c)).ToArray(),
            [22] = newHashes.SelectMany(h => BitConverter.GetBytes(h)).ToArray(),
        };
        var order = info.Keys.OrderBy(t => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(info[t] + 4))).ToList();
        if (order.Any(t => !Section0Layout.ContainsKey(t))) return null;
        var sec = new MemoryStream();
        var r = (byte[])work.Clone();
        foreach (int t in order)
        {
            var (entry, align) = Section0Layout[t];
            while (sec.Length % align != 0) sec.WriteByte(0);
            int start = (int)sec.Length;
            sec.Write(work, Off(t), Count(t) * entry);
            int added = 0;
            if (extra.TryGetValue(t, out var more) && more.Length > 0) { sec.Write(more); added = more.Length / entry; }
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(info[t] + 4), start);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(info[t] + 2), (ushort)(Count(t) + added));
        }
        while (sec.Length % 16 != 0) sec.WriteByte(0);
        int newSize0 = (int)sec.Length;
        int between = s1 - (s0 + size0);   // the bytes between the sections, kept as they are
        var w = new MemoryStream();
        w.Write(r, 0, s0);
        w.Write(sec.ToArray());
        w.Write(work, s0 + size0, d.Length - (s0 + size0));
        var outData = w.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(outData.AsSpan(0x2C), newSize0);
        BinaryPrimitives.WriteInt32LittleEndian(outData.AsSpan(0x30), s0 + newSize0 + between);
        changed = edits.Count;
        return outData;
    }

    /// <summary>
    /// Section 0 block types: entry size and the alignment the game's exporter starts each at (fitted on 140 vanilla
    /// models, Oct 26: packing the blocks in their order with these alignments reproduces every offset).
    /// </summary>
    private static readonly Dictionary<int, (int entry, int align)> Section0Layout = new()
    {
        [0] = (0x30, 16), [1] = (8, 16), [2] = (0x20, 8), [3] = (0x30, 8), [4] = (0x10, 8), [5] = (0x44, 8), [6] = (4, 4),
        [7] = (4, 4), [8] = (4, 4), [9] = (8, 4), [10] = (8, 4), [11] = (4, 4), [12] = (8, 8), [13] = (0x20, 16),
        [14] = (0x10, 16), [16] = (0x10, 16), [17] = (8, 16), [18] = (8, 8), [20] = (0x80, 8), [21] = (8, 8), [22] = (8, 8),
    };

    /// <summary>
    /// A copy of a model whose material textures point at other paths: the texture path codes (block 21, the 64-bit
    /// .ftex path codes of a model that stores hashes) in <paramref name="map"/> are replaced. Geometry and materials
    /// are unchanged. Null when the model names its textures with strings (no block 21).
    /// </summary>
    public static byte[]? RetargetTextures(byte[] data, IReadOnlyDictionary<ulong, ulong> map, out int changed)
    {
        changed = 0;
        var d = (byte[])data.Clone();
        ulong infoOffset = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(8));
        int n0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x20));
        int s0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x28));
        for (int i = 0, p = (int)infoOffset; i < n0; i++, p += 8)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)) != 21) continue;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2)), off = s0 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
            for (int k = 0; k < count; k++)
            {
                var at = d.AsSpan(off + k * 8, 8);
                if (!map.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(at), out var to)) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(at, to);
                changed++;
            }
            return d;
        }
        return null;
    }

    /// <summary>
    /// A copy of a model that names its textures as text (block 6: file name + folder strings) whose texture file names
    /// in <paramref name="rename"/> (old file name without extension → new one of the same length) are replaced in the
    /// string data, in place, so the textures load from new paths. Same length only: the string table keeps its layout.
    /// Null when the model stores hashes instead (see RetargetTextures).
    /// </summary>
    public static byte[]? RenameTextureFiles(byte[] data, IReadOnlyDictionary<string, string> rename, out int changed)
    {
        changed = 0;
        var d = (byte[])data.Clone();
        ulong infoOffset = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(8));
        int n0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x20));
        int n1 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x24));
        int s0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x28));
        int s1 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x30));
        var blocks = new Dictionary<int, (int count, int offset)>();
        int p = (int)infoOffset;
        for (int i = 0; i < n0; i++, p += 8)
            blocks[BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p))] = (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2)), s0 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4)));
        int stringBase = -1;
        for (int i = 0; i < n1; i++, p += 12)
            if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)) == 3) stringBase = s1 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
        if (blocks.ContainsKey(21) || !blocks.TryGetValue(12, out var strs) || !blocks.TryGetValue(6, out var tex) || stringBase < 0) return null;
        var done = new HashSet<int>();
        for (int t = 0; t < tex.count; t++)
        {
            int ni = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(tex.offset + t * 4));
            if (ni >= strs.count || !done.Add(ni)) continue;
            int len = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(strs.offset + ni * 8 + 2));
            int so = stringBase + (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(strs.offset + ni * 8 + 4));
            string name = Encoding.Latin1.GetString(d, so, len);
            int dot = name.IndexOf('.');
            string stem = dot < 0 ? name : name[..dot];
            if (!rename.TryGetValue(stem, out var to) || to.Length != stem.Length) continue;
            Encoding.Latin1.GetBytes(to).CopyTo(d.AsSpan(so));
            changed++;
        }
        return d;
    }

    /// <summary>
    /// A copy of a model with only some mesh groups shown: every other group gets the hidden flag (block 1, u16 at +2,
    /// the flag vanilla uses for meshes shown only by a look, e.g. the scarf pulled over the face on the scarf
    /// fatigues), and the kept groups are moved under the root group (u16 at +4), so a hidden parent can't take them
    /// along. Group 0 (the root) stays as it is. Nothing else changes.
    /// </summary>
    public static byte[] ShowOnlyGroups(byte[] data, IReadOnlySet<int> keep)
    {
        var d = (byte[])data.Clone();
        ulong infoOffset = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(8));
        int n0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x20));
        int s0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x28));
        for (int i = 0, p = (int)infoOffset; i < n0; i++, p += 8)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)) != 1) continue;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2)), off = s0 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
            for (int g = 1; g < count; g++)
            {
                int o = off + g * 8;
                bool show = keep.Contains(g);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o + 2), (ushort)(show ? 0 : 1));
                if (show) BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(o + 4), 0);
            }
            return d;
        }
        throw new InvalidDataException("The model has no mesh groups.");
    }

    /// <summary>
    /// A copy of a model in which only the meshes in <paramref name="keep"/> draw (issue #126: the knot band and tails of
    /// Snake's bandana face, which share mesh group 1 with the face). Every index of each other mesh is set to 0, so all
    /// its triangles are degenerate and draw nothing. The layout, bones, groups and vertices do not change.
    /// </summary>
    public static byte[] KeepMeshes(byte[] data, IReadOnlySet<int> keep)
    {
        var d = (byte[])data.Clone();
        var m = Read(d);
        foreach (var x in m.Meshes)
        {
            if (keep.Contains(x.Index)) continue;
            if (x.IndexOffset is not int ib) throw new InvalidDataException("The model has no index offsets.");
            d.AsSpan(ib, x.Indices.Length * 2).Clear();
        }
        return d;
    }

    public static FmdlModel Read(byte[] d)
    {
        try { return ReadChecked(d); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("model", e); }
    }

    private static FmdlModel ReadChecked(byte[] d)
    {
        if (!d.AsSpan(0, 4).SequenceEqual("FMDL"u8)) throw new InvalidDataException("Not an .fmdl file.");
        ulong infoOffset = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(8));
        int n0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x20));
        int n1 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x24));
        int s0 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x28));
        int s1 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x30));

        var blocks = new Dictionary<int, (int count, int offset)>();
        int p = (int)infoOffset;
        for (int i = 0; i < n0; i++, p += 8)
            blocks[BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p))] =
                (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2)), s0 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4)));
        var blocks1 = new Dictionary<int, (int offset, int length)>();
        for (int i = 0; i < n1; i++, p += 12)
            blocks1[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p))] =
                (s1 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 8)));

        (int count, int offset) B(int type) => blocks.TryGetValue(type, out var b) ? b : (0, 0);
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));
        Half H(int o) => BinaryPrimitives.ReadHalfLittleEndian(d.AsSpan(o));

        // Strings (block 12 + section-1 block 3) and hashes (block 22), path codes (block 21).
        var strings = new List<string>();
        {
            var (count, off) = B(12);
            int baseOff = blocks1.TryGetValue(3, out var sb) ? sb.offset : 0;
            for (int i = 0; i < count; i++)
            {
                int len = U16(off + i * 8 + 2);
                int so = (int)U32(off + i * 8 + 4);
                strings.Add(Encoding.Latin1.GetString(d, baseOff + so, len));
            }
        }
        var hashes = new List<ulong>();
        { var (count, off) = B(22); for (int i = 0; i < count; i++) hashes.Add(BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off + i * 8))); }
        var paths = new List<ulong>();
        { var (count, off) = B(21); for (int i = 0; i < count; i++) paths.Add(BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off + i * 8))); }
        bool hashedNames = hashes.Count > 0;
        (string? name, ulong hash) Name(int index)
        {
            if (hashedNames) return (null, index < hashes.Count ? hashes[index] : 0);
            string? s = index < strings.Count ? strings[index] : null;
            return (s, s != null ? FoxHash.StrCode64(s) : 0);
        }

        var m = new FmdlModel();

        // Bones (block 0, 0x30 each).
        var bones = new List<FmdlBone>();
        { var (count, off) = B(0); for (int i = 0; i < count; i++) { int o = off + i * 0x30; var (nm, h) = Name(U16(o)); bones.Add(new FmdlBone(i, nm, h, (short)U16(o + 2), new Vector3(F(o + 0x20), F(o + 0x24), F(o + 0x28)))); } }
        m.Bones = bones;

        // Mesh groups (block 1, 8 each).
        var groups = new List<(string?, ulong, bool)>();
        { var (count, off) = B(1); for (int i = 0; i < count; i++) { int o = off + i * 8; var (nm, h) = Name(U16(o)); groups.Add((nm, h, U16(o + 2) != 0)); } }
        m.MeshGroups = groups;

        // Mesh group entries (block 2, 0x20 each): mesh ranges per group.
        var meshGroupOf = new Dictionary<int, int>();
        { var (count, off) = B(2); for (int i = 0; i < count; i++) { int o = off + i * 0x20 + 4; int g = U16(o), mc = U16(o + 2), first = U16(o + 4); for (int k = 0; k < mc; k++) meshGroupOf[first + k] = g; } }

        // Textures (block 6) and material parameters (block 7).
        var texNames = new List<(string? file, ulong path)>();
        {
            var (count, off) = B(6);
            for (int i = 0; i < count; i++)
            {
                int ni = U16(off + i * 4), pi = U16(off + i * 4 + 2);
                string? file = !hashedNames && ni < strings.Count ? strings[ni] : null;
                ulong code = paths.Count > 0 ? (pi < paths.Count ? paths[pi] : 0)
                           : pi < strings.Count ? FoxHash.PathCode64(strings[pi].TrimEnd('/') + "/" + System.IO.Path.ChangeExtension(file ?? "", ".ftex")) : 0;
                texNames.Add((file, code));
            }
        }
        var (_, paramOff) = B(7);

        // Material instances (block 4, 0x10 each).
        var mats = new List<FmdlMaterial>();
        {
            var (count, off) = B(4);
            for (int i = 0; i < count; i++)
            {
                int o = off + i * 0x10;
                var (nm, h) = Name(U16(o));
                int texCount = d[o + 6], firstTex = U16(o + 8);
                var slots = new List<FmdlTextureSlot>();
                for (int k = 0; k < texCount; k++)
                {
                    int po = paramOff + (firstTex + k) * 4;
                    var (role, rh) = Name(U16(po));
                    int ti = U16(po + 2);
                    var (file, code) = ti < texNames.Count ? texNames[ti] : (null, 0UL);
                    slots.Add(new FmdlTextureSlot(role, rh, file, code));
                }
                // Shader (material block 8: name, type) and vector parameters (block 7 entries after the textures,
                // pointing at 16-byte vectors in section-1 block 0).
                int matIndex = U16(o + 4), paramCount = d[o + 7], firstParam = U16(o + 10);
                string? shader = null;
                ulong shaderHash = 0;
                if (B(8) is var (mc, mo) && matIndex < mc)
                {
                    var (tn, th) = Name(U16(mo + matIndex * 4 + 2));
                    shader = tn ?? th.ToString("x");
                    shaderHash = th;
                }
                var pars = new Dictionary<string, Vector4>(StringComparer.Ordinal);
                int vecBase = blocks1.TryGetValue(0, out var vb) ? vb.offset : -1;
                for (int k = 0; k < paramCount && vecBase >= 0; k++)
                {
                    int po = paramOff + (firstParam + k) * 4;
                    var (pn, ph) = Name(U16(po));
                    int vo = vecBase + U16(po + 2) * 16;
                    if (vo + 16 > d.Length) continue;
                    pars[pn ?? ph.ToString("x")] = new Vector4(F(vo), F(vo + 4), F(vo + 8), F(vo + 12));
                }
                mats.Add(new FmdlMaterial(i, nm, h, slots) { Shader = shader, ShaderHash = shaderHash, Parameters = pars });
            }
        }
        m.Materials = mats;

        // Geometry: mesh info (3), format info (9), formats (10), vertex elements (11), buffers (14).
        var (meshCount, meshOff) = B(3);
        var (_, fiOff) = B(9);
        var (_, fmtOff) = B(10);
        var (_, veOff) = B(11);
        var (bufCount, bufOff) = B(14);
        int buffer = blocks1.TryGetValue(2, out var bb) ? bb.offset : 0;
        var bufferOffsets = new int[bufCount];
        for (int i = 0; i < bufCount; i++) bufferOffsets[i] = (int)U32(bufOff + i * 0x10 + 8);

        var meshes = new List<FmdlMesh>();
        for (int i = 0; i < meshCount; i++)
        {
            int o = meshOff + i * 0x30;
            int mat = U16(o + 4), boneGroup = U16(o + 6), vcount = U16(o + 0x0A);
            if (mat >= mats.Count) throw new InvalidDataException($"The model file is damaged: mesh {i} uses material {mat} of {mats.Count}.");
            int firstIndex = (int)U32(o + 0x10), indexCount = (int)U32(o + 0x14);
            int fi = fiOff + i * 8;
            int firstFormat = U16(fi + 4), elemCount = d[fi + 1], firstElem = U16(fi + 6);

            int posBase = buffer + bufferOffsets[0] + (int)U32(fmtOff + firstFormat * 8 + 4);
            var pos = new Vector3[vcount];
            for (int v = 0; v < vcount; v++) pos[v] = new Vector3(F(posBase + v * 12), F(posBase + v * 12 + 4), F(posBase + v * 12 + 8));

            int f1 = fmtOff + (firstFormat + 1) * 8;
            int stride = d[f1 + 2];
            int addBase = buffer + bufferOffsets[1] + (int)U32(f1 + 4);
            Vector3[]? normals = null;
            Vector4[]? tangents = null;
            Vector2[]? uv = null, uv1 = null;
            int[]? boneIdx = null;
            float[]? boneW = null;
            for (int e = 0; e < elemCount; e++)
            {
                int eo = veOff + (firstElem + e) * 4;
                int usage = d[eo], type = d[eo + 1], eoff = U16(eo + 2);
                if (usage == 2 && type == 6)
                {
                    normals = new Vector3[vcount];
                    for (int v = 0; v < vcount; v++) { int q = addBase + v * stride + eoff; normals[v] = new Vector3((float)H(q), (float)H(q + 2), (float)H(q + 4)); }
                }
                else if (usage == 14 && type == 6)
                {
                    tangents = new Vector4[vcount];
                    for (int v = 0; v < vcount; v++) { int q = addBase + v * stride + eoff; tangents[v] = new Vector4((float)H(q), (float)H(q + 2), (float)H(q + 4), (float)H(q + 6)); }
                }
                else if (usage == 8 && type == 7)
                {
                    uv = new Vector2[vcount];
                    for (int v = 0; v < vcount; v++) { int q = addBase + v * stride + eoff; uv[v] = new Vector2((float)H(q), (float)H(q + 2)); }
                }
                // Skin (FMDL Studio): usage 1 BONE_WEIGHT0 as 4 UNORM8, usage 7 BONE_INDEX0 as 4 UINT8 into the mesh's bone group.
                else if (usage == 1 && type == 8)
                {
                    boneW = new float[vcount * 4];
                    for (int v = 0; v < vcount; v++) for (int k = 0; k < 4; k++) boneW[v * 4 + k] = d[addBase + v * stride + eoff + k] / 255f;
                }
                else if (usage == 7 && type == 9 && B(5) is var (bgCount, bgOff) && boneGroup < bgCount)
                {
                    int g = bgOff + boneGroup * 0x44, gc = U16(g + 2);
                    boneIdx = new int[vcount * 4];
                    for (int v = 0; v < vcount; v++) for (int k = 0; k < 4; k++) { int li = d[addBase + v * stride + eoff + k]; boneIdx[v * 4 + k] = li < gc ? U16(g + 4 + li * 2) : -1; }
                }
                else if (usage == 9 && type == 7)
                {
                    uv1 = new Vector2[vcount];
                    for (int v = 0; v < vcount; v++) { int q = addBase + v * stride + eoff; uv1[v] = new Vector2((float)H(q), (float)H(q + 2)); }
                }
            }

            int ib = buffer + bufferOffsets[2] + firstIndex * 2;
            if ((uint)ib > (uint)d.Length || (uint)indexCount > (uint)(d.Length - ib) / 2) throw new InvalidDataException("A mesh's index list is past the end of the model.");
            var idx = new ushort[indexCount];
            for (int k = 0; k < indexCount; k++) idx[k] = U16(ib + k * 2);

            meshes.Add(new FmdlMesh
            {
                Index = i, MaterialIndex = mat, MeshGroup = meshGroupOf.GetValueOrDefault(i, -1),
                Positions = pos, Normals = normals, Tangents = tangents, Uv = uv, Uv1 = uv1, Indices = idx, IndexOffset = ib,
                BoneIndices = boneIdx, BoneWeights = boneW,
            });
        }
        m.Meshes = meshes;
        return m;
    }
}
