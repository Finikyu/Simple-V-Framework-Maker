using System.Text.RegularExpressions;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Mods;
using Svfm.Core.Rendering;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>A DD head model a mod brings: the model file and the texture pack, per body (dds5 = male, dds6 = female).</summary>
public sealed record DdHeadModel(PlayerBranch Body, string ModelPath, byte[] Fmdl, byte[]? Pack)
{
    /// <summary>The vanilla package the model came from (path without extension), whose texture pack stands in when the mod ships none.</summary>
    public string? SourcePackage { get; init; }
    /// <summary>The mod's archive files, for the loose textures the model names (copied with the head when at new paths).</summary>
    public IReadOnlyDictionary<string, byte[]>? ModFiles { get; init; }
}

/// <summary>
/// DD head models as a DD head option with its own model, so the vanilla head stays. Covers mods that replace a vanilla
/// soldier head package (cm_dds5/dds6_head_z_eye0, the balaclava, as PW Battle Dress does) and head models found inside
/// a body. A head option's own model needs fv2 + fpk (V Framework 1.7 RegisterHeadOption). The tool builds that package
/// itself, the way the vanilla face packages are made: the model at a new path, an attach .fv2 naming it (vanilla
/// head .fv2s attach just the model), a .fox2 that loads the texture pack found by the package name (copied from the
/// vanilla Snake face package; only internal IDs differ between these), and the mod's texture pack under the package's name.
/// </summary>
public static class DdHeadConverter
{
    private const string Fox2Source = "/Assets/tpp/pack/player/fova/plfova_sna0_face0_v00.fpkd";
    private const string FaceSource = "/Assets/tpp/pack/player/fova/plfova_sna0_face0_v00.fpk";

    /// <summary>Head models from mods that replace the soldier head packages (cm_dds5/6_head_*).</summary>
    public static List<DdHeadModel> FromHeadPackages(MgsvMod mod)
    {
        var list = new List<DdHeadModel>();
        foreach (var (path, data) in mod.ArchiveFiles)
        {
            var m = Regex.Match(path, @"/cm_dds([56])_head_[^/]+\.fpk$");
            if (!m.Success) continue;
            // The head model, not whichever model came first in the package: a head package can hold more than one (the
            // PW battle dress head packages carry the balaclava plus its alternate meshes), and taking the first one is
            // how a head option ended up built from the wrong model. SuitRender.HeadModel is the same rule the preview
            // uses, so the two can never disagree about which model is the head.
            var fpk = FoxPackage.Read(data);
            var f = fpk.Files.FirstOrDefault(x => x.Extension == "fmdl" && x.Path.EndsWith("_cov.fmdl", StringComparison.OrdinalIgnoreCase))
                ?? fpk.Files.Where(x => x.Extension == "fmdl").OrderByDescending(x => x.Data.Length).FirstOrDefault();
            if (f == null) continue;
            mod.ArchiveFiles.TryGetValue(path[..^4] + ".pftxs", out var pack);
            list.Add(new DdHeadModel(m.Groups[1].Value == "5" ? PlayerBranch.DdMale : PlayerBranch.DdFemale, f.Path, f.Data, pack) { SourcePackage = path[..^4], ModFiles = mod.ArchiveFiles });
        }
        return list;
    }

    /// <summary>Adds a DD head option with its own model per body.</summary>
    /// <param name="p">The project.</param>
    /// <param name="game">Game data (the face reference for the icon, vanilla packs).</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="title">English name.</param>
    /// <param name="description">English description.</param>
    /// <param name="models">The mod's head models, one per DD body it covers.</param>
    /// <remarks>
    /// A head goes only on the DD bodies the mod made a model for (user, Oct 25: the MSF balaclavas, male models, don't
    /// sit well on DD women; the rigs share the chest, neck and head bones but the face bones sit ~2 cm lower on women).
    /// </remarks>
    public static HeadOptionItem AddTo(ModProject p, GameData game, string itemId, string title, string description, IReadOnlyList<DdHeadModel> models)
    {
        if (models.Count == 0) throw new ArgumentException("No head models.");
        var (key, prefix) = p.NewItemNames(itemId);
        var spec = new HeadOptionSpec
        {
            Key = key, NameLang = $"{prefix}_name", RealNameLang = $"{prefix}_name", InfoLang = $"{prefix}_info",
            IconFtexPath = $"/Assets/tpp/ui/texture/EquipIcon/head/ui_head_{prefix}_alp",
            Develop = new DevelopRow { InitiallyAvailable = true, GmpCost = 0, Grade = 1 },
        };
        var item = new HeadOptionItem { Id = itemId, Key = key, Prefix = prefix, Spec = spec, Title = title };
        var fox2Pkg = FoxPackage.Read(game.Read(Fox2Source));
        var fox2 = fox2Pkg.Files.First(f => f.Extension == "fox2").Data;
        foreach (var hm in models)
        {
            string who = hm.Body == PlayerBranch.DdMale ? "m" : "f";
            string pkg = $"/Assets/tpp/pack/player/fova/plfova_{prefix}_{who}";
            string model = $"/Assets/tpp/chara/dds/Scenes/{prefix}_{who}_cov.fmdl", fv2 = $"/Assets/tpp/fova/chara/dds/{prefix}_{who}.fv2";
            var fpk = new FoxPackage();
            fpk.Files.Add(new PackageFile(model, hm.Fmdl));
            fpk.Files.Add(new PackageFile(fv2, Fv2File.Attach([new Fv2File.ModelAttach(FoxHash.PathCode64(model))]).Write()));
            var fpkd = new FoxPackage { IsFpkd = true };
            fpkd.Files.Add(new PackageFile($"/Assets/tpp/level_asset/chara/player/game_object/plfova_{prefix}_{who}.fox2", fox2));
            PackageOrder.ApplySnakeBiteOrder(fpk);
            PackageOrder.ApplySnakeBiteOrder(fpkd);
            item.Files[pkg + ".fpk"] = fpk.Write();
            item.Files[pkg + ".fpkd"] = fpkd.Write();
            // The mod's pack, else the vanilla pack of the package it replaced (the mod kept that one: it only replaced the model).
            var pack = hm.Pack ?? (hm.SourcePackage != null ? game.TryRead(hm.SourcePackage + ".pftxs") : null);
            if (pack != null) item.Files[pkg + ".pftxs"] = pack;
            else item.Notes.Add($"{hm.ModelPath}: no texture pack came with the model and the vanilla one was not found; its textures must load as loose files.");
            // Loose textures the model names that the mod ships at new paths: copied as they are (they load by path).
            if (hm.ModFiles != null)
            {
                var named = FmdlModel.Read(hm.Fmdl).Materials.SelectMany(m => m.Textures).Select(t => t.PathCode).ToHashSet();
                int copied = 0, atVanilla = 0;
                foreach (var (path, data) in hm.ModFiles)
                {
                    if (!path.Contains(".ftex", StringComparison.Ordinal)) continue;
                    string ftex = TextureCheck.FtexOf(path);
                    if (!named.Contains(FoxHash.PathCode64(ftex))) continue;
                    if (InstalledTextures.IsVanillaPath(game, ftex)) { atVanilla++; continue; }
                    item.Files[path] = data;
                    copied++;
                }
                if (copied > 0) item.Notes.Add($"{hm.ModelPath}: {copied} loose texture file(s) the model names at new paths, copied as they are.");
                if (atVanilla > 0) item.Notes.Add($"{hm.ModelPath}: {atVanilla} loose texture file(s) at vanilla paths are not copied (they would change the vanilla game).");
            }
            spec.Branches[hm.Body] = new HeadBranch { Fv2 = fv2, Fpk = pkg + ".fpk" };
        }
        spec.Header.Add($"{title}: own head model for {string.Join(" and ", models.Select(m => LuaWriter.BranchName(m.Body)))}.");
        var ft = FtexWriter.Write(Icon(game, models), withAlpha: true, FtexKind.Linear);
        item.Files[spec.IconFtexPath + ".ftex"] = ft.Ftex;
        foreach (var (k, d) in ft.Ftexs) item.Files[$"{spec.IconFtexPath}.{k}.ftexs"] = d;
        item.SetText($"{prefix}_name", title);
        item.SetText($"{prefix}_info", description);
        p.Items.Add(item);
        return item;
    }

    /// <summary>
    /// The icon of a DD head option: the head gear alone, as the vanilla headgear icons draw it; facing left when it
    /// encloses the head (a helmet, a balaclava), turned less when it is smaller (measured against Snake's plain face;
    /// models rooted at the waist are moved to the chest, as face models are).
    /// </summary>
    /// <param name="game">Game data (the vanilla face the framing is measured against).</param>
    /// <param name="models">The head models, one per DD body.</param>
    /// <returns>The icon picture.</returns>
    public static RgbaImage Icon(GameData game, IReadOnlyList<DdHeadModel> models) => Icon(game, FmdlModel.Read(models[0].Fmdl));

    /// <summary>The icon of a head option drawn from one head model (an item of a mod that is already a V Framework mod).</summary>
    /// <param name="game">Game data (the vanilla face the framing is measured against).</param>
    /// <param name="first">The head model.</param>
    /// <returns>The icon picture.</returns>
    public static RgbaImage Icon(GameData game, FmdlModel first)
    {
        List<FmdlMesh> refHead = [];
        if (game.TryRead(FaceSource) is { } facePkg && FoxPackage.Read(facePkg).Files.FirstOrDefault(f => f.Path.EndsWith("_cov.fmdl", StringComparison.Ordinal)) is { } face)
            refHead = HeadIcon.HeadSilhouette(FmdlModel.Read(face.Data));
        return HeadIcon.RenderGear(HeadIcon.Moved(HeadIcon.GearMeshes(first), HeadIcon.ToFaceSpace(first)), refHead);
    }

    /// <summary>The DD bodies a set of head models covers, and the ones it doesn't (the head is never offered to those).</summary>
    public static (IReadOnlyList<PlayerBranch> covered, IReadOnlyList<PlayerBranch> missing) Bodies(IReadOnlyList<DdHeadModel> models)
    {
        var covered = models.Select(m => m.Body).Distinct().OrderBy(b => b).ToList();
        return (covered, new[] { PlayerBranch.DdMale, PlayerBranch.DdFemale }.Where(b => !covered.Contains(b)).ToList());
    }
}
