using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Rendering;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>An extra model worn with Snake's face: a copy of a model with only some mesh groups shown.</summary>
public sealed record HeadAddonModel(string Id, byte[] Fmdl, IReadOnlySet<int> Groups);

/// <summary>One head option: Snake's face (or his bandana face) plus some of the extra models.</summary>
public sealed record HeadAddonOption(string Id, string Title, string Description, IReadOnlyList<string> Models, bool Bandana = false);

/// <summary>
/// Accessories as Snake head options (goggles, a scarf over the face), so they go with any outfit. A Snake head option
/// replaces the face, and its .fv2 is an attach list (the vanilla face attaches one model; the bandana face adds the
/// cloth .sim). So each option's .fv2 attaches the vanilla face model of the Demon stage, then the extra models. Per
/// stage one package holds copies of the vanilla face package(s), the extra models (only their own mesh groups shown)
/// and every option's .fv2; its texture pack is the vanilla face pack plus the small mips of the textures the extra
/// models use. Vanilla faces: face0/1/2 = stages 1–3, face4/5/6 the same with the bandana.
/// Avatar: every option also gets an Avatar branch: one package with the extra models and an .fv2 per option that
/// attaches only them, worn on the Avatar's own face. A bandana option also attaches the Avatar's vanilla bandana: a copy
/// of the Avatar face model avm0_type0 with only its bandana group (g2, hidden in the face) shown, with the face's cloth
/// .sim for the tails.
/// </summary>
public static class SnakeHeadAddon
{
    private const string FacePkg = "/Assets/tpp/pack/player/fova/plfova_sna0_face{0}_v00";
    private const string FaceFv2 = "/Assets/tpp/fova/chara/sna/sna0_face{0}_v00.fv2";
    private const string AvatarFacePkg = "/Assets/tpp/pack/player/avatar/face/plfova_avm0_type0_v00";
    private const string AvatarFaceModel = "/Assets/tpp/chara/avm/Scenes/avm0_type0_def.fmdl";
    /// <summary>The mesh group of the Avatar bandana in <see cref="AvatarFaceModel"/> (meshes 7 and 8, avm_bdn0 textures).</summary>
    private const int AvatarBandanaGroup = 2;

    /// <summary>
    /// Adds the options as head options (with their files) and returns them. <paramref name="texturesFrom"/> are texture
    /// packs that may hold the extra models' textures (small mips are copied); textures not found there must exist as
    /// loose files in the mod (passed in <paramref name="looseFiles"/>, copied as they are) or in the game.
    /// Each option gets an icon drawn from its own models (<see cref="HeadIcon"/>) unless <paramref name="iconFtexPath"/> names one.
    /// </summary>
    public static List<HeadOptionItem> AddTo(ModProject p, GameData game, string setId, IReadOnlyList<HeadAddonModel> models,
        IReadOnlyList<HeadAddonOption> options, IReadOnlyList<TexturePack> texturesFrom, IReadOnlyDictionary<string, byte[]>? looseFiles = null,
        string? iconFtexPath = null)
    {
        Naming.CheckId(setId);
        var (_, setPrefix) = p.NewItemNames(setId);
        var notes = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (looseFiles != null) foreach (var (path, data) in looseFiles) files[path] = data;

        // Extra models, at new paths.
        var modelPath = new Dictionary<string, string>();
        var modelData = new Dictionary<string, byte[]>();
        foreach (var m in models)
        {
            modelPath[m.Id] = Naming.SnaModel(setPrefix, m.Id);
            modelData[m.Id] = FmdlModel.ShowOnlyGroups(m.Fmdl, m.Groups);
        }

        // Textures the extra models use, small mips from the given packs.
        var extraTextures = new List<PackedTexture>();
        foreach (var m in models)
        {
        var fm = FmdlModel.Read(m.Fmdl);
        var mats = fm.Meshes.Where(x => m.Groups.Contains(x.MeshGroup)).Select(x => x.MaterialIndex).Distinct();
        foreach (var slot in mats.SelectMany(i => fm.Materials[i].Textures))
        {
            if (slot.PathCode == 0 || extraTextures.Any(t => t.PathCode == slot.PathCode)) continue;
            var found = texturesFrom.Select(tp => tp.Find(slot.PathCode)).FirstOrDefault(t => t != null);
            if (found != null) { extraTextures.Add(found); continue; }
            bool loose = looseFiles?.Keys.Any(k => k.EndsWith(".ftex", StringComparison.Ordinal) && FoxHash.PathCode64(k) == slot.PathCode) == true;
            string? vanillaPath = PathDictionary.Bundled.TryGetPath(slot.PathCode);
            if (!loose && vanillaPath != null && game.Contains(vanillaPath)) continue;
            if (!loose) notes.Add($"Texture {vanillaPath ?? slot.FileName ?? slot.PathCode.ToString("x16")} of {m.Id} is not in the given packs or the mod; it must come from the game's texture archives (full .ftex there loads on its own).");
        }
        }

        bool anyBandana = options.Any(o => o.Bandana);
        var stageFiles = new Dictionary<int, (string fpk, Dictionary<string, string> fv2ByOption)>();
        var faceModel = new Dictionary<bool, FmdlModel>();
        for (int stage = 1; stage <= 3; stage++)
        {
            string pkg = $"/Assets/tpp/pack/player/fova/plfova_{setPrefix}_s{stage}";
            var fpk = new FoxPackage();
            var fpkd = new FoxPackage { IsFpkd = true };
            var pack = new TexturePack();
            var faces = new Dictionary<bool, Fv2File.ModelAttach>();
            foreach (bool bandana in anyBandana ? new[] { false, true } : new[] { false })
            {
                int face = stage - 1 + (bandana ? 4 : 0);
                string vp = string.Format(FacePkg, face);
                var vfpk = FoxPackage.Read(game.Read(vp + ".fpk"));
                var vfv2 = vfpk.Find(string.Format(FaceFv2, face)) ?? throw new InvalidDataException($"{vp}.fpk has no face .fv2.");
                var attach = new Fv2File(vfv2.Data).Attachments();
                if (attach.Count != 1) throw new InvalidDataException($"{vp}: the face .fv2 does not attach one model.");
                faces[bandana] = attach[0];
                if (stage == 1)
                    faceModel[bandana] = FmdlModel.Read(vfpk.Files.FirstOrDefault(f => f.Path.EndsWith("_cov.fmdl", StringComparison.Ordinal))?.Data
                        ?? throw new InvalidDataException($"{vp}.fpk has no face model."));
                foreach (var f in vfpk.Files) if (fpk.Find(f.Path) == null) fpk.Files.Add(f);
                if (game.TryRead(vp + ".fpkd") is { } vd)
                    foreach (var f in FoxPackage.Read(vd).Files) if (fpkd.Find(f.Path) == null) fpkd.Files.Add(f);
                if (game.TryRead(vp + ".pftxs") is { } vt)
                    foreach (var t in TexturePack.Read(vt).Textures) if (pack.Find(t.PathCode) == null) pack.Textures.Add(t);
            }
            foreach (var t in extraTextures) if (pack.Find(t.PathCode) == null) pack.Textures.Add(t);
            foreach (var (id, path) in modelPath) fpk.Files.Add(new PackageFile(path, modelData[id]));

            var fv2s = new Dictionary<string, string>();
            foreach (var o in options)
            {
                var list = new List<Fv2File.ModelAttach> { faces[o.Bandana] };
                foreach (var mid in o.Models)
                    list.Add(new Fv2File.ModelAttach(FoxHash.PathCode64(modelPath.TryGetValue(mid, out var mp) ? mp : throw new ArgumentException($"Unknown model {mid}."))));
                string fv2Path = $"/Assets/tpp/fova/chara/sna/{setPrefix}_{o.Id}_s{stage}.fv2";
                fpk.Files.Add(new PackageFile(fv2Path, Fv2File.Attach(list).Write()));
                fv2s[o.Id] = fv2Path;
            }
            PackageOrder.ApplySnakeBiteOrder(fpk);
            PackageOrder.ApplySnakeBiteOrder(fpkd);
            files[pkg + ".fpk"] = fpk.Write();
            files[pkg + ".fpkd"] = fpkd.Write();
            files[pkg + ".pftxs"] = pack.Write();
            stageFiles[stage] = (pkg + ".fpk", fv2s);
        }

        // Avatar: V Framework 1.7 loads the head's avatar files through the game's Avatar head-option slot and keeps the
        // Avatar's own face (kHideAvatarCreatorFace is false). So the .fv2 attaches only the extra models, plus the
        // Avatar's own bandana for a bandana option (Snake's bandana is part of his face model).
        var avatarFv2 = new Dictionary<string, string>();
        string avatarPkg = $"/Assets/tpp/pack/player/fova/plfova_{setPrefix}_av";
        string avatarBandana = Naming.SnaModel(setPrefix, "avbandana");
        ulong? avatarBandanaSim = null;
        {
            var fpk = new FoxPackage();
            var fpkd = new FoxPackage { IsFpkd = true };
            var pack = new TexturePack();
            foreach (var t in extraTextures) pack.Textures.Add(t);
            foreach (var (id, path) in modelPath) fpk.Files.Add(new PackageFile(path, modelData[id]));
            if (anyBandana) avatarBandanaSim = AddAvatarBandana(game, avatarBandana, fpk, fpkd, pack, notes);
            foreach (var o in options)
            {
                var list = o.Models.Select(mid => new Fv2File.ModelAttach(FoxHash.PathCode64(modelPath[mid]))).ToList();
                if (o.Bandana) list.Insert(0, new Fv2File.ModelAttach(FoxHash.PathCode64(avatarBandana), avatarBandanaSim));
                string fv2Path = $"/Assets/tpp/fova/chara/sna/{setPrefix}_{o.Id}_av.fv2";
                fpk.Files.Add(new PackageFile(fv2Path, Fv2File.Attach(list).Write()));
                avatarFv2[o.Id] = fv2Path;
            }
            PackageOrder.ApplySnakeBiteOrder(fpk);
            PackageOrder.ApplySnakeBiteOrder(fpkd);
            files[avatarPkg + ".fpk"] = fpk.Write();
            files[avatarPkg + ".fpkd"] = fpkd.Write();
            files[avatarPkg + ".pftxs"] = pack.Write();
        }

        var items = new List<HeadOptionItem>();
        bool first = true;
        foreach (var o in options)
        {
            var (key, prefix) = p.NewItemNames(o.Id);
            string icon = iconFtexPath ?? $"/Assets/tpp/ui/texture/EquipIcon/head/ui_head_{prefix}_alp";
            byte[]? iconFtex = null;
            var iconFtexs = new Dictionary<string, byte[]>();
            if (iconFtexPath == null)
            {
                var img = Icon(o, models, faceModel[o.Bandana]);
                var ft = FtexWriter.Write(img, withAlpha: true, FtexKind.Linear);
                iconFtex = ft.Ftex;
                foreach (var (n, d) in ft.Ftexs) iconFtexs[$"{icon}.{n}.ftexs"] = d;
            }
            var stages = Enumerable.Range(1, 3).ToDictionary(s => s, s => (stageFiles[s].fv2ByOption[o.Id], stageFiles[s].fpk));
            var spec = new HeadOptionSpec
            {
                SnakeFaceOnly = true, Key = key, NameLang = $"{prefix}_name", RealNameLang = $"{prefix}_name", InfoLang = $"{prefix}_info",
                IconFtexPath = icon,
                // Worn with the bandana: deploys for the vanilla Bandana's GMP (user, Oct 25).
                Develop = new DevelopRow { InitiallyAvailable = true, GmpCost = 0, Grade = 1, UsageGmpCost = o.Bandana ? Knowledge.GameKnowledge.BandanaUsageGmp : 0 },
            };
            spec.Header.Add($"{o.Title}: Snake's {(o.Bandana ? "bandana " : "")}face with {string.Join(" + ", o.Models)}, one package per Demon stage.");
            spec.Branches[PlayerBranch.Snake] = new HeadBranch { Fv2 = stages[1].Item1, Fpk = stages[1].fpk, FaceStages = stages };
            if (avatarFv2.TryGetValue(o.Id, out var av))
            {
                spec.Branches[PlayerBranch.Avatar] = new HeadBranch { Fv2 = av, Fpk = avatarPkg + ".fpk" };
                spec.Header.Add($"{o.Title} (Avatar): the Avatar's own face with {string.Join(" + ", (o.Bandana ? o.Models.Prepend("the Avatar bandana") : o.Models))}.");
            }
            var item = new HeadOptionItem { Id = o.Id, Key = key, Prefix = prefix, Spec = spec, Title = o.Title };
            item.SetText($"{prefix}_name", o.Title);
            item.SetText($"{prefix}_info", o.Description);
            if (first) { foreach (var (path, data) in files) item.Files[path] = data; item.Notes.AddRange(notes); first = false; }
            if (iconFtex != null) { item.Files[icon + ".ftex"] = iconFtex; foreach (var (path, data) in iconFtexs) item.Files[path] = data; }
            p.Items.Add(item);
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Adds the Avatar's vanilla bandana to an Avatar head package: a copy of the Avatar face model with only the bandana
    /// group shown (at <paramref name="modelPath"/>), the face's cloth .sim and the bandana textures' small mips. Returns the
    /// .sim's path code for the .fv2 attach, or null (with a note) when the game has none.
    /// </summary>
    internal static ulong? AddAvatarBandana(GameData game, string modelPath, FoxPackage fpk, FoxPackage fpkd, TexturePack pack, List<string> notes)
    {
        ulong? sim = null;
        var avfpk = FoxPackage.Read(game.Read(AvatarFacePkg + ".fpk"));
        byte[] face = avfpk.Find(AvatarFaceModel)?.Data ?? throw new InvalidDataException($"{AvatarFacePkg}.fpk has no {AvatarFaceModel}.");
        fpk.Files.Add(new PackageFile(modelPath, FmdlModel.ShowOnlyGroups(face, new HashSet<int> { AvatarBandanaGroup })));
        // The cloth physics of the bandana tails: the vanilla face .fv2 attaches the face model with the one .sim of the
        // face .fpkd (avm0_type0_hair2.sim). The copy keeps the face's bones, so the same .sim drives its tails.
        if (game.TryRead(AvatarFacePkg + ".fpkd") is { } avd
            && FoxPackage.Read(avd).Files.Where(f => f.Path.EndsWith(".sim", StringComparison.Ordinal)).ToList() is [var simFile])
        {
            fpkd.Files.Add(simFile);
            sim = FoxHash.PathCode64(simFile.Path);
        }
        else notes.Add($"{AvatarFacePkg}: no cloth .sim found for the Avatar bandana; its tails do not move.");
        // The bandana textures (avm_bdn0): small mips from the Avatar face pack; the full .ftex files are in texture0.
        var fm = FmdlModel.Read(face);
        var codes = fm.Meshes.Where(x => x.MeshGroup == AvatarBandanaGroup).SelectMany(x => fm.Materials[x.MaterialIndex].Textures).Select(t => t.PathCode).ToHashSet();
        if (game.TryRead(AvatarFacePkg + ".pftxs") is { } avt)
            foreach (var t in TexturePack.Read(avt).Textures) if (codes.Contains(t.PathCode) && pack.Find(t.PathCode) == null) pack.Textures.Add(t);
        return sim;
    }

    /// <summary>
    /// Issue #126: adds Snake's vanilla bandana of Demon stage <paramref name="stage"/> (1-3) to a head package: a copy of
    /// the bandana face model (face4/5/6) in which only the bandana meshes (sna_bdn0 textures: knot band and tails) draw,
    /// at <paramref name="modelPath"/>, the face's cloth .sim and the bandana textures' small mips. Returns the .fv2 attach
    /// of the copy (with the .sim when the game has one).
    /// </summary>
    public static Fv2File.ModelAttach AddSnakeBandana(GameData game, int stage, string modelPath, FoxPackage fpk, FoxPackage fpkd, TexturePack pack, List<string> notes)
    {
        int face = stage + 3;
        string vp = string.Format(FacePkg, face);
        var vfpk = FoxPackage.Read(game.Read(vp + ".fpk"));
        byte[] data = vfpk.Files.FirstOrDefault(f => f.Path.EndsWith("_cov.fmdl", StringComparison.Ordinal))?.Data
            ?? throw new InvalidDataException($"{vp}.fpk has no face model.");
        var fm = FmdlModel.Read(data);
        var band = HeadIcon.Bandana(fm);
        if (band.Count == 0) throw new InvalidDataException($"{vp}: the face model has no bandana meshes.");
        if (fpk.Find(modelPath) == null) fpk.Files.Add(new PackageFile(modelPath, FmdlModel.KeepMeshes(data, band.Select(x => x.Index).ToHashSet())));
        // The tails' cloth: the vanilla face .fv2 attaches the face with its .sim. The copy keeps the face's bones.
        ulong? sim = null;
        var vfv2 = vfpk.Find(string.Format(FaceFv2, face));
        if (vfv2 != null && new Fv2File(vfv2.Data).Attachments() is [{ Sim: { } s }] && game.TryRead(vp + ".fpkd") is { } vd
            && FoxPackage.Read(vd).Files.FirstOrDefault(f => f.Path.EndsWith(".sim", StringComparison.Ordinal) && FoxHash.PathCode64(f.Path) == s) is { } simFile)
        {
            if (fpkd.Find(simFile.Path) == null) fpkd.Files.Add(simFile);
            sim = s;
        }
        else notes.Add($"{vp}: no cloth .sim found for the Snake bandana; its tails do not move.");
        var codes = band.SelectMany(x => fm.Materials[x.MaterialIndex].Textures).Select(t => t.PathCode).ToHashSet();
        if (game.TryRead(vp + ".pftxs") is { } vt)
            foreach (var t in TexturePack.Read(vt).Textures) if (codes.Contains(t.PathCode) && pack.Find(t.PathCode) == null) pack.Textures.Add(t);
        return new Fv2File.ModelAttach(FoxHash.PathCode64(modelPath), sim);
    }

    /// <summary>
    /// An option's icon: its extra models (and the bandana when it wears one) alone, in the head icon style their size
    /// calls for (measured against Snake's face of stage 1, which is not drawn).
    /// </summary>
    public static RgbaImage Icon(HeadAddonOption o, IReadOnlyList<HeadAddonModel> models, FmdlModel face)
    {
        var gear = new List<FmdlMesh>();
        foreach (var mid in o.Models)
        {
            var m = models.First(x => x.Id == mid);
            var fm = FmdlModel.Read(m.Fmdl);
            gear.AddRange(HeadIcon.Moved(fm.Meshes.Where(x => m.Groups.Contains(x.MeshGroup)), HeadIcon.ToFaceSpace(fm)));
        }
        if (o.Bandana) gear.AddRange(HeadIcon.Bandana(TailPose.Hang(face)));
        return HeadIcon.RenderGear(gear, HeadIcon.HeadSilhouette(face));
    }

    /// <summary>
    /// Vanilla Snake parts types that offer head options (the set Auditor's Debriefing mod extends with its head):
    /// fatigues, scarf fatigues, sneaking suits, naked, battle dress, leather, gold, silver and the MGS3 suits.
    /// </summary>
    public static IEnumerable<Knowledge.PartsTypeInfo> SuitsWithHeads() =>
        Knowledge.GameKnowledge.PartsTypes.Where(t => t.PartsType is { } n && n <= 18 && t.Packages.ContainsKey("SNAKE") && !t.NoHeadOptions);

    /// <summary>Lists the heads on every vanilla Snake suit that offers head options (ExtendVanillaOutfit by parts type).</summary>
    public static List<ExtraLooksItem> OfferOnVanillaSuits(ModProject p, string setId, IReadOnlyList<string> headKeys) =>
        OfferOnVanillaSuits(p, setId, headKeys, PlayerBranch.Snake);

    /// <summary>
    /// The vanilla suits a character can wear that offer head options (a package for that character, no built-in or
    /// covered head; parts types up to 18, the ones ExtendVanillaOutfit takes).
    /// </summary>
    public static IEnumerable<Knowledge.PartsTypeInfo> SuitsWithHeads(PlayerBranch who)
    {
        string pk = who switch { PlayerBranch.DdMale => "DD_MALE", PlayerBranch.DdFemale => "DD_FEMALE", PlayerBranch.Avatar => "AVATAR", _ => "SNAKE" };
        return Knowledge.GameKnowledge.PartsTypes.Where(t => t.PartsType is { } n && n <= 18 && t.Packages.ContainsKey(pk) && !t.NoHeadOptions);
    }

    /// <summary>Lists the heads on every vanilla suit of <paramref name="who"/> that offers head options (ExtendVanillaOutfit by parts type).</summary>
    public static List<ExtraLooksItem> OfferOnVanillaSuits(ModProject p, string setId, IReadOnlyList<string> headKeys, PlayerBranch who)
    {
        var items = new List<ExtraLooksItem>();
        string tag = who switch { PlayerBranch.DdMale => "m", PlayerBranch.DdFemale => "f", PlayerBranch.Avatar => "a", _ => "" };
        foreach (var t in SuitsWithHeads(who))
        {
            var x = new ExtraLooksItem
            {
                Id = $"{setId}_{tag}{t.Name.ToLowerInvariant()}", Prefix = $"{setId}_{tag}{t.PartsType}", Title = $"Heads on {t.Name} ({LuaWriter.BranchName(who)})",
                Spec = new ExtendVanillaSpec { PartsType = t.PartsType },
            };
            x.Spec.Header.Add($"{t.Name}: the heads offered on the vanilla suit ({LuaWriter.BranchName(who)}).");
            x.Spec.Branches[who] = new VanillaBranch { HeadOptions = headKeys.ToList() };
            p.Items.Add(x);
            items.Add(x);
        }
        return items;
    }
}
