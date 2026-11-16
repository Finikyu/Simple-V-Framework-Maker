using System.Text;
using Svfm.Core.Formats;

namespace Svfm.Core.VFramework;

/// <summary>The weapon parts V Framework 1.7 can define (Custom_Weapons wiki): each has its own Set* call.</summary>
public enum WeaponPartKind { Damage, Receiver, Barrel, Magazine, Bullet, Stock, MuzzleOption, Sight, Option, UnderBarrel }

/// <summary>What a field holds, for an editor: a number, a 0/1 flag, a game constant (with its prefix) or text.</summary>
public enum PartFieldType { Number, Flag, Constant, Text }

/// <summary>One editable field of a part: its Lua name (a dotted path for the fields of a parameter set, e.g.
/// <c>receiverParamSetsBase.fireRate</c>), a label, and what it holds.</summary>
public sealed record PartField(string Path, string Label, PartFieldType Type, string? ConstantPrefix = null, string? Note = null)
{
    public string Group => Path.Contains('.') ? Path[..Path.IndexOf('.')] : "";
    public string Name => Path.Contains('.') ? Path[(Path.IndexOf('.') + 1)..] : Path;
}

/// <summary>
/// The fields of each part kind, in the column order of the game's own tables (EquipParameters.lua,
/// DamageParameterTables.lua), which is also the order V Framework reads a parameter set given as a plain list
/// (EquipPartParams.cpp field-name arrays). Labels follow the Custom_Weapons wiki.
/// </summary>
public static class WeaponPartFields
{
    private static PartField N(string p, string l, string? note = null) => new(p, l, PartFieldType.Number, null, note);
    private static PartField F(string p, string l, string? note = null) => new(p, l, PartFieldType.Flag, null, note);
    private static PartField C(string p, string l, string prefix, string? note = null) => new(p, l, PartFieldType.Constant, prefix, note);

    /// <summary>DamageParameterTables.lua columns 2–32 (column 1 is the damage ID).</summary>
    public static readonly IReadOnlyList<PartField> Damage =
    [
        N("oldLethalDamageVsSoldier", "Lethal damage vs soldiers and animals"), N("oldLethalDamageVsPlayer", "Lethal damage vs the player and buddies"),
        N("oldLethalDamageVsVehicle", "Lethal damage vs vehicles"), N("oldStaminaDamageVsSoldier", "Stamina damage vs soldiers"),
        N("oldStaminaDamageVsPlayer", "Stamina damage vs the player"), N("blowPower", "Blow power (1000+ staggers)"), N("shieldDamage", "Shield damage"),
        C("injureType", "Injury type", "TppDamage.INJ_TYPE_"), C("injurePart", "Injury part", "TppDamage.INJ_PART_"),
        N("injureMaxDistance", "Injury distance (m)"), N("injureRate", "Injury chance (%)"),
        F("isBullet", "Bullet"), F("isSniper", "Sniper"), F("isShotgun", "Shotgun"), F("isTranq", "Tranquilizer"), F("isStun", "Stun"),
        F("isExplosive", "Explosive"), F("isMelee", "Melee"), F("isBlade", "Blade"), F("isFire", "Fire"), F("isParasite", "Parasite"),
        F("isGas", "Gas"), F("isVehicleHit", "Vehicle hit"), F("unk25", "Unknown (unused)"), F("isElectric", "Electric"), F("isWater", "Water"),
        F("isPenetrating", "Penetrating"), C("damageSource", "Damage source", "TppDamage.DAM_SOURCE_"),
        N("lethalDamage", "Lethal damage"), N("staminaDamage", "Stamina damage"), N("impactForce", "Impact force"),
    ];

    public static readonly IReadOnlyList<PartField> Receiver =
    [
        C("attackId", "Damage row", "TppDamage.ATK_"),
        N("receiverParamSetsBase.fireRate", "Fire rate (rounds/min)"), N("receiverParamSetsBase.aimAssistDist", "Aim-assist distance"),
        N("receiverParamSetsBase.gunAimAdjust", "Auto-aim strength"), N("receiverParamSetsBase.effectiveRange", "Effective range"),
        N("receiverParamSetsBase.effectiveRangeUI", "Range shown in R&D"), N("receiverParamSetsBase.adsZoom", "Zoom while aiming"),
        N("receiverParamSetsBase.adsFov", "Field of view while aiming"), N("receiverParamSetsBase.reloadSpeed", "Reload speed (1 = normal)"),
        N("receiverParamSetsWobbling.spreadPerShot", "Bloom per shot"), N("receiverParamSetsWobbling.unk2", "Unknown"),
        N("receiverParamSetsWobbling.spreadRecovery", "Bloom recovery"), N("receiverParamSetsWobbling.spreadMin", "Minimum spread"),
        N("receiverParamSetsWobbling.spreadMax", "Maximum spread"), N("receiverParamSetsWobbling.shotKick", "Aim kick"),
        N("receiverParamSetsWobbling.shotKick2", "Aim kick 2"),
        C("receiverParamSetsSystem.eqpType", "Weapon family", "TppEquip.EQP_TYPE_"), C("receiverParamSetsSystem.reticleUiId", "HUD reticle", "TppEquip.RETICLE_UI_"),
        C("receiverParamSetsSystem.triggerId", "Trigger", "TppEquip.TRIGGER_"), F("receiverParamSetsSystem.showMagazineMesh", "Show magazine mesh"),
        F("receiverParamSetsSystem.plusOneChamber", "Round in the chamber"),
        N("receiverParamSetsSystem.missileMeshVariant", "Missile mesh variant", "Which missile model the launcher shows. 0 for a gun."),
        N("receiverParamSetsSystem.modelDedupExclude", "Model flag", "A V Framework option: it stops the game merging this gun's model with an identical one, so two guns that look the same stay separate. 0 is normal."),
        N("receiverParamSetsSystem.flag5", "Model flag 5", "A V Framework option with no documented meaning. Leave it as the vanilla gun has it."),
        N("receiverParamSetsSystem.sightMountMesh", "Sight mount mesh", "Which of the receiver's meshes a sight sits on. 0 is the usual mount."),
        N("receiverParamSetsSystem.railMountMesh", "Rail mount mesh", "Which mesh a rail attachment sits on. 0 is the usual rail."),
        N("receiverParamSetsSystem.railMountMesh2", "Rail mount mesh 2", "A second rail mount, for a gun with two. 0 when it has one."),
        N("receiverParamSetsSystem.altMagazineSocket", "Alternative magazine socket", "A V Framework option: the magazine socket to use instead of the receiver's own, for a gun whose magazine sits somewhere unusual. 0 keeps the receiver's."),
        new("receiverParamSetsSound", "Fire sound (root, e.g. ar01)", PartFieldType.Text),
        C("motionFrom", "Animations of receiver", "TppEquip.RC_"),
    ];

    public static readonly IReadOnlyList<PartField> Barrel =
    [
        N("barrelParamSetsBase.fireRateMult", "Fire rate ×"), N("barrelParamSetsBase.unk2", "Unknown"), N("barrelParamSetsBase.gunAimAdjustMult", "Aim adjustment ×"),
        N("barrelParamSetsBase.rangeMult", "Range ×"), N("barrelParamSetsBase.rangeUIMult", "Range shown in R&D ×"),
        N("barrelParamSetsBase.spreadMaxMult", "Maximum bloom ×"), N("barrelParamSetsBase.percentOverride", "Unknown (percentOverride)"),
        C("barrelLength", "Barrel length", "TppEquip.BARREL_LENGTH_"), F("hasScopeMount", "Scope mount"), N("unk2", "Unknown"),
        F("hasSideMount", "Side mount"), F("hasUnderMount", "Under mount"),
    ];

    public static readonly IReadOnlyList<PartField> Magazine =
    [
        C("equipAmmoId", "Ammo for resupply", "TppEquip.EQP_AM_"), N("capacity", "Magazine size"), N("totalCarry", "Rounds carried"),
        C("bulletId", "Bullet", "TppEquip.BL_"),
    ];

    private static IEnumerable<PartField> BulletCurve(string set, string who) =>
    [
        N($"{set}.tranqNear", $"{who}tranquilizer: full until (m)"), N($"{set}.tranqFar", $"{who}tranquilizer: faded by (m)"), N($"{set}.tranqResidual", $"{who}tranquilizer: after that ×"),
        N($"{set}.damageNear", $"{who}damage: full until (m)"), N($"{set}.damageFar", $"{who}damage: faded by (m)"), N($"{set}.damageResidual", $"{who}damage: after that ×"),
        N($"{set}.impactNear", $"{who}impact: full until (m)"), N($"{set}.impactFar", $"{who}impact: faded by (m)"), N($"{set}.impactResidual", $"{who}impact: after that ×"),
        C($"{set}.penNear", $"{who}penetration near", "TppEquip.PENETRATE_LEVEL_"), C($"{set}.penFar", $"{who}penetration far", "TppEquip.PENETRATE_LEVEL_"),
        N($"{set}.penSwitchDistance", $"{who}penetration switches at (m)"),
    ];

    public static readonly IReadOnlyList<PartField> Bullet =
    [
        N("bulletSpeed", "Speed (m/s)"), N("npcBulletSpeed", "Speed when soldiers fire it (m/s)"), N("dropRate", "Bullet drop"),
        .. BulletCurve("bulletParamSetsBase", ""), .. BulletCurve("npcBulletParamSetsBase", "Soldiers' "),
        new("bulletTrailEffect", "Trail (.vfx path, empty for none)", PartFieldType.Text), N("unk09", "Unknown"),
        C("ricochetSize", "Impact size", "TppEquip.RICOCHET_SIZE_"), C("bulletType", "Bullet type", "TppEquip.BULLET_TYPE_"),
        C("blastId", "Explosion", "TppEquip.BLA_"), F("isLethal", "Lethal"), C("eqpType", "Weapon family", "TppEquip.EQP_TYPE_"),
    ];

    public static readonly IReadOnlyList<PartField> Stock = [N("spreadRecovery", "Bloom recovery ×"), N("movementSway", "Movement sway ×")];

    public static readonly IReadOnlyList<PartField> MuzzleOption =
        [N("grouping", "Spread per shot ×"), N("durability", "Suppressor life (shots, -1 infinite)"), F("suppressor", "Suppressor")];

    public static readonly IReadOnlyList<PartField> Sight =
    [
        N("zoom1", "Zoom 1"), N("zoom2", "Zoom 2"), N("zoom3", "Zoom 3"), C("scopeUiId", "Overlay", "TppEquip.SCOPE_UI_"),
        F("booster", "Booster"), F("nvg", "Night vision"), F("builtIn", "Built in"), F("rangeFinder", "Range finder"), F("rangeFinderBulletDrop", "Bullet-drop range finder"),
    ];

    public static readonly IReadOnlyList<PartField> Option = [F("isLight", "Flashlight"), F("isLaser", "Laser")];

    public static readonly IReadOnlyList<PartField> UnderBarrel =
        [C("receiverId", "Receiver it fires with", "TppEquip.RC_"), C("magazineId", "Its magazine", "TppEquip.AM_"), N("underBarrelGrade", "Grade")];

    public static IReadOnlyList<PartField> For(WeaponPartKind k) => k switch
    {
        WeaponPartKind.Damage => Damage, WeaponPartKind.Receiver => Receiver, WeaponPartKind.Barrel => Barrel,
        WeaponPartKind.Magazine => Magazine, WeaponPartKind.Bullet => Bullet, WeaponPartKind.Stock => Stock,
        WeaponPartKind.MuzzleOption => MuzzleOption, WeaponPartKind.Sight => Sight, WeaponPartKind.Option => Option,
        _ => UnderBarrel,
    };

    /// <summary>V Framework's names: the Set* call, its ID field, the Declare call and the constant table and prefix.</summary>
    public static (string set, string idField, string declare, string table, string prefix) Lua(WeaponPartKind k) => k switch
    {
        WeaponPartKind.Damage => ("SetDamage", "damageId", "DeclareDamages", "TppDamage", "ATK_"),
        WeaponPartKind.Receiver => ("SetReceiver", "receiverId", "DeclareRCs", "TppEquip", "RC_"),
        WeaponPartKind.Barrel => ("SetBarrel", "barrelId", "DeclareBAs", "TppEquip", "BA_"),
        WeaponPartKind.Magazine => ("SetMagazine", "ammoId", "DeclareAMs", "TppEquip", "AM_"),
        WeaponPartKind.Bullet => ("SetBullet", "bulletId", "DeclareBLs", "TppEquip", "BL_"),
        WeaponPartKind.Stock => ("SetStock", "stockId", "DeclareSKs", "TppEquip", "SK_"),
        WeaponPartKind.MuzzleOption => ("SetMuzzle", "muzzleOptionId", "DeclareMOs", "TppEquip", "MO_"),
        WeaponPartKind.Sight => ("SetSight", "scopeId", "DeclareSTs", "TppEquip", "ST_"),
        WeaponPartKind.Option => ("SetOption", "optionId", "DeclareLTLS", "TppEquip", "LT_"),
        _ => ("SetUnderBarrel", "underBarrelId", "DeclareUBs", "TppEquip", "UB_"),
    };

    /// <summary>The SetGunBasic slot a part fills (damage and bullet hang off the receiver and magazine; null for those).</summary>
    public static string? Slot(WeaponPartKind k) => k == WeaponPartKind.Option ? null : WeaponSlots.Columns.FirstOrDefault(s => s.Stats == k)?.Key;
}

/// <summary>
/// The values of one weapon part, by field path (<see cref="PartField.Path"/>), in field order. <see cref="Id"/> is the
/// part's constant without its table (RC_10101, ATK_10101, BL_HgTranq).
/// </summary>
public sealed class PartValues
{
    public required WeaponPartKind Kind { get; init; }
    public required string Id { get; init; }
    public Dictionary<string, LuaValue> Values { get; } = new(StringComparer.Ordinal);

    public PartValues Copy()
    {
        var c = new PartValues { Kind = Kind, Id = Id };
        foreach (var (k, v) in Values) c.Values[k] = v;
        return c;
    }

    /// <summary>Fields whose value differs from <paramref name="other"/>'s.</summary>
    public List<string> Differences(PartValues other) =>
        WeaponPartFields.For(Kind).Select(f => f.Path)
            .Where(p => !Equals(Values.GetValueOrDefault(p)?.ToString(), other.Values.GetValueOrDefault(p)?.ToString())).ToList();

    /// <summary>The Set* call defining a part with these values under the constant <paramref name="constant"/>.</summary>
    public string SetCall(string constant, string indent = "  ")
    {
        var (set, idField, _, table, _) = WeaponPartFields.Lua(Kind);
        var w = new StringBuilder();
        w.Append($"{indent}V_TppEquip.{set}{{\n");
        w.Append($"{indent}  {idField} = {table}.{constant},\n");
        foreach (var group in WeaponPartFields.For(Kind).GroupBy(f => f.Group))
        {
            if (group.Key.Length == 0)
            {
                foreach (var f in group)
                    if (Values.TryGetValue(f.Path, out var v) && Write(f, v) is { } s) w.Append($"{indent}  {f.Name} = {s},\n");
                continue;
            }
            w.Append($"{indent}  {group.Key} = {{\n");
            foreach (var f in group)
                if (Values.TryGetValue(f.Path, out var v) && Write(f, v) is { } s) w.Append($"{indent}    {f.Name} = {s},\n");
            w.Append($"{indent}  }},\n");
        }
        w.Append($"{indent}}}\n");
        return w.ToString();
    }

    private static string? Write(PartField f, LuaValue v) => (f.Type, v) switch
    {
        // An empty trail is no trail (V Framework: index 0).
        (PartFieldType.Text, LuaValue.Text { Value: "" }) when f.Path == "bulletTrailEffect" => "0",
        _ => v.ToString(),
    };
}
