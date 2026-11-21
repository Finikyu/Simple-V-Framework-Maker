using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Rendering;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>What to draw for a rendered suit icon.</summary>
public sealed record SuitRenderInput
{
    /// <summary>Body package (path without extension); its main model is drawn.</summary>
    public required string BodyPackage { get; init; }
    /// <summary>The body package's files when they come from the mod (null = read the game's).</summary>
    public FoxPackage? BodyFpk { get; init; }
    public TexturePack? BodyPack { get; init; }
    /// <summary>The look's .fv2 files (camo, arms …): their texture swaps are applied.</summary>
    public List<byte[]> Fv2s { get; init; } = [];
    /// <summary>The mod's texture packs and loose texture files (by game path); they win over the game's.</summary>
    public List<TexturePack> ModPacks { get; init; } = [];
    public Dictionary<string, byte[]> ModFiles { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Head package to draw with the body (Snake's default face), or null for a body with its own head.</summary>
    public string? HeadPackage { get; init; } = SuitRender.SnakeFace;
    public FoxPackage? HeadFpk { get; init; }
    public TexturePack? HeadPack { get; init; }
    /// <summary>The head mesh groups to draw (others hidden), or null to show the hidden groups by the head rule.</summary>
    public int[]? HeadGroups { get; init; }
    /// <summary>A head option's .fv2 in the head package: the models it attaches are drawn (issue #129); null draws the head model only.</summary>
    public string? HeadFv2 { get; init; }
    /// <summary>
    /// Arm package to draw with the body (Snake's bionic arm), or null for none. The owner asked for this: "Snake 3d
    /// preview should have the arm visible, and switch between them unless otherwise overridden by a mod."
    /// </summary>
    public string? ArmPackage { get; init; }
    /// <summary>The arm package's files when they come from the mod (null = read the game's).</summary>
    public FoxPackage? ArmFpk { get; init; }
    public TexturePack? ArmPack { get; init; }
    public IconFraming? Framing { get; init; }
    public IconLighting? Lighting { get; init; }
}

/// <summary>
/// Renders a suit icon from the outfit's own model and textures (body + head + the look's .fv2 swaps),
/// framed and lit like the vanilla render icons, inside the drawn suit-icon frame.
/// </summary>
public static class SuitRender
{
    public const string SnakeFace = "/Assets/tpp/pack/player/fova/plfova_sna0_face0_v00";

    /// <summary>
    /// Snake's bionic arm (arm0). The arm is its own package, so the preview draws it as a second model. The other arm
    /// packages are the rocket arm (arm1/arm2), the hand of Jehuty (arm3), the stun arm (arm4) and the blast arm (arm6/7).
    /// </summary>
    public const string SnakeArm = "/Assets/tpp/pack/player/fova/plfova_sna0_arm0_v00";

    /// <summary>The arm packages a preview can draw, by the name the game gives them.</summary>
    public static readonly IReadOnlyList<(string Name, string Package)> Arms =
    [
        ("Bionic arm", SnakeArm),
        ("Rocket arm", "/Assets/tpp/pack/player/fova/plfova_sna0_arm1_v00"),
        ("Hand of Jehuty", "/Assets/tpp/pack/player/fova/plfova_sna0_arm3_v00"),
        ("Stun arm", "/Assets/tpp/pack/player/fova/plfova_sna0_arm4_v00"),
        ("Blast arm", "/Assets/tpp/pack/player/fova/plfova_sna0_arm6_v00"),
    ];

    /// <summary>
    /// The soldier face a DD body wears when the mod brings no head of its own: the first bare soldier face, male and
    /// female (cm_m0/cm_f0_h0_v000), a face like Snake's. The owner: "I mean the head without a head option, showing a
    /// face like Snake." The balaclava was the default before, and read as a head option already chosen. The owner asked for this: "We need to have a default DD-Male and DD-Female face model for the still".
    /// DD soldiers have no fova face package like Snake's; their faces are the soldier head packages under
    /// <c>pack/fova/common_source/chara/cm_head/face/</c>, which is what <c>TppEnemyFaceId</c> points at.
    /// </summary>
    public static string? SoldierFace(PlayerBranch who) => SoldierFaces(who).FirstOrDefault();

    /// <summary>
    /// The soldier faces a DD body can wear, best first. The owner's rule: "DD Male/Female uses different models/textures
    /// as there are a lot of them. We should use the first in whatever category that is." So the first bare face
    /// (cm_m0/cm_f0_h0_v000_eye0) is the default; the balaclava head (cm_dds5/dds6_head_z_eye0) and the enemy soldier heads
    /// (cm_dds3/dds8_eqhd*_eye0) are the fallbacks for a game that lacks it. The male and female sets are separate: they are different models with different textures.
    /// </summary>
    /// <param name="who">The character.</param>
    /// <returns>The candidate packages, best first; empty for Snake.</returns>
    public static IReadOnlyList<string> SoldierFaces(PlayerBranch who) => who switch
    {
        // Issue #78: the Avatar's own default face (first male type, owner's decision), then the DD male faces.
        PlayerBranch.Avatar => ["/Assets/tpp/pack/player/avatar/face/plfova_avm0_type0_v00", .. SoldierFaces(PlayerBranch.DdMale)],
        PlayerBranch.DdMale =>
        [
            "/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_m0_h0_v000_eye0",
            "/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_dds5_head_z_eye0",
            "/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_dds3_eqhd1_eye0",
        ],
        PlayerBranch.DdFemale =>
        [
            "/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_f0_h0_v000_eye0",
            "/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_dds6_head_z_eye0",
            "/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_dds8_eqhd1_eye0",
        ],
        _ => [],
    };

    /// <summary>
    /// The soldier face to draw for a DD body, taking the first candidate the game actually has. A partial game dump (the
    /// tests) may hold only one of them, and a missing head package used to mean a headless body.
    /// </summary>
    /// <param name="who">The character.</param>
    /// <param name="game">The game's files, or null to take the preferred package without checking.</param>
    /// <returns>The package path, or null for anyone who is not a DD soldier.</returns>
    public static string? SoldierFace(PlayerBranch who, GameData? game)
    {
        var candidates = SoldierFaces(who);
        if (game == null) return candidates.FirstOrDefault();
        return candidates.FirstOrDefault(c => game.TryRead(c + ".fpk") != null) ?? candidates.FirstOrDefault();
    }

    /// <summary>The icon; <paramref name="missing"/>, when given, receives the textures the models use that neither the mod nor the game has.</summary>
    public static RgbaImage Icon(GameData game, PathDictionary? dict, SuitRenderInput input, int supersample = 3, ICollection<ulong>? missing = null)
    {
        var scene = Scene(game, dict, input);
        var picture = IconRenderer.Render(scene.Models, scene.Source.Get, framing: input.Framing, lighting: input.Lighting,
            supersample: supersample, fv2Swaps: scene.Swaps);
        if (missing != null) foreach (var c in scene.Source.Missing) missing.Add(c);
        return SuitIcon.Frame(picture, SuitIcon.DrawnMask());
    }

    /// <summary>The models, texture source and swaps the icon and the preview draw.</summary>
    public static (List<FmdlModel> Models, TextureSource Source, List<Fv2File.TextureSwap> Swaps) Scene(GameData game, PathDictionary? dict, SuitRenderInput input)
    {
        var src = new TextureSource(game, dict);
        foreach (var p in input.ModPacks) src.AddModPack(p);
        foreach (var (path, data) in input.ModFiles) src.AddModFile(path, data);

        var bodyFpk = input.BodyFpk ?? FoxPackage.Read(game.Read(input.BodyPackage + ".fpk"));
        var bodyPack = input.BodyPack ?? (game.TryRead(input.BodyPackage + ".pftxs") is { } bp ? TexturePack.Read(bp) : null);
        if (input.BodyPack != null) src.AddModPack(input.BodyPack);
        else if (bodyPack != null) src.AddGamePack(bodyPack);
        var models = new List<FmdlModel> { MainModel(bodyFpk) ?? throw new InvalidDataException($"{input.BodyPackage}.fpk has no model") };

        if (input.HeadPackage != null)
        {
            var headFpk = input.HeadFpk ?? (game.TryRead(input.HeadPackage + ".fpk") is { } hf ? FoxPackage.Read(hf) : null);
            var headPack = input.HeadPack ?? (game.TryRead(input.HeadPackage + ".pftxs") is { } hp ? TexturePack.Read(hp) : null);
            if (input.HeadPack != null) src.AddModPack(input.HeadPack);
            else if (headPack != null) src.AddGamePack(headPack);
            // A head is drawn with every mesh group shown. A head package hides most of its groups because in game a
            // look's .fv2 shows them; the preview has no look, and both renderers skip a hidden group, so the vanilla DD
            // soldier face came out as a pair of eyes with no head around it (the owner's report). Bodies keep their
            // hidden groups: a body hides parts for a reason.
            // Issue #129: an extra that the .fv2 attaches keeps its own hidden groups, as in game.
            if (headFpk != null && HeadModels(headFpk, input.HeadFv2) is var (heads, faceFirst))
                for (int k = 0; k < heads.Count; k++)
                    models.Add(input.HeadGroups is { } g ? heads[k].WithGroupsShown(i => g.Contains(i)) : faceFirst && k == 0 ? heads[k].WithHeadGroupsShown() : heads[k]);
        }

        // The arm, when one is asked for: Snake's bionic arm is its own package, so it is drawn as a second model.
        if (input.ArmPackage != null)
        {
            var armFpk = input.ArmFpk ?? (game.TryRead(input.ArmPackage + ".fpk") is { } af ? FoxPackage.Read(af) : null);
            if (input.ArmPack != null) src.AddModPack(input.ArmPack);
            else if (game.TryRead(input.ArmPackage + ".pftxs") is { } ap) src.AddGamePack(TexturePack.Read(ap));
            if (armFpk != null && MainModel(armFpk) is { } arm) models.Add(arm);
        }

        var swaps = input.Fv2s.SelectMany(f => new Fv2File(f).TextureSwaps()).ToList();
        return (models, src, swaps);
    }

    /// <summary>
    /// The main texture of the scene's body: the base texture of its largest shown mesh, after the look's swaps. It can be
    /// a game texture. The camo crop of an outfit with no camo texture of its own uses it (issue #32).
    /// </summary>
    /// <param name="scene">The scene from <see cref="Scene"/>.</param>
    /// <returns>The texture, or null when no mesh names one that the mod or the game has.</returns>
    public static RgbaImage? MainTexture((List<FmdlModel> Models, TextureSource Source, List<Fv2File.TextureSwap> Swaps) scene)
    {
        var body = scene.Models[0];
        var swaps = new Dictionary<(uint, uint), ulong>();
        foreach (var sw in scene.Swaps) swaps[(sw.Material, sw.Slot)] = sw.Texture;
        var shown = body.Meshes.Where(m => m.Uv != null && m.MaterialIndex < body.Materials.Count
            && !(m.MeshGroup >= 0 && m.MeshGroup < body.MeshGroups.Count && body.MeshGroups[m.MeshGroup].hidden));
        foreach (var mesh in shown.OrderByDescending(m => m.Indices.Length))
        {
            var mat = body.Materials[mesh.MaterialIndex];
            foreach (var role in new[] { "Base_Tex_SRGB", "Base_Tex_LIN" })
            {
                ulong code = swaps.TryGetValue(((uint)mat.NameHash, Hashing.FoxHash.StrCode32(role)), out var s) ? s : mat.Slot(role)?.PathCode ?? 0;
                if (code != 0 && scene.Source.Get(code) is { } t) return t;
            }
        }
        return null;
    }

    /// <summary>The package's main model: the largest .fmdl (props such as the feather hat are small).</summary>
    public static FmdlModel? MainModel(FoxPackage fpk)
    {
        var f = fpk.Files.Where(x => x.Extension == "fmdl").OrderByDescending(x => x.Data.Length).FirstOrDefault();
        return f == null ? null : FmdlModel.Read(f.Data);
    }

    /// <summary>
    /// The head model of a head package: the <c>_cov.fmdl</c> when there is one (that is the head; the vanilla soldier
    /// faces and the vanilla Snake faces both name it so), otherwise the largest .fmdl. A head package can hold more than
    /// one model (the PW battle dress head packages carry the balaclava plus its alternate meshes), and taking whichever
    /// came first in the package is how a head option ended up built from the wrong model.
    /// </summary>
    /// <param name="fpk">The head package.</param>
    /// <returns>The head model, or null when the package has no model.</returns>
    public static FmdlModel? HeadModel(FoxPackage fpk)
    {
        var files = fpk.Files.Where(x => x.Extension == "fmdl").ToList();
        var f = files.FirstOrDefault(x => x.Path.EndsWith("_cov.fmdl", StringComparison.OrdinalIgnoreCase))
            ?? files.OrderByDescending(x => x.Data.Length).FirstOrDefault();
        return f == null ? null : FmdlModel.Read(f.Data);
    }

    /// <summary>
    /// The models a head option's .fv2 attaches from its package (issue #129: a Snake head add-on attaches a copy of the
    /// face plus the extras, the Avatar branch only the extras). The face (<see cref="HeadModel"/>'s pick) comes first
    /// when it is attached. Without such a .fv2 it is <see cref="HeadModel"/> alone.
    /// Cloth tails hang down (<see cref="Rendering.TailPose"/>, issue #128).
    /// </summary>
    /// <param name="fpk">The head package.</param>
    /// <param name="fv2">The branch's .fv2 path in the package, or null.</param>
    /// <returns>The models, and whether the first is the face.</returns>
    public static (List<FmdlModel> Models, bool FaceFirst) HeadModels(FoxPackage fpk, string? fv2)
    {
        var faceFile = fpk.Files.Where(x => x.Extension == "fmdl").ToList() is var all
            ? all.FirstOrDefault(x => x.Path.EndsWith("_cov.fmdl", StringComparison.OrdinalIgnoreCase)) ?? all.OrderByDescending(x => x.Data.Length).FirstOrDefault() : null;
        if (fv2 != null && fpk.Find(fv2) is { } f && new Fv2File(f.Data).Attachments() is { Count: > 0 } attach)
        {
            var codes = attach.Select(a => a.Model).ToHashSet();
            var files = all.Where(x => codes.Contains(Hashing.FoxHash.PathCode64(x.Path))).OrderBy(x => x == faceFile ? 0 : 1).ToList();
            if (files.Count > 0) return (files.Select(x => Rendering.TailPose.Hang(FmdlModel.Read(x.Data))).ToList(), files[0] == faceFile);
        }
        return faceFile == null ? ([], false) : ([Rendering.TailPose.Hang(FmdlModel.Read(faceFile.Data))], true);
    }

    /// <summary>The .fv2 files inside a package (a camo package holds one).</summary>
    public static IEnumerable<byte[]> Fv2sIn(FoxPackage fpk) => fpk.Files.Where(x => x.Extension == "fv2").Select(x => x.Data);
}
