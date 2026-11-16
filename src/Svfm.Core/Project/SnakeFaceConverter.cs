using System.Text.RegularExpressions;
using Svfm.Core.Archives;
using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Mods;
using Svfm.Core.Rendering;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>Names and text for the head options made from a Snake face mod; null = defaults from the mod.</summary>
public sealed record SnakeFaceOptions
{
    public string? Name { get; init; }
    public string? BandanaName { get; init; }
    public string? Description { get; init; }
    /// <summary>Attach the hair physics the mod puts in the body packages to the plain faces (default yes).</summary>
    public bool AttachHairPhysics { get; init; } = true;
    /// <summary>List the heads on the vanilla Snake suits that offer head options (default yes).</summary>
    public bool OfferOnVanillaSuits { get; init; } = true;
}

/// <summary>
/// Snake face replacers (plfova_sna0_face0/1/2 and the bandana faces 4/5/6, such as the TMWSTW Mullet) as Snake head
/// options, so the vanilla face stays. One head option for the plain faces and one for the bandana faces, each with the
/// three Demon stages (face0/1/2 = stages 1–3, face4/5/6 the same with the bandana). Per face, its own package: the
/// vanilla face package with the mod's files merged over it, the face model moved to a new path, and a new attach .fv2
/// that names it (keeping the vanilla attachments, e.g. the bandana cloth .sim). The mod's hair physics (.sim it puts in
/// the body packages) is packed with the plain faces and attached by their .fv2, since a head option can't change the
/// body packages; the bandana faces already use that slot for the bandana cloth. Loose textures at new paths are copied
/// as they are; story packages and UI pictures are left out.
/// </summary>
public static class SnakeFaceConverter
{
    private const string FacePkg = "/Assets/tpp/pack/player/fova/plfova_sna0_face{0}_v00";
    private static readonly int[] Plain = [0, 1, 2], Bandana = [4, 5, 6];

    /// <summary>The Snake face numbers whose package the mod replaces.</summary>
    public static List<int> ReplacedFaces(MgsvMod mod) =>
        mod.ArchiveFiles.Keys.Select(p => Regex.Match(p, @"/plfova_sna0_face(\d)_v00\.fpk$")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(n => n).ToList();

    public static List<HeadOptionItem> AddTo(ModProject p, GameData game, MgsvMod mod, string setId, SnakeFaceOptions o)
    {
        Naming.CheckId(setId);
        var replaced = ReplacedFaces(mod);
        if (replaced.Count == 0) throw new ArgumentException("The mod replaces no Snake face package.");
        var (_, setPrefix) = p.NewItemNames(setId);
        var notes = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // Loose textures at new paths come along; anything at a vanilla path is refused (never change vanilla files).
        foreach (var (path, data) in mod.ArchiveFiles)
        {
            var kind = ModClassifier.KindOf(path, mod);
            if (!path.Contains(".ftex", StringComparison.Ordinal) || kind is ModFileKind.Ui or ModFileKind.Icon) continue;
            string ftex = TextureCheck.FtexOf(path); // a stray "tex.ftexs" (one dot) threw here
            if (InstalledTextures.IsVanillaPath(game, ftex)) continue;   // moved whole under a new name below
            if (!mod.Ships(path)) continue;   // a separately installed dependency's file
            files[path] = data;
        }
        // Textures the mod installs at vanilla paths: complete copies under new names; the face models point at them.
        InstalledTextures.Result relocated;
        {
            string firstPkg = string.Format(FacePkg, replaced[0]);
            var moved = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            relocated = InstalledTextures.Collect(game, mod, TextureCheck.Referenced(mod.ArchiveFiles), setPrefix,
                mod.ArchiveFiles.TryGetValue(firstPkg + ".pftxs", out var fmp) ? TexturePack.Read(fmp) : null,
                game.TryRead(firstPkg + ".pftxs") is { } fvp ? TexturePack.Read(fvp) : null, moved, notes);
            foreach (var (path, data) in moved) files.TryAdd(path, data);
        }
        foreach (var path in mod.ArchiveFiles.Keys.Where(k => ModClassifier.KindOf(k, mod) is ModFileKind.MissionOrCutscene or ModFileKind.Ui))
            notes.Add($"{path}: {(ModClassifier.KindOf(path, mod) == ModFileKind.Ui ? "UI picture" : "story/cutscene package")}; not part of a head, left out.");

        // Hair physics the mod puts in the body packages (the same file in every one, as in the Mullet).
        byte[]? hairSim = null;
        if (o.AttachHairPhysics)
        {
            var sims = mod.ArchiveFiles.Where(kv => ModClassifier.KindOf(kv.Key, mod) == ModFileKind.BodyPhysics)
                .SelectMany(kv => FoxPackage.Read(kv.Value).Files.Where(f => f.Extension == "sim" && f.Path.Contains("hair", StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Data).ToList();
            if (sims.Count > 0)
            {
                hairSim = sims[0];
                if (sims.Any(s => !s.AsSpan().SequenceEqual(hairSim))) notes.Add("The mod's hair physics differs between bodies; the first one is used.");
            }
        }
        string simPath = $"/Assets/tpp/chara/sna/Fox_files/{setPrefix}_hair.sim";

        // One package per face.
        var pkgOf = new Dictionary<int, (string fpk, string fv2)>();
        FmdlModel? iconFace = null, iconBandana = null;
        foreach (int n in Plain.Concat(replaced.Any(r => r >= 4) ? Bandana : []))
        {
            string van = string.Format(FacePkg, n), pkg = $"/Assets/tpp/pack/player/fova/plfova_{setPrefix}_f{n}";
            var fpk = FoxPackage.Read(game.Read(van + ".fpk"));
            var fpkd = game.TryRead(van + ".fpkd") is { } vd ? FoxPackage.Read(vd) : new FoxPackage { IsFpkd = true };
            foreach (var (ext, target) in new[] { (".fpk", fpk), (".fpkd", fpkd) })
                if (mod.ArchiveFiles.TryGetValue(van + ext, out var md))
                    foreach (var f in FoxPackage.Read(md).Files)
                    {
                        int i = target.Files.FindIndex(x => x.Path == f.Path);
                        if (i >= 0) target.Files[i] = f; else target.Files.Add(f);
                    }

            string vanFv2 = $"/Assets/tpp/fova/chara/sna/sna0_face{n}_v00.fv2";
            var attach = new Fv2File((fpk.Find(vanFv2) ?? throw new InvalidDataException($"{van}.fpk has no face .fv2.")).Data).Attachments().ToList();
            fpk.Files.RemoveAll(f => f.Path == vanFv2);
            string vanModel = $"/Assets/tpp/chara/sna/Scenes/sna0_face{n}_cov.fmdl", newModel = $"/Assets/tpp/chara/sna/Scenes/{setPrefix}_f{n}_cov.fmdl";
            int mi = fpk.Files.FindIndex(f => f.Path == vanModel);
            if (mi < 0) throw new InvalidDataException($"{van}.fpk has no face model.");
            var model = fpk.Files[mi].Data;
            fpk.Files[mi] = new PackageFile(newModel, model);
            ulong vanCode = FoxHash.PathCode64(vanModel), newCode = FoxHash.PathCode64(newModel);
            for (int i = 0; i < attach.Count; i++)
                if (attach[i].Model == vanCode) attach[i] = attach[i] with { Model = newCode };
            if (hairSim != null && n < 4 && replaced.Contains(n))
            {
                int fi = attach.FindIndex(a => a.Model == newCode && a.Sim == null);
                if (fi >= 0) { attach[fi] = attach[fi] with { Sim = FoxHash.PathCode64(simPath) }; fpkd.Files.Add(new PackageFile(simPath, hairSim)); }
            }
            string fv2Path = $"/Assets/tpp/fova/chara/sna/{setPrefix}_f{n}.fv2";
            fpk.Files.Add(new PackageFile(fv2Path, Fv2File.Attach(attach).Write()));
            if (n == 0) iconFace = FmdlModel.Read(model);
            if (n == 4) iconBandana = FmdlModel.Read(model);

            // Texture pack, renamed with the package: the mod's pack (complete, as the managers treat it) plus vanilla blocks it dropped.
            var vp = game.TryRead(van + ".pftxs") is { } vt ? TexturePack.Read(vt) : null;
            var pack = mod.ArchiveFiles.TryGetValue(van + ".pftxs", out var mp) ? TexturePack.Read(mp) : vp;
            if (pack != null && vp != null && pack != vp) foreach (var t in vp.Textures.Where(t => pack.Find(t.PathCode) == null)) pack.Textures.Add(t);

            InstalledTextures.Apply(fpk, relocated, notes);
            InstalledTextures.Apply(fpkd, relocated, notes);
            if (pack != null) InstalledTextures.RestoreVanilla(pack, vp, relocated);
            foreach (var dropped in PackageOrder.ApplySnakeBiteOrder(fpk).Concat(PackageOrder.ApplySnakeBiteOrder(fpkd)))
                notes.Add($"{dropped.Path}: a file type the managers leave out of packages; left out.");
            files[pkg + ".fpk"] = fpk.Write();
            files[pkg + ".fpkd"] = fpkd.Write();
            if (pack != null) files[pkg + ".pftxs"] = pack.Write();
            pkgOf[n] = (pkg + ".fpk", fv2Path);
            if (!replaced.Contains(n)) notes.Add($"The mod has no face{n}; that stage shows the vanilla face.");
        }
        if (hairSim != null) notes.Add("The mod's hair physics is added to the plain faces. Check it in game: the body's own hair physics stays too.");

        // Head options.
        string modName = mod.Metadata.Name.Length > 0 ? mod.Metadata.Name : setId;
        var sets = new List<(string id, int[] faces, string title)> { ("hair", Plain, o.Name ?? modName.ToUpperInvariant()) };
        if (replaced.Any(r => r >= 4)) sets.Add(("bandana", Bandana, o.BandanaName ?? $"{modName.ToUpperInvariant()} + BANDANA"));
        var items = new List<HeadOptionItem>();
        bool first = true;
        foreach (var (sid, faces, title) in sets)
        {
            var (key, prefix) = p.NewItemNames(sets.Count == 1 ? setId : $"{setId}{sid}");
            var stages = Enumerable.Range(1, 3).ToDictionary(s => s, s => (pkgOf[faces[s - 1]].fv2, pkgOf[faces[s - 1]].fpk));
            string icon = $"/Assets/tpp/ui/texture/EquipIcon/head/ui_head_{prefix}_alp";
            var ft = FtexWriter.Write(Icon(sid == "bandana" ? iconBandana ?? iconFace! : iconFace!), withAlpha: true, FtexKind.Linear);
            var spec = new HeadOptionSpec
            {
                SnakeFaceOnly = true, Key = key, NameLang = $"{prefix}_name", RealNameLang = $"{prefix}_name", InfoLang = $"{prefix}_info",
                IconFtexPath = icon,
                // The bandana version deploys for the vanilla Bandana's GMP (user, Oct 25).
                Develop = new DevelopRow { InitiallyAvailable = true, GmpCost = 0, Grade = 1, UsageGmpCost = sid == "bandana" ? Knowledge.GameKnowledge.BandanaUsageGmp : 0 },
            };
            spec.Header.Add($"{title}: Snake's face from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}, faces {string.Join("/", faces)} as Demon stages 1–3.");
            spec.Branches[PlayerBranch.Snake] = new HeadBranch { Fv2 = stages[1].fv2, Fpk = stages[1].fpk, FaceStages = stages };
            var item = new HeadOptionItem { Id = sets.Count == 1 ? setId : $"{setId}{sid}", Key = key, Prefix = prefix, Spec = spec, Title = title };
            item.SetText($"{prefix}_name", title);
            item.SetText($"{prefix}_info", o.Description ?? $"Snake's face from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}.");
            item.Files[icon + ".ftex"] = ft.Ftex;
            foreach (var (k, d) in ft.Ftexs) item.Files[$"{icon}.{k}.ftexs"] = d;
            if (first) { foreach (var (path, data) in files) item.Files[path] = data; item.Notes.AddRange(notes); first = false; }
            p.Items.Add(item);
            items.Add(item);
        }
        if (o.OfferOnVanillaSuits) SnakeHeadAddon.OfferOnVanillaSuits(p, setId, items.Select(i => i.Spec.Key).ToList());
        return items;
    }

    /// <summary>
    /// The face model a Snake face head option's icon is drawn from: face0 for the plain faces, face4 for the bandana
    /// ones (the mod's own model when it replaced that package, else the vanilla one).
    /// </summary>
    /// <param name="game">Game data.</param>
    /// <param name="mod">The mod.</param>
    /// <param name="bandana">True for the bandana faces (face4), false for the plain ones (face0).</param>
    /// <returns>The face model.</returns>
    public static FmdlModel IconFace(GameData game, MgsvMod mod, bool bandana)
    {
        int n = bandana ? 4 : 0;
        string van = string.Format(FacePkg, n);
        var pkg = mod.ArchiveFiles.TryGetValue(van + ".fpk", out var md) ? FoxPackage.Read(md) : FoxPackage.Read(game.Read(van + ".fpk"));
        string model = $"/Assets/tpp/chara/sna/Scenes/sna0_face{n}_cov.fmdl";
        var f = pkg.Files.FirstOrDefault(x => x.Path == model) ?? throw new InvalidDataException($"{van}.fpk has no face model.");
        return FmdlModel.Read(f.Data);
    }

    /// <summary>
    /// The icon of a Snake face head option: the hair (and the bandana, when the face wears one) alone, as the vanilla
    /// headgear icons draw it, with the face left out.
    /// </summary>
    /// <param name="face">The face model the icon is drawn from (face0 for the plain faces, face4 for the bandana ones).</param>
    /// <returns>The icon picture.</returns>
    public static RgbaImage Icon(FmdlModel face)
    {
        var gear = face.Meshes.Where(m => HeadIcon.UsesTexture(face, m, "hair", "sna_bdn0")).ToList();
        return HeadIcon.Render(gear.Count > 0 ? gear : face.Meshes.ToList(), gear.Count > 0 ? HeadIcon.HeadSilhouette(face) : []);
    }
}
