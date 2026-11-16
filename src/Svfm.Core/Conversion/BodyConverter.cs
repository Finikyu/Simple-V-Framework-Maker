using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>Settings for turning a body replacer (a mod that changes a vanilla plparts_* package) into its own outfit.</summary>
public sealed record BodyConversionOptions
{
    /// <summary>Short lower-case ID, e.g. "pninja".</summary>
    public required string Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? RealName { get; init; }
    public required string Description { get; init; }
    /// <summary>Behaviour preset ID (GameKnowledge.Presets), e.g. "cyborg_ninja".</summary>
    public string Preset { get; init; } = "fatigues";
    /// <summary>Who can wear it. Snake also covers Avatar (V Framework shares their data).</summary>
    public IReadOnlyList<PlayerBranch> Bodies { get; init; } = [PlayerBranch.Snake];
    /// <summary>null = detect: a model with its own face/hair hides the character head.</summary>
    public bool? EnableHead { get; init; }
    /// <summary>null = detect: off when the vanilla suit has no bionic arm of its own (MGS1 suit) or the head is built in.</summary>
    public bool? EnableArm { get; init; }
    public IconChoice Icon { get; init; } = new IconChoice.Render();
    /// <summary>Heads offered; null = the character's vanilla heads, or none when the body has its own head.</summary>
    public IReadOnlyList<string>? HeadOptions { get; init; }
    public PathDictionary? Dictionary { get; init; }
    public string ModName { get; init; } = "";
    public string ModVersion { get; init; } = "1.0";
    public string ModAuthor { get; init; } = "";
    public string ModDescription { get; init; } = "";
    public DevelopRow Develop { get; init; } = new();
    /// <summary>Add Snake's lip-sync and facial setting data when the body's .parts has none (null = yes).</summary>
    public bool? AddFaceAnimation { get; init; }
    /// <summary>Take head models the mod put inside the body package out of the port (they become head options; see HeadsInside).</summary>
    public bool HeadsAsHeadOptions { get; init; } = true;
    /// <summary>
    /// Offer a DD fatigues port's camos as colours in Sortie Prep's palette (step 7b). Off by default: on the IGA Suit
    /// the colour menu appeared but no pick changed the outfit (user, Oct 26), and it is a menu few players use; kept
    /// for later research.
    /// </summary>
    public bool CamoColours { get; init; }
    /// <summary>
    /// Copy the mod's textures at its own (new) paths under this port's names too. For a mod whose optional downloads (camo
    /// sets) ship different textures at the same paths: each download ported as its own look needs its own copies.
    /// </summary>
    public bool OwnTextureCopies { get; init; }
}

/// <summary>A vanilla body package a mod replaces.</summary>
public sealed record BodyReplacement(string VanillaPackage, PartsTypeInfo? PartsType);

/// <summary>
/// Converter for body replacers (Parasite Ninja on the MGS1 suit, PW Battle Dress …). Recipe from the research:
/// <list type="number">
/// <item>The port's packages = the vanilla body package with the mod's files merged over it (what SnakeBite and
/// DMM do on install), under a new name. Files inside keep their paths: the .parts at its vanilla path inside
/// the renamed .fpkd works in game (the user's MSF GZ Sneaking Suit mod does the same).</item>
/// <item>The .fox2 loads the texture pack named like the package (empty texturePackPath), so the mod's .pftxs is
/// renamed with the package. Vanilla blocks the mod's pack dropped are added back (vanilla effects in the
/// package still use them).</item>
/// <item>Loose textures at new paths are copied as is; a loose texture at a vanilla path is copied whole under a new
/// name and the models pointed at it (InstalledTextures), as shipping it there would change the vanilla game.</item>
/// <item>A mod that ships only the suit's .pftxs (a retexture) is a body replacer too.</item>
/// <item>A model with its own face or hair gets <c>enableHead = false</c>.</item>
/// </list>
/// </summary>
public static class BodyConverter
{
    private const string Parts = "/Assets/tpp/pack/player/parts/";
    /// <summary>Snake's lip-sync file and its data-set string hash (from vanilla sna0_main0_def_v00.parts).</summary>
    public const string LipAdjustPath = "/Assets/tpp/motion/lip_adjust_data/sna0.ladb";
    public const ulong LipAdjustHash = 0x27D39D6EB8C1;
    /// <summary>Snake's facial setting file and its data-set string hash (same source).</summary>
    public const string FacialSettingPath = "/Assets/tpp/motion/facial_setting_data/tppPlayer.fsd";
    public const ulong FacialSettingHash = 0xA3A11FB2EBFD;

    /// <summary>Quiet's lip-sync file and hash (vanilla quip_main0_def_v00.parts, packed in plparts_quiet.fpk).</summary>
    public const string QuietLipAdjustPath = "/Assets/tpp/motion/lip_adjust_data/avf.ladb";
    public const ulong QuietLipAdjustHash = 0xD1725E15724B;

    /// <summary>
    /// The face animation files a body needs, with the vanilla package each is copied from. Every vanilla player body
    /// except SOLID SNAKE, Ninja and Raiden has both (audit of all 39 packages); Snake, Avatar, DD soldiers and Ocelot
    /// use sna0.ladb, Quiet avf.ladb (hospital Snake sna1.ladb), all tppPlayer.fsd.
    /// </summary>
    private static (string prop, string path, ulong hash, string file, bool inFpkd, string source)[] FaceAnimationFiles(bool quiet) =>
    [
        quiet ? ("lipAdjustBinaryFile", QuietLipAdjustPath, QuietLipAdjustHash, "avf.ladb", false, "plparts_quiet")
              : ("lipAdjustBinaryFile", LipAdjustPath, LipAdjustHash, "sna0.ladb", false, "plparts_normal"),
        ("facialSettingFile", FacialSettingPath, FacialSettingHash, "tppPlayer.fsd", true, "plparts_normal"),
    ];

    /// <summary>
    /// The vanilla body packages this mod changes: their .fpk, .fpkd or texture pack (a retexture that ships only the
    /// suit's .pftxs, as GZ Sneaking Suit Black does, is a body replacer too: the port is the vanilla package with that pack).
    /// </summary>
    public static List<BodyReplacement> Detect(GameData game, MgsvMod mod)
    {
        var found = DetectPackages(game, mod);
        return found.Count > 0 ? found : DetectRetexture(game, mod);
    }

    /// <summary>
    /// A mod that ships only loose textures (no package, no model) and whose textures a vanilla body's models use is a
    /// retexture of that suit (Faithful MGS1 Cyborg Ninja: the vanilla ninja's nin1_* textures). The body with the most
    /// of the mod's textures wins; the port is that vanilla package with the mod's textures moved to new names
    /// (InstalledTextures). Mods that ship textures at new paths only (a download another mod needs) match nothing.
    /// </summary>
    public static List<BodyReplacement> DetectRetexture(GameData game, MgsvMod mod)
    {
        var files = mod.ArchiveFiles.Keys.Where(k => !k.StartsWith("/Assets/tpp/ui/", StringComparison.Ordinal)).ToList();
        if (files.Count == 0 || !files.All(k => k.Contains(".ftex", StringComparison.Ordinal))) return [];
        var codes = files.Select(k => Hashing.FoxHash.PathCode64(TextureCheck.FtexOf(k))).ToHashSet();
        var best = new List<(string pkg, int n)>();
        foreach (var pkg in GameKnowledge.PartsTypes.SelectMany(t => t.Packages.Values).Select(v => v[..^4]).Distinct())
        {
            var parts = new Dictionary<string, byte[]>();
            if (game.TryRead(pkg + ".fpk") is { } a) parts[pkg + ".fpk"] = a; else continue;
            if (game.TryRead(pkg + ".fpkd") is { } b) parts[pkg + ".fpkd"] = b;
            int n = TextureCheck.Referenced(parts).Count(codes.Contains);
            if (n > 0) best.Add((pkg, n));
        }
        if (best.Count == 0) return [];
        int top = best.Max(x => x.n);
        // The mod must be about this suit: most of its textures are the suit's.
        if (top * 2 < codes.Count) return [];
        var pick = best.Where(x => x.n == top).Select(x => x.pkg).OrderBy(x => x, StringComparer.Ordinal).First();
        return [new BodyReplacement(pick, GameKnowledge.PartsTypes.FirstOrDefault(t => t.Packages.Values.Contains(pick + ".fpk")))];
    }

    private static List<BodyReplacement> DetectPackages(GameData game, MgsvMod mod) =>
        mod.ArchiveFiles.Keys
            .Where(p => p.StartsWith(Parts, StringComparison.Ordinal) && (p.EndsWith(".fpk", StringComparison.Ordinal) || p.EndsWith(".fpkd", StringComparison.Ordinal) || p.EndsWith(".pftxs", StringComparison.Ordinal)))
            .Select(p => p[..p.LastIndexOf('.')]).Distinct()
            .Where(pkg => game.Contains(pkg + ".fpk"))
            .Select(pkg => new BodyReplacement(pkg, GameKnowledge.PartsTypes.FirstOrDefault(t => t.Packages.Values.Contains(pkg + ".fpk"))))
            .ToList();

    public static ConversionResult Convert(GameData game, MgsvMod mod, BodyReplacement body, BodyConversionOptions o)
    {
        string id = o.Id.ToLowerInvariant();
        var project = new ModProject
        {
            Id = id,
            Name = o.ModName.Length > 0 ? o.ModName : $"{o.Name} (V Framework)",
            Version = o.ModVersion,
            Author = o.ModAuthor.Length > 0 ? o.ModAuthor : mod.Metadata.Author,
            Description = o.ModDescription.Length > 0 ? o.ModDescription
                : $"{o.Name}: a new outfit made from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}. " +
                  "The vanilla suit is unchanged.\r\n\r\nRequires Infinite Heaven, IHHook and V Framework 1.7.",
        };
        var (item, icon) = ToItem(game, mod, body, o, $"svfm_{id}");
        project.Items.Add(item);
        var built = ProjectBuilder.Build(project, game);
        var result = new ConversionResult { Mod = built.Mod, Outfit = item.Spec, Lua = built.Lua, Icon = icon };
        result.Notes.AddRange(item.Notes);
        result.Notes.AddRange(built.Errors);
        return result;
    }

    public static (OutfitItem item, RgbaImage? icon) ToItem(GameData game, MgsvMod mod, BodyReplacement body, BodyConversionOptions o, string prefix)
    {
        string id = o.Id.ToLowerInvariant();
        Naming.CheckId(id);
        var notes = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string van = body.VanillaPackage;
        string pkg = $"{Parts}plparts_{prefix}";

        // 1. Packages: vanilla + the mod's files over it, as the managers merge them.
        var fpk = Merge(game, mod, van + ".fpk", isFpkd: false, notes);
        // Head models a mod hides in a body package (the balaclava model at its vanilla path, so the balaclava shows
        // the mod's helmet while this body is loaded) become head options instead (HeadsInside).
        if (o.HeadsAsHeadOptions)
            foreach (var f in fpk.Files.Where(f => HeadModelBody(f.Path) != null).ToList())
            {
                fpk.Files.Remove(f);
                notes.Add($"{f.Path}: a head model inside the body; offered as a head option instead.");
            }
        var fpkd = Merge(game, mod, van + ".fpkd", isFpkd: true, notes);
        files[pkg + ".fpk"] = fpk.Write();
        files[pkg + ".fpkd"] = fpkd.Write();

        // 2. Texture pack, renamed with the package; vanilla blocks it dropped are added back.
        var vanillaPack = game.TryRead(van + ".pftxs") is { } vp ? TexturePack.Read(vp) : null;
        TexturePack? pack = mod.ArchiveFiles.TryGetValue(van + ".pftxs", out var mp) ? TexturePack.Read(mp) : vanillaPack;
        if (pack != null)
        {
            if (vanillaPack != null && pack != vanillaPack)
            {
                int added = 0;
                foreach (var t in vanillaPack.Textures.Where(t => pack.Find(t.PathCode) == null)) { pack.Textures.Add(t); added++; }
                if (added > 0) notes.Add($"Added back {added} vanilla texture(s) the mod's pack left out.");
            }
            files[pkg + ".pftxs"] = pack.Write();
        }

        // 3. Loose textures: new paths used by the mod's models are copied; vanilla paths are refused; files of a
        // separately installed dependency are read, not copied.
        var loose = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // The DD fatigues camo packages this body wears (offered as colours, step 7b): not "left out".
        var camoPackages = o.Bodies.Distinct().SelectMany(who => FatiguesCamos(body, who)).Select(c => c.package).ToHashSet(StringComparer.Ordinal);
        var referenced = TextureCheck.Referenced(mod.ArchiveFiles);
        // A retexture that ships no package: the suit's own models say which textures matter.
        if (!mod.ArchiveFiles.Keys.Any(k => k.StartsWith(van + ".", StringComparison.Ordinal)))
            referenced.UnionWith(TextureCheck.Referenced(new Dictionary<string, byte[]> { [van + ".fpk"] = game.Read(van + ".fpk"), [van + ".fpkd"] = game.Read(van + ".fpkd") }));
        bool VanillaTexture(string ftex) => InstalledTextures.IsVanillaPath(game, ftex) || vanillaPack?.Find(Hashing.FoxHash.PathCode64(ftex)) != null;
        var external = new HashSet<string>(StringComparer.Ordinal);
        int unused = 0;
        foreach (var (path, data) in mod.ArchiveFiles)
        {
            if (path.StartsWith(van + ".", StringComparison.Ordinal)) continue;
            if (path.LastIndexOf('.') is int dot and > 0 && camoPackages.Contains(path[..dot])) continue;
            if (path.StartsWith("/Assets/tpp/ui/", StringComparison.Ordinal))
            {
                // Icons and other UI pictures: never copied (a mod's icon sits at the vanilla icon's path; the port makes
                // its own icon, which can be made from this one with IconChoice.ModIcon).
                if (path.EndsWith(".ftex", StringComparison.Ordinal)) notes.Add($"{path}: UI picture (a vanilla icon path); not copied.");
                continue;
            }
            if (path.Contains(".ftex", StringComparison.Ordinal))
            {
                string ftex = TextureCheck.FtexOf(path);
                loose[path] = data;   // for the rendered icon, whatever ships
                if (!mod.Ships(path)) { external.Add(mod.ExternalFiles[path]); continue; }
                // A vanilla path: moved as one complete texture under a new name below (InstalledTextures), never shipped there.
                if (VanillaTexture(ftex)) continue;
                // Only what the mod's models use: a texture download shared by several outfits ships per outfit.
                if (!referenced.Contains(Hashing.FoxHash.PathCode64(ftex))) { unused++; continue; }
                if (o.OwnTextureCopies) continue;   // copied under the port's names below
                files[path] = data;
            }
            else if (ModClassifier.KindOf(path, mod) is not (ModFileKind.Body or ModFileKind.DdHead or ModFileKind.SnakeFace or ModFileKind.BodyPhysics))
                notes.Add($"{path}: not part of the body; left out.");
        }

        // Textures the mod installs at vanilla paths: complete copies under new names, the models pointed at them.
        {
            var moved = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var relocated = InstalledTextures.Collect(game, mod, referenced, prefix,
                mod.ArchiveFiles.TryGetValue(van + ".pftxs", out var modPackBytes) ? TexturePack.Read(modPackBytes) : null, vanillaPack, moved, notes, o.OwnTextureCopies);
            foreach (var (path, data) in moved)
                if (!files.ContainsKey(path)) { files[path] = data; loose[path] = data; }
            InstalledTextures.Apply(fpk, relocated, notes);
            InstalledTextures.Apply(fpkd, relocated, notes);
            if (pack != null && InstalledTextures.RestoreVanilla(pack, vanillaPack, relocated)) files[pkg + ".pftxs"] = pack.Write();
            files[pkg + ".fpk"] = fpk.Write();
            files[pkg + ".fpkd"] = fpkd.Write();
        }
        if (unused > 0) notes.Add($"{unused} loose texture file(s) no model of the mod uses; left out.");
        foreach (var e in external) notes.Add($"Textures from \"{e}\" are not copied: it must be installed with this mod (required dependency).");

        // 4. Model checks: built-in head, parts path.
        var model = SuitRender.MainModel(fpk) ?? throw new InvalidDataException("The body package has no model.");
        // A port behaves as the replacer did. The game loads no separate face for SOLID SNAKE, Ninja and Raiden, so their
        // replacers draw the head in the body model; every other vanilla body gets the character's face, so a replacer
        // of one showed that face too, even if its model has face-like materials.
        // Ocelot too: both head values worked in game on Ocelot (Nov 15), and only false keeps Snake's face off the icon.
        bool builtIn = body.PartsType?.BuiltInHead == true;
        bool looksOwn = HasOwnHead(model);
        bool ownHead = body.PartsType != null ? builtIn : looksOwn;
        bool enableHead = o.EnableHead ?? !ownHead;
        // A body that models its own left forearm and hand (both arms reach as far) needs no bionic arm: the vanilla
        // Snake bodies stop at the left elbow (0.51 m vs 0.92 m) because the arm is a separate model. V Framework 1.7
        // has one switch for the arm: enableArm = false hides the bionic arm and also turns its functions off.
        bool ownArm = HasOwnLeftArm(model);
        bool enableArm = o.EnableArm ?? !(ownHead || ownArm || van.EndsWith("plparts_mgs1", StringComparison.Ordinal));
        if (o.EnableArm == null && ownArm && !ownHead && o.Bodies.Any(b => b is PlayerBranch.Snake or PlayerBranch.Avatar))
            notes.Add("The model has its own left arm and hand: the bionic arm is hidden (enableArm = false), which in V Framework 1.7 also turns the bionic arm's functions off.");
        if (o.EnableHead == null && ownHead)
            notes.Add(builtIn
                ? $"{body.PartsType!.Name} has no separate face (the head is in the body model): the character's head is hidden (enableHead = false)."
                : "The model has its own face/hair: the character's head is hidden (enableHead = false).");
        else if (o.EnableHead == null && looksOwn)
            notes.Add("The model has face or hair materials, but the replaced suit shows the character's face, so the port keeps it " +
                      "(as the replacer did). Turn the head off if the model really has its own face.");
        var partsFile = fpkd.Files.SingleOrDefault(f => f.Extension == "parts")
            ?? throw new InvalidDataException($"{van}.fpkd does not hold exactly one .parts file.");

        // The mod's .parts is kept as is. (Clearing its facialSettingFile, as vanilla Raiden has it, was tried in
        // Parasite Ninja 0.2 and did not stop the mission crash: that crash is V Framework 1.7 running a body with a
        // built-in head on a custom parts type, which also happens with the untouched vanilla SOLID SNAKE body.)
        byte[] partsData = partsFile.Data;

        // Face animation data: every vanilla body that wears Venom's separate face sets lipAdjustBinaryFile = sna0.ladb
        // (packed in the .fpk) and facialSettingFile = tppPlayer.fsd (packed in the .fpkd). SOLID SNAKE and Ninja leave
        // the lip sync empty, Raiden both. V Framework 1.7 sets custom outfits up like the fatigues, which expect both:
        // without lip sync the game crashes on deploy (tested: SOLID SNAKE and Parasite Ninja work once it is added),
        // and lip sync without the facial setting crashes in PluginFacial_ApplyMotion (tested on Raiden).
        if (o.AddFaceAnimation ?? true)
        {
            var ds = FoxDataSet.Read(partsData);
            var added = new List<string>();
            bool quiet = o.Bodies.Count > 0 && o.Bodies.All(b => b == PlayerBranch.Quiet);
            foreach (var (prop, path, hash, file, inFpkd, source) in FaceAnimationFiles(quiet))
            {
                if (!ds.StringValues("ModelDescription", prop).Any(v => v.value == "")) continue;
                ds = ds.WithString("ModelDescription", prop, path, hash);
                var target = inFpkd ? fpkd : fpk;
                if (target.Find(path) == null
                    && game.TryRead($"{Parts}{source}{(inFpkd ? ".fpkd" : ".fpk")}") is { } normal
                    && FoxPackage.Read(normal).Find(path) is { } vanillaFile)
                {
                    target.Files.Add(new PackageFile(path, vanillaFile.Data));
                    PackageOrder.ApplySnakeBiteOrder(target);
                }
                added.Add(file);
            }
            if (added.Count > 0)
            {
                partsData = ds.Data;
                files[pkg + ".fpk"] = fpk.Write();
                notes.Add($"Added Snake's face animation data ({string.Join(", ", added)}), which V Framework outfits need.");
            }
        }

        // 4a. A .parts that differs from the vanilla one gets its own path, so it never stands in for the vanilla
        // suit's file when both packages are loaded.
        string partsPath = partsFile.Path;
        var vanillaFpkd = game.TryRead(van + ".fpkd") is { } vf ? FoxPackage.Read(vf) : null;
        var vanillaParts = vanillaFpkd?.Find(partsFile.Path);
        if (vanillaParts == null || !vanillaParts.Data.AsSpan().SequenceEqual(partsData))
            partsPath = $"{partsFile.Path[..(partsFile.Path.LastIndexOf('/') + 1)]}{prefix}_v00.parts";
        fpkd.Files[fpkd.Files.IndexOf(partsFile)] = new PackageFile(partsPath, partsData);
        PackageOrder.ApplySnakeBiteOrder(fpkd);
        files[pkg + ".fpkd"] = fpkd.Write();

        // 5. Text.
        string L(string what) => $"{prefix}_{what}";
        var text = new Dictionary<string, string> { [L("name")] = o.Name, [L("real")] = o.RealName ?? o.Name, [L("info")] = o.Description };

        // 6. Icon, rendered from the port's own files.
        string iconPath = $"/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_{prefix}_alp";
        RgbaImage? icon = null;
        if (o.Icon is IconChoice.Render rr)
        {
            var blank = new List<ulong>();
            icon = SuitRender.Icon(game, o.Dictionary, new SuitRenderInput
            {
                BodyPackage = pkg, BodyFpk = fpk, BodyPack = pack,
                ModFiles = loose,
                // The face the character actually wears: Snake's own, or a DD soldier's vanilla soldier face. Using
                // Snake's face on a DD body drew the wrong head (and, for a DD woman, the wrong size).
                HeadPackage = !enableHead ? null
                    : o.Bodies.FirstOrDefault() is PlayerBranch.DdMale or PlayerBranch.DdFemale
                        ? SuitRender.SoldierFace(o.Bodies[0], game)
                        : SuitRender.SnakeFace,
                // A body only DD women wear gets her framing (her own body is rendered). A chosen framing moves it; before
                // issue #9 a chosen framing replaced it, so a nudge of the framing also lost her framing.
                Framing = FemaleOnly(body, o.Bodies) ? Rendering.IconFraming.DdFemale with { OffsetX = rr.Framing?.OffsetX ?? 0f, OffsetY = rr.Framing?.OffsetY ?? 0f, Zoom = rr.Framing?.Zoom ?? 1f } : rr.Framing,
                Lighting = rr.Lighting,
            }, missing: blank);
            // Vanilla textures the dictionary names are in the game even when only part of it was given.
            if (!game.IsComplete) blank.RemoveAll(c => (o.Dictionary ?? PathDictionary.Bundled).TryGetPath(c)?.StartsWith("/Assets/tpp/", StringComparison.Ordinal) == true);
            if (blank.Count > 0)
                notes.Add($"The rendered icon is missing {blank.Count} texture{(blank.Count == 1 ? "" : "s")} ({string.Join(", ", blank.Take(4).Select(c => o.Dictionary?.TryGetPath(c) ?? c.ToString("x16")))}{(blank.Count > 4 ? ", …" : "")}): neither the mod nor the game has them, so the model will flicker in game. Add the mod's texture files.");
            var ft = SuitIcon.ToGameFiles(icon);
            files[iconPath + ".ftex"] = ft.Ftex;
            foreach (var (n, d) in ft.Ftexs) files[$"{iconPath}.{n}.ftexs"] = d;
        }
        else if (o.Icon is IconChoice.ModIcon mi && mod.ArchiveFiles.TryGetValue(mi.FtexPath.EndsWith(".ftex") ? mi.FtexPath : mi.FtexPath + ".ftex", out var mft))
        {
            string bp = mi.FtexPath.EndsWith(".ftex") ? mi.FtexPath[..^5] : mi.FtexPath;
            var parts = new Dictionary<int, byte[]>();
            for (int k = 1; k <= 6; k++) if (mod.ArchiveFiles.TryGetValue($"{bp}.{k}.ftexs", out var d)) parts[k] = d;
            icon = FtexTexture.Read(mft).DecodeLargestAvailable(parts);
            var ft = SuitIcon.ToGameFiles(icon);
            files[iconPath + ".ftex"] = ft.Ftex;
            foreach (var (n, d) in ft.Ftexs) files[$"{iconPath}.{n}.ftexs"] = d;
        }
        else if (o.Icon is IconChoice.Picture pic)
        {
            icon = pic.Icon();
            var ft = SuitIcon.ToGameFiles(icon);
            files[iconPath + ".ftex"] = ft.Ftex;
            foreach (var (n, d) in ft.Ftexs) files[$"{iconPath}.{n}.ftexs"] = d;
        }
        else if (o.Icon is IconChoice.GameIcon gi)
        {
            iconPath = VanillaIcons.Check(game, o.Bodies.All(b => b == PlayerBranch.Quiet) && gi.FtexPath.Contains("/buddy/") ? IconKind.QuietCostume : IconKind.Suit, gi.FtexPath);
            icon = game.TryReadTexture(iconPath + ".ftex");
        }
        else if (o.Icon is not IconChoice.Vanilla)
            throw new ArgumentException("Body ports make their icon by rendering, from the mod's own icon, or use the vanilla suit's.");
        else iconPath = body.PartsType?.Icon ?? iconPath;

        // 7. Outfit. A suit without head options in vanilla (built-in or covered head) gets none.
        if (body.PartsType?.NoHeadOptions == true && o.HeadOptions is { Count: > 0 })
            notes.Add($"{body.PartsType.Name} has no heads worn in vanilla (the head is built in or covered): the heads asked for are left out.");
        var preset = GameKnowledge.Preset(o.Preset);
        var look = new Look { PartsPath = partsPath, FpkPath = pkg + ".fpk", EnableHead = enableHead ? null : false, EnableArm = enableArm ? null : false };
        var spec = new OutfitSpec
        {
            Key = o.Key, NameLang = L("name"), RealNameLang = L("real"), InfoLang = L("info"),
            IconFtexPath = iconPath, Develop = o.Develop,
        };
        spec.Header.Add($"{o.Name}: V Framework outfit converted from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}.");
        spec.Header.Add($"Body: {van[(van.LastIndexOf('/') + 1)..]} → {pkg[(pkg.LastIndexOf('/') + 1)..]}. Behaves like: {preset.Label}.");
        // Issue #89: the replaced suit's camo values, then the Environments that the mod's description names.
        var camoStart = new Dictionary<string, int>(body.PartsType is { } pt ? GameKnowledge.CamoValuesOfParts(pt.Name) ?? new Dictionary<string, int>() : []);
        var camoTicked = GameKnowledge.FillCamoFromDescription(camoStart, body.PartsType is { } ptl
            ? CamoConverter.ModInfoText(mod, [.. GameKnowledge.Camos.Where(c => c.PlayerParts.Contains(ptl.Name)).Select(c => c.LangInfo), ptl.DevelopId is { } dv ? $"info_st_{dv}" : null])
            : null);
        foreach (var who in o.Bodies.Distinct())
            spec.Branches[who] = new OutfitBranch
            {
                // Issue #69: sparks come from the ported package, not from the preset.
                Base = look, Abilities = preset.Abilities with { SprintSpark = SprintSpark.In(files) },
                HeadOptions = body.PartsType?.NoHeadOptions == true || !enableHead ? [] : o.HeadOptions?.ToList() ?? VanillaHeads.For(who),
                CamoBonusValues = new Dictionary<string, int>(camoStart),
            };

        // 7b. A DD soldier's fatigues take the camo picked in Sortie Prep from one fova package per camo
        // (plfova_dds5/dds6_main0_ply_vNN). V Framework 1.7 gives a custom outfit no camo of its own, but its colour
        // palette can serve each camo package (colors.primary: fv2 + fpk). So the port offers every fatigues camo the
        // replacer showed: the mod's own version of a camo package copied under the port's name (CamoPort), the vanilla
        // package for camos the mod leaves alone.
        foreach (var who in o.Bodies.Distinct())
        {
            if (!o.CamoColours)
            {
                if (FatiguesCamos(body, who).Any())
                    notes.Add($"{LuaWriter.BranchName(who)}: the fatigues camos are not offered as Sortie Prep colours (off by default: the colour menu changed nothing in game on the IGA Suit).");
                continue;
            }
            var colours = CamoColours(game, mod, body, who, prefix, files, notes);
            if (colours.Count > 0) spec.Branches[who] = spec.Branches[who] with { PrimaryColors = colours };
        }

        var item = new OutfitItem { Id = id, Key = o.Key, Prefix = prefix, Spec = spec, Title = o.Name };
        item.CamoFromDescription.AddRange(camoTicked);
        foreach (var (p, d) in files) item.Files[p] = d;
        foreach (var (lid, t) in text) item.SetText(lid, t);
        item.Notes.AddRange(notes);
        // The port against the mod as installed (PortCheck): texture mixes are errors; the deliberate changes (the .parts
        // at its own path, face animation added) are listed.
        foreach (var c in PortCheck.Compare(game, mod, files, [(van, pkg)]))
            (c.Error ? item.Problems : item.Notes).Add(c.Text);
        return (item, icon);
    }

    /// <summary>The body the outfit's icon, name and women framing come from: the first body of the lowest character.
    /// This is the body that <see cref="Rendering.OutfitPreview.DefaultLook"/> picks once the outfit is built (issue #58).</summary>
    /// <param name="bodies">The bodies of one mod, in the order they are built.</param>
    /// <returns>The body and the character who wears it.</returns>
    public static (BodyReplacement body, PlayerBranch who) IconBody(IReadOnlyList<(BodyReplacement body, PlayerBranch who)> bodies) =>
        bodies.OrderBy(x => x.who).First();

    /// <summary>
    /// One outfit from a <see cref="BodyPlan"/>. Each character gets one branch. The first body of a character is its Base
    /// look, each later one a Variant with its own package. Bodies for different characters (PW Battle Dress: DD male on
    /// plparts_ddm_battledress, DD female on plparts_ddf_battledress) each get their own branch and package. When a
    /// character has more than one body, the Nth look is named by <c>{prefix}_look{N}_label</c> (issue #52). The outfit's
    /// name, text and icon come from the first body of the first character.
    /// </summary>
    public static (OutfitItem item, RgbaImage? icon) ToItemFromPlan(GameData game, MgsvMod mod,
        IReadOnlyDictionary<PlayerBranch, List<BodyReplacement>> plan, BodyConversionOptions o, string prefix)
    {
        if (plan.Count == 0) throw new ArgumentException("No bodies.");
        string LookName(int n) => $"{prefix}_look{n}_label";
        OutfitItem? item = null;
        RgbaImage? icon = null;
        // The Base of a character with Variants gets a look name too.
        void NameBase(PlayerBranch who, BodyReplacement body)
        {
            item!.SetText(LookName(1), SuitName(game, body));
            var first = item.Spec.Branches[who];
            item.Spec.Branches[who] = first with { Base = first.Base with { DisplayName = LookName(1) } };
        }
        foreach (var (who, list) in plan.OrderBy(p => p.Key))
        {
            for (int n = 1; n <= list.Count; n++)
            {
                var body = list[n - 1];
                if (item == null)
                {
                    (item, icon) = ToItem(game, mod, body, o with { Bodies = [who] }, prefix);
                    if (list.Count > 1) NameBase(who, body);
                    continue;
                }
                string sub = $"{prefix}_{LuaWriter.BranchName(who).ToLowerInvariant()}{(n > 1 ? n : "")}";
                var (other, _) = ToItem(game, mod, body, o with { Bodies = [who], Icon = new IconChoice.Vanilla() }, sub);
                var branch = other.Spec.Branches[who];
                if (n == 1)
                {
                    item.Spec.Branches[who] = branch;
                    if (list.Count > 1) NameBase(who, body);
                }
                else
                {
                    item.SetText(LookName(n), SuitName(game, body));
                    var variant = branch.Base with { DisplayName = LookName(n), HeadOptions = branch.HeadOptions };
                    var first = item.Spec.Branches[who];
                    item.Spec.Branches[who] = first with { Variants = [.. first.Variants, variant] };
                }
                foreach (var (path, data) in other.Files)
                    if (!path.Contains("/ui/texture/EquipIcon/", StringComparison.Ordinal)) item.Files[path] = data;
                item.Notes.AddRange(other.Notes.Select(m => $"{LuaWriter.BranchName(who)}: {m}"));
            }
        }
        // Snake and Avatar wear the same body package, so a Snake port is an Avatar port too (issue #45).
        if (item!.Spec.Branches.TryGetValue(PlayerBranch.Snake, out var snake) && !plan.ContainsKey(PlayerBranch.Avatar))
            item.Spec.Branches[PlayerBranch.Avatar] = PlayerBranches.AvatarFrom(snake);
        return (item, icon);
    }

    /// <summary>The name of a body's suit type for the look button: the game's name of the vanilla outfit, else the parts type spelled out.</summary>
    public static string SuitName(GameData game, BodyReplacement body)
    {
        static string Spelled(string raw) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').ToLowerInvariant());
        if (body.PartsType is not { } pt) return Spelled(Path.GetFileName(body.VanillaPackage).Replace("plparts_", ""));
        if (pt.DevelopId is { } d && GameKnowledge.EnglishName(game, GameKnowledge.VanillaDevelop(d)?.Name) is { Length: > 0 } n) return n;
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(pt.Name.Replace('_', ' ').ToLowerInvariant());
    }

    /// <summary>
    /// The fatigues camo packages (without extension) a DD soldier wears over this body: the camos of the vanilla DD
    /// fatigues (parts type NORMAL on the character's own package, plparts_dd_male / plparts_dd_female); none otherwise.
    /// </summary>
    public static IEnumerable<(CamoInfo camo, string package)> FatiguesCamos(BodyReplacement body, PlayerBranch who)
    {
        string? branch = who switch { PlayerBranch.DdMale => "DD_MALE", PlayerBranch.DdFemale => "DD_FEMALE", _ => null };
        if (branch == null || body.PartsType?.Name != "NORMAL" || !body.PartsType.Packages.TryGetValue(branch, out var own)
            || own != body.VanillaPackage + ".fpk") yield break;
        foreach (var c in GameKnowledge.Camos)
            if ((who == PlayerBranch.DdMale ? c.Dd?.Male : c.Dd?.Female) is { Length: > 0 } fpk && c.PlayerParts.Contains("NORMAL"))
                yield return (c, fpk[..^".fpk".Length]);
    }

    /// <summary>The colour palette of a DD fatigues port (step 7b): one colour per camo, the mod's camo packages ported.</summary>
    private static List<OutfitColor> CamoColours(GameData game, MgsvMod mod, BodyReplacement body, PlayerBranch who, string prefix,
        Dictionary<string, byte[]> files, List<string> notes)
    {
        var colours = new List<OutfitColor>();
        int own = 0, vanilla = 0;
        var missing = new List<string>();
        string tag = who == PlayerBranch.DdMale ? "ddm" : "ddf";
        foreach (var (camo, package) in FatiguesCamos(body, who))
        {
            bool shipped = mod.ArchiveFiles.Keys.Any(k => k.StartsWith(package + ".", StringComparison.Ordinal));
            if (!game.Contains(package + ".fpk") || !game.Contains(package + ".fpkd")) { if (shipped) missing.Add(camo.Name); continue; }
            if (shipped && CamoPort.ModReplaces(game, mod, package))
            {
                string number = package[(package.LastIndexOf('_') + 1)..];   // v00
                var port = CamoPort.Port(game, mod, package, $"/Assets/tpp/pack/player/fova/plfova_{prefix}_{tag}_{number}", []);
                foreach (var (p, d) in port.Files) files[p] = d;
                colours.Add(new OutfitColor(port.CamoFv2, port.CamoFpk, camo.Icon));
                own++;
            }
            else if (FoxPackage.Read(game.Read(package + ".fpk")).Files.FirstOrDefault(f => f.Extension == "fv2") is { } fv2)
            {
                colours.Add(new OutfitColor(fv2.Path, package + ".fpk", camo.Icon));
                vanilla++;
            }
        }
        if (colours.Count > 0)
            notes.Add($"{LuaWriter.BranchName(who)}: the fatigues camos as colours in Sortie Prep's palette: {own} from the mod's camo packages " +
                      $"(copied under the port's name), {vanilla} vanilla.");
        if (missing.Count > 0)
            notes.Add($"{LuaWriter.BranchName(who)}: {missing.Count} camo package(s) the mod ships are left out because the game data given " +
                      $"lacks their vanilla package ({string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? ", …" : "")}).");
        return colours;
    }

    /// <summary>
    /// One outfit from a body replacer and its optional downloads (camo sets that ship other textures at the same paths,
    /// as ToE PlaYeR's Hardened Battlegear and LRR Concealment: a main .mgsv with the body, six camo .mgsv to pick one
    /// from). Each download, installed over the main mod, becomes one look of the outfit (the first the base look, the
    /// rest V Framework variants), with its own package and its own copies of the shared textures
    /// (<see cref="BodyConversionOptions.OwnTextureCopies"/>). The icon is drawn from the first look.
    /// </summary>
    public static (OutfitItem item, RgbaImage? icon) ToItemWithLooks(GameData game, MgsvMod main, IReadOnlyList<(MgsvMod download, string label)> looks,
        BodyConversionOptions o, string prefix)
    {
        if (looks.Count == 0) throw new ArgumentException("No looks.");
        OutfitItem? item = null;
        RgbaImage? icon = null;
        var variants = new Dictionary<PlayerBranch, List<Look>>();
        for (int i = 0; i < looks.Count; i++)
        {
            var mod = MgsvMod.Combine([main, looks[i].download]);
            var found = Detect(game, mod);
            if (found.Count != 1) throw new InvalidDataException($"{looks[i].label}: expected one body package, found {found.Count}.");
            string sub = i == 0 ? prefix : $"{prefix}_l{i + 1}";
            var (it, ic) = ToItem(game, mod, found[0], o with { OwnTextureCopies = true, Icon = i == 0 ? o.Icon : new IconChoice.Vanilla() }, sub);
            string label = $"{prefix}_l{i + 1}_label";
            if (i == 0)
            {
                item = it; icon = ic;
                foreach (var who in it.Spec.Branches.Keys.ToList())
                    it.Spec.Branches[who] = it.Spec.Branches[who] with { Base = it.Spec.Branches[who].Base with { DisplayName = label } };
            }
            else
            {
                foreach (var (who, br) in it.Spec.Branches)
                    (variants.TryGetValue(who, out var l) ? l : variants[who] = []).Add(br.Base with { DisplayName = label });
                foreach (var (path, data) in it.Files)
                    if (!path.Contains("/ui/texture/EquipIcon/", StringComparison.Ordinal)) item!.Files[path] = data;
                item!.Notes.AddRange(it.Notes.Select(n => $"{looks[i].label}: {n}"));
                item.Problems.AddRange(it.Problems.Select(n => $"{looks[i].label}: {n}"));
            }
            item!.SetText(label, looks[i].label);
        }
        foreach (var (who, list) in variants)
            item!.Spec.Branches[who] = item.Spec.Branches[who] with { Variants = [.. item.Spec.Branches[who].Variants, .. list] };
        item!.Spec.Header.Add($"Looks: {string.Join(", ", looks.Select(l => l.label))} (one per optional download, each with its own texture copies).");
        return (item, icon);
    }

    /// <summary>True when the icon's body is worn by DD women only (the option's wearers, or a DD female package).</summary>
    internal static bool FemaleOnly(BodyReplacement body, IReadOnlyList<PlayerBranch> bodies) =>
        (bodies.Count > 0 && bodies.All(b => b == PlayerBranch.DdFemale))
        || GameKnowledge.IsFemaleOnlyPackage(body.VanillaPackage);

    /// <summary>Who wears a head model at this path (DD male dds5_head_*, DD female dds6_head_*), or null when it is no head model.</summary>
    public static PlayerBranch? HeadModelBody(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, @"/chara/dds/Scenes/dds([56])_head_[a-z0-9_]+_cov\.fmdl$");
        return !m.Success ? null : m.Groups[1].Value == "5" ? PlayerBranch.DdMale : PlayerBranch.DdFemale;
    }

    /// <summary>
    /// Head models the mod put inside a body package (VBSS, GRU, Haven Trooper helmet, Tactical Outfit: the balaclava
    /// model at its vanilla path), with a texture pack for each: the body's pack (the mod's, or vanilla) plus the small
    /// mips of loose textures the head uses that the mod ships.
    /// </summary>
    public static List<Project.DdHeadModel> HeadsInside(GameData game, MgsvMod mod, BodyReplacement body)
    {
        var list = new List<Project.DdHeadModel>();
        string van = body.VanillaPackage;
        if (!mod.ArchiveFiles.TryGetValue(van + ".fpk", out var fd)) return list;
        foreach (var f in FoxPackage.Read(fd).Files)
        {
            if (HeadModelBody(f.Path) is not { } who) continue;
            var pack = mod.ArchiveFiles.TryGetValue(van + ".pftxs", out var mp) ? TexturePack.Read(mp)
                : game.TryRead(van + ".pftxs") is { } vp ? TexturePack.Read(vp) : new TexturePack();
            var model = FmdlModel.Read(f.Data);
            foreach (var slot in model.Materials.SelectMany(m => m.Textures))
            {
                if (slot.PathCode == 0 || pack.Find(slot.PathCode) != null) continue;
                var ftex = mod.ArchiveFiles.Keys.FirstOrDefault(k => k.EndsWith(".ftex", StringComparison.Ordinal) && FoxHash.PathCode64(k) == slot.PathCode);
                if (ftex == null || !mod.Ships(ftex)) continue;   // a required dependency's texture loads from that mod
                var t = new PackedTexture { PathCode = slot.PathCode };
                t.Parts.Add(new TexturePart(FoxHash.PathCode64(ftex), mod.ArchiveFiles[ftex]));
                if (mod.ArchiveFiles.TryGetValue(ftex[..^5] + ".1.ftexs", out var p1)) t.Parts.Add(new TexturePart(FoxHash.PathCode64(ftex[..^5] + ".1.ftexs"), p1));
                pack.Textures.Add(t);
            }
            list.Add(new Project.DdHeadModel(who, f.Path, f.Data, pack.Write()));
        }
        return list;
    }

    /// <summary>A vanilla package with the mod's version merged over it (union, the mod wins, vanilla references kept).</summary>
    internal static FoxPackage Merge(GameData game, MgsvMod mod, string path, bool isFpkd, List<string> notes)
    {
        var baseBytes = game.TryRead(path);
        var merged = baseBytes != null ? FoxPackage.Read(baseBytes) : new FoxPackage { IsFpkd = isFpkd };
        if (mod.ArchiveFiles.TryGetValue(path, out var modBytes))
        {
            var m = FoxPackage.Read(modBytes);
            foreach (var f in m.Files)
            {
                int i = merged.Files.FindIndex(x => x.Path == f.Path);
                if (i >= 0) merged.Files[i] = f; else merged.Files.Add(f);
            }
            if (baseBytes == null) merged.References.AddRange(m.References);
        }
        foreach (var dropped in PackageOrder.ApplySnakeBiteOrder(merged))
            notes.Add($"{dropped.Path}: SnakeBite and DMM leave this file type out of {(isFpkd ? ".fpkd" : ".fpk")}; left out.");
        return merged;
    }

    /// <summary>
    /// What a body package the mod replaces leaves out of the vanilla one: meshes and materials the vanilla model has
    /// and the mod's does not. A mod that replaces a whole body package is expected to carry every part of it; one that
    /// drops meshes builds a suit with holes in it, which is what the owner saw with "MSF emblem undersuit 2 versions"
    /// ("it would let me build it, but in reality it shouldn't because it's missing so many parts").
    /// </summary>
    /// <param name="game">Game data (the vanilla package).</param>
    /// <param name="mod">The mod.</param>
    /// <param name="vanillaPackage">The package the mod replaces, without extension.</param>
    /// <returns>One line per model that lost meshes or materials; empty when the mod's models are complete.</returns>
    public static List<string> MissingParts(GameData game, MgsvMod mod, string vanillaPackage)
    {
        var missing = new List<string>();
        if (!mod.ArchiveFiles.TryGetValue(vanillaPackage + ".fpk", out var modBytes)) return missing;
        if (game.TryRead(vanillaPackage + ".fpk") is not { } vanBytes) return missing;
        FoxPackage modPkg, vanPkg;
        try { modPkg = FoxPackage.Read(modBytes); vanPkg = FoxPackage.Read(vanBytes); }
        catch (InvalidDataException) { return missing; }
        foreach (var vanFile in vanPkg.Files.Where(f => f.Extension == "fmdl"))
        {
            string leaf = vanFile.Path.Split('/').Last();
            // A model the mod does not ship at all is not a gap: the port merges the vanilla package, so it comes back
            // from the game (fxm_patlitecone and msk0_fthr0_sta are like this in the MSF emblem mod). Only a model the
            // mod does ship, and ships with parts left out, is a real hole.
            var modFile = modPkg.Files.FirstOrDefault(f => f.Path.EndsWith(leaf, StringComparison.Ordinal));
            if (modFile == null) continue;
            FmdlModel vanModel, modModel;
            try
            {
                vanModel = FmdlModel.Read(vanFile.Data);
                modModel = FmdlModel.Read(modFile.Data);
            }
            catch (InvalidDataException) { continue; }
            // Meshes are matched by index: a mod that rebuilds a model keeps the vanilla order for the parts it keeps.
            var modMeshes = modModel.Meshes.Select(m => m.Index).ToHashSet();
            int keptMeshes = vanModel.Meshes.Count(m => modMeshes.Contains(m.Index));
            int lostMeshes = vanModel.Meshes.Count - keptMeshes;
            var modMaterials = modModel.Materials.Select(m => m.Index).ToHashSet();
            int lostMaterials = vanModel.Materials.Count(m => !modMaterials.Contains(m.Index));
            // Only a model that is the vanilla one with parts taken out is a gap. A model that is a different character
            // altogether (the Haven Trooper on a DD battledress keeps 11 of 28 meshes) is a replacement, not a hole, and
            // reporting it would cry wolf on every such mod. Half the vanilla meshes is the line.
            if (lostMeshes == 0 && lostMaterials == 0) continue;
            if (keptMeshes * 2 < vanModel.Meshes.Count) continue;
            missing.Add($"{leaf}: {lostMeshes} of {vanModel.Meshes.Count} mesh(es) and {lostMaterials} of {vanModel.Materials.Count} material(s) missing");
        }
        return missing;
    }

    /// <summary>
    /// True when the model reaches as far on Snake's left (+X, the bionic-arm side) as on his right: the body has its
    /// own left forearm and hand. Vanilla Snake bodies reach 0.51 m on the left and 0.92 m on the right; bodies with
    /// both arms (DD bodies, "2 arms" mods) reach about 0.92 m on both sides.
    /// </summary>
    public static bool HasOwnLeftArm(FmdlModel m)
    {
        float left = 0, right = 0;
        foreach (var mesh in m.Meshes)
        {
            if (mesh.MeshGroup >= 0 && mesh.MeshGroup < m.MeshGroups.Count && m.MeshGroups[mesh.MeshGroup].hidden) continue;
            foreach (var p in mesh.Positions) { left = MathF.Max(left, p.X); right = MathF.Max(right, -p.X); }
        }
        return right > 0.3f && left >= 0.85f * right;
    }

    /// <summary>True when the model draws its own face or hair (materials or textures named for them).</summary>
    public static bool HasOwnHead(FmdlModel m)
    {
        static bool HeadWord(string? s) => s != null &&
            (s.Contains("face", StringComparison.OrdinalIgnoreCase) || s.Contains("hair", StringComparison.OrdinalIgnoreCase)
             || s.Contains("_eye", StringComparison.OrdinalIgnoreCase) || s.Contains("mout", StringComparison.OrdinalIgnoreCase));
        return m.Bone("SKL_004_HEAD") != null
            && m.Materials.Any(mat => HeadWord(mat.Name) || mat.Textures.Any(t => HeadWord(t.FileName)));
    }
}
