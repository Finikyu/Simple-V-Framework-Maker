using System.Numerics;
using Svfm.Core.Archives;
using Svfm.Core.Formats;

namespace Svfm.Core.Rendering;

/// <summary>
/// Head option icons (EquipIcon/head) in the vanilla style: the gear alone as a white outline drawing (vanilla headgear
/// icons are three-quarter views facing left, inside about 85×103 px around the centre). Gear that encloses the head
/// faces left as the vanilla balaclava does; smaller gear worn on the face is turned less (<see cref="Accessory"/>).
/// Face and hair mods, which are the head, are drawn over a faint silhouette of the head (<see cref="Render"/>); hair is
/// left out of that silhouette (hair cards give it a ragged edge).
/// </summary>
public static class HeadIcon
{
    /// <summary>
    /// The gear: item style, facing left, framed to the head icon window. Smoother than the item style (user, Oct 25: the
    /// first head icons were noisy): no lines along the edges of open surfaces (hair cards, cloth strips), creases only
    /// above 60°, line groups under 12 px dropped, and anti-aliased lines (not snapped to whole pixels).
    /// </summary>
    public static OutlineIconOptions Gear => OutlineIconOptions.Item with
    {
        Yaw = -65, Pitch = 5, MaxWidth = 86, MaxHeight = 97, MaxPixelsPerMetre = 5000,
        OpenEdgeLines = false, CreaseAngle = 60, MinLinePixels = 12, Crisp = false, LineHalfWidth = 0.6f, Supersample = 6, HollowInside = true,
        // The vanilla balaclava and bandana icons are the standard (user, Oct 25): white lines, nearly empty inside.
        // Measured by distance from the lines: the helmets have a flat fill of alpha 13–16 (≈ 6 %, the surface base 0.03
        // plus 0.03 here), no rim shading, no inner glow and nothing outside the outline (no dark halo).
        FillAlpha = 0.03f, RimAlpha = 0f, InnerGlow = 0f, HaloAlpha = 0f,
    };

    /// <summary>
    /// Small gear worn on a face (goggles, a face scarf, a bandana): the gear alone, turned less (yaw -35, pitch 10) so
    /// the front of the gear shows. Chosen by the user (Oct 25) over drawing the head behind it.
    /// </summary>
    public static OutlineIconOptions Accessory => Gear with { Yaw = -35, Pitch = 10 };

    /// <summary>The head behind: outline at half strength and a very faint fill, no inner lines, no halo.</summary>
    public static OutlineIconOptions Ghost => Gear with
    {
        LineAlpha = 0, DepthLines = false, FillAlpha = 0, RimAlpha = 0, InnerGlow = 0, OutlineAlpha = 0.5f, HaloAlpha = 0, XRay = false, HollowInside = false,
    };

    /// <summary>Draws <paramref name="gear"/> over the head silhouette <paramref name="head"/>, both framed together.</summary>
    public static RgbaImage Render(IReadOnlyList<FmdlMesh> gear, IReadOnlyList<FmdlMesh> head, OutlineIconOptions? gearOptions = null, OutlineIconOptions? ghostOptions = null)
    {
        var frame = gear.Concat(head).ToList();
        var top = OutlineIconRenderer.Render(gear, gearOptions ?? Gear, occluders: head, frame: frame);
        if (head.Count == 0) return top;
        return Over(top, OutlineIconRenderer.Render(head, ghostOptions ?? Ghost, frame: frame));
    }

    /// <summary>
    /// The gear alone in the style its size calls for: <see cref="Gear"/> (facing left, as the vanilla balaclava) when it
    /// encloses the head, else <see cref="Accessory"/>. <paramref name="head"/> is the head it sits on, used only to
    /// measure; it is not drawn.
    /// </summary>
    public static RgbaImage RenderGear(IReadOnlyList<FmdlMesh> gear, IReadOnlyList<FmdlMesh> head) =>
        OutlineIconRenderer.Render(gear, head.Count > 0 && !Encloses(gear, head) ? Accessory : Gear);

    /// <summary>
    /// True when the gear closes round the head: the less covered of the crown (top quarter of the head) and the back of
    /// the head, within 2 cm of the gear, is at least 80 %. Measured (Oct 25), crown / back: PW Battle Dress, Tactical
    /// Outfit and MSF balaclava 100 / 100; goggles with Snake's bandana 76 / 58 (the bandana covers the crown, so the
    /// crown alone can't tell it from a helmet), MSF headphones 27 / 19, goggles 7 / 20, face scarf 0 / 0. 80 % sits in
    /// the middle of the gap between the most-covering small gear (58) and the helmets (100). Calibrated on few helmets:
    /// re-check with every new helmet mod.
    /// </summary>
    public static bool Encloses(IReadOnlyList<FmdlMesh> gear, IReadOnlyList<FmdlMesh> head)
    {
        var (crown, back) = Coverage(gear, head);
        return Math.Min(crown, back) >= EnclosesAt;
    }

    /// <summary>The share both the crown and the back of the head must reach for gear to count as enclosing the head.</summary>
    public const float EnclosesAt = 0.8f;

    /// <summary>Share of the crown and of the back of <paramref name="head"/> that has gear within 2 cm.</summary>
    public static (float crown, float back) Coverage(IReadOnlyList<FmdlMesh> gear, IReadOnlyList<FmdlMesh> head)
    {
        var pts = head.SelectMany(m => m.Positions).ToArray();
        if (pts.Length == 0) return (0, 0);
        float yTop = pts.Max(p => p.Y), yBot = pts.Min(p => p.Y), zMin = pts.Min(p => p.Z), zMax = pts.Max(p => p.Z), h = yTop - yBot;
        var crown = pts.Where(p => p.Y > yTop - 0.25f * h).ToArray();
        var back = pts.Where(p => p.Z < zMin + 0.3f * (zMax - zMin) && p.Y < yTop - 0.35f * h && p.Y > yTop - 0.7f * h).ToArray();
        static (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X * 100), (int)MathF.Floor(p.Y * 100), (int)MathF.Floor(p.Z * 100));
        var cells = gear.SelectMany(m => m.Positions).Select(Cell).ToHashSet();
        float Share(Vector3[] set)
        {
            if (set.Length == 0) return 0;
            int hit = 0;
            foreach (var p in set)
            {
                var (x, y, z) = Cell(p);
                bool any = false;
                for (int dx = -2; dx <= 2 && !any; dx++) for (int dy = -2; dy <= 2 && !any; dy++) for (int dz = -2; dz <= 2 && !any; dz++) any = cells.Contains((x + dx, y + dy, z + dz));
                if (any) hit++;
            }
            return hit / (float)set.Length;
        }
        return (Share(crown), Share(back));
    }

    private const string BandanaIcon = "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_bandana_alp.ftex";
    private const string InfinityBandanaIcon = "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_infinity_bandana_alp.ftex";

    /// <summary>
    /// The infinity mark of the vanilla Infinity Bandana icon, at its place in the 256×128 icon: the pixels that differ
    /// from the Bandana icon (two rings, at about x 148–187, y 75–95), the rest transparent. Null when the game lacks either icon.
    /// </summary>
    public static RgbaImage? InfinityMark(GameData game) => Marks.GetValue(game, g => new(ReadInfinityMark(g))).Value;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameData, System.Runtime.CompilerServices.StrongBox<RgbaImage?>> Marks = new();

    private static RgbaImage? ReadInfinityMark(GameData game)
    {
        if (game.TryReadTexture(BandanaIcon) is not { } a || game.TryReadTexture(InfinityBandanaIcon) is not { } b || a.Width != b.Width || a.Height != b.Height) return null;
        var m = new RgbaImage(b.Width, b.Height);
        for (int i = 0; i < b.Width * b.Height * 4; i += 4)
        {
            int d = 0;
            for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(a.Pixels[i + c] - b.Pixels[i + c]));
            if (d > 8) b.Pixels.AsSpan(i, 4).CopyTo(m.Pixels.AsSpan(i, 4));
        }
        return m;
    }

    /// <summary>
    /// The files with the icon at <paramref name="icon"/> (an .ftex path without the extension) redrawn with
    /// <paramref name="mark"/> over it (scaled to the icon's size).
    /// </summary>
    public static Dictionary<string, byte[]> WithInfinityMark(IReadOnlyDictionary<string, byte[]> files, string icon, RgbaImage mark)
    {
        var parts = new Dictionary<int, byte[]>();
        for (int k = 1; k <= 6; k++) if (files.TryGetValue($"{icon}.{k}.ftexs", out var d)) parts[k] = d;
        var img = FtexTexture.Read(files[icon + ".ftex"]).DecodeLargestAvailable(parts);
        var copy = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        var marked = WithInfinityMark(img, mark);
        var ft = FtexWriter.Write(marked, withAlpha: true, FtexKind.Linear);
        for (int k = 1; k <= 6; k++) copy.Remove($"{icon}.{k}.ftexs");
        copy[icon + ".ftex"] = ft.Ftex;
        foreach (var (n, d) in ft.Ftexs) copy[$"{icon}.{n}.ftexs"] = d;
        return copy;
    }

    /// <summary>The icon with <paramref name="mark"/> over it, the mark scaled to the icon's size when the sizes differ.</summary>
    public static RgbaImage WithInfinityMark(RgbaImage icon, RgbaImage mark) =>
        Over(icon.Width == mark.Width && icon.Height == mark.Height ? mark : Scaled(mark, icon.Width, icon.Height), icon);

    /// <summary>Bilinear scale of <paramref name="img"/>; the colour is weighted by alpha so the transparent pixels do not darken the edges.</summary>
    public static RgbaImage Scaled(RgbaImage img, int width, int height)
    {
        var o = new RgbaImage(width, height);
        Span<float> c = stackalloc float[4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float sx = Math.Clamp((x + 0.5f) * img.Width / width - 0.5f, 0, img.Width - 1);
            float sy = Math.Clamp((y + 0.5f) * img.Height / height - 0.5f, 0, img.Height - 1);
            int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
            float fx = sx - x0, fy = sy - y0;
            c.Clear();
            foreach (var (px, py, w) in new[] { (x0, y0, (1 - fx) * (1 - fy)), (x1, y0, fx * (1 - fy)), (x0, y1, (1 - fx) * fy), (x1, y1, fx * fy) })
            {
                var p = img.Pixel(px, py);
                float wa = w * p[3];
                for (int k = 0; k < 3; k++) c[k] += p[k] * wa;
                c[3] += wa;
            }
            var q = o.Pixel(x, y);
            for (int k = 0; k < 3; k++) q[k] = c[3] > 0 ? (byte)Math.Round(c[k] / c[3]) : (byte)0;
            q[3] = (byte)Math.Round(c[3]);
        }
        return o;
    }

    /// <summary>Alpha-over of two white-on-transparent drawings.</summary>
    public static RgbaImage Over(RgbaImage top, RgbaImage bottom)
    {
        var o = new RgbaImage(top.Width, top.Height);
        for (int i = 0; i < top.Width * top.Height; i++)
        {
            float at = top.Pixels[i * 4 + 3] / 255f, ab = bottom.Pixels[i * 4 + 3] / 255f;
            float a = at + ab * (1 - at);
            float c = a > 0 ? (top.Pixels[i * 4] / 255f * at + bottom.Pixels[i * 4] / 255f * ab * (1 - at)) / a : 0;
            o.Pixels[i * 4] = o.Pixels[i * 4 + 1] = o.Pixels[i * 4 + 2] = (byte)MathF.Round(255 * c);
            o.Pixels[i * 4 + 3] = (byte)MathF.Round(255 * a);
        }
        return o;
    }

    /// <summary>Meshes moved by <paramref name="by"/> (to bring a body-rooted model into a face model's space).</summary>
    public static List<FmdlMesh> Moved(IEnumerable<FmdlMesh> meshes, Vector3 by) => meshes.Select(x => new FmdlMesh
    {
        Index = x.Index, MaterialIndex = x.MaterialIndex, MeshGroup = x.MeshGroup, Indices = x.Indices, Normals = x.Normals,
        Positions = x.Positions.Select(p => p + by).ToArray(),
    }).ToList();

    /// <summary>
    /// The offset that brings a model into Snake's face space: face models are rooted at SKL_002_CHEST (at the origin),
    /// body models at the waist. Zero when the model has no chest bone.
    /// </summary>
    public static Vector3 ToFaceSpace(FmdlModel m) => m.Bone("SKL_002_CHEST") is { } chest ? -chest.World : Vector3.Zero;

    /// <summary>True when a mesh's material uses a texture whose path contains one of <paramref name="words"/>.</summary>
    public static bool UsesTexture(FmdlModel m, FmdlMesh x, params string[] words) =>
        m.Materials[x.MaterialIndex].Textures.Any(t => PathDictionary.Bundled.TryGetPath(t.PathCode) is string p && words.Any(w => p.Contains(w, StringComparison.Ordinal)));

    /// <summary>The head silhouette of a Snake face model: its main mesh group without hair, eyelashes or bandana.</summary>
    public static List<FmdlMesh> HeadSilhouette(FmdlModel face) =>
        face.Meshes.Where(x => x.MeshGroup == 1 && !UsesTexture(face, x, "hair", "eyelash", "sna_bdn0")).ToList();

    /// <summary>
    /// Skin parts of a head model (face, eyes, mouth on the game's common head textures, Snake's face): the vanilla
    /// headgear icons draw only the gear, leaving the face out (the balaclava icon shows the helmet and jaw plates only).
    /// </summary>
    public static bool IsSkin(FmdlModel m, FmdlMesh x) =>
        UsesTexture(m, x, "/cm_head/", "/cm_m0_", "/cm_f0_", "/cm_eye", "/cm_iris", "/cm_mout", "_face0_def", "_face1_def", "_face2_def", "_eyes0");

    /// <summary>
    /// The gear of a head model for its icon: everything but the skin and placeholder meshes (all of it when nothing else
    /// is left). A placeholder is a mesh under 1 cm in every direction (Haven Trooper's helmet has a 2 mm box at the
    /// model's origin in its MESH_ROOT group, which made the framing 58 cm tall instead of 26 cm).
    /// </summary>
    public static List<FmdlMesh> GearMeshes(FmdlModel m)
    {
        var g = m.Meshes.Where(x => !IsSkin(m, x) && !Placeholder(x) && !(x.MeshGroup >= 0 && x.MeshGroup < m.MeshGroups.Count && m.MeshGroups[x.MeshGroup].hidden)).ToList();
        return g.Count > 0 ? g : m.Meshes.ToList();
    }

    /// <summary>A mesh too small to be seen (under 1 cm in every direction), or with no vertices.</summary>
    public static bool Placeholder(FmdlMesh x)
    {
        if (x.Positions.Length == 0) return true;
        var lo = x.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var hi = x.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var size = hi - lo;
        return MathF.Max(size.X, MathF.Max(size.Y, size.Z)) < 0.01f;
    }

    /// <summary>The bandana meshes of a Snake bandana face (face4/5/6): materials on the sna_bdn0 textures.</summary>
    public static List<FmdlMesh> Bandana(FmdlModel face) => face.Meshes.Where(x => UsesTexture(face, x, "sna_bdn0")).ToList();
}
