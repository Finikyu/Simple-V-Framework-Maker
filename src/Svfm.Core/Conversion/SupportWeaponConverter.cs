using System.Text.RegularExpressions;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>
/// A support weapon model a mod replaces in the resident common package, with the vanilla items that use it (a model
/// can be shared: the smoke and sleeping gas grenades use tr00). The mod's model is never portable (V Framework 1.7 can't give
/// a custom support weapon a model of its own: <see cref="SupportWeaponConverter.ModelReason"/>). A <see cref="Thrown"/> item
/// can still become a new item thrown like the vanilla one, with the vanilla look and a script of its own; placed items
/// can't (SetSupportWeapon takes only a vanilla thrown item as throwAs). <see cref="Reason"/> says why.
/// </summary>
public sealed record SupportWeaponReplacement(string ModelPath, string Family, string VanillaEquip, VanillaSupportWeapon Vanilla,
    IReadOnlyList<string> SharedWith, bool Thrown, string Reason);

public sealed class SupportWeaponConversionOptions
{
    public required string Id { get; init; }
    /// <summary>English name; default: the mod's name without its "(…)" part, in capitals.</summary>
    public string? Name { get; init; }
    /// <summary>English description; default: the vanilla item's.</summary>
    public string? Description { get; init; }
    /// <summary>Show what happens on screen (announce log) when the item is thrown, goes off and is removed.</summary>
    public bool Diagnostics { get; init; }
    /// <summary>What the item does when thrown; default: the vanilla item's effect (<see cref="ThrowScript.VanillaEffect"/>).
    /// The item's <see cref="SupportWeaponSpec.Script"/> can be changed afterwards too.</summary>
    public ThrowScript? Script { get; init; }
}

/// <summary>
/// Thrown support weapon model replacers (in /Assets/tpp/pack/collectible/common/col_common_tpp, the package every
/// mission keeps loaded): the model can't be ported (<see cref="ModelReason"/>), but a thrown item can become a new V
/// Framework 1.7 support weapon with the vanilla item's row (its .parts, COMMON, no package), so the vanilla item stays;
/// it is held, thrown and flies like the vanilla one (throwAs = the vanilla item). V Framework never runs the throwAs effect for a custom item, so the
/// effect is run from Lua with the V_ThrowingImpl.UpdateAction* call for that kind of item when it goes off
/// (OnActivateThrowing). Placed items (decoys, mines, C4, cages) are refused: V Framework 1.7 can't make custom
/// placed items (P.T. Decoy, thrown and scripted as a decoy, did nothing in game, Oct 25).
/// </summary>
public static class SupportWeaponConverter
{
    public const string CommonPackage = "/Assets/tpp/pack/collectible/common/col_common_tpp";

    /// <summary>
    /// Support weapon models the mod changes in the common package, each with the vanilla item it belongs to (the
    /// lowest grade of the first item using it) and whether V Framework can make it.
    /// </summary>
    public static List<SupportWeaponReplacement> Detect(GameData game, MgsvMod mod)
    {
        var list = new List<SupportWeaponReplacement>();
        if (!mod.ArchiveFiles.TryGetValue(CommonPackage + ".fpk", out var md) || game.TryRead(CommonPackage + ".fpk") is not { } vd) return list;
        var van = FoxPackage.Read(vd).Files.ToDictionary(f => f.Path, f => f.Data);
        var vanFpkd = game.TryRead(CommonPackage + ".fpkd") is { } vf ? FoxPackage.Read(vf) : null;
        FoxPackage modPkg;
        try { modPkg = FoxPackage.Read(md); } catch (InvalidDataException) { return list; } // a damaged package must not stop the whole mod's intake
        foreach (var f in modPkg.Files.Where(f => f.Extension == "fmdl"))
        {
            if (van.TryGetValue(f.Path, out var v) && v.AsSpan().SequenceEqual(f.Data)) continue;
            // The items using the model: equip rows naming it, or naming a .parts that names it.
            var users = new HashSet<string>(StringComparer.Ordinal) { f.Path };
            foreach (var pf in vanFpkd?.Files.Where(x => x.Extension == "parts") ?? [])
                if (FoxDataSet.Read(pf.Data).ModelFiles.Contains(f.Path)) users.Add(pf.Path);
            var rows = GameKnowledge.SupportWeapons.Where(kv => users.Contains(kv.Value.Parts))
                .OrderBy(kv => kv.Value.EquipType == "Throwing" ? 0 : 1).ThenBy(kv => kv.Value.Grade ?? 99).ThenBy(kv => kv.Key.Length).ToList();
            // Else by the model's name (the decoy's equip rows name its folded model pt07_main0_sta; the inflated pt07_main0_def
            // only appears in a .parts no row names).
            string fam = Path.GetFileName(f.Path).Split('_')[0];
            if (rows.Count == 0)
                rows = GameKnowledge.SupportWeapons.Where(kv => Path.GetFileName(kv.Value.Parts).StartsWith(fam + "_", StringComparison.Ordinal))
                    .OrderBy(kv => kv.Value.EquipType == "Throwing" ? 0 : 1).ThenBy(kv => kv.Value.Grade ?? 99).ThenBy(kv => kv.Key.Length).ToList();
            if (rows.Count == 0) continue;
            var (equip, row) = (rows[0].Key, rows[0].Value);
            var shared = rows.Select(r => Family(r.Key)).Distinct().Where(x => x != Family(equip)).ToList();
            bool thrown = row.EquipType == "Throwing";
            string reason = thrown ? ModelReason(Family(equip)) :
                // Short on purpose (the owner: "our tests on the PT Decoy should not be included like a user would know that").
                // Why: SetSupportWeapon takes a vanilla thrown item as throwAs, so a new placed item has no behaviour; the
                // P.T. Decoy, thrown and scripted as a decoy, did nothing in game.
                $"{Family(equip)} is a placed item. V Framework 1.7 can only add thrown items, so this item can't be ported.";
            list.Add(new SupportWeaponReplacement(f.Path, fam, equip, row, shared, thrown, reason));
        }
        return list;
    }

    /// <summary>
    /// Why the mod's model can't come along (V Framework 1.7 source, IH start.lua and TppMain, in game Oct 27): an
    /// EQP_BLOCK_COMMON row's .parts must be in the resident equip common block, which start.lua loads once with
    /// col_common_tpp.fpk only (a package added to every mission is not in it: crash when thrown, 0.7a and 0.8); an
    /// EQP_BLOCK_MISSION row is loaded only through TppEquip.RequestLoadToEquipMissionBlock with the engine's equip ID,
    /// which V Framework doesn't hook or expose for its custom IDs (above 0x7FF, aliased in the engine; nothing thrown, 0.6).
    /// The vanilla .parts in a COMMON row works (0.7b, the wiki's chaff grenade).
    /// </summary>
    public static string ModelReason(string family) =>
        // Why (kept out of the text the user reads): the item's row must be EQP_BLOCK_COMMON, whose models come only from the
        // resident common package (a package of its own crashed the game when thrown), and an EQP_BLOCK_MISSION row is never
        // loaded for a support weapon (nothing was thrown).
        "V Framework 1.7 can't give a new thrown item a model of its own, so the mod's model can't be ported. " +
        $"A new item thrown like {family}, with its own effect (chaff, stun, explosion...), can be made instead.";

    /// <summary>The icon style of thrown items (see step 3 of <see cref="AddTo"/>).</summary>
    public static Rendering.OutlineIconOptions SupportIconOptions => Rendering.OutlineIconOptions.Item with { Yaw = -90 };

    /// <summary>An item's family name without its grade: EQP_SWP_Grenade_G02 → EQP_SWP_Grenade.</summary>
    public static string Family(string equip) => SupportWeaponFamilies.Family(equip);

    /// <summary>The V Framework call that runs a vanilla thrown item's effect on a custom item (<see cref="SupportWeaponFamilies.EffectCall"/>).</summary>
    public static (string call, bool removeAfter)? EffectCall(string swp) => SupportWeaponFamilies.EffectCall(swp);

    /// <summary>The model a vanilla item's row names: the model itself, or the model its .parts (in the .fpkd) names.</summary>
    private static PackageFile? VanillaModel(GameData game, FoxPackage pkg, string parts)
    {
        if (pkg.Find(parts) is { } direct) return direct;
        var pf = game.TryRead(CommonPackage + ".fpkd") is { } d ? FoxPackage.Read(d).Find(parts) : null;
        var model = pf == null ? null : FoxDataSet.Read(pf.Data).ModelFiles.FirstOrDefault(x => x != null);
        return model == null ? null : pkg.Find(model);
    }

    public static SupportWeaponItem AddTo(ModProject p, GameData game, MgsvMod mod, SupportWeaponReplacement r, SupportWeaponConversionOptions o)
    {
        if (!r.Thrown) throw new InvalidOperationException(r.Reason);
        var (key, prefix) = p.NewItemNames(o.Id);
        var notes = new List<string> { r.Reason! };
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // 1. No package: the vanilla item's own .parts in an EQP_BLOCK_COMMON row, as every vanilla support weapon (and the
        // wiki's chaff grenade). The mod's model can't come along (ModelReason).
        foreach (var path in mod.ArchiveFiles.Keys.Where(k => k.StartsWith("/Assets/tpp/sound/", StringComparison.Ordinal)))
            notes.Add($"{path}: replaces a vanilla sound bank; not portable (the port never changes vanilla files, and V Framework 1.7 can't add sounds to an item).");

        // 2. Icon drawn from the vanilla model (the item looks like the vanilla one) in the vanilla item-icon style, at the
        // vanilla scale (520 px per metre, upright, turned -90°: fitted on the vanilla grenade icon, its outline box within
        // 2 px of ui_swp_grenade_00_10's).
        var vanPkg = FoxPackage.Read(game.Read(CommonPackage + ".fpk"));
        // A model the mod adds under a new name was matched by the name only: the vanilla item's own model is drawn then.
        var vanModel = (vanPkg.Find(r.ModelPath) ?? VanillaModel(game, vanPkg, r.Vanilla.Parts))?.Data
            ?? throw new InvalidDataException($"The game has no model for {r.VanillaEquip}.");
        string iconPath = $"/Assets/tpp/ui/texture/EquipIcon/supportweapon/ui_swp_{prefix}_alp";
        var ft = FtexWriter.Write(Rendering.OutlineIconRenderer.Render([FmdlModel.Read(vanModel)], SupportIconOptions), withAlpha: true, FtexKind.Linear);
        files[iconPath + ".ftex"] = ft.Ftex;
        foreach (var (n, d) in ft.Ftexs) files[$"{iconPath}.{n}.ftexs"] = d;

        // 4. The item: the vanilla item's stats (the mod's, where it ships its own copy of the table), held and thrown like it.
        var v = r.Vanilla;
        string id = Naming.Pascal(o.Id);
        var vanTables = SupportTables.Load(game.TryRead);
        var modTables = SupportTables.Load(path => mod.ArchiveFiles.GetValueOrDefault(path));
        string swp = v.Swp.Replace("TppEquip.", "");
        var stats = vanTables?.Stats.GetValueOrDefault(swp) ?? new SupportStats((int)(v.Ammo ?? 1), v.P1 ?? 0, v.P2 ?? 0, v.Grade ?? 1);
        if (modTables?.Stats.GetValueOrDefault(swp) is { } ms && ms != stats)
        {
            notes.Add($"The mod changes {swp}'s stats ({Changes(("ammo", stats.Ammo, ms.Ammo), ("fuse", stats.P1, ms.P1), ("effect time", stats.P2, ms.P2), ("grade", stats.Grade, ms.Grade))}); the new item uses the mod's.");
            stats = ms;
        }
        var spec = new SupportWeaponSpec
        {
            Key = key, EquipConstant = $"EQP_SWP_SVFM_{id}", SwpConstant = $"SWP_SVFM_{id}", ThrowAs = r.VanillaEquip,
            // The vanilla item's row as it is: its block (all 88 are EQP_BLOCK_COMMON), its .parts, no package. In game
            // (Oct 27) only this works: see ModelReason.
            PartsPath = v.Parts, FpkPath = "",
            Block = v.Block is { Length: > 0 } b ? b : "EQP_BLOCK_COMMON",
            Ammo = stats.Ammo, FuseTime = stats.P1, ActiveTime = stats.P2, Grade = stats.Grade,
            DevelopType = v.Develop?.Type ?? "Throwing",
            NameLang = $"{prefix}_name", RealNameLang = $"{prefix}_name", InfoLang = $"{prefix}_info", IconFtexPath = iconPath,
            Develop = new DevelopRow
            {
                Grade = 1, GmpCost = 0, InitiallyAvailable = true,
                UsageGmpCost = v.Develop != null ? GameKnowledge.VanillaDevelop(v.Develop.DevelopId)?.UsageGmp ?? 0 : 0,
            },
        };
        AttackAndBlast(game, mod, spec, swp, vanTables, modTables, notes);
        spec.Header.Add($"{o.Name ?? mod.Metadata.Name}: a new item with {r.VanillaEquip}'s model and .parts ({v.Parts}), thrown like it; " +
                        $"made from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}, whose model V Framework 1.7 can't give a custom support weapon.");
        if (r.SharedWith.Count > 0)
            notes.Add($"The model is shared with {string.Join(", ", r.SharedWith)}; the port makes one item, like {Family(r.VanillaEquip)}. Another can be made from the same model.");
        string name = o.Name ?? Regex.Replace(mod.Metadata.Name, @"\s*\(.*?\)\s*", " ").Trim().ToUpperInvariant();
        var item = new SupportWeaponItem { Id = o.Id, Key = key, Prefix = prefix, Spec = spec, Title = name };

        // 5. What it does: V Framework never runs the throwAs effect for a custom item; the script runs the vanilla one when
        // it goes off, unless the user gave another.
        spec.Script = o.Script ?? ThrowScript.VanillaEffect(v.Swp);
        if (o.Script == null && EffectCall(v.Swp) == null)
            notes.Add($"V Framework 1.7 has no call that runs {Family(v.Swp)}'s effect on a custom item (only grenade, stun, smoke, sleeping gas, Molotov, bait and fake sign): " +
                      "it is held and thrown like the vanilla one, but does nothing unless a script is given (chaff, stun, smoke, fire, explosion, noise...).");
        if (o.Diagnostics) spec.Script.WithDiagnostics();

        // 6. Text: the user's, else the mod's name and the vanilla description.
        string info = o.Description ?? VanillaText(game, v.Develop?.Info) ?? "";
        item.SetText($"{prefix}_name", name);
        item.SetText($"{prefix}_info", info);
        foreach (var (path, d) in files) item.Files[path] = d;
        item.Notes.AddRange(notes);
        p.Items.Add(item);
        return item;
    }

    /// <summary>
    /// The vanilla attack and blast named for the throwAs item (ATK_/BLA_ + its kind: ATK_Grenade, BLA_Grenade; the grade's
    /// ATK_…_Gn where the game has one), noted on the spec as what the item's own copy starts from. The game picks a thrown
    /// item's attack in code, so these are by name only; the item keeps its throwAs item's unless the mod changes the named
    /// row in its own copy of the tables: then the item gets its own copy with the mod's values (the vanilla rows stay).
    /// </summary>
    private static void AttackAndBlast(GameData game, MgsvMod mod, SupportWeaponSpec spec, string swp, SupportTables? vanTables, SupportTables? modTables, List<string> notes)
    {
        string kind = Family(swp).Replace("SWP_", "");
        var grade = Regex.Match(swp, "_G0([0-9])$");
        var vanDamage = EquipTables.Load(game.TryRead);
        var modDamage = EquipTables.Load(path => mod.ArchiveFiles.GetValueOrDefault(path));
        string? atk = new[] { grade.Success ? $"ATK_{kind}_G{grade.Groups[1].Value}" : null, $"ATK_{kind}" }
            .FirstOrDefault(a => a != null && vanDamage?.Part(WeaponPartKind.Damage, a) != null);
        spec.ThrowAsAttack = atk;
        if (atk != null && modDamage?.Part(WeaponPartKind.Damage, atk) is { } md && md.Differences(vanDamage!.Part(WeaponPartKind.Damage, atk)!) is { Count: > 0 } changed)
        {
            spec.OwnAttack = md;
            notes.Add($"The mod changes {atk} ({string.Join(", ", changed)}); the new item gets its own copy with the mod's values (the game picks a thrown item's attack in code: {atk} is the one named for {swp}).");
        }
        string bla = $"BLA_{kind}";
        if (vanTables?.Blasts.GetValueOrDefault(bla) is { } vb)
        {
            spec.ThrowAsBlast = bla;
            if (modTables?.Blasts.GetValueOrDefault(bla) is { } mb && mb != vb)
            {
                spec.OwnBlast = mb;
                notes.Add($"The mod changes {bla} ({Changes(("noise", vb.BlastFlag, mb.BlastFlag), ("radius", vb.EffectRange, mb.EffectRange), ("others' radius", vb.OptimalRange, mb.OptimalRange))}); the new item gets its own copy with the mod's values.");
            }
        }
    }


    private static string Changes(params (string name, double from, double to)[] values) =>
        string.Join(", ", values.Where(v => v.from != v.to).Select(v => $"{v.name} {LuaLiteral.FormatNumber(v.from)} → {LuaLiteral.FormatNumber(v.to)}"));

    /// <summary>English text of a vanilla lang ID from any language file in the English lang package.</summary>
    private static string? VanillaText(GameData game, string? langId)
    {
        if (langId == null || game.TryRead("/Assets/tpp/pack/ui/lang/lang_default_data_eng.fpk") is not { } fpk) return null;
        foreach (var f in FoxPackage.Read(fpk).Files.Where(f => f.Extension == "lng2"))
            if (LangFile.Read(f.Data)[langId] is { } t) return t;
        return null;
    }
}
