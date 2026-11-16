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
/// Avatar: V Framework uses the Snake branch for Avatar, so Avatar would wear Snake's face; options are Snake only.
/// </summary>
public static class SnakeHeadAddon
{
    private const string FacePkg = "/Assets/tpp/pack/player/fova/plfova_sna0_face{0}_v00";
    private const string FaceFv2 = "/Assets/tpp/fova/chara/sna/sna0_face{0}_v00.fv2";

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
            modelPath[m.Id] = $"/Assets/tpp/chara/sna/Scenes/{setPrefix}_{m.Id}.fmdl";
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
        if (o.Bandana) gear.AddRange(HeadIcon.Bandana(face));
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
        string tag = who switch { PlayerBranch.DdMale => "m", PlayerBranch.DdFemale => "f", _ => "" };
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
