using System.Numerics;
using Svfm.Core.Formats;

namespace Svfm.Core.Rendering;

/// <summary>How a weapon or item is turned towards the camera.</summary>
public enum OutlineView
{
    /// <summary>Weapons: seen from the right-hand side (+X), muzzle (+Z) to the left, no perspective.</summary>
    Side,
    /// <summary>Items such as the cardboard box: turned 45° and seen slightly from above.</summary>
    ThreeQuarter,
    /// <summary>Turned by <see cref="OutlineIconOptions.Yaw"/>, <see cref="OutlineIconOptions.Pitch"/> and <see cref="OutlineIconOptions.Roll"/>.</summary>
    Custom,
}

/// <summary>Which model axis points up in a custom view; the minus forms turn the item over.</summary>
public enum UpAxis { Y, MinusY, X, MinusX, Z, MinusZ }

/// <summary>Settings for <see cref="OutlineIconRenderer"/>. Defaults are fitted on 14 vanilla weapon icons (brightness spread of the white areas, haze by distance).</summary>
public sealed record OutlineIconOptions
{
    public OutlineView View { get; init; } = OutlineView.Side;
    /// <summary>Custom view, in degrees: turn about the model's up axis, then tip towards the camera, then turn in the picture.</summary>
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    public float Roll { get; init; }
    /// <summary>Custom view: the model axis that points up before turning (vanilla item icons stand items upright).</summary>
    public UpAxis Up { get; init; } = UpAxis.Y;
    /// <summary>Largest picture width and height in pixels (vanilla long guns are about 200 px wide).</summary>
    public float MaxWidth { get; init; } = 200f;
    public float MaxHeight { get; init; } = 100f;
    /// <summary>Most pixels per metre, so small things stay small (vanilla handguns: about 280; grenades: about 520).</summary>
    public float MaxPixelsPerMetre { get; init; } = 280f;
    /// <summary>Zoom (&gt;1 = bigger) and offset in pixels, for manual framing.</summary>
    public float Zoom { get; init; } = 1f;
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    /// <summary>Edges between faces bent more than this many degrees are drawn as detail lines.</summary>
    public float CreaseAngle { get; init; } = 35f;
    /// <summary>Detail line strength and half-width in output pixels.</summary>
    public float LineAlpha { get; init; } = 0.38f;
    public float LineHalfWidth { get; init; } = 0.55f;
    /// <summary>Flat see-through fill inside the outline, and the extra brightness for surfaces turned away from the camera.</summary>
    public float FillAlpha { get; init; } = 0.16f;
    /// <summary>Strength of the one-pixel outline.</summary>
    public float OutlineAlpha { get; init; } = 0.85f;
    /// <summary>Cap on how bright overlapping see-through layers get.</summary>
    public float LayerCap { get; init; } = 0.7f;
    /// <summary>See-through rendering: every layer adds to the fill (weapons). Off: only the nearest surface is shaded (cleaner).</summary>
    public bool XRay { get; init; } = true;
    /// <summary>Strength of detail lines hidden behind a nearer surface (1 = drawn like visible ones).</summary>
    public float HiddenLineAlpha { get; init; } = 1f;
    public float RimAlpha { get; init; } = 0.7f;
    /// <summary>Lines and outline snapped to whole pixels at full strength, as in the vanilla item icons, instead of soft.</summary>
    public bool Crisp { get; init; }
    /// <summary>Connected detail lines shorter than this many output pixels in all are left out (bolts, rivets, small print).</summary>
    public float MinLinePixels { get; init; }
    /// <summary>Also draw a line where a nearer part overlaps a farther one (the inner outlines of the vanilla item icons).</summary>
    public bool DepthLines { get; init; }
    /// <summary>Glow just inside the outline (the vanilla item icons brighten the first 3–4 px inside); 1 = as fitted.</summary>
    public float InnerGlow { get; init; }
    /// <summary>Gap in depth, as a share of the model's depth, that counts as one part in front of another.</summary>
    public float DepthLineGap { get; init; } = 0.04f;
    /// <summary>Draw the edges of open surfaces (hair cards, cloth strips). Off gives cleaner icons of hair and cloth;
    /// the outline and the depth lines still draw their shape.</summary>
    public bool OpenEdgeLines { get; init; } = true;
    /// <summary>Leave unfilled where we look into the object (the nearest surface faces away), with a line round it, as
    /// the vanilla headgear icons draw a helmet's face opening.</summary>
    public bool HollowInside { get; init; }
    /// <summary>
    /// Dark haze around the outline (the vanilla icons' soft shadow): strength (1 = as in the vanilla icons) and how far
    /// it reaches (1 = about 6 px, as in the vanilla icons).
    /// </summary>
    public float HaloAlpha { get; init; } = 1.0f;
    public float HaloRadius { get; init; } = 1.0f;
    public int Supersample { get; init; } = 4;

    /// <summary>
    /// Item icons (supportweapon, item, tool): fitted on 14 vanilla item icons (Oct 24). They are cleaner than the weapon
    /// icons: a flat fill (alpha about 70–95), and crisp full-white lines for the outline, the main edges and the
    /// inner outlines where one part is in front of another; nothing hidden is drawn.
    /// </summary>
    public static OutlineIconOptions Item => new()
    {
        View = OutlineView.Custom, XRay = false, HiddenLineAlpha = 0, Crisp = true, DepthLines = true,
        LineAlpha = 1f, OutlineAlpha = 1f, LineHalfWidth = 0.5f, CreaseAngle = 45f, MinLinePixels = 6f,
        FillAlpha = 0.2f, RimAlpha = 0.15f, InnerGlow = 1f, LayerCap = 1f, MaxWidth = 105f, MaxHeight = 85f, MaxPixelsPerMetre = 520f,
    };
}

/// <summary>
/// Draws a weapon or item icon in the style of the vanilla weapon and item icons (EquipIcon/weapon, supportweapon,
/// item, tool): a white line drawing on transparent, 256×128. Findings from the vanilla icons (IconLab, Oct 24):
/// colour is white wherever the object is and black elsewhere; the alpha channel carries the picture: a bright
/// one-pixel outline, thinner detail lines, a dim see-through fill, and a soft halo outside the outline that reads as
/// a dark shadow because its colour is black. Weapons are side on, muzzle left, no perspective, centred on
/// (128, 64); long guns are about 200 px wide and handguns keep their real size (about 280 px per metre).
/// No vanilla icon content is used: everything comes from the model.
/// </summary>
public static class OutlineIconRenderer
{
    public const int Width = 256, Height = 128;

    /// <summary>
    /// Mesh groups left out by default (StrCode64 names, lower 48 bits): MESH_AMO (the spare magazine posed for the
    /// reload) and MESH_LIGHT (a flashlight option). MESH_SUP (suppressor) stays, as in the vanilla icons.
    /// </summary>
    public static readonly IReadOnlySet<ulong> DefaultHiddenGroups = new HashSet<ulong> { 0x41b84e943dfc, 0xc2f0b25a171e };
    public const ulong GroupSuppressor = 0xb4786cab0e2a, GroupMagazine = 0x41b84e943dfc, GroupLight = 0xc2f0b25a171e;

    /// <summary>The meshes an icon shows: groups not hidden in the model and not in <paramref name="hiddenGroups"/>.</summary>
    public static List<FmdlMesh> VisibleMeshes(FmdlModel m, IReadOnlySet<ulong>? hiddenGroups = null)
    {
        hiddenGroups ??= DefaultHiddenGroups;
        return m.Meshes.Where(x =>
        {
            if (x.MeshGroup < 0 || x.MeshGroup >= m.MeshGroups.Count) return true;
            var g = m.MeshGroups[x.MeshGroup];
            return !g.hidden && !hiddenGroups.Contains(g.hash & 0xFFFFFFFFFFFF);
        }).ToList();
    }

    public static RgbaImage Render(IReadOnlyList<FmdlModel> models, OutlineIconOptions? o = null, IReadOnlySet<ulong>? hiddenGroups = null) =>
        Render(models.SelectMany(m => VisibleMeshes(m, hiddenGroups)).ToList(), o);

    /// <summary>
    /// The framing that <see cref="Render(IReadOnlyList{FmdlMesh}, OutlineIconOptions?, IReadOnlyList{FmdlMesh}?, IReadOnlyList{FmdlMesh}?)"/>
    /// uses at zoom 1 and no offset: pixels per metre and the picture centre in view space (metres).
    /// </summary>
    /// <param name="meshes">The meshes the picture is framed on.</param>
    /// <param name="o">The options (view and size limits).</param>
    /// <returns>The framing, or null when the meshes have no points.</returns>
    public static (float PixelsPerMetre, float CentreX, float CentreY)? FitOf(IReadOnlyList<FmdlMesh> meshes, OutlineIconOptions o) =>
        Fit(meshes, o) is { } f ? (f.Ppm, f.Cx, f.Cy) : null;

    private static (float Ppm, float Cx, float Cy, Vector3 Min, Vector3 Max)? Fit(IReadOnlyList<FmdlMesh> meshes, OutlineIconOptions o)
    {
        var rot = ViewMatrix(o);
        var pts = meshes.SelectMany(x => x.Positions).Select(p => Vector3.Transform(p, rot)).ToArray();
        if (pts.Length == 0) return null;
        var mn = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var mx = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        float ppm = MathF.Min(o.MaxWidth / MathF.Max(1e-4f, mx.X - mn.X), o.MaxHeight / MathF.Max(1e-4f, mx.Y - mn.Y));
        return (MathF.Min(ppm, o.MaxPixelsPerMetre), (mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, mn, mx);
    }

    /// <summary>Model space → picture space (x right, y up, z towards the camera) for a view.</summary>
    public static Matrix4x4 ViewMatrix(OutlineIconOptions o) => o.View switch
    {
        OutlineView.Side => new Matrix4x4(0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1),   // screen x = −Z, y = Y, depth = X
        OutlineView.ThreeQuarter => Matrix4x4.CreateRotationY(MathF.PI / 4) * Matrix4x4.CreateRotationX(0.18f * MathF.PI),
        _ => UpMatrix(o.Up) * Matrix4x4.CreateRotationY(o.Yaw * MathF.PI / 180) * Matrix4x4.CreateRotationX(o.Pitch * MathF.PI / 180)
             * Matrix4x4.CreateRotationZ(o.Roll * MathF.PI / 180),
    };

    /// <summary>Turns the chosen model axis to +Y.</summary>
    public static Matrix4x4 UpMatrix(UpAxis up)
    {
        float q = MathF.PI / 2;
        return up switch
        {
            UpAxis.MinusY => Matrix4x4.CreateRotationZ(2 * q),
            UpAxis.X => Matrix4x4.CreateRotationZ(q),
            UpAxis.MinusX => Matrix4x4.CreateRotationZ(-q),
            UpAxis.Z => Matrix4x4.CreateRotationX(-q),
            UpAxis.MinusZ => Matrix4x4.CreateRotationX(q),
            _ => Matrix4x4.Identity,
        };
    }

    /// <summary>
    /// The axis an item stands on, as in the vanilla item icons: a long item (one side at least 1.5× the others) stands
    /// on its end, with the heavier end down; anything else keeps the model's own up.
    /// </summary>
    public static UpAxis AutoUp(IReadOnlyList<FmdlMesh> meshes)
    {
        var pts = meshes.SelectMany(m => m.Positions).ToArray();
        if (pts.Length == 0) return UpAxis.Y;
        var mn = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var mx = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var size = mx - mn;
        float[] e = [size.X, size.Y, size.Z];
        int longest = e[0] >= e[1] && e[0] >= e[2] ? 0 : e[1] >= e[2] ? 1 : 2;
        float second = e.Where((_, i) => i != longest).Max();
        int axis = e[longest] >= 1.5f * second ? longest : 1;
        // Wider end down: compare how much room the vertices in the end quarters take up across the axis (a C4 block's
        // wires and a grenade's fuse are thin, so they end up on top).
        float lo = Comp(mn, axis), hi = Comp(mx, axis), span = hi - lo;
        float EndArea(bool top)
        {
            var end = pts.Where(v => top ? Comp(v, axis) >= hi - span / 4 : Comp(v, axis) <= lo + span / 4).ToArray();
            if (end.Length == 0) return 0;
            int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
            return (end.Max(v => Comp(v, a1)) - end.Min(v => Comp(v, a1))) * (end.Max(v => Comp(v, a2)) - end.Min(v => Comp(v, a2)));
        }
        bool flip = axis != 1 && EndArea(true) > EndArea(false);
        return (axis, flip) switch
        {
            (0, false) => UpAxis.X, (0, true) => UpAxis.MinusX,
            (2, false) => UpAxis.Z, (2, true) => UpAxis.MinusZ,
            _ => UpAxis.Y,
        };
    }

    private static float Comp(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    /// <summary>
    /// The turn for an upright item: the front (0°), or the right side (270°) when the side shows clearly more of it
    /// (silhouette over 1.08× the front's). Matches 13 of 14 vanilla item icons: grenades, the capture cage and the
    /// magazine side on; mines, C4, the decoy and the iDroid from the front.
    /// </summary>
    public static float AutoYaw(IReadOnlyList<FmdlMesh> meshes, UpAxis up)
    {
        var pts = meshes.SelectMany(q => q.Positions).ToArray();
        if (pts.Length == 0) return 0;
        var size = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min) - pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        float scale = 120f / MathF.Max(1e-4f, size.Length());
        int Area(float yaw)
        {
            var r = ViewMatrix(new OutlineIconOptions { View = OutlineView.Custom, Up = up, Yaw = yaw });
            const int N = 256;
            var mask = new bool[N * N];
            var c3 = Vector3.Transform(pts.Aggregate(Vector3.Zero, (acc, v) => acc + v) / pts.Length, r);
            foreach (var mesh in meshes)
            {
                var sp = mesh.Positions.Select(p => { var v = Vector3.Transform(p, r); return new Vector2(N / 2f + (v.X - c3.X) * scale, N / 2f - (v.Y - c3.Y) * scale); }).ToArray();
                for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
                    if (a >= sp.Length || b >= sp.Length || c >= sp.Length) continue;
                    Raster(sp[a], sp[b], sp[c], N, N, i => mask[i] = true);
                }
            }
            return mask.Count(m => m);
        }
        return Area(270) > 1.08f * Area(0) ? 270 : 0;
    }

    /// <summary>
    /// Draws <paramref name="meshes"/>. <paramref name="occluders"/> hide what is behind them without being drawn (a head
    /// inside headgear); <paramref name="frame"/> are the meshes the picture is framed on (default: the drawn meshes and
    /// the occluders), so several drawings can share one framing.
    /// </summary>
    public static RgbaImage Render(IReadOnlyList<FmdlMesh> meshes, OutlineIconOptions? o = null,
        IReadOnlyList<FmdlMesh>? occluders = null, IReadOnlyList<FmdlMesh>? frame = null)
    {
        o ??= new OutlineIconOptions();
        int ss = Math.Max(1, o.Supersample), w = Width * ss, h = Height * ss;
        Matrix4x4 rot = ViewMatrix(o);
        Vector3 V(Vector3 p) => Vector3.Transform(p, rot);

        if (Fit(frame ?? meshes.Concat(occluders ?? []).ToList(), o) is not { } fit) return new RgbaImage(Width, Height);
        var (ppm, cx, cy, mn, mx) = fit;
        ppm *= o.Zoom * ss;
        Vector2 S(Vector3 p)
        {
            var v = V(p);
            return new(w / 2f + (v.X - cx) * ppm + o.OffsetX * ss, h / 2f - (v.Y - cy) * ppm - o.OffsetY * ss);
        }

        var fill = new float[w * h];
        var cover = new float[w * h];
        var line = new float[w * h];
        var depth = new float[w * h];
        var front = new float[w * h];
        var side = new sbyte[w * h];
        Array.Fill(depth, float.NegativeInfinity);
        float cosCrease = MathF.Cos(o.CreaseAngle * MathF.PI / 180);
        float depthEps = MathF.Max(1e-4f, (mx.Z - mn.Z) * 0.01f);
        float[]? occ = null;
        if (occluders is { Count: > 0 })
        {
            occ = new float[w * h];
            Array.Fill(occ, float.NegativeInfinity);
            foreach (var mesh in occluders)
            {
                var sp = mesh.Positions.Select(S).ToArray();
                var sz = mesh.Positions.Select(q => V(q).Z).ToArray();
                for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
                    if (a >= sp.Length || b >= sp.Length || c >= sp.Length) continue;
                    float za = sz[a], zb = sz[b], zc = sz[c];
                    RasterBary(sp[a], sp[b], sp[c], w, h, (idx, w0, w1, w2) => { float z = w0 * za + w1 * zb + w2 * zc; if (z > occ[idx]) occ[idx] = z; });
                }
            }
        }
        var edgeLists = new List<(Vector2[] sp, float[] sz, List<(int, int)> features)>();
        foreach (var mesh in meshes)
        {
            var p = mesh.Positions;
            var sp = p.Select(S).ToArray();
            var sz = p.Select(q => V(q).Z).ToArray();
            // Weld vertices split at UV seams so creases are found across them.
            var weldKey = new Dictionary<(int, int, int), int>();
            var weld = new int[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                var k = ((int)MathF.Round(p[i].X * 2000), (int)MathF.Round(p[i].Y * 2000), (int)MathF.Round(p[i].Z * 2000));
                if (!weldKey.TryGetValue(k, out int wi)) weldKey[k] = wi = i;
                weld[i] = wi;
            }
            var edges = new Dictionary<(int, int), List<Vector3>>();
            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
                if (a >= p.Length || b >= p.Length || c >= p.Length) continue;
                var cr = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
                if (cr.LengthSquared() < 1e-14f) continue;
                var fn = Vector3.Normalize(cr);
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                {
                    int wu = weld[u], wv = weld[v];
                    var e = wu < wv ? (wu, wv) : (wv, wu);
                    if (!edges.TryGetValue(e, out var list)) edges[e] = list = [];
                    list.Add(fn);
                }
                // See-through fill: every layer adds a little, surfaces turned away from the camera add more.
                // Without x-ray only the surface nearest the camera counts.
                float fz = Vector3.TransformNormal(fn, rot).Z;
                float facing = MathF.Abs(fz);
                sbyte sideOf = fz >= 0 ? (sbyte)1 : (sbyte)-1;
                float add = 0.03f + o.RimAlpha * (1 - facing) * (1 - facing);
                float za = sz[a], zb = sz[b], zc = sz[c];
                RasterBary(sp[a], sp[b], sp[c], w, h, (idx, w0, w1, w2) =>
                {
                    float z = w0 * za + w1 * zb + w2 * zc;
                    if (occ != null && z < occ[idx] - depthEps) return;
                    if (o.XRay) fill[idx] += add;
                    cover[idx] = 1;
                    if (z > depth[idx]) { depth[idx] = z; front[idx] = add; side[idx] = sideOf; }
                });
            }
            var features = new List<(int, int)>();
            foreach (var (e, normals) in edges)
                if (normals.Count == 1 ? o.OpenEdgeLines : Vector3.Dot(normals[0], normals[1]) < cosCrease) features.Add(e);
            if (o.MinLinePixels > 0) features = DropShortChains(features, sp, o.MinLinePixels * ss);
            edgeLists.Add((sp, sz, features));
        }
        if (!o.XRay)
            for (int i = 0; i < fill.Length; i++) if (cover[i] > 0) fill[i] = front[i];
        // Hollow: where the nearest surface faces away from the camera we look into the object (a helmet's face opening);
        // vanilla headgear icons leave that unfilled. The winding of the model decides the sign, so the side most of the
        // picture shows is taken as the outside.
        var hollow = new float[w * h];
        if (o.HollowInside)
        {
            long pos = 0, neg = 0;
            for (int i = 0; i < side.Length; i++) if (cover[i] > 0) { if (side[i] > 0) pos++; else if (side[i] < 0) neg++; }
            sbyte inside = pos >= neg ? (sbyte)-1 : (sbyte)1;
            for (int i = 0; i < side.Length; i++) if (cover[i] > 0 && side[i] == inside) { hollow[i] = 1; fill[i] = 0; }
            var marks = new List<Vector2>();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (cover[i] <= 0 || hollow[i] > 0) continue;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int X = x + dx, Y = y + dy;
                    if (X < 0 || Y < 0 || X >= w || Y >= h) continue;
                    if (hollow[Y * w + X] > 0) { marks.Add(new Vector2(x + 0.5f, y + 0.5f)); break; }
                }
            }
            foreach (var m in marks) Line(m, m, line, w, h, o.LineHalfWidth * ss);
        }
        // Detail lines; with HiddenLineAlpha below 1, lines behind a nearer surface are drawn fainter (cleaner icons).
        foreach (var (sp, sz, features) in edgeLists)
            foreach (var (i0, i1) in features)
                LineDepth(sp[i0], sp[i1], sz[i0], sz[i1], line, depth, depthEps, o.HiddenLineAlpha, w, h, o.LineHalfWidth * ss, occ);
        if (o.DepthLines) DrawDepthLines(cover, depth, line, w, h, MathF.Max(1e-4f, (mx.Z - mn.Z) * o.DepthLineGap), o.LineHalfWidth * ss);

        var alpha = new float[Width * Height];
        var cov = new float[Width * Height];
        float n = ss * ss;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float f = 0, c = 0, l = 0, ho = 0;
            for (int j = 0; j < ss; j++)
            for (int i = 0; i < ss; i++)
            {
                int idx = (y * ss + j) * w + x * ss + i;
                f += MathF.Min(o.LayerCap, fill[idx]); c += cover[idx]; l += line[idx]; ho += hollow[idx];
            }
            f /= n; c /= n; l /= n; ho /= n;
            if (o.Crisp) { c = c >= 0.5f ? 1 : 0; l = l >= 0.4f ? 1 : 0; f = c > 0 ? f / MathF.Max(1e-4f, CoverShare(cover, x, y, ss, w)) : 0; }
            alpha[y * Width + x] = MathF.Min(1, o.FillAlpha * MathF.Max(0, c - ho) + f + o.LineAlpha * l * (o.Crisp ? c : 1));
            cov[y * Width + x] = c;
        }
        // Outline: the coverage minus its erosion, at full strength.
        var eroded = Morph(cov, false);
        for (int i = 0; i < alpha.Length; i++) alpha[i] = MathF.Max(alpha[i], o.OutlineAlpha * MathF.Min(1, cov[i] - eroded[i]));
        if (o.InnerGlow > 0)
        {
            // Rings 1–4 px inside the outline, from the median of 13 vanilla item icons (alpha +54, +39, +14, +4).
            float[] glow = [0.21f, 0.15f, 0.055f, 0.015f];
            var cur = eroded;
            foreach (float g in glow)
            {
                var next = Morph(cur, false);
                for (int i = 0; i < alpha.Length; i++) alpha[i] = MathF.Min(1, alpha[i] + o.InnerGlow * g * MathF.Max(0, cur[i] - next[i]));
                cur = next;
            }
        }
        // The haze lies only outside the outline (inside, the drawing stays white). Its strength by distance from the
        // outline is the average of 14 vanilla weapon icons (alpha 107, 75, 42, 20, 4 at 2–6 px).
        var halo = Haze(cov, o.HaloRadius);

        var img = new RgbaImage(Width, Height);
        for (int i = 0; i < alpha.Length; i++)
        {
            float af = alpha[i], ah = MathF.Min(1, o.HaloAlpha * halo[i]) * (1 - cov[i]);
            float a = af + ah * (1 - af);
            float white = a > 0 ? af / a : 0;
            byte g = (byte)MathF.Round(255 * white);
            img.Pixels[i * 4] = img.Pixels[i * 4 + 1] = img.Pixels[i * 4 + 2] = g;
            img.Pixels[i * 4 + 3] = (byte)MathF.Round(255 * Math.Clamp(a, 0, 1));
        }
        return img;
    }

    /// <summary>Share of an output pixel's samples that are covered.</summary>
    private static float CoverShare(float[] cover, int x, int y, int ss, int w)
    {
        float c = 0;
        for (int j = 0; j < ss; j++) for (int i = 0; i < ss; i++) c += cover[(y * ss + j) * w + x * ss + i];
        return c / (ss * ss);
    }

    /// <summary>Leaves out connected groups of feature edges whose length on screen is under <paramref name="min"/>.</summary>
    private static List<(int, int)> DropShortChains(List<(int, int)> edges, Vector2[] sp, float min)
    {
        var parent = new Dictionary<int, int>();
        int Find(int a) { while (parent.TryGetValue(a, out int p) && p != a) { parent[a] = parent.GetValueOrDefault(p, p); a = p; } return a; }
        foreach (var (a, b) in edges)
        {
            parent.TryAdd(a, a); parent.TryAdd(b, b);
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }
        var len = new Dictionary<int, float>();
        foreach (var (a, b) in edges) { int r = Find(a); len[r] = len.GetValueOrDefault(r) + Vector2.Distance(sp[a], sp[b]); }
        return edges.Where(e => len[Find(e.Item1)] >= min).ToList();
    }

    /// <summary>Marks a line on the nearer side wherever neighbouring samples jump in depth by more than <paramref name="gap"/>.</summary>
    private static void DrawDepthLines(float[] cover, float[] depth, float[] line, int w, int h, float gap, float r)
    {
        var marks = new List<Vector2>();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (cover[i] <= 0) continue;
            foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
            {
                int X = x + dx, Y = y + dy;
                if (X >= w || Y >= h) continue;
                int k = Y * w + X;
                if (cover[k] <= 0 || MathF.Abs(depth[i] - depth[k]) <= gap) continue;
                marks.Add(depth[i] > depth[k] ? new Vector2(x + 0.5f, y + 0.5f) : new Vector2(X + 0.5f, Y + 0.5f));
            }
        }
        foreach (var m in marks) Line(m, m, line, w, h, r);
    }

    private static void RasterBary(Vector2 p0, Vector2 p1, Vector2 p2, int w, int h, Action<int, float, float, float> plot)
    {
        float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (MathF.Abs(area) < 1e-6f) return;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        int maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) / area;
            float w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) / area;
            float w2 = 1 - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            plot(y * w + x, w0, w1, w2);
        }
    }

    private static void LineDepth(Vector2 a, Vector2 b, float za, float zb, float[] buf, float[] depth, float eps, float hiddenAlpha,
        int w, int h, float r, float[]? occ = null)
    {
        var d = b - a;
        float len2 = d.LengthSquared();
        int minX = Math.Max(0, (int)(MathF.Min(a.X, b.X) - r - 1)), maxX = Math.Min(w - 1, (int)(MathF.Max(a.X, b.X) + r + 1));
        int minY = Math.Max(0, (int)(MathF.Min(a.Y, b.Y) - r - 1)), maxY = Math.Min(h - 1, (int)(MathF.Max(a.Y, b.Y) + r + 1));
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            var q = new Vector2(x + 0.5f, y + 0.5f);
            float t = len2 < 1e-10f ? 0 : Math.Clamp(Vector2.Dot(q - a, d) / len2, 0, 1);
            if ((a + d * t - q).LengthSquared() > r * r) continue;
            int idx = y * w + x;
            float zl = za + (zb - za) * t;
            if (occ != null && zl < occ[idx] - eps) continue;
            float v = hiddenAlpha >= 1 || zl >= depth[idx] - eps ? 1 : hiddenAlpha;
            if (v > buf[idx]) buf[idx] = v;
        }
    }

    private static void Raster(Vector2 p0, Vector2 p1, Vector2 p2, int w, int h, Action<int> plot)
    {
        float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (MathF.Abs(area) < 1e-6f) return;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        int maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) / area;
            float w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) / area;
            if (w0 < 0 || w1 < 0 || 1 - w0 - w1 < 0) continue;
            plot(y * w + x);
        }
    }

    private static void Line(Vector2 a, Vector2 b, float[] buf, int w, int h, float r)
    {
        var d = b - a;
        float len2 = d.LengthSquared();
        int minX = Math.Max(0, (int)(MathF.Min(a.X, b.X) - r - 1)), maxX = Math.Min(w - 1, (int)(MathF.Max(a.X, b.X) + r + 1));
        int minY = Math.Max(0, (int)(MathF.Min(a.Y, b.Y) - r - 1)), maxY = Math.Min(h - 1, (int)(MathF.Max(a.Y, b.Y) + r + 1));
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            var q = new Vector2(x + 0.5f, y + 0.5f);
            float t = len2 < 1e-10f ? 0 : Math.Clamp(Vector2.Dot(q - a, d) / len2, 0, 1);
            if ((a + d * t - q).LengthSquared() <= r * r) buf[y * w + x] = 1;
        }
    }

    private static float[] Morph(float[] c, bool dilate)
    {
        var o = new float[c.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float m = dilate ? 0 : 1;
            for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int X = x + i, Y = y + j;
                float v = X < 0 || Y < 0 || X >= Width || Y >= Height ? 0 : c[Y * Width + X];
                m = dilate ? MathF.Max(m, v) : MathF.Min(m, v);
            }
            o[y * Width + x] = m;
        }
        return o;
    }

    /// <summary>Haze alpha by distance (px) from the covered area, fitted on the vanilla weapon icons.</summary>
    private static readonly float[] HazeCurve = [0.70f, 0.58f, 0.44f, 0.29f, 0.15f, 0.06f, 0.008f, 0f];

    private static float[] Haze(float[] cov, float reach)
    {
        const int R = 8;
        var o = new float[cov.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float best = float.MaxValue;
            for (int j = -R; j <= R; j++)
            {
                int Y = y + j;
                if (Y < 0 || Y >= Height) continue;
                for (int i = -R; i <= R; i++)
                {
                    int X = x + i;
                    if (X < 0 || X >= Width) continue;
                    float c = cov[Y * Width + X];
                    if (c <= 0) continue;
                    // A partly covered pixel counts as nearer by its uncovered share.
                    float d = MathF.Sqrt(i * i + j * j) - (c - 0.5f);
                    if (d < best) best = d;
                }
            }
            if (best == float.MaxValue) continue;
            float t = Math.Clamp(best / MathF.Max(0.1f, reach), 0, HazeCurve.Length - 1);
            int k = (int)t;
            o[y * Width + x] = k >= HazeCurve.Length - 1 ? 0 : HazeCurve[k] + (HazeCurve[k + 1] - HazeCurve[k]) * (t - k);
        }
        return o;
    }

    private static float[] Blur(float[] c, float sigma)
    {
        int r = (int)MathF.Ceiling(sigma * 3);
        var k = Enumerable.Range(-r, 2 * r + 1).Select(i => MathF.Exp(-i * i / (2 * sigma * sigma))).ToArray();
        float ks = k.Sum();
        var t = new float[c.Length];
        var o = new float[c.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float v = 0;
            for (int i = -r; i <= r; i++) v += c[y * Width + Math.Clamp(x + i, 0, Width - 1)] * k[i + r];
            t[y * Width + x] = v / ks;
        }
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float v = 0;
            for (int i = -r; i <= r; i++) v += t[Math.Clamp(y + i, 0, Height - 1) * Width + x] * k[i + r];
            o[y * Width + x] = v / ks;
        }
        return o;
    }
}
