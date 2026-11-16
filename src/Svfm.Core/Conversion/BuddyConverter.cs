using System.Text.RegularExpressions;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>
/// A vanilla buddy package a mod replaces (/Assets/tpp/pack/buddy/quiet|horse|dog/…), with the vanilla costume it
/// stands for when known (develop ID, lang IDs and icon of its R&amp;D row).
/// </summary>
public sealed record BuddyReplacement(string VanillaPackage, string Buddy, int? DevelopId, string? NameLang, string? InfoLang, string? VanillaIcon)
{
    /// <summary>What won't work as in vanilla with the mod's model (e.g. her rifle not shown); the port is still made, as the mod plays.</summary>
    public List<string> Warnings { get; init; } = [];
    /// <summary>D-Dog: the vanilla gear value of the replaced package (0 NAKED … 4 SNEAKING (KNIFE)); the port plays like it.</summary>
    public int? DogGear { get; init; }
    /// <summary>D-Horse: the vanilla tack value of the replaced package (0 NORMAL … 6 WESTERN CLOTH, V Framework's soundType); the port plays like it.</summary>
    public int? HorseTack { get; init; }
    /// <summary>Ability lines of the vanilla costume's R&amp;D row (BATTLE DRESS: ability_1082).</summary>
    public IReadOnlyList<string> PowerUpInfoLangs { get; init; } = [];
    /// <summary>
    /// The package is the mod's own, at a new path (a Zeta buddy mod's LoadBuddyParts package), not a vanilla one it
    /// replaces: the port copies it as installed under its own name.
    /// </summary>
    public bool OwnPackage { get; init; }
    /// <summary>Name and description from the mod's script when it has no text of its own (Zeta: modName + the package's option).</summary>
    public string? Title { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Where a buddy port's models and .parts go. The default (Auto = Keep) copies the mod as SnakeBite installs it: every
/// file inside the costume's own package at the path the replacer put it. V Framework loads one costume package at a
/// time, so nothing else sees those paths, and every keep-paths build worked in game (GRAY XOF copy, Skull Swap, Cuba's
/// prototype; Oct 26). Moving files is an untested change to the mod: moving GRAY XOF's .parts crashed (the game names
/// it), moving models works on NAKED's packages. The other values stay for tests.
/// </summary>
public enum BuddyModelPaths
{
    /// <summary>Keep: the mod as installed.</summary>
    Auto,
    /// <summary>A changed model gets a new path; the .parts stays at its vanilla path (naming the moved model).</summary>
    ModelOnly,
    /// <summary>A changed model and its .parts get new paths.</summary>
    NewWhenChanged,
    /// <summary>Every file at its vanilla path inside the outfit's own package, as the replacer had it.</summary>
    Keep,
    /// <summary>Even unchanged main models move (tests).</summary>
    AlwaysNew,
}

public sealed class BuddyConversionOptions
{
    public required string Id { get; init; }
    /// <summary>Name in Sortie Prep; null = the mod's text for the vanilla costume, else the package name.</summary>
    public string? Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Where the package's models and .parts go (see BuddyModelPaths); Auto by default.</summary>
    public BuddyModelPaths ModelPaths { get; init; } = BuddyModelPaths.Auto;
    /// <summary>Quiet: show NAKED's bruises (when a script turns them on).</summary>
    public bool Bruise { get; init; }
    /// <summary>D-Dog: how the gear plays; null = like the vanilla gear the mod replaced (DogGearBehaviour.Vanilla).</summary>
    public DogGearBehaviour? Dog { get; init; }
    /// <summary>D-Horse: how the tack plays; null = like the vanilla tack the mod replaced (HorseTackBehaviour.Vanilla).</summary>
    public HorseTackBehaviour? Horse { get; init; }
    /// <summary>
    /// The R&amp;D icon; null = the mod's own icon for the costume when it ships one, else a render (Quiet). Choices:
    /// IconChoice.Render (Quiet's chest or D-Dog's left flank, framed like the vanilla icons), ModIcon, Picture (an uploaded image),
    /// GameIcon (any vanilla icon of this buddy), Vanilla (the replaced costume's).
    /// </summary>
    public IconChoice? Icon { get; init; }
    public PathDictionary? Dictionary { get; init; }
    /// <summary>R&amp;D row; null = available from the start, free to develop, and the vanilla costume's deployment cost.</summary>
    public DevelopRow? Develop { get; init; }
}

/// <summary>
/// Buddy replacers (The Fully Clothed Sniper: Quiet's four costume packages) as V Framework buddy outfits
/// (RegisterBuddyOutfit). Recipe from the Buddy_Outfits wiki and the vanilla packages:
/// <list type="number">
/// <item>The outfit's package is the vanilla buddy package with the mod's files merged over it (as SnakeBite and DMM
/// install them), under a new name; the .pftxs goes with it (its .fox2 loads the pack named like the package).</item>
/// <item>Files the mod changed at vanilla paths that the vanilla package names elsewhere (models, .parts) move to new
/// paths, and the .parts/.fox2 that name them are updated, so the vanilla costume stays as it is when both are loaded.</item>
/// <item>Name and text come from the mod's lang files (keyed by the vanilla costume's lang IDs), the icon from the mod's
/// icon at the vanilla icon path; neither is installed at the vanilla path.</item>
/// </list>
/// </summary>
public static class BuddyConverter
{
    // A mod changes a costume when it ships any of the costume package's three files: its .fpk/.fpkd, or its .pftxs alone
    // (texture-pack retextures: D-Dog Camos, Black Suits), as for body packages.
    private static readonly Regex Package = new(@"^/Assets/tpp/pack/buddy/(quiet|horse|dog)/([a-z0-9_]+)\.(fpkd?|pftxs)$", RegexOptions.Compiled);

    /// <summary>Vanilla Quiet costume packages: develop ID, icon name (EquipIcon/buddy/ui_qwp_suit_{icon}_alp).</summary>
    internal static readonly Dictionary<string, (int dev, string icon)> QuietCostumes = new(StringComparer.Ordinal)
    {
        ["buddy_quiet2_00"] = (51000, "qui0"),
        ["buddy_quiet2_01"] = (51001, "qui0_bld"),
        ["buddy_quiet2_02"] = (51002, "qui0_slv"),
        ["buddy_quiet2_03"] = (51003, "qui0_gld"),
        // The two blueprint costumes: package 04 holds qui4 (GRAY XOF, 51005), 05 qui5 (SNIPER WOLF, 51004).
        ["buddy_quiet2_04"] = (51005, "qui4"),
        ["buddy_quiet2_05"] = (51004, "qui5"),
    };

    /// <summary>
    /// Vanilla D-Dog gear packages: develop ID, icon (EquipIcon/buddy/ui_dd_ddg0_{icon}_alp) and gear value (the value
    /// V Framework and the game keep per gear: 0 NAKED, 1 FULTON, 2 STUN, 3 BATTLE DRESS, 4 KNIFE; V_DogCommandType is
    /// valued the same). Package N holds the model whose number matches its R&amp;D row's icon: 01 ddg0_main4 / m4 (24005
    /// TACTICAL (FULTON)), 02 main1 / m1 (24003 SNEAKING (STUN)), 03 main3 / m3 (24004 BATTLE DRESS), 04 main2 + blade
    /// bl00 / m2 (24002 SNEAKING (KNIFE)); D-Dog Camos replaces 02 and 04 and ships icons m1 and m2, which agrees.
    /// </summary>
    private static readonly Dictionary<string, (int dev, string icon, int gear)> DogGears = new(StringComparer.Ordinal)
    {
        ["buddy_dog2_00"] = (24001, "m0", 0),
        ["buddy_dog2_01"] = (24005, "m4", 1),
        ["buddy_dog2_02"] = (24003, "m1", 2),
        ["buddy_dog2_03"] = (24004, "m3", 3),
        ["buddy_dog2_04"] = (24002, "m2", 4),
    };

    /// <summary>
    /// Vanilla D-Horse tack packages: develop ID and icon (EquipIcon/buddy/ui_dh_{icon}_alp; null = not in the game data
    /// seen). From the packages' models and the R&amp;D rows (Oct 26): 00 hrs0 NORMAL (25001); 02_0/02_1/02_2 hrs2_main0/1/2
    /// BATTLE DRESS grades 2–4 (25002–25004, all named name_dh_25002; icons hrs2_m0–m2); 03 hrs3 FURICORN (25005); the two
    /// DLC tacks in 00.dat: 04 hrs4 WESTERN CLOTH (25006, the model with the lasso), 05 hrs5 PARADE CLOTH (25007; the
    /// Parade Tack retextures replace 05). Their icons are not in any dump on E:\.
    /// </summary>
    private static readonly Dictionary<string, (int dev, string? icon, int tack)> HorseTacks = new(StringComparer.Ordinal)
    {
        ["buddy_horse2_00"] = (25001, "hrs0_m0", 0),
        ["buddy_horse2_02_0"] = (25002, "hrs2_m0", 2),
        ["buddy_horse2_02_1"] = (25003, "hrs2_m1", 3),
        ["buddy_horse2_02_2"] = (25004, "hrs2_m2", 4),
        ["buddy_horse2_03"] = (25005, "hrs3_m0", 1),
        ["buddy_horse2_04"] = (25006, null, 6),
        ["buddy_horse2_05"] = (25007, null, 5),
    };

    public const string IconDir = "/Assets/tpp/ui/texture/EquipIcon/buddy/";

    public static List<BuddyReplacement> Detect(GameData game, MgsvMod mod) =>
        mod.ArchiveFiles.Keys.Select(k => Package.Match(k)).Where(m => m.Success)
            .Select(m => (buddy: m.Groups[1].Value, name: m.Groups[2].Value)).Distinct()
            .Select(x => (x.buddy, x.name, pkg: $"/Assets/tpp/pack/buddy/{x.buddy}/{x.name}"))
            .Where(x => game.Contains(x.pkg + ".fpk"))
            .OrderBy(x => x.pkg, StringComparer.Ordinal)
            .Select(x => (QuietCostumes.TryGetValue(x.name, out var c)
                ? new BuddyReplacement(x.pkg, x.buddy, c.dev, $"name_qe_{c.dev}", $"info_qe_{c.dev}", $"{IconDir}ui_qwp_suit_{c.icon}_alp")
                : DogGears.TryGetValue(x.name, out var d)
                ? new BuddyReplacement(x.pkg, x.buddy, d.dev, $"name_dd_{d.dev}", $"info_dd_{d.dev}", $"{IconDir}ui_dd_ddg0_{d.icon}_alp")
                    { DogGear = d.gear, PowerUpInfoLangs = d.gear == 3 ? ["ability_1082"] : [] }
                : HorseTacks.TryGetValue(x.name, out var h)
                ? new BuddyReplacement(x.pkg, x.buddy, h.dev, HorseLang(h.dev, "name"), HorseLang(h.dev, "info"), h.icon != null ? $"{IconDir}ui_dh_{h.icon}_alp" : null) { HorseTack = h.tack }
                : new BuddyReplacement(x.pkg, x.buddy, null, null, null, null)) with { Warnings = RigWarnings(game, mod, x.pkg) })
            .ToList();

    /// <summary>The lang ID of a horse tack's name or text: the BATTLE DRESS grades share 25002's (their R&amp;D rows name it).</summary>
    private static string HorseLang(int dev, string what) =>
        (GameKnowledge.VanillaDevelop(dev)?.Name is { } n && n.StartsWith("name_", StringComparison.Ordinal) ? n[5..] : $"dh_{dev}") is var id ? $"{what}_{id}" : "";

    /// <summary>
    /// Zeta buddy mods (/mod/zeta/*.lua with LoadBuddyParts): the script hands Zeta one package per costume slot
    /// ({BuddyType = "Dog", CostumeType = i, FpkPath = …} for every gear), so the mod replaces every costume with it.
    /// Each package it names that the mod ships becomes one buddy outfit (Blade Wolf: DEFAULT, VISOR_ON, DESPERADO, the
    /// options of its Zeta menu; GZ Watch Dog: one). The packages are complete (they are loaded instead of the vanilla one,
    /// as V Framework loads an outfit's), so the port copies each as installed.
    /// </summary>
    public static List<BuddyReplacement> DetectZeta(MgsvMod mod)
    {
        var found = new List<BuddyReplacement>();
        foreach (var (path, data) in mod.GameFiles.Where(kv => kv.Key.StartsWith("/mod/zeta/", StringComparison.OrdinalIgnoreCase) && kv.Key.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
        {
            string lua = System.Text.Encoding.UTF8.GetString(data);
            if (!lua.Contains("LoadBuddyParts", StringComparison.Ordinal)) continue;
            var type = Regex.Match(lua, @"BuddyType\s*=\s*""(Dog|Horse|Quiet)""", RegexOptions.IgnoreCase);
            string buddy = type.Success ? type.Groups[1].Value.ToLowerInvariant() : "dog";
            string modName = Regex.Match(lua, @"modName\s*=\s*""([^""]*)""") is { Success: true } mn ? mn.Groups[1].Value : mod.Metadata.Name;
            string? modDesc = Regex.Match(lua, @"modDesc\s*=\s*""([^""]*)""") is { Success: true } md ? md.Groups[1].Value : null;
            // Packages named in the script, with the table key in front when there is one (DEFAULT="…fpk"). A path built
            // from a menu value (Retali8 Quiet Suits: "…/buddy_quiet_"..n..".fpk") gives each shipped package, named by the menu.
            var menu = ZetaScript.MenuList(lua);
            var packages = ZetaScript.ShippedPackages(lua, mod.ArchiveFiles);
            foreach (var (pkg, number, key) in packages)
            {
                bool named = packages.Count > 1 && key != null && !key.Equals("DOG_FPK", StringComparison.OrdinalIgnoreCase) && !key.Equals("FpkPath", StringComparison.OrdinalIgnoreCase);
                string? label = named ? key!.Replace('_', ' ') : number is { } n ? (n >= 1 && n <= menu.Count ? menu[n - 1].ToUpperInvariant() : n.ToString()) : null;
                string title = Regex.Replace(modName, @"\s*\(.*?\)\s*", " ").Trim().ToUpperInvariant() + (label != null && packages.Count > 1 ? $" ({label})" : "");
                found.Add(new BuddyReplacement(pkg, buddy, null, null, null, null) { OwnPackage = true, Title = title, Description = modDesc });
            }
        }
        return found;
    }

    /// <summary>
    /// What the mod's model loses against the buddy's package: connect points (.fcnp; where the buddy's weapon and
    /// effects attach) the mod's file lacks, and bones they hang on that its model lacks. Skull Swap: a Skull body
    /// without Quiet's sniper-sling bone SKL_642_SNIPER_SIM, the Skull's own connect points without CNP_SNRROOT. The
    /// original mod runs with it and her rifle is not shown (user, Oct 25), so this is a warning, not a refusal.
    /// </summary>
    public static List<string> RigWarnings(GameData game, MgsvMod mod, string van)
    {
        var problems = new List<string>();
        if (!mod.ArchiveFiles.TryGetValue(van + ".fpk", out var mb) || game.TryRead(van + ".fpk") is not { } vb) return problems;
        var modFpk = FoxPackage.Read(mb);
        var vanFpk = FoxPackage.Read(vb);
        var vanModel = SuitRender.MainModel(vanFpk);
        if (vanModel == null) return problems;
        // The model the port shows: the mod's version of the vanilla main model's file, else the vanilla one.
        string mainPath = vanFpk.Files.Where(f => f.Extension == "fmdl").OrderByDescending(f => f.Data.Length).First().Path;
        var model = modFpk.Find(mainPath) is { } mm ? FmdlModel.Read(mm.Data) : vanModel;
        var modelBones = model.Bones.Select(b => b.NameHash).ToHashSet();
        var vanBones = vanModel.Bones.Select(b => b.NameHash).ToHashSet();
        foreach (var vf in vanFpk.Files.Where(f => f.Extension == "fcnp"))
        {
            var mine = modFpk.Find(vf.Path)?.Data ?? vf.Data;
            var (vanCnps, vanParents) = ConnectPoints(vf.Data);
            var (myCnps, myParents) = ConnectPoints(mine);
            var lostCnps = vanCnps.Except(myCnps).ToList();
            if (lostCnps.Count > 0)
                problems.Add($"{vf.Path[(vf.Path.LastIndexOf('/') + 1)..]}: the mod's connect points lack {string.Join(", ", lostCnps)}" +
                             (lostCnps.Contains("CNP_SNRROOT") ? " (CNP_SNRROOT holds Quiet's rifle: it won't show, as in the original mod)." : " (what attaches there won't show, as in the original mod)."));
            var lostBones = myParents.Concat(vanParents).Distinct()
                .Where(b => vanBones.Contains(Hashing.FoxHash.StrCode64(b)) && !modelBones.Contains(Hashing.FoxHash.StrCode64(b))).ToList();
            if (lostBones.Count > 0 && mainPath.EndsWith(Path.ChangeExtension(vf.Path, ".fmdl")[(vf.Path.LastIndexOf('/') + 1)..], StringComparison.Ordinal))
                problems.Add($"The mod's model lacks bone{(lostBones.Count == 1 ? "" : "s")} {string.Join(", ", lostBones)} that the {Buddy(van)}'s connect points hang on (it was made for another character; adding bones would mean editing the model).");
        }
        return problems;
    }

    private static string Buddy(string van) => van.Contains("/quiet/") ? "Quiet costume" : van.Contains("/dog/") ? "D-Dog gear" : "D-Horse tack";

    /// <summary>Connect point names (CNP_*) and the bones they hang on (SKL_*) in an .fcnp.</summary>
    private static (HashSet<string> cnps, HashSet<string> bones) ConnectPoints(byte[] fcnp)
    {
        var text = System.Text.Encoding.Latin1.GetString(fcnp);
        return (Regex.Matches(text, "CNP_[0-9A-Za-z_]+").Select(m => m.Value).ToHashSet(StringComparer.Ordinal),
                Regex.Matches(text, "SKL_[0-9A-Z_]+").Select(m => m.Value).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Makes the costume item and adds it to the project (<see cref="ToItem"/>).</summary>
    public static BuddyOutfitItem AddTo(ModProject p, GameData game, MgsvMod mod, BuddyReplacement r, BuddyConversionOptions o)
    {
        var item = ToItem(p, game, mod, r, o);
        p.Items.Add(item);
        return item;
    }

    /// <summary>Makes the costume item. The project gives the names only; the item is not added to it.</summary>
    public static BuddyOutfitItem ToItem(ModProject p, GameData game, MgsvMod mod, BuddyReplacement r, BuddyConversionOptions o)
    {
        var (key, prefix) = p.NewItemNames(o.Id);
        var notes = new List<string>(r.Warnings);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string van = r.VanillaPackage;
        // The outfit's package sits next to the vanilla buddy packages (a mod's own package leaves the mod's folder).
        string pkg = r.OwnPackage ? $"/Assets/tpp/pack/buddy/{r.Buddy}/{prefix}" : $"{van[..(van.LastIndexOf('/') + 1)]}{prefix}";

        // 1. Packages: vanilla + the mod's files over it.
        var fpk = BodyConverter.Merge(game, mod, van + ".fpk", isFpkd: false, notes);
        var fpkd = BodyConverter.Merge(game, mod, van + ".fpkd", isFpkd: true, notes);
        // The package the port starts from: the vanilla one, or (a mod's own package) the mod's as installed.
        var vanFpk = FoxPackage.Read(game.TryRead(van + ".fpk") ?? mod.ArchiveFiles[van + ".fpk"]);
        var vanFpkd = (game.TryRead(van + ".fpkd") ?? (r.OwnPackage ? mod.ArchiveFiles.GetValueOrDefault(van + ".fpkd") : null)) is { } vd ? FoxPackage.Read(vd) : new FoxPackage { IsFpkd = true };

        // 2. Models and .parts that differ from vanilla move to new paths, and the .parts/.fox2 that name them follow; a
        // .parts that changes because its model moved moves too (repeated until nothing more changes).
        var paths = o.ModelPaths == BuddyModelPaths.Auto ? BuddyModelPaths.Keep : o.ModelPaths;
        var renames = new List<(string from, string to)>();
        var moved = new HashSet<string>(StringComparer.Ordinal);
        // Only models and .parts move (as in The Fully Clothed Sniper, which works). Skull Swap 0.1 also moved its changed
        // .fcnp/.frdv and crashed on selection, while the original mod runs; so other files keep their paths inside the
        // outfit's own package (V Framework loads it instead of the vanilla one).
        bool Movable(PackageFile f) => f.Extension == "fmdl" || f.Extension == "parts" && paths != BuddyModelPaths.ModelOnly;
        foreach (var (pack, vanilla) in new[] { (fpk, vanFpk), (fpkd, vanFpkd) })
            foreach (var f in pack.Files)
                if (!Movable(f) && f.Extension != "fox2" && vanilla.Find(f.Path) is { } vf && !vf.Data.AsSpan().SequenceEqual(f.Data))
                    notes.Add($"{f.Path}: changed by the mod; kept at its path inside the outfit's own package.");
        for (bool again = true; again;)
        {
            again = false;
            foreach (var (pack, vanilla) in new[] { (fpk, vanFpk), (fpkd, vanFpkd) })
                for (int i = 0; i < pack.Files.Count; i++)
                {
                    var f = pack.Files[i];
                    if (f.Extension is "parts" or "fox2")
                    {
                        var ds = FoxDataSet.Read(f.Data);
                        foreach (var (from, to) in renames) ds = ds.Replacing(from, to, out _);
                        if (!ReferenceEquals(ds.Data, f.Data) && !ds.Data.AsSpan().SequenceEqual(f.Data)) pack.Files[i] = f = new PackageFile(f.Path, ds.Data);
                    }
                    if (!Movable(f) || moved.Contains(f.Path) || paths == BuddyModelPaths.Keep) continue;
                    if (vanilla.Find(f.Path) is not { } vf) continue;
                    bool changed = !vf.Data.AsSpan().SequenceEqual(f.Data);
                    // AlwaysNew moves the main model even unchanged (a .parts then follows because it changed).
                    if (!changed && !(paths == BuddyModelPaths.AlwaysNew && f.Extension == "fmdl" && f.Path.Contains("_main0_"))) continue;
                    string dir = f.Path[..(f.Path.LastIndexOf('/') + 1)], file = f.Path[(f.Path.LastIndexOf('/') + 1)..];
                    // qui0_main0_def.fmdl → svfm_tfcs_khaki_main0_def.fmdl: the part after the character code stays.
                    string newPath = $"{dir}{prefix}_{Regex.Replace(file, @"^[a-z]{3}[0-9]_", "")}";
                    pack.Files[i] = new PackageFile(newPath, f.Data);
                    renames.Add((f.Path, newPath));
                    moved.Add(newPath);
                    again = true;
                }
        }
        bool anyChange = new[] { (fpk, vanFpk), (fpkd, vanFpkd) }.Any(x => x.Item1.Files.Any(f => x.Item2.Find(f.Path) is not { } v || !v.Data.AsSpan().SequenceEqual(f.Data)));
        if (renames.Count == 0 && !anyChange && !mod.ArchiveFiles.ContainsKey(van + ".pftxs") && !r.OwnPackage)
            notes.Add($"The mod changes nothing in {van[(van.LastIndexOf('/') + 1)..]} but its package files; the outfit looks like the vanilla costume.");
        foreach (var (from, to) in renames)
            if (!new[] { fpk, fpkd }.Any(pk => pk.Files.Any(f => f.Extension is "parts" or "fox2" && FoxDataSet.Read(f.Data).Strings.Values.Contains(to))))
                notes.Add($"{from} → {to}: nothing in the package names it.");
        // 2a. Physics (.sim) bound to bones the vanilla model had and the mod's model lacks (Skull Swap: Quiet's hair
        // physics on a Skull body without hair bones): noted, kept as the mod has it (the original runs with it).
        foreach (var n in OrphanPhysics(fpk, fpkd, vanFpk)) notes.Add(n);

        PackageOrder.ApplySnakeBiteOrder(fpk);
        PackageOrder.ApplySnakeBiteOrder(fpkd);
        files[pkg + ".fpk"] = fpk.Write();
        files[pkg + ".fpkd"] = fpkd.Write();

        // 3. Texture pack, named like the package (TexturePackLoadConditioner with an empty path loads that one).
        var vanPack = game.TryRead(van + ".pftxs") is { } vp ? TexturePack.Read(vp) : null;
        var texPack = mod.ArchiveFiles.TryGetValue(van + ".pftxs", out var mp) ? TexturePack.Read(mp) : vanPack;
        if (texPack != null)
        {
            if (vanPack != null && texPack != vanPack)
            {
                int added = 0;
                foreach (var t in vanPack.Textures.Where(t => texPack.Find(t.PathCode) == null)) { texPack.Textures.Add(t); added++; }
                if (added > 0) notes.Add($"Added back {added} vanilla texture(s) the mod's pack left out.");
            }
            files[pkg + ".pftxs"] = texPack.Write();
        }
        else notes.Add(r.OwnPackage ? $"The mod's package {van[(van.LastIndexOf('/') + 1)..]} has no texture pack (its textures are loose files, copied as they are)."
                                    : $"No vanilla {van}.pftxs in the game data given.");

        // 4. Loose textures the models use: new paths as they are; textures the mod installs at vanilla paths as complete
        // copies under new names, the models pointed at them (InstalledTextures; never a mix of the mod's and vanilla's pieces).
        {
            var relocated = InstalledTextures.Collect(game, mod, TextureCheck.Referenced(files), prefix,
                mod.ArchiveFiles.TryGetValue(van + ".pftxs", out var modPackBytes) ? TexturePack.Read(modPackBytes) : null, vanPack, files, notes);
            InstalledTextures.Apply(fpk, relocated, notes);
            files[pkg + ".fpk"] = fpk.Write();
            if (texPack != null && InstalledTextures.RestoreVanilla(texPack, vanPack, relocated)) files[pkg + ".pftxs"] = texPack.Write();
        }

        // 5. Icon.
        string iconPath = r.Buddy switch { "dog" => $"{IconDir}ui_dd_{prefix}_alp", "horse" => $"{IconDir}ui_dh_{prefix}_alp", _ => $"{IconDir}ui_qwp_suit_{prefix}_alp" };
        var kind = r.Buddy switch { "quiet" => IconKind.QuietCostume, "dog" => IconKind.DDog, _ => IconKind.DHorse };
        bool modHasIcon = r.VanillaIcon != null && mod.ArchiveFiles.ContainsKey(r.VanillaIcon + ".ftex");
        var choice = o.Icon ?? (modHasIcon ? new IconChoice.ModIcon(r.VanillaIcon!) : new IconChoice.Render());
        RgbaImage? made = null;
        switch (choice)
        {
            case IconChoice.ModIcon mi when mod.ArchiveFiles.TryGetValue(Ftex(mi.FtexPath) + ".ftex", out var ft):
                // Copied as it is (the mod's own file at the outfit's own path).
                files[iconPath + ".ftex"] = ft;
                for (int k = 1; k <= 6; k++)
                    if (mod.ArchiveFiles.TryGetValue($"{Ftex(mi.FtexPath)}.{k}.ftexs", out var d)) files[$"{iconPath}.{k}.ftexs"] = d;
                break;
            case IconChoice.ModIcon mi:
                throw new FileNotFoundException($"The mod has no icon {mi.FtexPath}.");
            case IconChoice.Render rr:
                made = r.Buddy switch
                {
                    "quiet" => RenderQuiet(game, o.Dictionary, mod, fpk, texPack, rr, notes, files, fpkd),
                    "dog" => RenderDog(game, o.Dictionary, mod, fpk, texPack, rr, notes, files, fpkd),
                    _ => RenderHorse(game, o.Dictionary, mod, fpk, texPack, rr, notes, files, fpkd),
                };
                break;
            case IconChoice.Picture pic:
                made = pic.Icon();
                break;
            case IconChoice.GameIcon gi:
                iconPath = VanillaIcons.Check(game, kind, gi.FtexPath);
                break;
            case IconChoice.Vanilla:
                iconPath = r.VanillaIcon ?? throw new InvalidOperationException($"No vanilla icon is known for {van}; pick a game icon.");
                break;
            default:
                throw new ArgumentException($"{choice.GetType().Name} is not an icon choice for a buddy outfit.");
        }
        if (made != null)
        {
            var ft = SuitIcon.ToGameFiles(made);
            files[iconPath + ".ftex"] = ft.Ftex;
            foreach (var (n, d) in ft.Ftexs) files[$"{iconPath}.{n}.ftexs"] = d;
        }

        // 6. Text: the user's, else the mod's for the vanilla costume (every language it has), else the package name.
        var name = new Dictionary<string, string>(StringComparer.Ordinal);
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (lang, entries) in ModLang(mod))
        {
            if (r.NameLang != null && entries.Get(Hashing.FoxHash.StrCode32(r.NameLang)) is { } n) name[lang] = n;
            if (r.InfoLang != null && entries.Get(Hashing.FoxHash.StrCode32(r.InfoLang)) is { } t) info[lang] = t;
        }
        // Without the mod's own text: the mod's name, and its description. A mod replacing several costumes without text
        // (Black Suits: four D-Dog gears) gets the vanilla costume's name after it, so each item has its own name.
        string modTitle = Regex.Replace(mod.Metadata.Name, @"\s*\(.*?\)\s*", " ").Trim().ToUpperInvariant();
        string title = o.Name ?? (name.TryGetValue("eng", out var en) ? en
            : r.Title ?? (Detect(game, mod).Count > 1 && VanillaText(game, r.NameLang) is { } vn ? $"{modTitle} ({vn})" : modTitle));
        if (o.Description == null && info.Count == 0 && (r.Description ?? mod.Metadata.Description) is { Length: > 0 } md) info["eng"] = md.Trim();

        var vanRow = r.DevelopId is { } dev ? GameKnowledge.VanillaDevelop(dev) : null;
        var spec = new BuddyOutfitSpec
        {
            Key = key, Buddy = r.Buddy, FpkPath = pkg + ".fpk", Bruise = o.Bruise,
            Dog = r.Buddy == "dog" ? o.Dog ?? (r.DogGear is { } g ? DogGearBehaviour.Vanilla(g) : null) : null,
            Horse = r.Buddy == "horse" ? o.Horse ?? (r.HorseTack is { } ht && ht != 0 ? HorseTackBehaviour.Vanilla(ht) : null) : null,
            PowerUpInfoLangs = [.. r.PowerUpInfoLangs],
            NameLang = $"{prefix}_name", InfoLang = $"{prefix}_info", IconFtexPath = iconPath,
            Develop = o.Develop ?? new DevelopRow { Grade = 1, GmpCost = 0, InitiallyAvailable = true, UsageGmpCost = vanRow?.UsageGmp ?? 0 },
        };
        if (r.Buddy == "dog" && o.Dog == null && r.OwnPackage)
            // The mod gives every gear the same look and Zeta keeps each gear's own behaviour; one V Framework gear has one.
            notes.Add("Plays like NAKED. Pick another behaviour in the tool if wanted.");
        if (r.Buddy == "dog" && o.Dog == null && r.DogGear is { } dg && dg != 0)
            notes.Add($"Plays like the vanilla gear it replaced ({VanillaText(game, r.NameLang) ?? $"gear {dg}"}): {string.Join(", ", DescribeDog(spec.Dog!))}.");
        if (r.Buddy == "horse" && o.Horse == null && r.HorseTack is { } tk && tk != 0)
            notes.Add($"Plays like the vanilla tack it replaced ({HorseTackBehaviour.TackNames[tk]}): {string.Join(", ", DescribeHorse(spec.Horse!))}.");
        spec.Header.Add(r.OwnPackage
            ? $"{title}: {r.Buddy} costume from \"{mod.Metadata.Name}\" by {mod.Metadata.Author} (its package {van[(van.LastIndexOf('/') + 1)..]}, copied under this outfit's name). No vanilla costume is changed."
            : $"{title}: {r.Buddy} costume from \"{mod.Metadata.Name}\" by {mod.Metadata.Author} ({van[(van.LastIndexOf('/') + 1)..]}), in its own package. The vanilla costume is unchanged.");
        // 7. The port against the mod as installed (PortCheck).
        var check = PortCheck.Compare(game, mod, files, [(van, pkg)]);
        var item = new BuddyOutfitItem { Id = o.Id, Key = key, Prefix = prefix, Spec = spec, Title = title };
        item.SetText(spec.NameLang, title);
        if (o.Name == null) foreach (var (lang, t) in name.Where(kv => kv.Key != "eng")) item.SetText(spec.NameLang, lang, t);
        if (o.Description != null) item.SetText(spec.InfoLang!, o.Description);
        else if (info.Count > 0) foreach (var (lang, t) in info) item.SetText(spec.InfoLang!, lang, t);
        else item.SetText(spec.InfoLang!, "");
        foreach (var (path, d) in files) item.Files[path] = d;
        item.Notes.AddRange(notes);
        item.Notes.AddRange(check.Where(c => !c.Error).Select(c => c.Text));
        item.Problems.AddRange(check.Where(c => c.Error).Select(c => c.Text));
        return item;
    }

    /// <summary>
    /// The model the buddy shows: the one the .parts names that the package's .fox2 names (partsFile). A replacer's
    /// package can keep the vanilla model next to its own (GZ Guard Dog: ddg0_main0 stays, the .fox2 names the GZ dog's
    /// .parts). Without the .fpkd, or when nothing is named, the package's largest model.
    /// </summary>
    /// <param name="fpk">The package's .fpk.</param>
    /// <param name="fpkd">Its .fpkd (with the .fox2 and .parts), or null.</param>
    public static FmdlModel? ShownModel(FoxPackage fpk, FoxPackage? fpkd)
    {
        if (fpkd != null)
            foreach (var fox2 in fpkd.Files.Where(f => f.Extension == "fox2"))
                foreach (var partsPath in FoxDataSet.Read(fox2.Data).Strings.Values.Where(v => v.EndsWith(".parts", StringComparison.Ordinal)))
                    if (fpkd.Find(partsPath) is { } parts)
                        foreach (var model in FoxDataSet.Read(parts.Data).ModelFiles)
                            if (model != null && fpk.Find(model) is { } m) return FmdlModel.Read(m.Data);
        return SuitRender.MainModel(fpk);
    }

    /// <summary>Notes every physics file that drives bones the vanilla package's models have and the port's models don't.</summary>
    public static List<string> OrphanPhysics(FoxPackage fpk, FoxPackage fpkd, FoxPackage vanillaFpk)
    {
        var notes = new List<string>();
        static HashSet<ulong> Bones(FoxPackage p) => p.Files.Where(f => f.Extension == "fmdl")
            .SelectMany(f => { try { return FmdlModel.Read(f.Data).Bones.Select(b => b.NameHash); } catch (InvalidDataException) { return []; } })
            .ToHashSet();
        var have = Bones(fpk);
        var had = Bones(vanillaFpk);
        for (int i = 0; i < fpkd.Files.Count; i++)
        {
            var parts = fpkd.Files[i];
            if (parts.Extension != "parts") continue;
            var ds = FoxDataSet.Read(parts.Data);
            foreach (var (_, sim) in ds.StringValues("SimDescription", "simFile").ToList())
            {
                if (string.IsNullOrEmpty(sim) || fpkd.Find(sim) is not { } simFile) continue;
                var names = Regex.Matches(System.Text.Encoding.ASCII.GetString(simFile.Data), "SKL_[0-9A-Z_]+").Select(m => m.Value).Distinct();
                var lost = names.Where(n => had.Contains(Hashing.FoxHash.StrCode64(n)) && !have.Contains(Hashing.FoxHash.StrCode64(n))).ToList();
                if (lost.Count == 0) continue;
                notes.Add($"{sim}: physics for bones the mod's model doesn't have ({string.Join(", ", lost)}); kept as in the original mod.");
            }
        }
        return notes;
    }

    private static string Ftex(string p) => p.EndsWith(".ftex", StringComparison.Ordinal) ? p[..^5] : p;

    /// <summary>Quiet's costume icon: her chest from the costume's own package, framed and lit like the vanilla icons (textures from the mod, the port's own copies and the game).</summary>
    public static RgbaImage RenderQuiet(GameData game, PathDictionary? dict, MgsvMod mod, FoxPackage fpk, TexturePack? pack,
        IconChoice.Render rr, List<string>? notes = null, IReadOnlyDictionary<string, byte[]>? portFiles = null, FoxPackage? fpkd = null) =>
        RenderQuiet(game, dict, mod.ArchiveFiles, fpk, pack, rr, notes, portFiles, fpkd);

    private static RgbaImage RenderQuiet(GameData game, PathDictionary? dict, IReadOnlyDictionary<string, byte[]> modFiles, FoxPackage fpk, TexturePack? pack,
        IconChoice.Render rr, List<string>? notes, IReadOnlyDictionary<string, byte[]>? portFiles, FoxPackage? fpkd)
    {
        var src = new Rendering.TextureSource(game, dict);
        if (pack != null) src.AddModPack(pack);
        foreach (var (path, data) in modFiles.Where(kv => kv.Key.Contains(".ftex", StringComparison.Ordinal)))
            src.AddModFile(path, data);
        foreach (var (path, data) in (portFiles ?? new Dictionary<string, byte[]>()).Where(kv => kv.Key.Contains(".ftex", StringComparison.Ordinal) && !kv.Key.Contains("/ui/")))
            src.AddModFile(path, data);
        var model = ShownModel(fpk, fpkd) ?? throw new InvalidDataException("The costume package has no model.");
        var picture = Rendering.IconRenderer.Render([model], src.Get, framing: Rendering.IconFraming.QuietBuddy.MovedBy(rr.Framing), lighting: rr.Lighting ?? Rendering.IconLighting.WithShoulder);
        var missing = src.Missing.ToList();
        if (!game.IsComplete) missing.RemoveAll(c => (dict ?? PathDictionary.Bundled).TryGetPath(c)?.StartsWith("/Assets/tpp/", StringComparison.Ordinal) == true);
        if (missing.Count > 0)
            notes?.Add($"The rendered icon is missing {missing.Count} texture(s): neither the mod nor the game has them, so the model will flicker in game. Add the mod's texture files.");
        return SuitIcon.Frame(picture, SuitIcon.DrawnMask());
    }

    /// <summary>
    /// The icon of a buddy costume drawn from the costume's own files (an item of a mod that is already a V Framework mod):
    /// the same render as a port, with the costume's package, texture pack and loose textures.
    /// </summary>
    /// <param name="game">The game's files.</param>
    /// <param name="dict">Path names for the texture codes.</param>
    /// <param name="item">The costume (its files hold the package).</param>
    /// <param name="rr">The framing.</param>
    /// <returns>The framed icon.</returns>
    public static RgbaImage RenderFromFiles(GameData game, PathDictionary? dict, BuddyOutfitItem item, IconChoice.Render rr)
    {
        string pkg = item.Spec.FpkPath.EndsWith(".fpk", StringComparison.Ordinal) ? item.Spec.FpkPath[..^4] : item.Spec.FpkPath;
        if (!item.Files.TryGetValue(pkg + ".fpk", out var fpkData)) throw new InvalidOperationException("The costume's package is not in the mod.");
        var fpk = FoxPackage.Read(fpkData);
        var pack = item.Files.TryGetValue(pkg + ".pftxs", out var pd) ? TexturePack.Read(pd) : null;
        var fpkd = item.Files.TryGetValue(pkg + ".fpkd", out var dd) ? FoxPackage.Read(dd) : null;
        return item.Spec.Buddy switch
        {
            "quiet" => RenderQuiet(game, dict, item.Files, fpk, pack, rr, null, null, fpkd),
            "dog" => RenderSide(game, dict, item.Files, fpk, pack, rr, Rendering.IconFraming.DogBuddy, layerColourMap: true, null, null, fpkd),
            _ => RenderSide(game, dict, item.Files, fpk, pack, rr, Rendering.IconFraming.HorseBuddy, layerColourMap: false, null, null, fpkd),
        };
    }

    /// <summary>
    /// D-Dog's gear icon: his left flank from the gear's own package, framed like the vanilla gear icons
    /// (IconFraming.DogBuddy, also for other dogs: user, Oct 26), the fur coloured by its coat map (default coat c00, as
    /// the vanilla models name); the model is the one the package's .fox2 shows (ShownModel). Textures
    /// from the mod, the port's own copies (the models point at them) and the game.
    /// </summary>
    public static RgbaImage RenderDog(GameData game, PathDictionary? dict, MgsvMod mod, FoxPackage fpk, TexturePack? pack,
        IconChoice.Render rr, List<string>? notes = null, IReadOnlyDictionary<string, byte[]>? portFiles = null, FoxPackage? fpkd = null) =>
        RenderSide(game, dict, mod.ArchiveFiles, fpk, pack, rr, Rendering.IconFraming.DogBuddy, layerColourMap: true, notes, portFiles, fpkd);

    /// <summary>A D-Horse tack icon: his left side at the front of the saddle (IconFraming.HorseBuddy), as the vanilla tack icons.</summary>
    public static RgbaImage RenderHorse(GameData game, PathDictionary? dict, MgsvMod mod, FoxPackage fpk, TexturePack? pack,
        IconChoice.Render rr, List<string>? notes = null, IReadOnlyDictionary<string, byte[]>? portFiles = null, FoxPackage? fpkd = null) =>
        RenderSide(game, dict, mod.ArchiveFiles, fpk, pack, rr, Rendering.IconFraming.HorseBuddy, layerColourMap: false, notes, portFiles, fpkd);

    /// <summary>A buddy seen from its left side (D-Dog, D-Horse): the package's shown model, the mod's and the port's textures.</summary>
    private static RgbaImage RenderSide(GameData game, PathDictionary? dict, IReadOnlyDictionary<string, byte[]> modFiles, FoxPackage fpk, TexturePack? pack,
        IconChoice.Render rr, Rendering.IconFraming framing, bool layerColourMap, List<string>? notes, IReadOnlyDictionary<string, byte[]>? portFiles, FoxPackage? fpkd)
    {
        var src = new Rendering.TextureSource(game, dict);
        if (pack != null) src.AddModPack(pack);
        foreach (var (path, data) in modFiles.Where(kv => kv.Key.Contains(".ftex", StringComparison.Ordinal)))
            src.AddModFile(path, data);
        foreach (var (path, data) in (portFiles ?? new Dictionary<string, byte[]>()).Where(kv => kv.Key.Contains(".ftex", StringComparison.Ordinal) && !kv.Key.Contains("/ui/")))
            src.AddModFile(path, data);
        var model = ShownModel(fpk, fpkd) ?? throw new InvalidDataException("The package has no model.");
        var picture = Rendering.IconRenderer.Render([model], src.Get, framing: framing.MovedBy(rr.Framing),
            lighting: rr.Lighting ?? Rendering.IconLighting.WithShoulder, options: new Rendering.IconShaderOptions { LayerColourMap = layerColourMap });
        var missing = src.Missing.ToList();
        if (!game.IsComplete) missing.RemoveAll(c => (dict ?? PathDictionary.Bundled).TryGetPath(c)?.StartsWith("/Assets/tpp/", StringComparison.Ordinal) == true);
        if (missing.Count > 0)
            notes?.Add($"The rendered icon is missing {missing.Count} texture(s): neither the mod nor the game has them, so the model will flicker in game. Add the mod's texture files.");
        return SuitIcon.Frame(picture, SuitIcon.DrawnMask());
    }

    private static IEnumerable<string> DescribeHorse(HorseTackBehaviour h)
    {
        if (h.DamageRate is { } d) yield return $"damage ×{d:0.###}";
        if (h.GallopSpeedRate is { } g) yield return $"gallop ×{g:0.##}";
        if (h.BodyAttack is { } a) yield return $"ram {a}";
        if (h.SoundType is { } s) yield return $"{HorseTackBehaviour.TackNames[s]} movement sounds";
    }

    private static IEnumerable<string> DescribeDog(DogGearBehaviour d)
    {
        if (d.CommandType != null) yield return $"order {d.CommandType}";
        if (d.DamageRate is { } r) yield return $"damage ×{r:0.##}";
        if (d.BulletFlinch == false) yield return "no bullet stagger";
        if (d.ArmorHitEffect == true || d.ArmorHitSound == true) yield return "armour hit effect and sound";
        if (d.EscapeDistance is { } e) yield return $"escape {e:0.#} m";
        if (d.HitRetreatDistance is { } h) yield return $"hit retreat {h:0.#} m";
        if (d.RetargetRange is { } t) yield return $"retarget +{t:0.#} m";
        if (d.SoldierThreat == true) yield return "soldiers treat him as armed";
    }

    /// <summary>English text of a vanilla lang ID (any language file in the English lang package), or null.</summary>
    private static string? VanillaText(GameData game, string? langId)
    {
        if (langId == null || game.TryRead("/Assets/tpp/pack/ui/lang/lang_default_data_eng.fpk") is not { } fpk) return null;
        foreach (var f in FoxPackage.Read(fpk).Files.Where(f => f.Extension == "lng2"))
            if (LangFile.Read(f.Data)[langId] is { } t) return t;
        return null;
    }

    /// <summary>The mod's lang files by language (".eng.lng2" → "eng"), all files of a language merged.</summary>
    public static Dictionary<string, LangFile> ModLang(MgsvMod mod)
    {
        var byLang = new Dictionary<string, LangFile>(StringComparer.Ordinal);
        foreach (var (path, data) in mod.ArchiveFiles.Where(kv => kv.Key.StartsWith("/Assets/tpp/pack/ui/lang/", StringComparison.Ordinal) && kv.Key.EndsWith(".fpk", StringComparison.Ordinal)))
            foreach (var f in FoxPackage.Read(data).Files.Where(f => f.Extension == "lng2"))
            {
                var m = Regex.Match(f.Path, @"\.([a-z]{3})\.lng2$");
                if (!m.Success) continue;
                if (!byLang.TryGetValue(m.Groups[1].Value, out var l)) byLang[m.Groups[1].Value] = l = new LangFile();
                foreach (var e in LangFile.Read(f.Data).Entries) l.Set(e.Key, e.Text, e.Color);
            }
        return byLang;
    }
}
