using System.Buffers.Binary;
using System.Numerics;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;
using Svfm.Core.Project;
using Svfm.Core.Rendering;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>Settings for turning a chimera-receiver weapon replacer into its own V Framework weapon.</summary>
public sealed record WeaponConversionOptions
{
    /// <summary>Short lower-case ID, e.g. "banana".</summary>
    public required string Id { get; init; }
    /// <summary>English name; null = the mod's own text for the weapon (else the vanilla name). Other languages come from
    /// the mod's language files; <see cref="Texts"/> overrides any of them.</summary>
    public string? Name { get; init; }
    public string? RealName { get; init; }
    public string? Description { get; init; }
    /// <summary>Text set by the user, per language package ("eng", "fre" …) and field ("name", "real", "info"); wins over the mod's.</summary>
    public Dictionary<(string language, string field), string> Texts { get; init; } = [];
    /// <summary>Where the icon comes from: the mod's own icon when it has one, else one drawn from the mod's model.</summary>
    public WeaponIconSource Icon { get; init; } = WeaponIconSource.Auto;
    /// <summary>The vanilla weapon whose parts the port uses (e.g. "WP_10703"); null = the lowest grade on the replaced receiver.</summary>
    public string? BaseWeapon { get; init; }
    /// <summary>A vanilla weapon as a stat-table mod has it (its lang ids and icon); used as the base weapon, on any receiver package.</summary>
    public ChimeraWeapon? Renamed { get; init; }
    public DevelopRow Develop { get; init; } = new();
    public string ModVersion { get; init; } = "1.0";
    /// <summary>
    /// Stat changes (part kind → field path → value, field paths as in <see cref="WeaponPartFields"/>). Applied over the
    /// copied weapon's values (the game's, or the mod's where it ships its own parameter tables); changed parts become the
    /// copy's own.
    /// </summary>
    public Dictionary<WeaponPartKind, Dictionary<string, LuaValue>> Edits { get; init; } = [];
    /// <summary>Bake the game's default weapon paint into a vanilla model's placeholder texture slots (step 2b).</summary>
    public bool DefaultPaint { get; init; } = true;
    /// <summary>A fixed camo baked in place of the default paint: "c00" to "c94" (fova/color/wfv_camo_cNN). Null = default
    /// paint. A custom weapon has no camo menu, so the camo is part of the model.</summary>
    public string? Camo { get; init; }
    /// <summary>Move the model (and the .parts naming it) to its own path. Null = no: a receiver model moved off its vanilla
    /// path made the weapon load no model and crash in Sortie Prep (Fulton Gun 0.3a, Oct 27); kept at its path in the copy's
    /// own package it works (0.4c).</summary>
    public bool? OwnModelPath { get; init; }
    /// <summary>
    /// The mod's own weapon package: the .parts path of a Zeta EquipIdTable row (the package path is the
    /// <c>vanillaFpk</c> argument of <see cref="WeaponConverter.ToItem"/>). The port copies this package and not a chimera
    /// receiver package, <see cref="Renamed"/> gives the weapon, and no part package of the game is added: the game loads
    /// only this package for the mod's weapon, so it is the whole gun.
    /// </summary>
    public string? OwnPartsPath { get; init; }
}

public enum WeaponIconSource { Auto, ModIcon, Generated, Vanilla }

public sealed class WeaponConversionResult
{
    public required MgsvBuilder Mod { get; init; }
    public required WeaponSpec Weapon { get; init; }
    public required string Lua { get; init; }
    public List<string> Notes { get; } = [];
    /// <summary>Why the build is refused (also in <see cref="Notes"/>).</summary>
    public List<string> Errors { get; } = [];
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Converter for weapon replacers that change a chimera receiver package (…/collectible/chimera/receiver/*.fpk), such as a
/// model swap of the water pistol. Recipe from the research:
/// <list type="number">
/// <item>The port's package = the vanilla receiver package with the mod's files merged over it (what the managers do on
/// install), under a new name. The model, collision, .parts and effects keep their paths inside it.</item>
/// <item>The mod's texture pack is renamed with the package (the .fox2 loads the pack named like the package).</item>
/// <item>The weapon is a new V Framework weapon: the replaced vanilla weapon's part IDs (so it shoots like it), its own
/// IDs, the new package as its model, its own name, icon and R&amp;D row.</item>
/// </list>
/// The vanilla weapon is left as it was.
/// </summary>
public static class WeaponConverter
{
    private const string Receiver = "/Assets/tpp/pack/collectible/chimera/receiver/";
    private const string Lang = "/Assets/tpp/pack/ui/lang/lang_default_data_eng.fpk";
    private const string LangFilePath = "/Assets/tpp/lang/ui/tpp_weapon.eng.lng2";

    /// <summary>The weapon packages this mod replaces (vanilla .fpk paths): chimera receivers, and EX weapons
    /// (…/collectible/special/EQP_WP_EX_*.fpk: the whole gun as one model, same row shapes).</summary>
    public static List<string> Detect(MgsvMod mod) =>
        mod.ArchiveFiles.Keys
            .Where(p => GameKnowledge.ChimeraReceivers.ContainsKey(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The vanilla weapons a stat-table mod (its own EquipParameters.lua and R&amp;D table, no receiver package) makes into
    /// new guns: R&amp;D rows whose name or icon differ from vanilla. Each comes with the receiver package of the mod's
    /// receiver for it, and the weapon with the mod's lang ids (and icon, when the mod ships it).
    /// </summary>
    public static List<(string Fpk, ChimeraWeapon Weapon)> DetectRenamed(GameData game, MgsvMod mod)
    {
        var found = new List<(string, ChimeraWeapon)>();
        if (!mod.ArchiveFiles.TryGetValue(GameKnowledge.DevelopConstPath, out var dev) || !mod.ArchiveFiles.ContainsKey(EquipTables.EquipParametersPath))
            return found;
        var tables = EquipTables.Load(p => mod.ArchiveFiles.GetValueOrDefault(p));
        var byEquip = GameKnowledge.ChimeraReceivers.SelectMany(r => r.Value.Weapons.Select(w => (fpk: r.Key, w)))
            .GroupBy(x => x.w.Equip).ToDictionary(g => g.Key, g => g.First());
        static string? Txt(LuaValue.Table row, string f) => row[f] is LuaValue.Text t ? t.Value : null;
        static List<LuaValue.Table> Rows(byte[]? lua) => lua == null ? [] : LuaLiteral.CallArguments(System.Text.Encoding.UTF8.GetString(lua), "RegCstDev");
        var vanillaRows = Rows(game.TryRead(GameKnowledge.DevelopConstPath)).Where(r => r["p01"] != null)
            .GroupBy(r => $"{r["p00"]}/{r["p01"]}").ToDictionary(g => g.Key, g => g.First()); // Walker Gear rows reuse a weapon id
        foreach (var row in Rows(dev))
        {
            if (row["p01"] is not { } p01 || !byEquip.TryGetValue(EquipTables.Strip(p01.ToString()!), out var van)) continue;
            string? name = Txt(row, "p06"), icon = Txt(row, "p08");
            var vr = vanillaRows.GetValueOrDefault($"{row["p00"]}/{p01}");
            if (vr == null || (name == Txt(vr, "p06") && icon == Txt(vr, "p08"))) continue;
            string fpk = van.fpk;
            if (tables?.GunBasic(van.w.Wp)?.GetValueOrDefault("receiverId") is { } rc
                && GameKnowledge.ChimeraParts.TryGetValue(rc, out var part) && GameKnowledge.ChimeraReceivers.ContainsKey(part.Fpk))
                fpk = part.Fpk;
            if (mod.ArchiveFiles.ContainsKey(fpk)) continue; // a model replacer: Detect handles it
            found.Add((fpk, van.w with
            {
                NameLang = name ?? van.w.NameLang, InfoLang = Txt(row, "p07") ?? van.w.InfoLang, RealLang = Txt(row, "p30") ?? van.w.RealLang,
                Icon = icon != null && mod.ArchiveFiles.ContainsKey(icon + ".ftex") ? icon : van.w.Icon,
            }));
        }
        return found;
    }

    public static WeaponConversionResult Convert(GameData game, MgsvMod mod, string vanillaFpk, WeaponConversionOptions o)
    {
        string id = o.Id.ToLowerInvariant();
        var project = new ModProject
        {
            Id = id,
            Name = $"{o.Name ?? WeaponName(game, mod, vanillaFpk, o.BaseWeapon) ?? id} (V Framework)",
            Version = o.ModVersion,
            Author = mod.Metadata.Author,
            Description = !string.IsNullOrEmpty(o.Description) ? o.Description
                : $"{o.Name ?? WeaponName(game, mod, vanillaFpk, o.BaseWeapon) ?? id}: a new weapon made from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}. The vanilla weapon is unchanged.\r\n\r\nRequires Infinite Heaven, IHHook and V Framework 1.7.",
        };
        var item = ToItem(game, mod, vanillaFpk, o, $"svfm_{id}", project.NewItemNames(id).key);
        project.Items.Add(item);
        var built = ProjectBuilder.Build(project, game);
        var result = new WeaponConversionResult { Mod = built.Mod, Weapon = item.Spec, Lua = built.Lua };
        result.Notes.AddRange(item.Notes);
        result.Notes.AddRange(built.Errors);
        return result;
    }

    /// <summary>
    /// A copy of a vanilla weapon (WP_…) as a new weapon: its receiver package copied under the copy's name, its parts and
    /// stats as the game has them (or as <paramref name="tables"/> has them: a Zeta script's tables laid over the game's),
    /// then the edits in <paramref name="o"/>. The vanilla weapon is left as it is.
    /// </summary>
    public static WeaponItem CopyVanilla(GameData game, string weapon, WeaponConversionOptions o, string prefix, string key,
        EquipTables? tables = null, MgsvMod? source = null)
    {
        var recv = GameKnowledge.ChimeraReceivers.FirstOrDefault(kv => kv.Value.Weapons.Any(w => w.Wp == weapon));
        if (recv.Key == null) throw new ArgumentException($"{weapon} is not a chimera weapon I know the model of.");
        var item = ToItem(game, source ?? MgsvMod.Empty($"vanilla {weapon}"), recv.Key,
            o with { BaseWeapon = weapon, Icon = o.Icon == WeaponIconSource.Auto ? WeaponIconSource.Vanilla : o.Icon }, prefix, key, tables);
        return item;
    }

    public static WeaponItem ToItem(GameData game, MgsvMod mod, string vanillaFpk, WeaponConversionOptions o, string prefix, string key,
        EquipTables? tables = null)
    {
        string id = o.Id.ToLowerInvariant();
        Naming.CheckId(id);
        ChimeraPackage? recv = null;
        if (o.OwnPartsPath == null && !GameKnowledge.ChimeraReceivers.TryGetValue(vanillaFpk, out recv))
            throw new ArgumentException($"{vanillaFpk} is not a chimera receiver package I know.");
        var baseWp = o.Renamed ?? (recv == null ? null : o.BaseWeapon != null ? recv.Weapons.FirstOrDefault(w => w.Wp == o.BaseWeapon) : recv.Weapons.OrderBy(w => w.Grade).First())
            ?? throw new ArgumentException($"{o.BaseWeapon} does not use {vanillaFpk}.");
        string receiverParts = o.OwnPartsPath ?? recv!.PartsPath;
        var notes = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string van = vanillaFpk[..^4];
        string pkg = $"{Receiver}{prefix}";

        // 1. Packages: vanilla + the mod's files over it. Without the game's package (a weapon the mod doesn't ship, as a
        // Zeta script's copy of a vanilla gun) the copy would have no model or textures: refused.
        bool noPackage = game.TryRead(van + ".fpk") == null && !mod.ArchiveFiles.ContainsKey(van + ".fpk");
        files[pkg + ".fpk"] = Merge(game, mod, van + ".fpk", false, notes).Write();
        files[pkg + ".fpkd"] = Merge(game, mod, van + ".fpkd", true, notes).Write();

        // 2. Texture pack, renamed with the package; the vanilla textures the mod's pack left out are added back.
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

        // 2b. The default paint. A vanilla weapon model leaves its colours to the weapon customisation: its materials
        // point at flat placeholders (cm_flat_white) and the paint package fova/receiver/<model>_def (an .fv2 swapping in
        // the real textures, with them in its pack) dresses it in game. Nothing loads that package for a custom weapon,
        // so a copy of a vanilla gun showed white (Fulton Gun 0.1). The copy's model gets the default paint baked in
        // (only placeholder slots change, so a mod's own textures stay), moves to its own path with a copy of the .parts
        // naming it, and the paint's textures go into the copy's pack (identical copies of vanilla entries).
        string partsPath = receiverParts;
        {
            var fpk = FoxPackage.Read(files[pkg + ".fpk"]);
            var fpkd = FoxPackage.Read(files[pkg + ".fpkd"]);
            TexturePack? paintPack = null;
            bool changedPackage = false;
            foreach (var mf in fpk.Files.Where(f => f.Extension == "fmdl").ToList())
            {
                string stem = Path.GetFileNameWithoutExtension(mf.Path);
                string paint = $"{PaintFolder}{stem}";
                byte[]? baked = null;
                int changed = 0;
                var pt = o.DefaultPaint ? Paint(game, paint + ".fpk", null, o.Camo, notes) : null;
                if (pt != null)
                    baked = FmdlModel.ApplyTextureSwaps(mf.Data, pt.Value.Swaps, IsPaintPlaceholder, out changed);
                if (changed == 0) baked = null;
                // The mod's paint pack first (Guns By Glecas retextures hg00_main1), then the game's for what it left out.
                if (baked != null)
                    foreach (var pp in pt!.Value.Packs.SelectMany(p => new[] { mod.ArchiveFiles.GetValueOrDefault(p), game.TryRead(p) }).OfType<byte[]>())
                    {
                        paintPack ??= files.TryGetValue(pkg + ".pftxs", out var own) ? TexturePack.Read(own) : new TexturePack();
                        foreach (var t in TexturePack.Read(pp).Textures.Where(t => paintPack.Find(t.PathCode) == null)) paintPack.Textures.Add(t);
                    }
                bool move = o.OwnModelPath ?? false;
                if (baked == null && !move) continue;
                string newModel = move ? mf.Path[..(mf.Path.LastIndexOf('/') + 1)] + $"{prefix}_{stem}.fmdl" : mf.Path;
                fpk.Files[fpk.Files.IndexOf(mf)] = new PackageFile(newModel, baked ?? mf.Data);
                changedPackage = true;
                if (move)
                    foreach (var pf in fpkd.Files.Where(f => f.Extension == "parts").ToList())
                    {
                        var ds = FoxDataSet.Read(pf.Data);
                        if (!ds.ModelFiles.Contains(mf.Path)) continue;
                        string newParts = pf.Path[..(pf.Path.LastIndexOf('/') + 1)] + $"{prefix}_{Path.GetFileName(pf.Path)}";
                        fpkd.Files[fpkd.Files.IndexOf(pf)] = new PackageFile(newParts, ds.WithString("ModelDescription", "modelFile", newModel, FoxHash.StrCode64(newModel)).Data);
                        if (pf.Path == partsPath) partsPath = newParts;
                    }
                if (baked == null) continue;
                notes.Add($"{stem}: the game's {pt!.Value.Name} ({changed} texture slots) baked into the copy's model ({newModel}, in its own package); " +
                          "a custom weapon gets no paint from the weapon customisation, so without it the vanilla model is white.");
            }
            if (changedPackage)
            {
                PackageOrder.ApplySnakeBiteOrder(fpk);
                PackageOrder.ApplySnakeBiteOrder(fpkd);
                files[pkg + ".fpk"] = fpk.Write();
                files[pkg + ".fpkd"] = fpkd.Write();
                if (paintPack != null) files[pkg + ".pftxs"] = paintPack.Write();
            }
        }

        // 3. Icon: the mod's own icon, written under a new path; without one, drawn from the mod's model (outline style).
        string iconPath = $"/Assets/tpp/ui/texture/EquipIcon/weapon/ui_wp_{prefix}_alp";
        string? vanillaIcon = baseWp.Icon;
        RgbaImage? img = null;
        bool modHasIcon = vanillaIcon != null && mod.ArchiveFiles.ContainsKey(vanillaIcon + ".ftex");
        var source = o.Icon == WeaponIconSource.Auto ? (modHasIcon ? WeaponIconSource.ModIcon : WeaponIconSource.Generated) : o.Icon;
        if (source == WeaponIconSource.ModIcon && modHasIcon)
        {
            var parts = new Dictionary<int, byte[]>();
            for (int k = 1; k <= 6; k++)
                if (mod.ArchiveFiles.TryGetValue($"{vanillaIcon}.{k}.ftexs", out var part)) parts[k] = part;
            img = FtexTexture.Read(mod.ArchiveFiles[vanillaIcon! + ".ftex"]).DecodeLargestAvailable(parts);
        }
        else if (source == WeaponIconSource.Generated)
        {
            var model = FoxPackage.Read(files[pkg + ".fpk"]).Files.Where(f => f.Extension == "fmdl").Select(f => FmdlModel.Read(f.Data)).ToList();
            if (model.Count > 0) img = Rendering.OutlineIconRenderer.Render(model);
            else notes.Add("No model found to draw an icon from: the vanilla icon is used.");
        }
        if (img != null)
        {
            var ft = FtexWriter.Write(img, withAlpha: true, FtexKind.Linear);
            files[iconPath + ".ftex"] = ft.Ftex;
            foreach (var (n, d) in ft.Ftexs) files[$"{iconPath}.{n}.ftexs"] = d;
        }
        else
        {
            iconPath = vanillaIcon ?? throw new InvalidDataException("The vanilla weapon has no icon and none could be made.");
            notes.Add("The vanilla icon is used.");
        }

        // 4. Text: per language, the user's text, else what the mod's language file says for the weapon, else vanilla.
        var text = WeaponText(game, mod, baseWp);
        foreach (var ((lang, field), t) in o.Texts) text[(lang, field)] = t;
        if (o.Name != null) text[("eng", "name")] = o.Name;
        if (o.RealName != null) text[("eng", "real")] = o.RealName;
        if (o.Description != null) text[("eng", "info")] = o.Description;
        if (!text.ContainsKey(("eng", "name"))) text[("eng", "name")] = id;
        string name = text[("eng", "name")];

        // 5. The weapon.
        string equipConst = $"EQP_WP_SVFM_{Naming.Pascal(id)}", wpConst = $"WP_SVFM_{Naming.Pascal(id)}";
        var spec = new WeaponSpec
        {
            Key = key, EquipConstant = equipConst, WeaponConstant = wpConst,
            EquipType = WeaponType(baseWp, receiverParts), DevelopType = WeaponType(baseWp, receiverParts),
            PartsPath = partsPath, FpkPath = pkg + ".fpk",
            WeaponGrade = baseWp.Grade,
            NameLang = $"{prefix}_name", InfoLang = $"{prefix}_info", RealNameLang = $"{prefix}_real",
            IconFtexPath = iconPath, Develop = o.Develop,
        };
        spec.Header.Add($"{name}: V Framework weapon converted from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}.");
        spec.Header.Add($"Model: {van[(van.LastIndexOf('/') + 1)..]} → {pkg[(pkg.LastIndexOf('/') + 1)..]}. Parts and stats of vanilla {baseWp.Wp}.");
        spec.Parts["receiverId"] = baseWp.ReceiverId;
        spec.Parts["barrelId"] = baseWp.BarrelId;
        spec.Parts["ammoId"] = baseWp.AmmoId;
        spec.Parts["muzzleId"] = baseWp.MuzzleId;
        spec.Parts["muzzleOptionId"] = baseWp.MuzzleOptionId;
        spec.Parts["stockId"] = baseWp.StockId;
        spec.Parts["scope1Id"] = baseWp.Scope1Id;
        spec.Parts["scope2Id"] = baseWp.Scope2Id;
        spec.Parts["underBarrelId"] = baseWp.UnderBarrelId;
        spec.Parts["laserFlash1Id"] = baseWp.LaserFlash1Id;
        spec.Parts["laserFlash2Id"] = baseWp.LaserFlash2Id;

        // 6. Stats: a copy of the vanilla weapon's parts, filled from the game's parameter tables, the mod's own copy of
        // them (the mod as installed) and the user's edits. Changed parts become the copy's own; the rest stay vanilla.
        var modModels = ModModels(mod);
        if (EquipTables.Load(game.TryRead) is { } vanillaTables && vanillaTables.GunBasic(baseWp.Wp) is { } vanillaGun)
        {
            var modTables = tables ?? EquipTables.Load(path => mod.ArchiveFiles.GetValueOrDefault(path));
            var moves = ModPartMoves(modModels.Keys, modTables?.GunBasic(baseWp.Wp) ?? vanillaGun, vanillaTables, notes);
            var tuning = WeaponTuning.For(baseWp.Wp, vanillaTables, modTables, o.Edits, moves);
            spec.TuningLua = tuning.Apply(spec, Naming.Pascal(id));
            spec.Tuning = tuning;
            tuning.Camo = o.Camo; // baked below; MakeCurrent at build keeps it unless the Stats tab changes it
            notes.AddRange(tuning.Notes);
            var own = tuning.Parts.Where(kv => kv.Value.Own).Select(kv => kv.Value.Constant).ToList();
            if (own.Count > 0) spec.Header.Add($"Own parts (changed stats): {string.Join(", ", own)}.");
        }
        else if (o.Edits.Count > 0) throw new InvalidOperationException("The game's weapon parameter tables (EquipParameters.lua, DamageParameterTables.lua) are needed to change a weapon's stats.");

        // 7. Attachments: a vanilla part with a package of its own (sight, laser/light, barrel, stock, under-barrel…) is
        // loaded by the game for its vanilla weapons only (ChimeraPartsPackageTable); a custom weapon loads its one
        // package, so the attachment had no model (Fulton Gun 0.3b/0.4c: no laser, no light). Its package's files go
        // into the copy's package, as installed, with its textures and default paint (V Framework's wiki asks the same
        // of a sight: its UI files in the weapon's package). The mod's version of a part wins over the game's.
        if (o.OwnPartsPath == null) AddPartPackages(game, mod, modModels, files, pkg, spec, notes, o.Camo);
        var unmerged = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        MergePartModels(files, pkg, spec, notes, unmerged);

        var paintCopies = new Dictionary<ulong, ulong>();
        // Loose textures the models use (Halo Assault Rifle ships its base colour map as loose files, no pack): new paths as
        // they are; textures the mod installs at vanilla paths as complete copies under new names (InstalledTextures).
        {
            // The mod's packs: the receiver's, and the paint packs it ships (Guns By Glecas: loose .2/.3 mips of hg00_main1
            // at vanilla paths, header and .1 in fova/receiver/hg00_main1_def.pftxs).
            var modPack = mp != null ? TexturePack.Read(mp) : null;
            foreach (var (path, data) in mod.ArchiveFiles.Where(f => f.Key.StartsWith(PaintFolder, StringComparison.Ordinal) && f.Key.EndsWith(".pftxs", StringComparison.Ordinal)))
            {
                modPack ??= new TexturePack();
                foreach (var t in TexturePack.Read(data).Textures.Where(t => modPack.Find(t.PathCode) == null)) modPack.Textures.Add(t);
            }
            var relocated = InstalledTextures.Collect(game, mod, TextureCheck.Referenced(files), prefix, modPack, vanillaPack, files, notes);
            if (relocated.Codes.Count > 0)
            {
                var fpk = FoxPackage.Read(files[pkg + ".fpk"]);
                InstalledTextures.Apply(fpk, relocated, notes);
                files[pkg + ".fpk"] = fpk.Write();
            }
            if (relocated.MadeVanilla.Count > 0 && TexturePack.Read(files[pkg + ".pftxs"]) is var kept && InstalledTextures.RestoreVanilla(kept, vanillaPack, relocated))
                files[pkg + ".pftxs"] = kept.Write();
            foreach (var (k, v) in relocated.Codes) paintCopies[k] = v;
            // The models were pointed at the texture copies after the merge; the unmerged receiver model needs the same.
            if (relocated.Codes.Count > 0)
            {
                var pristine = new FoxPackage();
                pristine.Files.AddRange(unmerged.Where(kv => kv.Key.EndsWith(".fmdl", StringComparison.Ordinal)).Select(kv => new PackageFile(kv.Key, kv.Value)));
                InstalledTextures.Apply(pristine, relocated, []);
                foreach (var f in pristine.Files) unmerged[f.Path] = f.Data;
            }
            AddModCamoTextures(game, mod, files, pkg, spec, prefix, modPack, paintCopies, notes);
        }

        var item = new WeaponItem { Id = id, Key = key, Prefix = prefix, Spec = spec, Title = name };
        foreach (var (k, v) in paintCopies) item.PaintCopies[k] = v;
        foreach (var (k, v) in unmerged) item.Unmerged[k] = v;
        item.MergedSlots = MergeSignature(spec);
        item.OwnPackage = o.OwnPartsPath != null;
        foreach (var (p, d) in files) item.Files[p] = d;
        if (noPackage) item.Problems.Add($"The game's {van}.fpk is not in the game data opened: the copy would have no model or textures. Open the game folder (or the dump that holds it).");
        foreach (var ((lang, field), t) in text) item.SetText($"{prefix}_{field}", lang, t);
        if (!text.ContainsKey(("eng", "real"))) item.SetText($"{prefix}_real", name);
        if (!text.ContainsKey(("eng", "info"))) item.SetText($"{prefix}_info", "");
        item.Notes.AddRange(notes);
        return item;
    }

    private static LangFile? LangOf(byte[]? fpk, string suffix = "eng")
    {
        if (fpk == null) return null;
        var f = FoxPackage.Read(fpk).Find($"/Assets/tpp/lang/ui/tpp_weapon.{suffix}.lng2");
        return f == null ? null : LangFile.Read(f.Data);
    }

    /// <summary>
    /// The weapon's text per language (package name) and field ("name", "real", "info"): the mod's language file when it
    /// has the weapon's entry, else the vanilla text (English from the game; other languages only when the mod ships them).
    /// </summary>
    public static Dictionary<(string language, string field), string> WeaponText(GameData game, MgsvMod mod, ChimeraWeapon wp)
    {
        var text = new Dictionary<(string, string), string>();
        if (wp.NameLang == null && NamedSibling(wp) is { } named)
            wp = wp with { NameLang = named.NameLang, InfoLang = wp.InfoLang ?? named.InfoLang, RealLang = wp.RealLang ?? named.RealLang };
        foreach (var (package, suffix, _) in VFramework.ModText.Languages)
        {
            string fpk = $"/Assets/tpp/pack/ui/lang/lang_default_data_{package}.fpk";
            // Every language file of the mod's package, tpp_weapon first (Guns By Glecas keeps its text in tpp_gbg.eng.lng2).
            var modLangs = mod.ArchiveFiles.GetValueOrDefault(fpk) is { } mf
                // HK 33 (Zeta) names its files hk33.lng2 and hk33sg1.lng2, with no language in the name.
                ? FoxPackage.Read(mf).Files.Where(f => f.Path.EndsWith($".{suffix}.lng2", StringComparison.Ordinal)
                        || (f.Extension == "lng2" && !Path.GetFileNameWithoutExtension(f.Path).Contains('.')))
                    .OrderBy(f => f.Path.Contains("/tpp_weapon.", StringComparison.Ordinal) ? 0 : 1).Select(f => LangFile.Read(f.Data)).ToList()
                : [];
            var vanLang = package == "eng" ? LangOf(game.TryRead(fpk), suffix) : null;
            foreach (var (field, langId) in new[] { ("name", wp.NameLang), ("real", wp.RealLang), ("info", wp.InfoLang) })
            {
                if (langId == null) continue;
                var t = modLangs.Select(l => l[langId]).FirstOrDefault(v => v != null) ?? vanLang?[langId];
                if (t != null) text[(package, field)] = t;
            }
        }
        return text;
    }

    /// <summary>
    /// For a weapon with no text of its own (an enemy weapon such as WP_10024, or WP_10136, the only weapon on
    /// hg01_main2): the lowest-grade named weapon on its receiver, else on a receiver of the same model (hg01_*).
    /// </summary>
    private static ChimeraWeapon? NamedSibling(ChimeraWeapon wp)
    {
        var recv = GameKnowledge.ChimeraReceivers.FirstOrDefault(kv => kv.Value.Weapons.Any(w => w.Wp == wp.Wp));
        if (recv.Key == null) return null;
        string family = recv.Key[(recv.Key.LastIndexOf('/') + 1)..][..4];
        return GameKnowledge.ChimeraReceivers
            .OrderBy(kv => kv.Key == recv.Key ? 0 : 1).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Where(kv => kv.Key[(kv.Key.LastIndexOf('/') + 1)..].StartsWith(family, StringComparison.Ordinal))
            .Select(kv => kv.Value.Weapons.Where(w => w.NameLang != null).OrderBy(w => w.Grade).FirstOrDefault())
            .FirstOrDefault(w => w != null);
    }

    /// <summary>The English name the mod gives the weapon on this receiver (or the vanilla name).</summary>
    public static string? WeaponName(GameData game, MgsvMod mod, string vanillaFpk, string? baseWeapon = null)
    {
        if (!GameKnowledge.ChimeraReceivers.TryGetValue(vanillaFpk, out var recv)) return null;
        var wp = baseWeapon != null ? recv.Weapons.FirstOrDefault(w => w.Wp == baseWeapon) : recv.Weapons.OrderBy(w => w.Grade).FirstOrDefault();
        return wp == null ? null : WeaponText(game, mod, wp).GetValueOrDefault(("eng", "name"));
    }

    /// <summary>
    /// The models the mod ships in any of its packages, by model name. A part is found by its model and not by its package
    /// path, because a mod can install a model in a package that the game does not load for that part.
    /// </summary>
    private static Dictionary<string, PackageFile> ModModels(MgsvMod mod)
    {
        var models = new Dictionary<string, PackageFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, data) in mod.ArchiveFiles.Where(kv => kv.Key.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            foreach (var f in FoxPackage.Read(data).Files.Where(f => f.Extension == "fmdl"))
                models.TryAdd(Path.GetFileNameWithoutExtension(f.Path), f);
        return models;
    }

    /// <summary>
    /// The slots that change so that the port shows the models the mod ships. A slot moves only when all of these are true:
    /// the mod ships one receiver (with more, a part cannot be given to one gun); the weapon fills the slot; the mod does not
    /// ship the model of the slot's part; the mod ships one model of that kind; and a part with that model has the same stats.
    /// </summary>
    /// <param name="shipped">The names of the models that the mod ships.</param>
    /// <param name="gun">The weapon's SetGunBasic slots.</param>
    /// <param name="vanilla">The game's part tables.</param>
    /// <param name="notes">Receives one note for each model that the rule cannot use.</param>
    /// <returns>Slot to part constant.</returns>
    public static Dictionary<string, string> ModPartMoves(IEnumerable<string> shipped, IReadOnlyDictionary<string, string> gun, EquipTables vanilla, List<string> notes)
    {
        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = shipped.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = GameKnowledge.ChimeraParts.Where(kv => names.Contains(WeaponAttachments.ModelKey(kv.Value.Parts))).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (parts.Where(kv => kv.Value.Kind == "receiver").Select(kv => WeaponAttachments.ModelKey(kv.Value.Parts)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) return moves;
        foreach (var (slot, kind, stats) in WeaponSlots.Attachments.Where(s => s.PartKind != null).Select(s => (s.Key, s.PartKind!, s.Stats)))
        {
            if (gun.GetValueOrDefault(slot) is not { } current || !GameKnowledge.ChimeraParts.TryGetValue(current, out var cp) || names.Contains(WeaponAttachments.ModelKey(cp.Parts))) continue;
            var candidates = parts.Where(kv => kv.Value.Kind == kind).ToList();
            var models = candidates.Select(kv => WeaponAttachments.ModelKey(kv.Value.Parts)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (models.Count == 0) continue;
            if (models.Count > 1)
            {
                notes.Add($"{slot}: the mod ships more than one {kind} model ({string.Join(", ", models)}) and none is the model of {current}; {current} stays.");
                continue;
            }
            var same = candidates.FirstOrDefault(kv => stats is not { } k
                || (vanilla.Part(k, current) is { } a && vanilla.Part(k, kv.Key) is { } b && a.Differences(b).Count == 0));
            if (same.Key == null) notes.Add($"{slot}: the mod's {models[0]} belongs to parts with stats other than {current}'s; {current} stays.");
            else { moves[slot] = same.Key; notes.Add($"{slot}: {current} changed to {same.Key}, the part of the mod's model {models[0]}."); }
        }
        return moves;
    }

    /// <summary>
    /// The package files of the weapon as a build needs them, for the parts and the camo that the tuning has now. The item
    /// is not changed. The steps, in this order:
    /// (1) The package of each part in the slots that the port does not have yet (a part swapped on the Stats tab) goes
    /// into the port's package: a custom weapon loads only its own package.
    /// (2) When a merge slot holds another part than at the last merge, the receiver model, connect points and .parts of
    /// before the merge (WeaponItem.Unmerged) are put back, and the part models are merged again: the game draws only the
    /// receiver model. The owner approved this on Nov 10.
    /// (3) The models are painted with the camo of the tuning (<see cref="WeaponTuning.Camo"/>, null = the default
    /// paint). The model that step 2 puts back has the default paint, so the paint is the last step. Each paint source
    /// (the receiver's fova package, each part's) swaps only textures of its own paint (default or camo-ready), a camo
    /// layer or a flat placeholder, so a mod's own textures stay. Merged part materials keep their names, so a part in
    /// the merged receiver model is painted too.
    /// A build stores the result (<see cref="MakeCurrent"/>). The 3D preview draws it.
    /// </summary>
    /// <returns>The files, the merge slots that the files hold (for WeaponItem.MergedSlots), and the notes.</returns>
    public static (Dictionary<string, byte[]> Files, string MergedSlots, List<string> Notes) Current(GameData game, WeaponItem w)
    {
        var files = new Dictionary<string, byte[]>(w.Files, StringComparer.Ordinal);
        var notes = new List<string>();
        if (w.Spec.Tuning is not { } tuning) return (files, w.MergedSlots, notes);
        string pkg = w.Spec.FpkPath[..^4];
        // shortcut: the package of a part swapped out stays in the port (unused files, a larger mod); remove when sizes matter.
        // The mod's own package is the whole gun: only a part that was swapped since the last merge gets its package.
        var kept = w.OwnPackage ? w.MergedSlots.Split(';').ToHashSet(StringComparer.Ordinal) : null;
        AddPartPackages(game, MgsvMod.Empty("parts"), [], files, pkg, w.Spec, notes, skip: kept);
        string merged = Remerge(files, w, notes);
        if (Repaint(game, w, tuning.Camo, notes, files) is { } painted)
        {
            // shortcut: the textures of the paint swapped out stay in the pack (a larger mod); drop them when sizes matter.
            var pack = files.TryGetValue(pkg + ".pftxs", out var pd) ? TexturePack.Read(pd) : new TexturePack();
            foreach (var data in painted.Packs.Distinct().Select(game.TryRead).OfType<byte[]>())
                foreach (var t in TexturePack.Read(data).Textures.Where(t => pack.Find(t.PathCode) == null)) pack.Textures.Add(t);
            files[pkg + ".fpk"] = painted.Fpk.Write();
            files[pkg + ".pftxs"] = pack.Write();
            notes.Add($"Weapon paint set to {tuning.Camo ?? "the default paint"}.");
        }
        return (files, merged, notes);
    }

    /// <summary>Stores <see cref="Current"/> in the item: its package files are then those of the tuning. A build calls this. Returns the notes.</summary>
    public static List<string> MakeCurrent(GameData game, WeaponItem w)
    {
        var (files, merged, notes) = Current(game, w);
        foreach (var (path, data) in files) w.Files[path] = data;
        w.MergedSlots = merged;
        return notes;
    }

    /// <summary>True when a camo changes the look of the weapon: a dry run of the paint step of <see cref="Current"/> with camo c00 or with
    /// the default paint changes at least one texture slot. False for a mod's own model with its own textures (no game paint to swap).</summary>
    public static bool CanTakeCamo(GameData game, WeaponItem w) =>
        (HasCamo00(game) && Repaint(game, w, "c00", []) != null) || Repaint(game, w, null, []) != null;

    /// <summary>False for game data with no camo packages (a partial dump): Paint throws for a camo that is not there.</summary>
    private static bool HasCamo00(GameData game) => game.TryRead($"{CamoFolder}wfv_camo_c00.fpk") != null;

    /// <summary>The weapon package with its models painted in <paramref name="camo"/>, and the texture packs that paint needs; null when no texture slot changes.
    /// <paramref name="files"/>: the package files to paint, when they are not the item's own (the steps of <see cref="Current"/>).</summary>
    internal static (FoxPackage Fpk, List<string> Packs)? Repaint(GameData game, WeaponItem w, string? camo, List<string> notes, IReadOnlyDictionary<string, byte[]>? files = null)
    {
        string pkg = w.Spec.FpkPath[..^4];
        if (!(files ?? w.Files).TryGetValue(pkg + ".fpk", out var fpkData)) return null;
        // A project with a camo, opened with game data that has no such camo: keep the paint it has (Paint would throw).
        if (WeaponTuning.IsCamo(camo) && game.TryRead($"{CamoFolder}wfv_camo_{camo}.fpk") == null)
        {
            notes.Add($"Camo {camo} is not in the game data opened, so the weapon keeps its paint.");
            return null;
        }
        var fpk = FoxPackage.Read(fpkData);
        var sources = PaintSources(game, fpk, w.Spec);
        // The mod's own version of a paint texture, where the port has a copy of it (WeaponItem.PaintCopies).
        Fv2File.TextureSwap Copy(Fv2File.TextureSwap s) => w.PaintCopies.TryGetValue(s.Texture, out ulong c) ? s with { Texture = c } : s;
        var packs = new List<string>();
        bool changedAny = false;
        foreach (var (paintFpk, fv2) in sources)
        {
            if (Paint(game, paintFpk, fv2, camo, notes) is not { } target) continue;
            var scratch = new List<string>();
            var own = new HashSet<ulong>(new[] { Paint(game, paintFpk, fv2, null, scratch), HasCamo00(game) ? Paint(game, paintFpk, fv2, "c00", scratch) : null }
                .Where(p => p != null).SelectMany(p => p!.Value.Swaps.Select(s => s.Texture)));
            own.UnionWith(own.Where(w.PaintCopies.ContainsKey).Select(t => w.PaintCopies[t]).ToList());
            var swaps = target.Swaps.Select(Copy).ToList();
            bool Replaceable(ulong t) => own.Contains(t) || IsPaintPlaceholder(t) || IsCamoLayer(t);
            // The paint's own part: a material whose base texture this paint sets (e.g. ar03 paint, not the sk03 stock merged in).
            bool OwnPart(ulong t) => own.Contains(t) && !IsPaintPlaceholder(t) && !IsCamoLayer(t);
            foreach (var mf in fpk.Files.Where(f => f.Extension == "fmdl").ToList())
                if (FmdlModel.ApplyTextureSwaps(mf.Data, swaps, Replaceable, out int changed, OwnPart) is { } baked && changed > 0)
                {
                    fpk.Files[fpk.Files.IndexOf(mf)] = new PackageFile(mf.Path, baked);
                    changedAny = true;
                }
            packs.AddRange(target.Packs);
        }
        return changedAny ? (fpk, packs) : null;
    }

    /// <summary>The paint packages of a weapon package: one for each model by its name (the receiver's), and that of each vanilla part in the slots.</summary>
    private static List<(string Fpk, string? Fv2)> PaintSources(GameData game, FoxPackage fpk, WeaponSpec spec) =>
        fpk.Files.Where(f => f.Extension == "fmdl").Select(f => (Fpk: $"{PaintFolder}{Path.GetFileNameWithoutExtension(f.Path)}.fpk", Fv2: (string?)null))
            .Concat(WeaponSlots.Attachments.Select(s => WeaponSlots.PartIn(spec, s.Key)).OfType<string>()
                .Select(p => GameKnowledge.ChimeraParts.GetValueOrDefault(p)).Where(cp => cp != null && cp.Kind != "receiver" && cp.PaintFpk.Length > 0)
                .Select(cp => (Fpk: cp!.PaintFpk, Fv2: (string?)cp.PaintFv2)))
            .Distinct().Where(s => game.TryRead(s.Fpk) != null).ToList();

    /// <summary>
    /// The mod's own camo-ready textures (the "_cam" paint of a receiver or part; Guns By Glecas retextures hg00_main1 and
    /// sr02_stck0 this way). A custom gun gets no paint from the game, so the installed mod's version must come with the
    /// port: loose files at vanilla paths become complete copies under new names (<paramref name="copies"/>, as
    /// InstalledTextures does for the default paint), and the entries of the mod's "_cam" pack go into the port's pack.
    /// Adds one "Camo: …" note for each part whose default paint the mod retextures (the Stats tab shows these notes).
    /// </summary>
    private static void AddModCamoTextures(GameData game, MgsvMod mod, Dictionary<string, byte[]> files, string pkg, WeaponSpec spec, string prefix,
        TexturePack? modDefaultPack, Dictionary<ulong, ulong> copies, List<string> notes)
    {
        if (!HasCamo00(game)) return;
        var scratch = new List<string>();
        TexturePack? pack = null;
        foreach (var (paintFpk, fv2) in PaintSources(game, FoxPackage.Read(files[pkg + ".fpk"]), spec))
        {
            static bool Real(ulong t) => !IsPaintPlaceholder(t) && !IsCamoLayer(t);
            var def = Paint(game, paintFpk, fv2, null, scratch)?.Swaps.Select(s => s.Texture).Where(Real).ToHashSet() ?? [];
            var cam = Paint(game, paintFpk, fv2, "c00", scratch)?.Swaps.Select(s => s.Texture).Where(Real).ToHashSet() ?? [];
            cam.ExceptWith(def); // a texture of both paints (the mtm map) came with the default paint
            string stem = paintFpk[..^4], name = stem[(stem.LastIndexOf('/') + 1)..];
            var modCam = mod.ArchiveFiles.TryGetValue(stem + "_cam.pftxs", out var mc) ? TexturePack.Read(mc) : null;
            var vanCam = game.TryRead(stem + "_cam.pftxs") is { } vc ? TexturePack.Read(vc) : null;
            var moved = InstalledTextures.Collect(game, mod, cam, prefix, modCam, vanCam, files, notes);
            foreach (var (k, v) in moved.Codes) copies[k] = v;
            // Not moved: read from the pack while the package is loaded, as the installed mod reads them.
            var entries = modCam?.Textures.Where(t => cam.Contains(t.PathCode) && !moved.Codes.ContainsKey(t.PathCode) && !moved.MadeVanilla.Contains(t.PathCode)).ToList() ?? [];
            if (entries.Count > 0)
            {
                pack ??= files.TryGetValue(pkg + ".pftxs", out var own) ? TexturePack.Read(own) : new TexturePack();
                foreach (var t in entries.Where(t => pack.Find(t.PathCode) == null)) pack.Textures.Add(t);
            }
            bool ownCamo = moved.Codes.Count > 0 || entries.Count > 0;
            bool retextured = def.Any(t => copies.ContainsKey(t) || modDefaultPack?.Find(t) != null);
            if (ownCamo) notes.Add($"Camo: {name} uses the mod's own camo textures.");
            else if (retextured) notes.Add($"Camo: a camo replaces the mod's own texture on {name}, as in the game with the mod installed.");
        }
        if (pack != null) files[pkg + ".pftxs"] = pack.Write();
    }

    /// <summary>The camo layer textures (common_source/layer: cm_camo4_cNN_lym and the like).</summary>
    private static bool IsCamoLayer(ulong texture) =>
        PathDictionary.Bundled.TryGetPath(texture)?.StartsWith("/Assets/tpp/common_source/layer/", StringComparison.Ordinal) == true;

    private static void AddPartPackages(GameData game, MgsvMod mod, Dictionary<string, PackageFile> modModels, Dictionary<string, byte[]> files, string pkg, WeaponSpec spec, List<string> notes, string? camo = null,
        IReadOnlySet<string>? skip = null)
    {
        var fpk = FoxPackage.Read(files[pkg + ".fpk"]);
        var fpkd = FoxPackage.Read(files[pkg + ".fpkd"]);
        var pack = files.TryGetValue(pkg + ".pftxs", out var own) ? TexturePack.Read(own) : new TexturePack();
        var added = new List<string>();
        foreach (var slot in WeaponSlots.Attachments.Select(s => s.Key))
        {
            // skip: "slot=part" entries (MergeSignature) that get no package.
            if (WeaponSlots.PartIn(spec, slot) is not { } part || skip?.Contains($"{slot}={part}") == true || !GameKnowledge.ChimeraParts.TryGetValue(part, out var cp) || cp.Kind == "receiver") continue;
            string van = cp.Fpk[..^4];
            modModels.TryGetValue(WeaponAttachments.ModelKey(cp.Parts), out var modModel);
            if (game.TryRead(cp.Fpk) == null && !mod.ArchiveFiles.ContainsKey(cp.Fpk) && modModel == null)
            {
                notes.Add($"{part} ({slot}): its package {cp.Fpk} is not in the game data opened; the attachment will have no model.");
                continue;
            }
            static int AddNew(FoxPackage into, FoxPackage from)
            {
                var add = from.Files.Where(f => into.Find(f.Path) == null).ToList();
                into.Files.AddRange(add);
                return add.Count;
            }
            var partFpk = Merge(game, mod, cp.Fpk, false, notes);
            int count = AddNew(fpk, partFpk) + AddNew(fpkd, Merge(game, mod, van + ".fpkd", true, notes));
            foreach (var data in new[] { mod.ArchiveFiles.GetValueOrDefault(van + ".pftxs"), game.TryRead(van + ".pftxs") })
                if (data != null)
                    foreach (var t in TexturePack.Read(data).Textures.Where(t => pack.Find(t.PathCode) == null)) pack.Textures.Add(t);
            if (modModel != null)
            {
                if (partFpk.Find(modModel.Path) == null)
                    notes.Add($"{part} ({slot}): the game loads this model from a package other than {Path.GetFileName(cp.Fpk)}; the mod's {modModel.Path} is copied into the weapon's package.");
                int at = fpk.Files.FindIndex(f => f.Path == modModel.Path);
                if (at >= 0) fpk.Files[at] = modModel; else fpk.Files.Add(modModel);
                count++;
            }
            if (count == 0) continue;
            // Its default paint, baked into its model at its vanilla path (as the receiver's, step 2b).
            if (cp.PaintFpk.Length > 0 && Paint(game, cp.PaintFpk, cp.PaintFv2, camo, notes) is { } pt)
            {
                foreach (var mf in fpk.Files.Where(f => f.Extension == "fmdl").ToList())
                {
                    var baked = FmdlModel.ApplyTextureSwaps(mf.Data, pt.Swaps, IsPaintPlaceholder, out int changed);
                    if (baked != null && changed > 0) fpk.Files[fpk.Files.IndexOf(mf)] = new PackageFile(mf.Path, baked);
                }
                foreach (var paintPack in pt.Packs.Select(game.TryRead).OfType<byte[]>())
                    foreach (var t in TexturePack.Read(paintPack).Textures.Where(t => pack.Find(t.PathCode) == null)) pack.Textures.Add(t);
            }
            added.Add($"{part} ({Path.GetFileNameWithoutExtension(cp.Fpk)})");
        }
        if (added.Count == 0) return;
        PackageOrder.ApplySnakeBiteOrder(fpk);
        PackageOrder.ApplySnakeBiteOrder(fpkd);
        files[pkg + ".fpk"] = fpk.Write();
        files[pkg + ".fpkd"] = fpkd.Write();
        files[pkg + ".pftxs"] = pack.Write();
        notes.Add($"Attachments' packages copied into the weapon's package (a custom weapon loads only its own): {string.Join(", ", added)}.");
    }

    /// <summary>
    /// The TppEquip.EQP_TYPE_* name of a vanilla weapon. 205 of the 480 vanilla rows have no R&amp;D row and so no
    /// devType; for those the receiver's parts folder gives the family (an empty type makes V Framework drop the row:
    /// "AddToEquipIdTable: malformed row").
    /// </summary>
    public static string WeaponType(ChimeraWeapon wp, string partsPath) => wp.DevType != "" ? wp.DevType
        : partsPath.Split('/')[^2] switch
        {
            "asr" => "Assault", "hag" => "Handgun", "smg" => "Submachinegun", "snr" => "Sniper", "shg" => "Shotgun",
            "mag" => "Machinegun", "mis" => "Missile", "grl" or "sas" => "GrenadeLauncher",
            var f => throw new InvalidDataException($"No weapon family known for parts folder \"{f}\" ({wp.Wp})."),
        };

    /// <summary>The receiver mesh groups that the game shows when a part is on: the under rail (laser or light) and the sight mount (ST_ part with a model).</summary>
    private const ulong GroupUnderRail = 0x73b27a6db119, GroupSightMount = 0x4dea3abf4e45;

    /// <summary>The effect points of a part .fcnp: laser beam, light cone, muzzle flash (.docs/Research_ModelMerge.md, "Connect points", items 4 and 6).</summary>
    private static readonly string[] EffectPoints = ["BEAM_POINT", "FLASH_POINT", "MUZZLE_FLASH", "BACK_BLAST", "MUZZLE_FLASH_UB"];

    /// <summary>
    /// Merges the part models of the weapon's slots into its receiver model (FmdlMerge). V Framework 1.7 draws only the
    /// receiver model of a custom gun, so without this the gun shows no barrel, magazine, sight or laser. The part
    /// packages stay in the weapon's package as they are.
    /// </summary>
    // unmerged: receives the receiver model, .fcnp and .parts as they are before the merge, by package path (for step 2 of Current).
    private static void MergePartModels(Dictionary<string, byte[]> files, string pkg, WeaponSpec spec, List<string> notes, Dictionary<string, byte[]>? unmerged = null)
    {
        var fpk = FoxPackage.Read(files[pkg + ".fpk"]);
        var fpkd = FoxPackage.Read(files[pkg + ".fpkd"]);
        string? rcPath = WeaponAttachments.ReceiverPath(fpkd, spec.PartsPath);
        if (rcPath == null || fpk.Find(rcPath) is not { } rcFile) { notes.Add("Model merge: the receiver model was not found, so the parts are not merged into it."); return; }
        bool Is(PackageFile f, string ext, string stem) => f.Extension == ext && Path.GetFileNameWithoutExtension(f.Path) is var s
            && (s.Equals(stem, StringComparison.OrdinalIgnoreCase) || stem.EndsWith("_" + s, StringComparison.OrdinalIgnoreCase));
        string rcStem = Path.GetFileNameWithoutExtension(rcPath);
        var rcFcnp = fpk.Files.FirstOrDefault(f => Is(f, "fcnp", rcStem));
        var rcPoints = rcFcnp != null ? Fcnp.Read(rcFcnp.Data) : [];
        if (unmerged != null)
        {
            unmerged[rcPath] = rcFile.Data;
            if (rcFcnp != null) unmerged[rcFcnp.Path] = rcFcnp.Data;
            if (fpkd.Find(spec.PartsPath) is { } rcParts) unmerged[rcParts.Path] = rcParts.Data;
        }
        var outPoints = rcPoints.ToList();
        var copied = new List<string>();
        var cnpHashes = rcFcnp != null ? Fcnp.Hashes(rcFcnp.Data) : [];
        var model = FmdlRaw.Read(rcFile.Data);
        (string? Bone, Vector3 Pos, Quaternion Rot, IReadOnlyList<Fcnp.Point> Points, FmdlModel Model)? barrel = null;
        var merged = new List<string>();
        var show = new HashSet<ulong>();
        var effects = new List<(string Part, FoxDataSet Parts, FoxDataSet.Entity Entity, string Name)>();
        bool altMagazine = spec.Tuning?.Parts.GetValueOrDefault(WeaponPartKind.Receiver)?.Values.Values.GetValueOrDefault("receiverParamSetsSystem.altMagazineSocket")?.AsNumber is > 0;
        // An assembled model (_aw: HK 33 Zeta) has the magazine in its own mesh group MESH_AMO; a merge would draw it twice.
        var rcModel = FmdlModel.Read(rcFile.Data);
        int amo = rcModel.MeshGroups.ToList().FindIndex(g => g.name == "MESH_AMO" || g.hash == FoxHash.StrCode64("MESH_AMO"));
        bool hasMagazine = amo >= 0 && rcModel.Meshes.Any(m => m.MeshGroup == amo);
        foreach (var (slot, points) in WeaponSlots.MergeOrder.Select(s => (s.Key, s.Points)))
        {
            if (WeaponSlots.PartIn(spec, slot) is not { } part || !GameKnowledge.ChimeraParts.TryGetValue(part, out var cp) || cp.Kind == "receiver") continue;
            if (slot == "ammoId" && hasMagazine)
            {
                notes.Add($"Model merge: {part} (ammoId) is not merged, because the receiver model {Path.GetFileName(rcPath)} has the magazine in it (mesh group MESH_AMO).");
                continue;
            }
            string key = WeaponAttachments.ModelKey(cp.Parts);
            if (fpk.Files.FirstOrDefault(f => f.Extension == "fmdl" && Path.GetFileNameWithoutExtension(f.Path).Equals(key, StringComparison.OrdinalIgnoreCase)) is not { } pm) continue;
            string[] names = cp.Parts.Contains("/flt/", StringComparison.Ordinal) ? ["SYSTEM_LIGHT"]
                // INFERENCE (Research_ModelMerge.md, rule 2): the receiver flag altMagazineSocket picks AMMO_2ND; the other point is the fallback.
                : slot == "ammoId" ? (altMagazine ? ["AMMO_2ND", "AMMO"] : ["AMMO", "AMMO_2ND"]) : points;
            string? bone = null;
            Vector3 t = Vector3.Zero, scale = Vector3.One;
            Quaternion q = Quaternion.Identity;
            if (names.Select(n => rcPoints.FirstOrDefault(p => p.Name == n)).FirstOrDefault(p => p != null) is { } rp)
                (bone, t, q, scale) = (rp.Parent, rp.Translation, rp.Rotation, rp.Scale);
            else if (barrel is { } b && names.Select(n => b.Points.FirstOrDefault(p => p.Name == n)).FirstOrDefault(p => p != null) is { } bp && b.Model.Bone(bp.Parent) is { } bb)
                // Barrel space to receiver space: the barrel was placed at b.Pos with b.Rot on b.Bone.
                (bone, t, q) = (b.Bone, Vector3.Transform(bb.World + bp.Translation, b.Rot) + b.Pos, Quaternion.Concatenate(bp.Rotation, b.Rot));
            else if (slot != "barrelId")
            {
                notes.Add($"Model merge: {part} ({slot}) has no connect point {string.Join(" or ", names)} on the receiver or the barrel, so it is not merged.");
                continue;
            }
            try { model = FmdlMerge.Merge(model, FmdlRaw.Read(pm.Data), bone, t, q, scale); }
            catch (FmdlMerge.NotMergeableException e) { notes.Add($"Model merge: {part} ({slot}) is not merged: {e.Message}"); continue; }
            var partFcnp = fpk.Files.FirstOrDefault(f => Is(f, "fcnp", key));
            var partPoints = partFcnp != null ? Fcnp.Read(partFcnp.Data) : [];
            var partModel = FmdlModel.Read(pm.Data);
            if (slot == "barrelId") barrel = (bone, t, q, partPoints, partModel);
            // The game starts the beam, the light and the flash at the part's own point; the merged model has only the receiver's .fcnp.
            if (bone != null && rcFcnp != null && partFcnp != null && cnpHashes.ContainsKey(bone))
                foreach (var pp in partPoints.Where(x => EffectPoints.Contains(x.Name)))
                {
                    if (partModel.Bone(pp.Parent) is not { } pb || !Fcnp.Hashes(partFcnp.Data).TryGetValue("CNP_" + pp.Name, out uint ph)) continue;
                    cnpHashes["CNP_" + pp.Name] = ph;
                    var np = new Fcnp.Point(pp.Name, bone, Vector3.Transform((pb.World + pp.Translation) * scale, q) + t, Quaternion.Concatenate(pp.Rotation, q), pp.Scale);
                    int at = outPoints.FindIndex(x => x.Name == pp.Name);
                    if (at >= 0) outPoints[at] = np; else outPoints.Add(np);
                    copied.Add($"{pp.Name} of {part}");
                }
            if (fpkd.Find(cp.Parts) is { } partParts)
            {
                var pds = FoxDataSet.Read(partParts.Data);
                foreach (var (ef, name) in pds.StringValues("EffectDescription", "partName")) effects.Add((part, pds, ef, name ?? ""));
            }
            if (slot.StartsWith("laserFlash", StringComparison.Ordinal)) show.Add(GroupUnderRail);
            if (slot.StartsWith("scope", StringComparison.Ordinal)) show.Add(GroupSightMount);
            merged.Add($"{part} at {(bone == null ? "the model origin" : string.Join("/", names))}{(partModel.Bones.Count > 1 ? " (its own bones do not move)" : "")}");
        }
        if (merged.Count == 0) return;
        FmdlMerge.ShowGroups(model, show);
        fpk.Files[fpk.Files.IndexOf(rcFile)] = new PackageFile(rcPath, model.Write());
        if (copied.Count > 0) fpk.Files[fpk.Files.IndexOf(rcFcnp!)] = new PackageFile(rcFcnp!.Path, Fcnp.Write(Fcnp.FileName(rcFcnp.Data), outPoints, cnpHashes));
        files[pkg + ".fpk"] = fpk.Write();
        CopyPartEffects(files, pkg, fpkd, spec.PartsPath, effects, outPoints, notes);
        notes.Add($"Part models merged into the receiver model {Path.GetFileName(rcPath)} (V Framework draws only that model): {string.Join(", ", merged)}.");
        if (copied.Count > 0) notes.Add($"Effect points copied into the receiver's connect points {Path.GetFileName(rcFcnp!.Path)}: {string.Join(", ", copied)}.");
    }

    /// <summary>The slots of the merge with their vanilla parts, as one text: a change means that the merged model is out of date.</summary>
    public static string MergeSignature(WeaponSpec spec) => string.Join(";", WeaponSlots.MergeOrder.Select(m => $"{m.Key}={WeaponSlots.PartIn(spec, m.Key)}"));

    /// <summary>
    /// Step 2 of <see cref="Current"/>: merges the part models again in <paramref name="files"/> when a merge slot holds
    /// another part than at the last merge. With no swap it changes nothing. Returns the merge slots that the files hold.
    /// </summary>
    private static string Remerge(Dictionary<string, byte[]> files, WeaponItem w, List<string> notes)
    {
        string now = MergeSignature(w.Spec);
        if (w.Unmerged.Count == 0 || now == w.MergedSlots) return w.MergedSlots;
        string pkg = w.Spec.FpkPath[..^4];
        var fpk = FoxPackage.Read(files[pkg + ".fpk"]);
        var fpkd = FoxPackage.Read(files[pkg + ".fpkd"]);
        foreach (var (path, data) in w.Unmerged)
        {
            var into = path.EndsWith(".parts", StringComparison.Ordinal) ? fpkd : fpk;
            int at = into.Files.FindIndex(f => f.Path == path);
            if (at >= 0) into.Files[at] = new PackageFile(path, data);
        }
        files[pkg + ".fpk"] = fpk.Write();
        files[pkg + ".fpkd"] = fpkd.Write();
        MergePartModels(files, pkg, w.Spec, notes);
        return now;
    }

    /// <summary>
    /// Copies the effects of the merged parts (EffectDescription: light cone, muzzle flash) into the receiver .parts.
    /// V Framework 1.7 loads only the receiver .parts of a custom gun, so the Uragan's light did not work. An effect is
    /// copied only when its connect points are on the receiver's .fcnp; per effect name the last slot wins (a muzzle
    /// option's flash over the muzzle's), and an effect name the receiver already has stays the receiver's.
    /// </summary>
    private static void CopyPartEffects(Dictionary<string, byte[]> files, string pkg, FoxPackage fpkd, string partsPath,
        List<(string Part, FoxDataSet Parts, FoxDataSet.Entity Entity, string Name)> effects, List<Fcnp.Point> points, List<string> notes)
    {
        if (effects.Count == 0 || fpkd.Find(partsPath) is not { } rcParts) return;
        var rds = FoxDataSet.Read(rcParts.Data);
        var have = rds.StringValues("EffectDescription", "partName").Select(v => v.value).ToHashSet();
        var cnps = points.Select(p => FoxHash.StrCode64("CNP_" + p.Name)).ToHashSet();
        var copied = new List<string>();
        foreach (var (part, pds, ef, name) in effects.GroupBy(x => x.Name).Select(g => g.Last()))
        {
            var cnpList = ef.Properties.FirstOrDefault(p => pds.Name(p.NameHash) == "connectDestinationCnpNames");
            var need = cnpList == null ? [] : Enumerable.Range(0, cnpList.Count).Select(i => BinaryPrimitives.ReadUInt64LittleEndian(pds.Data.AsSpan(cnpList.Offset + cnpList.ValueOffset + i * 8))).ToList();
            if (have.Contains(name)) { notes.Add($"Model merge: the receiver has its own {name} effect, so the one of {part} is not copied."); continue; }
            if (need.FirstOrDefault(h => !cnps.Contains(h)) is var miss and not 0)
            {
                notes.Add($"Model merge: the {name} effect of {part} needs connect point {pds.Name(miss) ?? miss.ToString("x")}, which the receiver does not have, so it is not copied.");
                continue;
            }
            int k = 0;
            while (rds.Name(FoxHash.StrCode64($"EffectDescription{k:D4}")) != null) k++;
            rds = rds.WithEntityCopy(pds, ef, $"EffectDescription{k:D4}", partsPath);
            copied.Add($"{name} of {part}");
        }
        if (copied.Count == 0) return;
        fpkd.Files[fpkd.Files.IndexOf(rcParts)] = new PackageFile(rcParts.Path, rds.Data);
        files[pkg + ".fpkd"] = fpkd.Write();
        notes.Add($"Part effects copied into the receiver's {Path.GetFileName(partsPath)} (V Framework loads only that file): {string.Join(", ", copied)}.");
    }

    /// <summary>The weapon paint packages of the receivers (fova/receiver/&lt;model&gt;_def, _cam, _clv).</summary>
    public const string PaintFolder = "/Assets/tpp/pack/collectible/fova/receiver/";

    /// <summary>The camo layer packages (fova/color/wfv_camo_c00 to c94).</summary>
    public const string CamoFolder = "/Assets/tpp/pack/collectible/fova/color/";

    /// <summary>The swatch of a camo in the game's customisation menu (128×128), as an .ftex path.</summary>
    public static string CamoIcon(string camo) => $"/Assets/tpp/ui/texture/Customize/color/ui_camo4_{camo}_lym.ftex";

    /// <summary>The camos the game data has a layer package for, c00 to c94.</summary>
    public static List<string> Camos(GameData game) =>
        Enumerable.Range(0, 95).Select(i => $"c{i:D2}").Where(c => game.TryRead($"{CamoFolder}wfv_camo_{c}.fpk") != null).ToList();

    /// <summary>
    /// The paint of a model (paintFpk = its fova "_def" package, fv2 = the file in it, or null for the first one): the
    /// default paint, or with a camo the camo-ready paint (the "_cam" package: base, gloss and layer mask) plus the camo
    /// pattern in the Layer slot (wfv_camo_cNN), as the customisation menu paints a vanilla gun. Packs = the texture packs
    /// the swaps need. A model with no "_cam" package keeps the default paint, with a note.
    /// </summary>
    internal static (IReadOnlyList<Fv2File.TextureSwap> Swaps, string[] Packs, string Name)? Paint(GameData game, string paintFpk, string? fv2, string? camo, List<string> notes)
    {
        static Fv2File? Fv2(byte[]? fpk, string? path) => fpk == null ? null
            : FoxPackage.Read(fpk).Files.FirstOrDefault(f => path != null ? f.Path == path : f.Extension == "fv2") is { } f ? new Fv2File(f.Data) : null;
        string stem = paintFpk[..^4], name = stem[(stem.LastIndexOf('/') + 1)..];
        if (camo != null)
        {
            if (!WeaponTuning.IsCamo(camo))
                throw new ArgumentException($"Camo \"{camo}\" is not one of c00 to c94.");
            var layer = Fv2(game.TryRead($"{CamoFolder}wfv_camo_{camo}.fpk"), null)
                ?? throw new InvalidDataException($"The camo package wfv_camo_{camo}.fpk is not in the game files.");
            if (Fv2(game.TryRead(stem + "_cam.fpk"), null) is { } cam)
                return ([.. cam.TextureSwaps(), .. layer.TextureSwaps()], [stem + "_cam.pftxs", $"{CamoFolder}wfv_camo_{camo}.pftxs"], $"camo {camo} ({name}_cam.fv2 + wfv_camo_{camo}.fv2)");
            notes.Add($"{name}: no camo paint (_cam) in the game for this model, so it keeps the default paint.");
        }
        return Fv2(game.TryRead(paintFpk), fv2) is { } def ? (def.TextureSwaps(), [stem + ".pftxs"], $"default paint ({name}.fv2)") : null;
    }

    /// <summary>The flat placeholder textures a vanilla weapon model leaves for its paint (common_source/flat).</summary>
    public static bool IsPaintPlaceholder(ulong texture) =>
        PathDictionary.Bundled.TryGetPath(texture)?.StartsWith("/Assets/tpp/common_source/flat/", StringComparison.Ordinal) == true;

    private static FoxPackage Merge(GameData game, MgsvMod mod, string path, bool isFpkd, List<string> notes)
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
}
