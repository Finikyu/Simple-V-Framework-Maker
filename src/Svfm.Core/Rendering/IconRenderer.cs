using System.Numerics;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;

namespace Svfm.Core.Rendering;

/// <summary>Where the camera looks and how much it shows.</summary>
public sealed record IconFraming
{
    /// <summary>Picture centre height, as a fraction from the CHEST bone (0) to the NECK bone (1).</summary>
    public float CentreAlongChestToNeck { get; init; } = 0.785f;
    /// <summary>Height of the 256×128 picture in world units, as a multiple of NECK − CHEST.</summary>
    public float HeightInChestToNeck { get; init; } = 0.96f;
    /// <summary>Camera distance in metres (the vanilla icons look nearly orthographic).</summary>
    public float CameraDistance { get; init; } = 2.0f;
    /// <summary>Nudges in picture fractions (manual framing): right/up, and zoom (&gt;1 = closer).</summary>
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float Zoom { get; init; } = 1f;
    /// <summary>
    /// A model without named CHEST/NECK bones (D-Dog's bones are unnamed): the model is turned by <see cref="View"/> (model
    /// space → camera space: +X right, +Y up, +Z towards the camera) and the picture centred on <see cref="Centre"/> (camera
    /// space, metres) with <see cref="WorldHeight"/> metres of height. null = the chest framing above.
    /// </summary>
    public Matrix4x4? View { get; init; }
    public Vector3 Centre { get; init; }
    public float WorldHeight { get; init; } = 0.3f;
    /// <summary>The models already share one space (the parts of a weapon), so no model moves onto the first one by a shared bone.</summary>
    public bool ModelsInPlace { get; init; }

    /// <summary>
    /// D-Horse's tack icons (EquipIcon/buddy/ui_dh_hrs*): his left side as D-Dog's (camera on +X), at the front edge of the
    /// saddle (shoulder coat on the left, the saddle flap and its buckles on the right), 0.82 m high. Fitted (Oct 26) by
    /// image correlation (edges and brightness, the icon window) on all five vanilla icons (NORMAL hrs0_m0, BATTLE DRESS
    /// hrs2_m0–m2, FURICORN hrs3_m0): every one is best at centre (−0.83, −0.03) m, height 0.80–0.84 m.
    /// </summary>
    public static IconFraming HorseBuddy => new()
    {
        View = new Matrix4x4(0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1),
        Centre = new Vector3(-0.83f, -0.03f, 0f),
        WorldHeight = 0.82f,
    };

    /// <summary>
    /// D-Dog's gear icons (EquipIcon/buddy/ui_dd_ddg0_m*): his left flank (camera on +X; Fox models face +Z with their left
    /// at +X), centred on the gear's side pouch, 0.5 m high. Fitted by eye on the five vanilla gear icons (Oct 26, with the
    /// user): pouch edges, top band and buckles within a few pixels. D-Dog's bones are unnamed, so the centre is in metres.
    /// </summary>
    public static IconFraming DogBuddy => new()
    {
        View = new Matrix4x4(0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1),
        Centre = new Vector3(-0.235f, -0.02f, 0f),
        WorldHeight = 0.5f,
    };

    /// <summary>
    /// DD female suit icons. Vanilla DD women wear the suit icons rendered on Snake; her body is slimmer and her gear sits
    /// lower, so the Snake framing shows her too small and too high. Fitted by edge correlation on the vanilla SNEAKING
    /// SUIT icon (ui_st_sna4) with her plparts_ddf_venom, as Snake's was (user, Oct 26; the same fit gives Snake 0.78/0.94,
    /// and DD men, whose sneaking suit renders exactly like Snake's, keep the default).
    /// </summary>
    public static IconFraming DdFemale => new() { CentreAlongChestToNeck = 0.69f, HeightInChestToNeck = 0.84f };

    /// <summary>
    /// Quiet's costume icons (EquipIcon/buddy/ui_qwp_suit_*): her chest, fitted on the six vanilla costume icons by edge
    /// correlation (centre 0.39 sat 1–3 px too high; 0.405 matches). Her model is rooted at the waist, so CHEST→NECK is longer than Snake's.
    /// </summary>
    public static IconFraming QuietBuddy => new() { CentreAlongChestToNeck = 0.405f, HeightInChestToNeck = 0.88f };

    /// <summary>The framing of the game's own costume icons for a buddy ("quiet", "dog", "horse").</summary>
    public static IconFraming ForBuddy(string buddy) => buddy switch { "quiet" => QuietBuddy, "dog" => DogBuddy, _ => HorseBuddy };

    /// <summary>
    /// This framing with the user's nudges (offset and zoom) of <paramref name="nudge"/>. Issue #33: a buddy icon with a
    /// moved framing used the nudge alone, which is Snake's chest framing: Quiet's icon showed the wrong part, and D-Dog and
    /// D-Horse (no chest bones) could not be drawn.
    /// </summary>
    public IconFraming MovedBy(IconFraming? nudge) => nudge == null ? this : this with { OffsetX = nudge.OffsetX, OffsetY = nudge.OffsetY, Zoom = nudge.Zoom };
}

/// <summary>How the normal map is read (fitted against the vanilla icons).</summary>
public sealed record IconShaderOptions
{
    public bool UseNormalMap { get; init; } = true;
    public bool FlipNormalX { get; init; }
    public bool FlipNormalY { get; init; }
    public float NormalStrength { get; init; } = 1f;
    /// <summary>Colour factor for surfaces seen from behind or deep inside the body (inside a collar); they read as shadow.</summary>
    public float InteriorShade { get; init; } = 0.12f;
    /// <summary>Surfaces this far (m) behind the chest bone, seen through an opening, count as inside.</summary>
    public float InteriorDepth { get; init; } = 0.07f;
    /// <summary>
    /// Multiply the base colour by the material's layer colour map (Layer_Tex_SRGB) read with the mesh's second UV set.
    /// D-Dog's fur cards are white with alpha; the coat colour comes from that map (ddg0_hair*_c00_lym). Off by
    /// default (the fitted Snake and Quiet renders don't use it).
    /// </summary>
    public bool LayerColourMap { get; init; }
}

/// <summary>
/// Studio lighting for the icon: key, fill and rim lights, a sky/ground ambient, a specular term driven
/// by the specular map, and a final exposure and contrast. Directions point from the surface towards the
/// light, in camera space (+X right, +Y up, +Z towards the camera). The defaults are the rig fitted
/// against the vanilla render icons (see IconLab).
/// </summary>
public sealed record IconLighting
{
    // Fitted on the SNEAKING SUIT, BATTLE DRESS, SV-SNEAKING SUIT, PARASITE SUIT and BLACK camo icons (IconLab "fit",
    // rig in tools/IconLab/rig.txt): key and fill from the front and above, a sky light, and a strong rim from
    // behind that lights the edges.
    public Vector3 Key { get; init; } = new(0.1630f, 0.5068f, 0.8465f);
    public Vector3 KeyColor { get; init; } = new(2.3350f, 2.4394f, 2.5437f);
    public Vector3 Fill { get; init; } = new(-0.3556f, 0.2132f, 0.9100f);
    public Vector3 FillColor { get; init; } = new(1.4313f);
    public Vector3 Rim { get; init; } = new(-0.0256f, -0.8067f, -0.5904f);
    public Vector3 RimColor { get; init; } = new(5.6769f);
    public Vector3 Sky { get; init; } = new(1.8132f);
    public Vector3 Ground { get; init; } = Vector3.Zero;
    /// <summary>
    /// Specular, from its own studio light <see cref="SpecLight"/>: strength × exp(SpecMapSlope × (specular map R − 0.5));
    /// Blinn-Phong power = SpecPower × exp(SpecPowerSlope × specular map G) (G is roughness when the slope is
    /// negative); specular map B tints the highlight with the surface colour.
    /// </summary>
    public Vector3 SpecLight { get; init; } = new(0.0482f, 0.3040f, 0.9514f);
    public float SpecMapSlope { get; init; } = 1.9512f;
    public float SpecStrength { get; init; } = 0.2266f;
    public float SpecPower { get; init; } = 53.4846f;
    public float SpecPowerSlope { get; init; } = -2.2291f;
    /// <summary>Final brightness multiplier (linear).</summary>
    public float Exposure { get; init; } = 0.3663f;
    /// <summary>Saturation in sRGB space (1 = unchanged); the vanilla icons are more saturated than the textures.</summary>
    public float Saturation { get; init; } = 1.1269f;
    /// <summary>Contrast around <see cref="Pivot"/> in sRGB space.</summary>
    public float Contrast { get; init; } = 1.2201f;
    public float Pivot { get; init; } = 0.4847f;
    /// <summary>
    /// Soft shoulder: values above this (sRGB 0–1) roll off towards white instead of clipping, so near-white fabric
    /// keeps its shading. 0 = off (the fitted Snake rig; its vanilla icons have no white areas to fit).
    /// </summary>
    public float Shoulder { get; init; }

    /// <summary>The Snake rig with the soft shoulder at 0.8, for buddy renders (white costumes such as the IGA suit).</summary>
    public static IconLighting WithShoulder => new() { Shoulder = 0.8f };

    /// <summary>
    /// The weapon preview rig: the Snake rig with no specular, a lower exposure and less contrast. Fitted on the mean
    /// colours of the grip, the frame and the cylinder of the ZORN-KP (hg09) in camo c07 in the game's weapon view
    /// (svfm_out/shots/camo_research/ingame_hg09_c07_3.png). The Snake rig made the gun about 2.5 times too bright.
    /// </summary>
    public static IconLighting Weapon => new() { SpecStrength = 0, Exposure = 0.12f, Contrast = 0.8f, Saturation = 1 };
}

/// <summary>What one render pass saw, per sub-sample: surface colour, normal, specular map and depth.</summary>
public sealed class GBuffer
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Supersample { get; init; }
    public required Vector3[] Albedo { get; init; }
    public required Vector3[] Normal { get; init; }
    public required Vector3[] Spec { get; init; }
    public required float[] Depth { get; init; }
    public bool Covered(int i) => !float.IsNegativeInfinity(Depth[i]);
}

/// <summary>
/// Software renderer for suit icons: draws the outfit's own model with its own textures, front on,
/// framed on the chest like the vanilla suit icons, and puts the result in the suit-icon frame.
/// No vanilla icon content is used. Framing rule, fitted on the vanilla render icons (IconLab "frame"):
/// centre at CHEST + 0.785 × (NECK − CHEST), picture height 0.96 × (NECK − CHEST), camera straight in front, 2 m away. Rendering is two
/// steps (<see cref="RenderGBuffer"/>, then <see cref="Shade"/>) so the lighting can be fitted quickly.
/// </summary>
public static class IconRenderer
{
    public const int Width = 256, Height = 128;

    /// <param name="models">The models that make up the look (body, plus head/arm parts if wanted).</param>
    /// <param name="albedo">Finds a texture by path code (mod files first, then game files); null = none.</param>
    /// <param name="textureSwaps">Texture path code → replacement (what the look's .fv2 does, e.g. the camo).</param>
    /// <param name="framing">Camera rule; defaults to the vanilla suit-icon framing.</param>
    /// <param name="lighting">Studio lighting.</param>
    /// <param name="supersample">Anti-aliasing factor per axis.</param>
    /// <param name="options">How the normal map is read.</param>
    /// <param name="fv2Swaps">Per-material swaps from the look's .fv2 files (e.g. the camo).</param>
    /// <param name="width">Picture width (the icon size by default).</param>
    /// <param name="height">Picture height.</param>
    public static RgbaImage Render(IReadOnlyList<FmdlModel> models, Func<ulong, RgbaImage?> albedo,
        IReadOnlyDictionary<ulong, ulong>? textureSwaps = null, IconFraming? framing = null, IconLighting? lighting = null,
        int supersample = 3, IconShaderOptions? options = null, IReadOnlyList<Fv2File.TextureSwap>? fv2Swaps = null, int width = Width, int height = Height) =>
        Shade(RenderGBuffer(models, albedo, textureSwaps, framing, supersample, options, fv2Swaps, width, height), lighting ?? new IconLighting());

    /// <summary>
    /// The part of the model the chest framing shows: the picture centre and height in metres (model space). null for a
    /// model without CHEST and NECK bones. The 3D preview uses it to draw the icon border (issue #9).
    /// </summary>
    /// <param name="body">The body model.</param>
    /// <param name="framing">The chest framing.</param>
    /// <returns>The centre and the height, or null.</returns>
    public static (Vector3 centre, float worldH)? ChestWindow(FmdlModel body, IconFraming framing)
    {
        if (body.Bone("SKL_002_CHEST") is not { } chest || body.Bone("SKL_003_NECK") is not { } neck) return null;
        float span = neck.World.Y - chest.World.Y;
        float worldH = framing.HeightInChestToNeck * span / framing.Zoom;
        var centre = new Vector3(framing.OffsetX * worldH * 2, chest.World.Y + framing.CentreAlongChestToNeck * span + framing.OffsetY * worldH, 0);
        return (centre, worldH);
    }

    public static GBuffer RenderGBuffer(IReadOnlyList<FmdlModel> models, Func<ulong, RgbaImage?> lookup,
        IReadOnlyDictionary<ulong, ulong>? textureSwaps = null, IconFraming? framing = null, int supersample = 3,
        IconShaderOptions? options = null, IReadOnlyList<Fv2File.TextureSwap>? fv2Swaps = null, int width = Width, int height = Height)
    {
        var matSwaps = new Dictionary<(uint, uint), ulong>();
        foreach (var sw in fv2Swaps ?? []) matSwaps[(sw.Material, sw.Slot)] = sw.Texture;
        framing ??= new IconFraming();
        options ??= new IconShaderOptions();
        var body = models[0];
        var view = framing.View;
        float worldH;
        Vector3 centre;
        float insideZ;
        if (view is { } vm)
        {
            worldH = framing.WorldHeight / framing.Zoom;
            centre = framing.Centre + new Vector3(framing.OffsetX * worldH * 2, framing.OffsetY * worldH, 0);
            insideZ = float.NegativeInfinity;
        }
        else
        {
            (centre, worldH) = ChestWindow(body, framing) ?? throw new InvalidDataException("model has no SKL_002_CHEST or SKL_003_NECK bone");
            insideZ = body.Bone("SKL_002_CHEST")!.World.Z - options.InteriorDepth;
        }

        int w = width * supersample, h = height * supersample;
        var gb = new GBuffer
        {
            Width = width, Height = height, Supersample = supersample,
            Albedo = new Vector3[w * h], Normal = new Vector3[w * h], Spec = new Vector3[w * h], Depth = new float[w * h],
        };
        Array.Fill(gb.Depth, float.NegativeInfinity);
        float camZ = framing.CameraDistance;
        float pixelsPerMetreAtCentre = h / worldH;

        var texCache = new Dictionary<ulong, RgbaImage?>();
        RgbaImage? Tex(ulong code)
        {
            if (code == 0) return null;
            if (textureSwaps != null && textureSwaps.TryGetValue(code, out var s)) code = s;
            if (!texCache.TryGetValue(code, out var t)) texCache[code] = t = lookup(code);
            return t;
        }

        foreach (var model in models)
        {
        // Parts such as the head are modelled relative to their own root bone (the face's is the CHEST);
        // move them onto the body by the first bone the two share.
        var offset = Vector3.Zero;
        if (model != body && !framing.ModelsInPlace)
            foreach (var bone in model.Bones)
                if (body.Bones.FirstOrDefault(b => b.NameHash == bone.NameHash && b.NameHash != 0) is { } match)
                { offset = match.World - bone.World; break; }
        foreach (var mesh in model.Meshes)
        {
            if (mesh.MeshGroup >= 0 && mesh.MeshGroup < model.MeshGroups.Count && model.MeshGroups[mesh.MeshGroup].hidden) continue;
            if (mesh.Uv == null) continue;
            var mat = mesh.MaterialIndex < model.Materials.Count ? model.Materials[mesh.MaterialIndex] : null;
            ulong SlotCode(string role)
            {
                if (mat == null) return 0;
                if (matSwaps.TryGetValue(((uint)mat.NameHash, FoxHash.StrCode32(role)), out var swapped)) return swapped;
                return mat.Slot(role)?.PathCode ?? 0;
            }
            ulong baseCode = SlotCode("Base_Tex_SRGB");
            if (baseCode == 0) baseCode = SlotCode("Base_Tex_LIN");
            // A model that names its base slot something else (some mods export Base_Tex, BaseColor or just Base) used to
            // come out flat grey, which reads as "missing its textures". Fall back to any slot whose role starts with
            // Base, then to the first slot that actually resolves to a picture.
            if (baseCode == 0 && mat != null)
            {
                baseCode = mat.Textures.FirstOrDefault(t => t.Role != null && t.Role.StartsWith("Base", StringComparison.OrdinalIgnoreCase))?.PathCode ?? 0;
                if (baseCode == 0)
                    baseCode = mat.Textures.Select(t => t.PathCode).FirstOrDefault(c => c != 0 && Tex(c) != null);
            }
            var tex = Tex(baseCode);
            var nrmTex = options.UseNormalMap ? Tex(SlotCode("NormalMap_Tex_NRM")) : null;
            var srmTex = Tex(SlotCode("SpecularMap_Tex_LIN"));
            var layerTex = options.LayerColourMap && mesh.Uv1 != null ? Tex(SlotCode("Layer_Tex_SRGB")) : null;
            // Layer-multiply shaders (fox3DDF_Blin_LayerMul_*: the DD fatigues, the MGO suits): the camo is the layer map,
            // tiled by URepeat/VRepeat_UV and shifted by U/VShift_UV, multiplied into the base where the layer mask is white
            // (the base is near white there). Hardened Battlegear: base equip1, layer equip5 (512², 5×5), mask equip6.
            bool layerMul = !options.LayerColourMap && mat?.IsLayerMultiply == true;
            var mulLayer = layerMul ? Tex(SlotCode("Layer_Tex_SRGB")) : null;
            var mulMask = mulLayer != null ? Tex(SlotCode("LayerMask_Tex_LIN")) : null;
            bool weaponLayer = mat != null && FmdlMaterial.WeaponLayerMulShaders.Contains(mat.ShaderHash);
            float Param(string name, float fallback) => mat?.Param(name, fallback) ?? fallback;
            var layerRepeat = new Vector2(Param("URepeat_UV", 1), Param("VRepeat_UV", 1));
            var layerShift = new Vector2(Param("UShift_UV", 0), Param("VShift_UV", 0));
            bool alphaTested = baseCode != 0 && FoxHash.ExtensionOf(baseCode) == "ftex" && tex != null && HasAlpha(tex);

            var p = offset == Vector3.Zero ? mesh.Positions : mesh.Positions.Select(v => v + offset).ToArray();
            var nr = mesh.Normals;
            var tg = mesh.Tangents;
            if (view is { } turn)
            {
                // Camera at Centre + camZ along +Z: positions relative to the centre's depth, directions turned alike.
                p = p.Select(v => Vector3.Transform(v, turn) - new Vector3(0, 0, centre.Z)).ToArray();
                nr = nr?.Select(v => Vector3.TransformNormal(v, turn)).ToArray();
                tg = tg?.Select(v => { var t = Vector3.TransformNormal(new Vector3(v.X, v.Y, v.Z), turn); return new Vector4(t, v.W); }).ToArray();
            }
            var sx = new float[p.Length]; var sy = new float[p.Length]; var sz = new float[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                float persp = camZ / Math.Max(0.05f, camZ - p[i].Z);
                sx[i] = w / 2f + (p[i].X - centre.X) * pixelsPerMetreAtCentre * persp;
                sy[i] = h / 2f - (p[i].Y - centre.Y) * pixelsPerMetreAtCentre * persp;
                sz[i] = p[i].Z;
            }

            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
                if (a >= p.Length || b >= p.Length || c >= p.Length) continue;
                RasterTriangle(a, b, c);
            }

            void RasterTriangle(int a, int b, int c)
            {
                float x0 = sx[a], y0 = sy[a], x1 = sx[b], y1 = sy[b], x2 = sx[c], y2 = sy[c];
                float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
                if (MathF.Abs(area) < 1e-6f) return;
                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))));
                int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2))));
                int maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));
                if (minX > maxX || minY > maxY) return;
                var uv = mesh.Uv!;
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area;
                    float w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * sz[a] + w1 * sz[b] + w2 * sz[c];
                    int idx = y * w + x;
                    if (z <= gb.Depth[idx]) continue;

                    Vector2 tc = w0 * uv[a] + w1 * uv[b] + w2 * uv[c];
                    Vector3 albedoRgb = new(0.5f);
                    float alpha = 1;
                    if (tex != null) (albedoRgb, alpha) = SampleSrgb(tex, tc);
                    if (alphaTested && alpha < 0.5f) continue;
                    if (layerTex != null) albedoRgb *= SampleSrgb(layerTex, w0 * mesh.Uv1![a] + w1 * mesh.Uv1[b] + w2 * mesh.Uv1[c]).rgb;
                    if (mulLayer != null)
                    {
                        float mk = mulMask != null ? SampleLinear(mulMask, tc).X : 1f;
                        if (weaponLayer)
                        {
                            // The game's weapon shader (FmdlMaterial.WeaponLayerMulShaders); UV0 when the mesh has no UV1.
                            var luv = mesh.Uv1 != null ? w0 * mesh.Uv1[a] + w1 * mesh.Uv1[b] + w2 * mesh.Uv1[c] : tc;
                            var (lrgb, la) = SampleSrgb(mulLayer, (luv + layerShift) * layerRepeat);
                            albedoRgb *= Vector3.Lerp(Vector3.One, lrgb, mk * la);
                        }
                        else albedoRgb *= Vector3.Lerp(Vector3.One, SampleSrgb(mulLayer, tc * layerRepeat + layerShift).rgb, mk);
                    }

                    Vector3 n = nr != null ? Vector3.Normalize(w0 * nr[a] + w1 * nr[b] + w2 * nr[c]) : Vector3.UnitZ;
                    bool interior = n.Z < -0.05f || z < insideZ;
                    if (nrmTex != null && tg != null)
                    {
                        var tv = w0 * tg[a] + w1 * tg[b] + w2 * tg[c];
                        var tan = new Vector3(tv.X, tv.Y, tv.Z);
                        tan -= n * Vector3.Dot(n, tan);
                        if (tan.LengthSquared() > 1e-10f)
                        {
                            tan = Vector3.Normalize(tan);
                            float hand = (tg[a].W + tg[b].W + tg[c].W) < 0 ? -1f : 1f;
                            var bit = Vector3.Cross(n, tan) * hand;
                            var m = SampleLinear(nrmTex, tc);
                            // DXT5nm layout: X in alpha, Y in green.
                            float nx = (m.W * 2 - 1) * options.NormalStrength, ny = (m.Y * 2 - 1) * options.NormalStrength;
                            if (options.FlipNormalX) nx = -nx;
                            if (options.FlipNormalY) ny = -ny;
                            float nz = MathF.Sqrt(MathF.Max(0, 1 - nx * nx - ny * ny));
                            n = Vector3.Normalize(tan * nx + bit * ny + n * nz);
                        }
                    }
                    if (n.Z < 0) n = -n; // two-sided: cloth backs face the camera too
                    Vector3 spec = new(0.5f, 0.5f, 0f);
                    if (srmTex != null) { var m = SampleLinear(srmTex, tc); spec = new Vector3(m.X, m.Y, m.Z); }
                    gb.Depth[idx] = z;
                    gb.Albedo[idx] = interior ? albedoRgb * options.InteriorShade : albedoRgb;
                    gb.Normal[idx] = n;
                    gb.Spec[idx] = spec;
                }
            }
        }
        }
        return gb;
    }

    /// <summary>Lights the G-buffer and averages the sub-samples down to 256×128. Empty pixels stay black.</summary>
    public static RgbaImage Shade(GBuffer g, IconLighting l)
    {
        int ss = g.Supersample, w = g.Width * ss;
        var img = new RgbaImage(g.Width, g.Height);
        for (int y = 0; y < g.Height; y++)
        for (int x = 0; x < g.Width; x++)
        {
            Vector3 sum = Vector3.Zero;
            bool any = false;
            for (int dy = 0; dy < ss; dy++)
            for (int dx = 0; dx < ss; dx++)
            {
                int i = (y * ss + dy) * w + x * ss + dx;
                if (g.Covered(i)) { sum += ShadePixel(g.Albedo[i], g.Normal[i], g.Spec[i], l); any = true; }
            }
            sum /= ss * ss;
            // An empty pixel stays black: a contrast below 1 would lift it to grey.
            var c = any ? ToneMap(sum, l) : Vector3.Zero;
            var d = img.Pixel(x, y);
            d[0] = ToByte(c.X); d[1] = ToByte(c.Y); d[2] = ToByte(c.Z); d[3] = 255;
        }
        return img;
    }

    /// <summary>Linear colour of one sample, before exposure/contrast mapping (the final curve is applied per pixel).</summary>
    public static Vector3 ShadePixel(Vector3 albedo, Vector3 n, Vector3 spec, IconLighting l)
    {
        var amb = Vector3.Lerp(l.Ground, l.Sky, n.Y * 0.5f + 0.5f);
        var diff = amb
            + l.KeyColor * MathF.Max(0, Vector3.Dot(n, l.Key))
            + l.FillColor * MathF.Max(0, Vector3.Dot(n, l.Fill))
            + l.RimColor * MathF.Max(0, Vector3.Dot(n, l.Rim));
        var col = albedo * diff;
        if (l.SpecStrength > 0)
        {
            float power = l.SpecPower * MathF.Exp(l.SpecPowerSlope * spec.Y);
            var view = Vector3.UnitZ;
            Vector3 sp = Vector3.Zero;
            void Add(Vector3 dir, Vector3 colour)
            {
                var hv = Vector3.Normalize(dir + view);
                sp += colour * MathF.Pow(MathF.Max(0, Vector3.Dot(n, hv)), power) * MathF.Max(0, Vector3.Dot(n, dir));
            }
            Add(l.SpecLight, Vector3.One);
            col += sp * Vector3.Lerp(Vector3.One, albedo, spec.Z) * (l.SpecStrength * MathF.Exp(l.SpecMapSlope * (spec.X - 0.5f)));
        }
        return col * l.Exposure;
    }

    /// <summary>Linear colour → final sRGB 0–1: sRGB curve, saturation, then contrast around the pivot.</summary>
    public static Vector3 ToneMap(Vector3 lin, IconLighting l)
    {
        var c = new Vector3(ToSrgbFloat(lin.X), ToSrgbFloat(lin.Y), ToSrgbFloat(lin.Z));
        float y = 0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z;
        c = new Vector3(y) + (c - new Vector3(y)) * l.Saturation;
        c = new Vector3(l.Pivot) + (c - new Vector3(l.Pivot)) * l.Contrast;
        if (l.Shoulder is > 0 and < 1)
        {
            float k = l.Shoulder, r = 1 - k;
            float Knee(float v) => v <= k ? v : k + r * (1 - MathF.Exp(-(v - k) / r));
            c = new Vector3(Knee(c.X), Knee(c.Y), Knee(c.Z));
        }
        return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
    }

    private static bool HasAlpha(RgbaImage t)
    {
        for (int i = 3; i < t.Pixels.Length; i += 4 * 97) if (t.Pixels[i] < 128) return true;
        return false;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    private static readonly float[] SrgbToLinear = Enumerable.Range(0, 256)
        .Select(v => v / 255f <= 0.04045f ? v / 255f / 12.92f : MathF.Pow((v / 255f + 0.055f) / 1.055f, 2.4f)).ToArray();

    public static float ToSrgbFloat(float l)
    {
        l = Math.Clamp(l, 0, 1);
        return l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f;
    }

    private static (Vector3 rgb, float a) SampleSrgb(RgbaImage t, Vector2 uv)
    {
        var c = Bilinear(t, uv, true);
        return (new Vector3(c.X, c.Y, c.Z), c.W);
    }

    private static Vector4 SampleLinear(RgbaImage t, Vector2 uv) => Bilinear(t, uv, false);

    /// <summary>Bilinear, wrapping. UV v points down the texture (Fox convention: v = 0 is the top row).</summary>
    private static Vector4 Bilinear(RgbaImage t, Vector2 uv, bool srgb)
    {
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        float fx = u * t.Width - 0.5f, fy = v * t.Height - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float ax = fx - x0, ay = fy - y0;
        Vector4 Px(int x, int y)
        {
            x = ((x % t.Width) + t.Width) % t.Width; y = ((y % t.Height) + t.Height) % t.Height;
            var p = t.Pixel(x, y);
            return srgb ? new Vector4(SrgbToLinear[p[0]], SrgbToLinear[p[1]], SrgbToLinear[p[2]], p[3] / 255f)
                        : new Vector4(p[0] / 255f, p[1] / 255f, p[2] / 255f, p[3] / 255f);
        }
        return Vector4.Lerp(Vector4.Lerp(Px(x0, y0), Px(x0 + 1, y0), ax), Vector4.Lerp(Px(x0, y0 + 1), Px(x0 + 1, y0 + 1), ax), ay);
    }
}
