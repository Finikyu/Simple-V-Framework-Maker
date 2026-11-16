using System.Numerics;
using Svfm.Core.Archives;
using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.Core.Rendering;

/// <summary>One drawable piece of the preview: a mesh with the textures it uses (already resolved, swaps and camo applied).</summary>
public sealed class PreviewMesh
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uv { get; init; }
    public required ushort[] Indices { get; init; }
    public RgbaImage? Albedo { get; init; }
    /// <summary>Layer-multiply shaders (the DD fatigues, the MGO suits): the camo layer, its mask, and its tiling.</summary>
    public RgbaImage? Layer { get; init; }
    public RgbaImage? LayerMask { get; init; }
    public Vector2 LayerRepeat { get; init; } = Vector2.One;
    public Vector2 LayerShift { get; init; }
    /// <summary>The base texture's alpha cuts the shape out (hair cards, straps).</summary>
    public bool AlphaTested { get; init; }
}

/// <summary>
/// An outfit as the preview shows it: the models, their textures and the look's swaps, taken from the item's own files
/// (what will be built) and the game's. Two ways to look at it: <see cref="RenderStill"/> (the software renderer, any
/// machine) and <see cref="Meshes"/> for a real-time view.
/// </summary>
public sealed class OutfitPreview
{
    private readonly List<FmdlModel> _models;
    private readonly TextureSource _source;
    private readonly List<Fv2File.TextureSwap> _swaps;
    private readonly (float bottom, float top) _span;
    private readonly float _width;
    private List<PreviewMesh>? _meshes;

    /// <summary>True when the body model has its own left forearm and hand (the first model is the body).</summary>
    public bool HasOwnLeftArm => _models.Count > 0 && BodyConverter.HasOwnLeftArm(_models[0]);

    private OutfitPreview(List<FmdlModel> models, TextureSource source, List<Fv2File.TextureSwap> swaps)
    {
        _models = models; _source = source; _swaps = swaps;
        float lo = float.MaxValue, hi = float.MinValue, wide = 0;
        foreach (var m in models) foreach (var mesh in m.Meshes) foreach (var v in mesh.Positions) { lo = Math.Min(lo, v.Y); hi = Math.Max(hi, v.Y); wide = Math.Max(wide, Math.Max(Math.Abs(v.X), Math.Abs(v.Z))); }
        _span = lo > hi ? (0f, 2f) : (lo, hi);
        _width = Math.Max(0.5f, wide * 2);
    }

    /// <summary>The render input for one look of an outfit, from the item's files and the game.</summary>
    /// <param name="game">The game's files.</param>
    /// <param name="item">The outfit.</param>
    /// <param name="who">The character (Snake gets his default face, the others draw the body alone).</param>
    /// <param name="look">The look to draw.</param>
    /// <returns>The input for <see cref="SuitRender.Scene"/>.</returns>
    public static SuitRenderInput InputFor(GameData game, ProjectItem item, PlayerBranch who, Look look)
    {
        string body = look.FpkPath.EndsWith(".fpk", StringComparison.Ordinal) ? look.FpkPath[..^4] : look.FpkPath;
        FoxPackage? bodyFpk = item.Files.TryGetValue(body + ".fpk", out var bf) ? FoxPackage.Read(bf) : null;
        TexturePack? bodyPack = item.Files.TryGetValue(body + ".pftxs", out var bp) ? TexturePack.Read(bp) : null;
        var fv2s = new List<byte[]>();
        var packs = new List<TexturePack>();
        if (look.CamoFpk != null)
        {
            string camo = look.CamoFpk.EndsWith(".fpk", StringComparison.Ordinal) ? look.CamoFpk[..^4] : look.CamoFpk;
            var cf = item.Files.TryGetValue(camo + ".fpk", out var cb) ? cb : game.TryRead(camo + ".fpk");
            if (cf != null) fv2s.AddRange(SuitRender.Fv2sIn(FoxPackage.Read(cf)));
            var cp = item.Files.TryGetValue(camo + ".pftxs", out var cpb) ? cpb : game.TryRead(camo + ".pftxs");
            if (cp != null) packs.Add(TexturePack.Read(cp));
        }
        var input = new SuitRenderInput
        {
            BodyPackage = body, BodyFpk = bodyFpk, BodyPack = bodyPack, Fv2s = fv2s, ModPacks = packs,
            ModFiles = item.Files.Where(f => f.Key.Contains(".ftex", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal),
            // Snake wears his own face; a DD soldier wears the vanilla soldier face, so the still has a head to draw
            // instead of a headless body; the Avatar wears its own default face (#78). A look that hides the head
            // (enableHead = false, its own value or that of the Main look) gets neither.
            HeadPackage = !(ProjectEdits.OutfitOf(item)?.Spec.Branches.GetValueOrDefault(who)?.ShowsHead(look) ?? look.EnableHead != false) ? null
                : who == PlayerBranch.Snake ? SuitRender.SnakeFace
                : SuitRender.SoldierFace(who, game),
            HeadGroups = PreviewHeads.VanillaGroups(null, who),
        };
        // Snake's bionic arm, unless the look turns it off (enableArm = false). The owner asked for it: "Snake 3d
        // preview should have the arm visible, and switch between them unless otherwise overridden by a mod."
        return WithArm(input, item, who is PlayerBranch.Snake or PlayerBranch.Avatar && look.EnableArm != false ? SuitRender.SnakeArm : null);
    }

    /// <summary>The input with this arm package, read from the outfit's files when the mod ships it (a mod arm wins over the game's).</summary>
    private static SuitRenderInput WithArm(SuitRenderInput input, ProjectItem item, string? arm) => input with
    {
        ArmPackage = arm,
        ArmFpk = arm != null && item.Files.TryGetValue(arm + ".fpk", out var af) ? FoxPackage.Read(af) : null,
        ArmPack = arm != null && item.Files.TryGetValue(arm + ".pftxs", out var ap) ? TexturePack.Read(ap) : null,
    };

    /// <summary>Reads the models and textures for an outfit's look.</summary>
    /// <param name="game">The game's files.</param>
    /// <param name="dict">Path names for the texture codes.</param>
    /// <param name="item">The outfit.</param>
    /// <param name="who">The character.</param>
    /// <param name="look">The look.</param>
    /// <returns>The preview.</returns>
    public static OutfitPreview For(GameData game, PathDictionary? dict, ProjectItem item, PlayerBranch who, Look look)
    {
        var (models, src, swaps) = SuitRender.Scene(game, dict, InputFor(game, item, who, look));
        return new OutfitPreview(models, src, swaps);
    }

    /// <summary>
    /// The same, with a head option's own head drawn instead of the default face. The owner asked for this: "We need to be
    /// able to display head options in the 3D preview".
    /// </summary>
    /// <param name="game">The game's files.</param>
    /// <param name="dict">Path names for the texture codes.</param>
    /// <param name="item">The outfit.</param>
    /// <param name="who">The character.</param>
    /// <param name="look">The look.</param>
    /// <param name="head">The head option to draw, or null for the outfit's own head.</param>
    /// <param name="armPackage">The arm package to draw, or null for the one the look implies (empty for no arm).</param>
    /// <param name="vanillaHeadPackage">A game head package to draw (a vanilla head), read from the game files; wins over the look's face.</param>
    /// <param name="vanillaHeadGroups">The mesh groups of that game head to draw (<see cref="PreviewHeads.VanillaGroups"/>), or null for the head rule.</param>
    /// <returns>The preview.</returns>
    public static OutfitPreview For(GameData game, PathDictionary? dict, ProjectItem item, PlayerBranch who, Look look, HeadOptionItem? head,
        string? armPackage = null, string? vanillaHeadPackage = null, int[]? vanillaHeadGroups = null)
    {
        var input = InputFor(game, item, who, look);
        // The arm the preview was asked for, over the one the look implies (Snake's bionic arm).
        if (armPackage != null) input = WithArm(input, item, armPackage.Length == 0 ? null : armPackage);
        // A head option's package is per branch (HeadBranch.Fpk), so only the branch being previewed can be drawn.
        if (head != null && head.Spec.Branches.TryGetValue(who, out var hb) && hb.Fpk is { Length: > 0 } fpk)
        {
            string hp = fpk.EndsWith(".fpk", StringComparison.Ordinal) ? fpk[..^4] : fpk;
            input = input with
            {
                HeadPackage = hp,
                HeadFpk = head.Files.TryGetValue(hp + ".fpk", out var hf) ? FoxPackage.Read(hf) : null,
                HeadPack = head.Files.TryGetValue(hp + ".pftxs", out var hpk) ? TexturePack.Read(hpk) : null,
                HeadGroups = null,
            };
        }
        if (vanillaHeadPackage != null) input = input with { HeadPackage = vanillaHeadPackage, HeadFpk = null, HeadPack = null, HeadGroups = vanillaHeadGroups };
        var (models, src, swaps) = SuitRender.Scene(game, dict, input);
        return new OutfitPreview(models, src, swaps);
    }

    /// <summary>
    /// A head option on its own: the head model the option carries, drawn with every mesh group shown.
    /// </summary>
    /// <remarks>
    /// The owner: "Head options do not appear on the 3D model, they should." Two things were wrong. On an outfit, the HEAD
    /// drop-down was empty until the character was switched (see <c>PreviewVm.LoadAsync</c>), so a head option could not be
    /// put on the body at all. And a head option selected in the items bar had no picture of its own, because the preview
    /// only knew outfits, weapons and buddies. This is that picture.
    /// </remarks>
    /// <param name="game">The game's files.</param>
    /// <param name="dict">Path names for the texture codes.</param>
    /// <param name="item">The head option.</param>
    /// <param name="who">The branch to draw; null takes the first branch the option has.</param>
    /// <returns>The preview, or null when the option has no model of its own (a soldier face the game already has).</returns>
    public static OutfitPreview? ForHeadOption(GameData game, PathDictionary? dict, HeadOptionItem item, PlayerBranch? who = null)
    {
        var branch = who is { } w && item.Spec.Branches.TryGetValue(w, out var b) ? b
            : item.Spec.Branches.OrderBy(k => k.Key).Select(k => k.Value).FirstOrDefault();
        if (branch?.Fpk is not { Length: > 0 } fpk) return null;
        string hp = fpk.EndsWith(".fpk", StringComparison.Ordinal) ? fpk[..^4] : fpk;
        if (!item.Files.TryGetValue(hp + ".fpk", out var fpkData)) return null;
        var model = SuitRender.HeadModel(FoxPackage.Read(fpkData));
        if (model == null) return null;

        var src = new TextureSource(game, dict);
        if (item.Files.TryGetValue(hp + ".pftxs", out var packData)) src.AddModPack(TexturePack.Read(packData));
        foreach (var (path, data) in item.Files.Where(f => f.Key.Contains(".ftex", StringComparison.Ordinal)))
            src.AddModFile(path, data);
        // Hidden groups shown, except copies of a shown group: a head package hides most of its groups because in game a
        // look's .fv2 shows them, and some (the PW helmet) hide a second visor that would draw twice.
        return new OutfitPreview([model.WithHeadGroupsShown()], src, []);
    }

    /// <summary>
    /// A buddy costume (Quiet, D-Dog, D-Horse) as the preview shows it: the costume's own model with its own textures.
    /// </summary>
    /// <remarks>
    /// This exists because buddy costumes used to be drawn by <c>OutlineIconRenderer</c>, the untextured outline pass that
    /// weapons use. The owner saw the result and reported it three times: "Quiet 3D Stills were a white wireframe, rather
    /// than properly rendered in. They were similar to how weapon icons look", then the same for D-Dog and D-Horse. The
    /// cause was <c>PreviewVm.IsWeapon</c>, which is true for any item with a package, including a buddy costume. A buddy is
    /// not a weapon: it has a full textured model, so it goes through the same software renderer as an outfit.
    /// </remarks>
    /// <param name="game">The game's files.</param>
    /// <param name="dict">Path names for the texture codes.</param>
    /// <param name="item">The buddy costume.</param>
    /// <returns>The preview, or null when the item has no package of its own.</returns>
    public static OutfitPreview? ForBuddy(GameData game, PathDictionary? dict, BuddyOutfitItem item)
    {
        string pkg = item.Spec.FpkPath.EndsWith(".fpk", StringComparison.Ordinal) ? item.Spec.FpkPath[..^4] : item.Spec.FpkPath;
        if (!item.Files.TryGetValue(pkg + ".fpk", out var fpkData)) return null;
        var fpk = FoxPackage.Read(fpkData);
        var fpkd = item.Files.TryGetValue(pkg + ".fpkd", out var fpkdData) ? FoxPackage.Read(fpkdData) : null;
        var model = BuddyConverter.ShownModel(fpk, fpkd);
        if (model == null) return null;

        var src = new TextureSource(game, dict);
        if (item.Files.TryGetValue(pkg + ".pftxs", out var packData)) src.AddModPack(TexturePack.Read(packData));
        // The costume's own loose textures, plus the game's for everything it doesn't ship (the vanilla body it is built on).
        foreach (var (path, data) in item.Files.Where(f => f.Key.Contains(".ftex", StringComparison.Ordinal)))
            src.AddModFile(path, data);
        return new OutfitPreview([model], src, []);
    }

    /// <summary>
    /// A weapon's own model and textures, drawn by the software renderer instead of the outline pass. The owner asked for
    /// this: "The gun also suffers from the same 3D preview issue as the Buddy, though this one facing to the side by
    /// default is preferable." The outline pass draws no textures, so a gun came out as a white wireframe; the side-on
    /// default is kept by <see cref="RenderStill"/>'s yaw, not by the outline renderer's fixed side view.
    /// </summary>
    /// <param name="game">The game's files.</param>
    /// <param name="dict">Path names for the texture codes.</param>
    /// <param name="item">The weapon or thrown item.</param>
    /// <returns>The preview, or null when the item has no package of its own.</returns>
    public static OutfitPreview? ForWeapon(GameData game, PathDictionary? dict, ProjectItem item)
    {
        string? pkg = item switch { WeaponItem w => w.Spec.FpkPath, SupportWeaponItem t => t.Spec.FpkPath, _ => null };
        if (pkg == null) return null;
        if (pkg.EndsWith(".fpk", StringComparison.Ordinal)) pkg = pkg[..^4];
        // A weapon port: the files as a build makes them (a swapped sight merged in, the camo painted), so the preview
        // shows the gun of the next build. The item is not changed.
        IReadOnlyDictionary<string, byte[]> files = item is WeaponItem { Spec.Tuning: not null } wi ? WeaponConverter.Current(game, wi).Files : item.Files;
        if (!files.TryGetValue(pkg + ".fpk", out var fpkData))
        {
            // A thrown item has no package of its own: V Framework 1.7 can't give a custom support weapon a model, so it
            // is thrown as a vanilla item and looks like that item. The owner asked to see it anyway: "3D models that use
            // assets in the game should be able to be imported to view in the 3D previewer if necessary, such as the
            // thrown weapon on the halo assault rifle mod." So the vanilla item's model is read out of the game.
            return item is SupportWeaponItem t ? ForVanillaThrown(game, dict, t.Spec.ThrowAs) : null;
        }
        var fpk = FoxPackage.Read(fpkData);
        // A custom gun shows only its receiver model in game; the part models in the package would be drawn a second time.
        string? receiver = item is WeaponItem rw ? WeaponAttachments.ReceiverModel(rw.Spec, files) : null;
        var fmdls = fpk.Files.Where(f => f.Extension == "fmdl" && (receiver == null || f.Path == receiver)).ToList();
        var models = fmdls.Select(f => FmdlModel.Read(f.Data)).ToList();
        if (models.Count == 0) return null;

        var src = new TextureSource(game, dict);
        if (files.TryGetValue(pkg + ".pftxs", out var packData)) src.AddModPack(TexturePack.Read(packData));
        // The weapon's own loose textures, plus the game's for everything it doesn't ship (the vanilla gun it is built on).
        foreach (var (path, data) in files.Where(f => f.Key.Contains(".ftex", StringComparison.Ordinal)))
            src.AddModFile(path, data);
        return new OutfitPreview(models, src, []) { ModelPaths = fmdls.Select(f => f.Path).ToList(), Lighting = IconLighting.Weapon };
    }

    /// <summary>The package paths of the models, in order (a weapon only; empty otherwise). The attachment toggles hide models by them.</summary>
    public IReadOnlyList<string> ModelPaths { get; private init; } = [];

    /// <summary>The studio lighting of <see cref="RenderStill"/>; null = the suit-icon rig.</summary>
    public IconLighting? Lighting { get; set; }

    /// <summary>
    /// The model a vanilla thrown item is drawn with, read out of the game. Every EQP_BLOCK_COMMON support weapon's model
    /// lives in the resident common package (<c>col_common_tpp.fpk</c>), and the <c>.parts</c> in its <c>.fpkd</c> names
    /// which model that is - the same route <see cref="BuddyConverter.ShownModel"/> takes for a buddy package.
    /// </summary>
    /// <param name="game">The game's files.</param>
    /// <param name="dict">Path names for the texture codes.</param>
    /// <param name="throwAs">The vanilla equip constant, e.g. "EQP_SWP_SmokeGrenade".</param>
    /// <returns>The preview, or null when the game has no such item or no model for it.</returns>
    public static OutfitPreview? ForVanillaThrown(GameData game, PathDictionary? dict, string throwAs)
    {
        if (!GameKnowledge.SupportWeapons.TryGetValue(throwAs, out var row) || row.Parts.Length == 0) return null;
        const string common = "/Assets/tpp/pack/collectible/common/col_common_tpp";
        if (game.TryRead(common + ".fpk") is not { } fpkBytes) return null;
        var fpk = FoxPackage.Read(fpkBytes);
        // The .parts names the model. Without it, fall back to the model whose name matches the .parts' own name.
        string? modelPath = null;
        if (game.TryRead(common + ".fpkd") is { } fpkdBytes)
            foreach (var pf in FoxPackage.Read(fpkdBytes).Files.Where(f => f.Extension == "parts"))
            {
                if (!string.Equals(pf.Path, row.Parts, StringComparison.OrdinalIgnoreCase)) continue;
                modelPath = FoxDataSet.Read(pf.Data).ModelFiles.FirstOrDefault(v => v != null && fpk.Find(v) != null);
                break;
            }
        modelPath ??= fpk.Files.FirstOrDefault(f => f.Extension == "fmdl" &&
            Path.GetFileName(f.Path).StartsWith(Path.GetFileNameWithoutExtension(row.Parts).Split('_')[0] + "_", StringComparison.OrdinalIgnoreCase))?.Path;
        if (modelPath == null || fpk.Find(modelPath) is not { } mf) return null;
        var models = new List<FmdlModel> { FmdlModel.Read(mf.Data) };
        // The common package's own textures, so the item is not drawn flat grey.
        var src = new TextureSource(game, dict);
        if (game.TryRead(common + ".pftxs") is { } pftxs) src.AddGamePack(TexturePack.Read(pftxs));
        return new OutfitPreview(models, src, []);
    }

    /// <summary>The first branch and look of an outfit (what the preview shows by default).</summary>
    /// <param name="item">The outfit.</param>
    /// <returns>The character and look.</returns>
    public static (PlayerBranch who, Look look) DefaultLook(OutfitItem item)
    {
        var (who, b) = item.Spec.Branches.OrderBy(k => k.Key).First();
        return (who, b.Variants.FirstOrDefault(v => v.Default) ?? b.Base);
    }

    /// <summary>The mesh group hashes the model has, so the PARTS row can offer only the parts that exist.</summary>
    public IReadOnlySet<ulong> MeshGroups => _models
        .SelectMany(m => m.MeshGroups.Select(g => g.hash & 0xFFFFFFFFFFFF))
        .ToHashSet();

    /// <summary>Model height in metres.</summary>
    public float Height => _span.top - _span.bottom;

    /// <summary>The whole outfit from a side, drawn by the software renderer.</summary>
    /// <param name="yawDegrees">0 = front; turns the outfit about its upright axis.</param>
    /// <param name="width">Picture width.</param>
    /// <param name="height">Picture height.</param>
    /// <param name="supersample">Anti-aliasing per axis.</param>
    /// <param name="pitchDegrees">
    /// 0 = level with the model; positive looks down on it from above, negative looks up at it from below. The owner asked
    /// for this: "3D previewer does not allow for zooming nor the ability to rotate from above/underneath".
    /// </param>
    /// <param name="zoom">1 = the whole model in frame; &gt;1 moves the camera in, &lt;1 pulls it back.</param>
    /// <returns>The picture.</returns>
    /// <param name="hiddenGroups">Mesh group name hashes to leave out (a weapon's suppressor, light or magazine).</param>
    /// <param name="panX">Slides the model sideways, in picture fractions (the owner asked to drag the model to look at
    /// different parts of it).</param>
    /// <param name="panY">Slides the model up (+) or down (−), in picture fractions.</param>
    /// <param name="hiddenModels">Attachment model keys to leave out (a weapon's merged barrel, scope, laser sight).</param>
    public RgbaImage RenderStill(float yawDegrees, int width, int height, int supersample = 2, float pitchDegrees = 0f, float zoom = 1f,
        IReadOnlySet<ulong>? hiddenGroups = null, float panX = 0f, float panY = 0f, IReadOnlySet<string>? hiddenModels = null)
    {
        var kept = hiddenModels == null || hiddenModels.Count == 0 || ModelPaths.Count != _models.Count
            ? _models
            : _models.Where((m, i) => !WeaponAttachments.IsHidden(ModelPaths[i], hiddenModels)).ToList();
        var models = hiddenGroups == null || hiddenGroups.Count == 0
            ? kept
            : kept.Select(m =>
            {
                var shown = OutlineIconRenderer.VisibleMeshes(m, hiddenGroups).ToHashSet();
                return m.WithMeshes(shown.Contains);
            }).ToList();
        var framing = ViewFraming(yawDegrees, width, height, pitchDegrees, zoom, panX, panY);
        return IconRenderer.Render(models, _source.Get, framing: framing, lighting: Lighting, supersample: supersample, fv2Swaps: _swaps, width: width, height: height);
    }

    /// <summary>
    /// The zoom and offset (fractions of 256x128, +Y up) that make an outline icon of the same view show the model at the
    /// size and place of <see cref="RenderStill"/> in a picture of the same 2:1 shape: the icon is a snapshot of the view.
    /// The still has perspective and the icon has none, so the match is exact at the depth of the model's centre only.
    /// </summary>
    /// <param name="icon">The outline icon options, with the view already set.</param>
    /// <param name="yawDegrees">The still's turn.</param>
    /// <param name="width">The still's width.</param>
    /// <param name="height">The still's height.</param>
    /// <param name="pitchDegrees">The still's tilt.</param>
    /// <param name="zoom">The still's zoom.</param>
    /// <param name="panX">The still's pan sideways.</param>
    /// <param name="panY">The still's pan up.</param>
    /// <param name="hiddenGroups">The hidden mesh groups.</param>
    /// <param name="hiddenModels">The hidden attachment models.</param>
    /// <returns>The zoom and offset, or null when the shown model has no points.</returns>
    public (float Zoom, float OffsetX, float OffsetY)? OutlineIconOf(OutlineIconOptions icon, float yawDegrees, int width, int height, float pitchDegrees,
        float zoom, float panX, float panY, IReadOnlySet<ulong>? hiddenGroups, IReadOnlySet<string>? hiddenModels)
    {
        var kept = hiddenModels == null || hiddenModels.Count == 0 || ModelPaths.Count != _models.Count
            ? _models
            : _models.Where((m, i) => !WeaponAttachments.IsHidden(ModelPaths[i], hiddenModels)).ToList();
        var meshes = kept.SelectMany(m => OutlineIconRenderer.VisibleMeshes(m, hiddenGroups)).ToList();
        if (OutlineIconRenderer.FitOf(meshes, icon) is not { } fit) return null;
        var f = ViewFraming(yawDegrees, width, height, pitchDegrees, zoom, panX, panY);
        var c = f.Centre + new Vector3(f.OffsetX * f.WorldHeight * 2, f.OffsetY * f.WorldHeight, 0);
        // Icon pixels per metre for the same share of the picture width as the still.
        float ppm = OutlineIconRenderer.Width * (float)height / (f.WorldHeight * width);
        return (ppm / fit.PixelsPerMetre, (fit.CentreX - c.X) * ppm / OutlineIconRenderer.Width, (fit.CentreY - c.Y) * ppm / OutlineIconRenderer.Height);
    }

    /// <summary>
    /// The icon a head option gets in Generate mode (issues #17 and #23): the head gear in the vanilla head-icon style
    /// (<see cref="HeadIcon.Gear"/>, as the head converters draw it), turned and tilted as the preview shows it instead of
    /// the fixed three-quarter view. The zoom scales the standard head-icon size, and the pan moves the gear by the same
    /// share of its size as in the preview. Tester note (Nov 4): "Head icon Generate acts as a screenshot."
    /// </summary>
    /// <param name="yawDegrees">The turn of the view.</param>
    /// <param name="width">The width of the preview picture.</param>
    /// <param name="height">The height of the preview picture.</param>
    /// <param name="pitchDegrees">The tilt of the view.</param>
    /// <param name="zoom">The zoom of the view.</param>
    /// <param name="panX">The sideways pan of the view.</param>
    /// <param name="panY">The up or down pan of the view.</param>
    /// <param name="supersample">Anti-aliasing per axis; 0 keeps the head-icon value (lower is faster, for a drag).</param>
    /// <returns>The 256x128 icon picture.</returns>
    public RgbaImage RenderViewIcon(float yawDegrees, int width, int height, float pitchDegrees, float zoom, float panX, float panY, int supersample = 0)
    {
        var f = ViewFraming(yawDegrees, width, height, pitchDegrees, zoom, panX, panY);
        var turn = f.View!.Value;
        // The preview moves its centre by the pan (IconRenderer: OffsetX x 2 and OffsetY times the world height), so the gear moves the other way.
        var shift = new Vector3(-f.OffsetX * 2 * f.WorldHeight, -f.OffsetY * f.WorldHeight, 0);
        var gear = HeadIcon.GearMeshes(_models[0]);
        List<FmdlMesh> Turned(Vector3 by) => gear.Select(x => new FmdlMesh
        {
            Index = x.Index, MaterialIndex = x.MaterialIndex, MeshGroup = x.MeshGroup, Indices = x.Indices,
            Normals = x.Normals?.Select(n => Vector3.TransformNormal(n, turn)).ToArray(),
            Positions = x.Positions.Select(p => Vector3.Transform(p, turn) + by).ToArray(),
        }).ToList();
        var framed = Turned(Vector3.Zero);
        // The meshes are already turned, so the outline renderer's own turn is none.
        var o = HeadIcon.Gear with { Yaw = 0, Pitch = 0, Zoom = Math.Max(0.05f, zoom) };
        if (supersample > 0) o = o with { Supersample = supersample };
        return OutlineIconRenderer.Render(shift == Vector3.Zero ? framed : Turned(shift), o, frame: framed);
    }

    private IconFraming ViewFraming(float yawDegrees, int width, int height, float pitchDegrees, float zoom, float panX, float panY)
    {
        // Pitch is applied after yaw so the model tips towards or away from the viewer rather than spinning about a tilted
        // axis: the camera orbits the model on a sphere. Vector3.Transform applies the left matrix first, so yaw is on the
        // left, as in OutlineIconRenderer: the "Default view" of a head option is then the vanilla head-icon view (issue #23).
        var view = Matrix4x4.CreateRotationY(yawDegrees * MathF.PI / 180f) * Matrix4x4.CreateRotationX(pitchDegrees * MathF.PI / 180f);
        return new IconFraming
        {
            View = view,
            Centre = new Vector3(0, (_span.bottom + _span.top) / 2f, 0),
            WorldHeight = Math.Max(Height * 1.1f, _width * 1.1f * height / width) / Math.Max(0.05f, zoom),
            CameraDistance = 6f,
            // The pan is applied in camera space by the renderer, so it slides the model across the picture whatever the
            // model's own axes are. OffsetX is in half-picture widths and OffsetY in picture heights (see IconRenderer).
            OffsetX = panX / 2f,
            OffsetY = panY,
            // A weapon's parts are in the gun's space, and their root bones differ (a magazine's sits at its socket).
            ModelsInPlace = ModelPaths.Count > 0,
        };
    }

    /// <summary>The model drawn with an icon framing (Generate mode, issue #9).</summary>
    /// <param name="framing">The icon framing.</param>
    /// <param name="width">Picture width.</param>
    /// <param name="height">Picture height.</param>
    /// <param name="supersample">Anti-aliasing per axis.</param>
    /// <returns>The picture.</returns>
    public RgbaImage RenderIcon(IconFraming framing, int width, int height, int supersample) =>
        IconRenderer.Render(_models, _source.Get, framing: framing, supersample: supersample, fv2Swaps: _swaps, width: width, height: height);

    /// <summary>The pixels the model covers with an icon framing (for the UI checks).</summary>
    /// <param name="framing">The icon framing.</param>
    /// <param name="width">Picture width.</param>
    /// <param name="height">Picture height.</param>
    /// <returns>The G-buffer, one sample per pixel.</returns>
    public GBuffer Coverage(IconFraming framing, int width, int height) =>
        IconRenderer.RenderGBuffer(_models, _source.Get, framing: framing, supersample: 1, fv2Swaps: _swaps, width: width, height: height);

    /// <summary>
    /// Where the icon window of a chest framing sits in the front view of <see cref="RenderStill"/>, in picture fractions.
    /// null when the model has no CHEST and NECK bones. The front view only: the icon is always drawn from the front.
    /// </summary>
    /// <param name="icon">The icon framing.</param>
    /// <param name="width">Picture width.</param>
    /// <param name="height">Picture height.</param>
    /// <param name="zoom">The view zoom.</param>
    /// <param name="panX">The view pan sideways.</param>
    /// <param name="panY">The view pan up.</param>
    /// <returns>Left, top, width and height (0-1), or null.</returns>
    public (double X, double Y, double W, double H)? IconRect(IconFraming icon, int width, int height, float zoom, float panX, float panY)
    {
        // A side-on buddy framing (D-Dog, D-Horse) is not a window of the front view.
        if (icon.View != null || IconRenderer.ChestWindow(_models[0], icon) is not { } win) return null;
        var (c, ih) = win;
        var vf = ViewFraming(0, width, height, 0, zoom, panX, panY);
        float vh = vf.WorldHeight;
        var vc = vf.Centre + new Vector3(vf.OffsetX * vh * 2, vf.OffsetY * vh, 0);
        double perMetreX = (double)height / vh / width;
        double wf = 2 * ih * perMetreX, hf = ih / vh;
        double cx = 0.5 + (c.X - vc.X) * perMetreX, cy = 0.5 - (c.Y - vc.Y) / vh;
        return (cx - wf / 2, cy - hf / 2, wf, hf);
    }

    /// <summary>The meshes with their textures resolved, for a real-time view. The camera looks along -Z at the model's front.</summary>
    public IReadOnlyList<PreviewMesh> Meshes => _meshes ??= BuildMeshes();

    /// <summary>
    /// How many of the textures the picture asked for are in neither the mod nor the game files. Counted as the picture
    /// is drawn, so it is complete only after a render or <see cref="Meshes"/>.
    /// </summary>
    /// <remarks>
    /// A texture that is not found draws as flat 0.5 grey, and the studio lights make that grey look white. The TMWSTW
    /// head preview showed white hair, a white beard and a white eyepatch for this reason: the test game folder did not
    /// have the vanilla Snake hair textures. The renderer was correct. Any mod can do the same on a game folder that is not
    /// complete, or with a model that uses another mod's textures, so the preview says so instead of drawing a wrong
    /// picture with no explanation.
    /// </remarks>
    public int MissingTextureCount => _source.Missing.Count;

    /// <summary>A line for the preview that tells the user why parts are drawn plain; empty when every texture was found.</summary>
    public string MissingTexturesNote => MissingTextureCount switch
    {
        0 => "",
        1 => "1 texture is not in the mod or the game files. Those parts are drawn plain.",
        int n => $"{n} textures are not in the mod or the game files. Those parts are drawn plain.",
    };

    /// <summary>Centre of the model, for the camera to look at.</summary>
    public float Width => _width;

    public Vector3 Centre => new(0, (_span.bottom + _span.top) / 2f, 0);

    private List<PreviewMesh> BuildMeshes()
    {
        var swapByMat = new Dictionary<(uint, uint), ulong>();
        foreach (var sw in _swaps) swapByMat[(sw.Material, sw.Slot)] = sw.Texture;
        var cache = new Dictionary<ulong, RgbaImage?>();
        RgbaImage? Tex(ulong code) { if (code == 0) return null; if (!cache.TryGetValue(code, out var t)) cache[code] = t = _source.Get(code); return t; }
        var list = new List<PreviewMesh>();
        var body = _models[0];
        foreach (var model in _models)
        {
            var offset = Vector3.Zero;
            if (model != body && ModelPaths.Count == 0)
                foreach (var bone in model.Bones)
                    if (body.Bones.FirstOrDefault(b => b.NameHash == bone.NameHash && b.NameHash != 0) is { } match) { offset = match.World - bone.World; break; }
            foreach (var mesh in model.Meshes)
            {
                if (mesh.MeshGroup >= 0 && mesh.MeshGroup < model.MeshGroups.Count && model.MeshGroups[mesh.MeshGroup].hidden) continue;
                if (mesh.Uv == null) continue;
                var mat = mesh.MaterialIndex < model.Materials.Count ? model.Materials[mesh.MaterialIndex] : null;
                ulong Slot(string role)
                {
                    if (mat == null) return 0;
                    if (swapByMat.TryGetValue(((uint)mat.NameHash, FoxHash.StrCode32(role)), out var s)) return s;
                    return mat.Slot(role)?.PathCode ?? 0;
                }
                ulong baseCode = Slot("Base_Tex_SRGB");
                if (baseCode == 0) baseCode = Slot("Base_Tex_LIN");
                // Same fallback as the icon renderer: a base slot named something else, or any slot that resolves.
                if (baseCode == 0 && mat != null)
                {
                    baseCode = mat.Textures.FirstOrDefault(t => t.Role != null && t.Role.StartsWith("Base", StringComparison.OrdinalIgnoreCase))?.PathCode ?? 0;
                    if (baseCode == 0) baseCode = mat.Textures.Select(t => t.PathCode).FirstOrDefault(c => c != 0 && Tex(c) != null);
                }
                var albedo = Tex(baseCode);
                bool layerMul = mat?.IsLayerMultiply == true;
                var layer = layerMul ? Tex(Slot("Layer_Tex_SRGB")) : null;
                var mask = layer != null ? Tex(Slot("LayerMask_Tex_LIN")) : null;
                float P(string n, float f) => mat?.Param(n, f) ?? f;
                bool alphaTested = false;
                if (albedo != null && baseCode != 0 && FoxHash.ExtensionOf(baseCode) == "ftex")
                    for (int i = 3; i < albedo.Pixels.Length; i += 4 * 97) if (albedo.Pixels[i] < 128) { alphaTested = true; break; }
                var normals = mesh.Normals ?? Enumerable.Repeat(Vector3.UnitZ, mesh.Positions.Length).ToArray();
                list.Add(new PreviewMesh
                {
                    Positions = offset == Vector3.Zero ? mesh.Positions : mesh.Positions.Select(v => v + offset).ToArray(),
                    Normals = normals, Uv = mesh.Uv, Indices = mesh.Indices,
                    Albedo = albedo, Layer = layer, LayerMask = mask,
                    LayerRepeat = new Vector2(P("URepeat_UV", 1), P("VRepeat_UV", 1)),
                    LayerShift = new Vector2(P("UShift_UV", 0), P("VShift_UV", 0)),
                    AlphaTested = alphaTested,
                });
            }
        }
        return list;
    }
}
