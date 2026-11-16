using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>Settings for turning a Zeta weapon script into a V Framework weapon.</summary>
public sealed record ZetaConversionOptions
{
    public required string Id { get; init; }
    /// <summary>English name. Null = the mod's own text for the weapon; without it the script's name (one weapon) or the vanilla name.</summary>
    public string? Name { get; init; }
    public string? Description { get; init; }
    /// <summary>The vanilla weapon the script changes (e.g. "WP_10107"); null = the first gunBasic row.</summary>
    public string? Weapon { get; init; }
    public DevelopRow? Develop { get; init; }
    public string ModVersion { get; init; } = "1.0";
    /// <summary>The user's changes over the script's values (part kind → field path → value), as for any weapon copy.</summary>
    public Dictionary<WeaponPartKind, Dictionary<string, Formats.LuaValue>> Edits { get; init; } = [];
    /// <summary>See <see cref="WeaponConversionOptions.DefaultPaint"/>.</summary>
    public bool DefaultPaint { get; init; } = true;
    /// <summary>See <see cref="WeaponConversionOptions.OwnModelPath"/>.</summary>
    public bool? OwnModelPath { get; init; }
}

/// <summary>
/// Translates a Zeta script that changes a vanilla weapon into a new V Framework weapon. Zeta edits the game's rows (its
/// EquipParameters() and DamageParameterTables() are laid over the game's tables); V Framework adds rows. So the port is a
/// copy of the vanilla weapon (its receiver package under the copy's name, <see cref="WeaponConverter.CopyVanilla"/>) whose
/// parts take the script's values: the script's tables are laid over the game's (<see cref="EquipTables.WithOverlay"/>)
/// and every part whose values differ from vanilla becomes the copy's own (<see cref="WeaponTuning"/>). The vanilla weapon
/// and every other weapon using those parts are left as they are. Sights stay vanilla (a custom sight needs the vanilla
/// sight's UI files in its package: not handled yet).
/// When the script's EquipIdTable() gives the weapon a package that the mod ships (HK 33), the port copies that package
/// and its model (<see cref="WeaponConversionOptions.OwnPartsPath"/>), not the vanilla receiver.
/// </summary>
public static class ZetaConverter
{
    // mod: the mod that holds the script (its packages, textures, icon and text); null = the script only.
    public static WeaponConversionResult Convert(Svfm.Core.Archives.GameData game, ZetaScript z, ZetaConversionOptions o, Mods.MgsvMod? mod = null)
    {
        string id = o.Id.ToLowerInvariant();
        var project = new ModProject
        {
            Id = id, Name = $"{o.Name ?? z.Name} (V Framework)", Version = o.ModVersion, Author = z.Author,
            Description = o.Description ?? $"{o.Name ?? z.Name}: a new weapon made from the Zeta script \"{z.Name}\" by {z.Author}. The vanilla weapon is unchanged.\r\n\r\nRequires Infinite Heaven, IHHook and V Framework 1.7.",
        };
        var item = ToItem(game, z, o, $"svfm_{id}", project.NewItemNames(id).key, mod);
        project.Items.Add(item);
        var built = ProjectBuilder.Build(project, game);
        var r = new WeaponConversionResult { Mod = built.Mod, Weapon = item.Spec, Lua = built.Lua };
        r.Notes.AddRange(item.Notes);
        r.Notes.AddRange(built.Errors);
        r.Errors.AddRange(built.Errors);
        return r;
    }

    /// <summary>The vanilla weapons a Zeta script changes (its gunBasic rows, else the weapons using a part it changes).</summary>
    public static List<string> Weapons(ZetaScript z) =>
        z.Section("gunBasic").Select(r => r.Part(0)).Where(w => w.StartsWith("WP_", StringComparison.Ordinal)).Distinct().ToList();

    /// <summary>The functions of the script that are not parameter tables: behaviour of its own, which a port cannot keep.</summary>
    public static List<string> OtherFunctions(ZetaScript z) =>
        System.Text.RegularExpressions.Regex.Matches(Formats.LuaLiteral.BlankComments(z.Source), @"function\s+this[.:](\w+)").Select(m => m.Groups[1].Value)
            .Where(f => f is not ("EquipIdTable" or "EquipParameters" or "DamageParameterTables" or "EquipDevelopConstSetting" or "EquipDevelopFlowSetting" or "EquipMotionData")).Distinct().ToList();

    /// <summary>
    /// The port of a Zeta script that changes rows of the game in place and has no weapon row (Armor Piercing Sniper
    /// Rifles, Tier 3 Infinite Suppressors): one Set* call for each part or damage row that the script changes, on the
    /// game's own ID and with every value of the row (V Framework sets a value that is left out to 0). The game's
    /// weapons that use the row change, as with the Zeta mod installed. No weapon is added.
    /// </summary>
    /// <returns>The Lua for LoadLibraries, and one line for each changed row.</returns>
    // Source: vf_src hooks/equip/EquipPartParams.cpp, the "vanillaRow" path of each setter (EquipParam_VanillaPreWrite and
    // EquipParam_VanillaPostWrite). V Framework marks a changed row of the game, and a weapon that uses it is blocked in FOB.
    public static (string Lua, List<string> Changes) VanillaRowChanges(EquipTables vanilla, ZetaScript z)
    {
        var (equip, damage) = EquipTables.ReadZeta(z.Source);
        var tables = vanilla.WithOverlay(equip, damage);
        var lua = new System.Text.StringBuilder();
        var changes = new List<string>();
        foreach (var p in tables.AllParts)
        {
            if (vanilla.Part(p.Kind, p.Id) is not { } game || p.Differences(game) is not { Count: > 0 } fields) continue;
            lua.Append($"  -- {p.Id}: the game's row with the values of the Zeta script \"{z.Name}\" ({string.Join(", ", fields)}).\n");
            lua.Append(p.SetCall(p.Id)).Append('\n');
            changes.Add($"{p.Id}: {string.Join(", ", fields.Select(f => $"{f} {game.Values.GetValueOrDefault(f)} → {p.Values.GetValueOrDefault(f)}"))}");
        }
        return (lua.ToString(), changes);
    }

    /// <summary>A player outfit of a Zeta script (LoadPlayerParts): who wears it, the suit it replaces, and its body packages with a label each.</summary>
    // Bodies.PartsFile: the .parts of this body when one package holds several bodies (Brothers Keeper), else null.
    public sealed record PlayerOutfit(PlayerBranch Who, PartsTypeInfo Parts, IReadOnlyList<(string Package, string Label, string? PartsFile)> Bodies);

    /// <summary>
    /// The mod with the package cut down to one of its bodies: the .parts file <paramref name="partsFile"/> and the files
    /// it names stay; the other .parts files and the files that only they name (model, connect points, help bones) go.
    /// Brothers Keeper has 8 bodies in plparts_azur, and the Zeta script picks the .parts by a menu value.
    /// </summary>
    public static Mods.MgsvMod OneBody(Mods.MgsvMod mod, string package, string partsFile)
    {
        var fpk = Formats.FoxPackage.Read(mod.ArchiveFiles[package + ".fpk"]);
        var fpkd = Formats.FoxPackage.Read(mod.ArchiveFiles[package + ".fpkd"]);
        HashSet<string> Names(Formats.PackageFile parts)
        {
            var ds = Formats.FoxDataSet.Read(parts.Data);
            var set = new HashSet<string>(StringComparer.Ordinal) { parts.Path };
            foreach (string prop in new[] { "modelFile", "connectPointFile", "helpBoneFile" })
                foreach (var (_, v) in ds.StringValues("ModelDescription", prop)) if (!string.IsNullOrEmpty(v)) set.Add(v);
            return set;
        }
        var all = fpkd.Files.Where(f => f.Extension == "parts").ToList();
        var mine = Names(all.First(f => f.Path == partsFile));
        var others = all.Where(f => f.Path != partsFile).SelectMany(Names).Where(p => !mine.Contains(p)).ToHashSet(StringComparer.Ordinal);
        fpk.Files.RemoveAll(f => others.Contains(f.Path));
        fpkd.Files.RemoveAll(f => others.Contains(f.Path));
        return mod.WithArchiveFiles(new Dictionary<string, byte[]> { [package + ".fpk"] = fpk.Write(), [package + ".fpkd"] = fpkd.Write() });
    }

    /// <summary>
    /// The player outfit of a Zeta script: LoadPlayerParts() gives a suit (<c>{"SNAKE","SNEAKING_SUIT_TPP"}</c> or
    /// <c>player="NORMAL_SCARF"</c>) the mod's own body package, picked by a menu value when the script has more than one.
    /// Null with a reason when the script has no LoadPlayerParts (reason null) or when the port is not possible.
    /// </summary>
    public static (PlayerOutfit? Outfit, string? Reason) PlayerOutfitOf(Mods.MgsvMod mod, string lua)
    {
        lua = Formats.LuaLiteral.BlankComments(lua);
        int at = lua.IndexOf("function this.LoadPlayerParts", StringComparison.Ordinal);
        if (at < 0) return (null, null);
        string body = lua[at..];
        PlayerBranch who = PlayerBranch.Snake;
        string? suit = null;
        if (System.Text.RegularExpressions.Regex.Match(body, @"\{\s*""(SNAKE|AVATAR|DD_MALE|DD_FEMALE|OCELOT|QUIET)""\s*,\s*""([A-Z0-9_]+)""\s*\}") is { Success: true } pair)
        {
            who = pair.Groups[1].Value switch { "AVATAR" => PlayerBranch.Avatar, "DD_MALE" => PlayerBranch.DdMale, "DD_FEMALE" => PlayerBranch.DdFemale, "OCELOT" => PlayerBranch.Ocelot, "QUIET" => PlayerBranch.Quiet, _ => PlayerBranch.Snake };
            suit = pair.Groups[2].Value;
        }
        else if (System.Text.RegularExpressions.Regex.Match(body, @"\bplayer\s*=\s*""([A-Z0-9_]+)""") is { Success: true } single) suit = single.Groups[1].Value;
        if (suit == null || GameKnowledge.PartsTypes.FirstOrDefault(p => p.Name == suit) is not { } parts)
            return (null, suit != null ? $"The script names a suit that the app does not know ({suit})."
                : body.Contains("hand", StringComparison.Ordinal) && !body.Contains("body", StringComparison.Ordinal) && !body.Contains(".parts", StringComparison.Ordinal)
                    ? "The script changes the bionic arm (hand packages), not a suit. The app can't port this kind yet."
                    : "The script chooses its suits and packages with Lua code, not with one table. The app can't port this kind yet.");
        var menu = ZetaScript.MenuList(lua);
        var packages = ZetaScript.ShippedPackages(body, mod.ArchiveFiles).Where(p => p.Key is null or "fpk").ToList();
        if (packages.Count == 0) return (null, "The mod does not ship the body package that the script names.");
        string Label(int? number, string fallback) => number is { } n && n >= 1 && n <= menu.Count ? menu[n - 1] : fallback;
        // One package with a body for each menu choice: the script builds the .parts path ("…_v00_"..costumeType..".parts").
        if (System.Text.RegularExpressions.Regex.Match(body, @"""(/Assets/[^""]*)""\s*\.\.\s*[\w.]+\s*\.\.\s*""([^""]*\.parts)""") is { Success: true } byParts)
        {
            if (packages.Count != 1 || !mod.ArchiveFiles.TryGetValue(packages[0].Package + ".fpkd", out var fpkdData))
                return (null, "The script picks a .parts file with a menu value, and the app cannot tell which one package holds the .parts files.");
            var fit = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(byParts.Groups[1].Value) + @"(\d+)" + System.Text.RegularExpressions.Regex.Escape(byParts.Groups[2].Value) + "$");
            var bodies = Formats.FoxPackage.Read(fpkdData).Files.Where(f => f.Extension == "parts").Select(f => fit.Match(f.Path)).Where(m => m.Success)
                .Select(m => (Number: int.Parse(m.Groups[1].Value), Path: m.Value)).OrderBy(x => x.Number).ToList();
            if (bodies.Count == 0) return (null, "The package has none of the .parts files that the script names.");
            return (new PlayerOutfit(who, parts, bodies.Select(b => (packages[0].Package, Label(b.Number, Path.GetFileNameWithoutExtension(b.Path)), (string?)b.Path)).ToList()), null);
        }
        return (new PlayerOutfit(who, parts, packages.Select(p => (p.Package, Label(p.Number, Path.GetFileName(p.Package)), (string?)null)).ToList()), null);
    }

    public static WeaponItem ToItem(Svfm.Core.Archives.GameData game, ZetaScript z, ZetaConversionOptions o, string prefix, string key, Mods.MgsvMod? mod = null)
    {
        string weapon = o.Weapon ?? Weapons(z).FirstOrDefault() ?? throw new ArgumentException("The script has no gunBasic row; name the weapon it changes.");
        var vanilla = EquipTables.Load(game.TryRead)
            ?? throw new InvalidOperationException("The game's weapon parameter tables (EquipParameters.lua, DamageParameterTables.lua) are needed to translate a Zeta weapon.");
        var (equip, damage) = EquipTables.ReadZeta(z.Source);
        var tables = vanilla.WithOverlay(equip, damage);
        // One weapon: the script's name is the gun's name (Fulton Gun). More: each keeps the vanilla name, for the user to change.
        string? name = o.Name ?? (Weapons(z).Count <= 1 && z.Name != "" ? z.Name : null);
        var options = new WeaponConversionOptions
        {
            Id = o.Id, Name = name, RealName = name,
            Description = o.Description ?? $"Converted from the Zeta script \"{z.Name}\" by {z.Author}.",
            Develop = o.Develop ?? new DevelopRow { InitiallyAvailable = true },
            ModVersion = o.ModVersion, Edits = o.Edits, DefaultPaint = o.DefaultPaint, OwnModelPath = o.OwnModelPath,
        };
        // The script's EquipIdTable row: the mod's own package and model, when the mod ships the package.
        WeaponItem item;
        if (mod != null && z.EquipIdOf(weapon) is { } row && mod.ArchiveFiles.ContainsKey(row.Values[5]) && tables.GunBasic(weapon) is { } gun)
        {
            var dev = z.DevelopConst.FirstOrDefault(d => d.Named.GetValueOrDefault("p01") == row.Values[0]);
            var wp = new ChimeraWeapon
            {
                Wp = weapon, ReceiverId = gun["receiverId"], BarrelId = gun["barrelId"], AmmoId = gun["ammoId"], StockId = gun["stockId"],
                MuzzleId = gun["muzzleId"], MuzzleOptionId = gun["muzzleOptionId"], Scope1Id = gun["scope1Id"], Scope2Id = gun["scope2Id"],
                UnderBarrelId = gun["underBarrelId"], LaserFlash1Id = gun["laserFlash1Id"], LaserFlash2Id = gun["laserFlash2Id"],
                Grade = int.TryParse(gun.GetValueOrDefault("weaponGrade"), out int g) ? g : 1,
                DevType = row.Part(1).Replace("EQP_TYPE_", ""),
                NameLang = dev?.Named.GetValueOrDefault("p06"), InfoLang = dev?.Named.GetValueOrDefault("p07"), RealLang = dev?.Named.GetValueOrDefault("p30"),
                Icon = dev?.Named.GetValueOrDefault("p08"),
            };
            item = WeaponConverter.ToItem(game, mod, row.Values[5], options with { Name = o.Name, RealName = null, Description = o.Description, Renamed = wp, OwnPartsPath = row.Values[4] }, prefix, key, tables);
            item.Spec.Header.Insert(0, $"{item.Title}: V Framework weapon translated from the Zeta script \"{z.Name}\" by {z.Author} (the mod's own package {Path.GetFileName(row.Values[5])}, the parts of {weapon} with the script's values).");
        }
        else
        {
            item = WeaponConverter.CopyVanilla(game, weapon, options, prefix, key, tables, Mods.MgsvMod.Empty(z.Name, z.Author));
            item.Spec.Header.Insert(0, $"{item.Title}: V Framework weapon translated from the Zeta script \"{z.Name}\" by {z.Author} (a copy of vanilla {weapon} with the script's values).");
        }
        if (z.Source.Contains("EquipMotionData", StringComparison.Ordinal))
            item.Notes.Add("The script's EquipMotionData (an animation file for the weapon) is not used: the weapon plays the animations of its receiver.");
        var other = z.Section("gunBasic").Select(r => r.Part(0)).Where(w => w != weapon).Distinct().ToList();
        if (other.Count > 0) item.Notes.Add($"The script also changes {string.Join(", ", other)}; each can be made as its own copy.");
        return item;
    }
}
