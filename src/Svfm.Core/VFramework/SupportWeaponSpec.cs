using Svfm.Core.Knowledge;

namespace Svfm.Core.VFramework;

/// <summary>
/// A V Framework 1.7 custom support weapon (Custom_Weapons wiki, "Support weapons"): its own EQP_SWP_/SWP_ IDs, stats
/// (SetSupportWeaponParameter), model and package (AddToEquipIdTable), the vanilla thrown item it is held, thrown and
/// flies like (SetSupportWeapon throwAs; V Framework accepts only EQP_TYPE_Throwing items there), and an R&amp;D row.
/// Its effect never runs natively: <see cref="Script"/> runs it on the Throwing messages. Every value is editable; the
/// item is always a new one (own IDs, own attack and blast rows when changed), never a vanilla row overwritten.
/// </summary>
public sealed class SupportWeaponSpec
{
    public required string Key { get; init; }
    /// <summary>"EQP_SWP_SVFM_…" and "SWP_SVFM_…".</summary>
    public required string EquipConstant { get; init; }
    public required string SwpConstant { get; init; }
    /// <summary>Vanilla thrown item, e.g. "EQP_SWP_SmokeGrenade".</summary>
    public required string ThrowAs { get; set; }
    /// <summary>TppEquip.EQP_TYPE_* without the prefix: "Throwing" (custom placed items get no behaviour in 1.7).</summary>
    public string EquipType { get; init; } = "Throwing";
    public string Block { get; init; } = "EQP_BLOCK_COMMON";
    public required string PartsPath { get; init; }
    public required string FpkPath { get; init; }
    public int Ammo { get; set; } = 1;
    /// <summary>Fuse time in seconds (p1; mines: sensor range, decoys: strength).</summary>
    public double FuseTime { get; set; }
    /// <summary>Effect time in seconds (p2).</summary>
    public double ActiveTime { get; set; }
    public int Grade { get; set; } = 1;
    /// <summary>Damage type icon in R&amp;D (1 DMG, 2 ZZZ, 3 STN, 4 SMK, 6 ---, 0 hidden); null: automatic.</summary>
    public int? DamageType { get; set; }
    /// <summary>Details-panel bar fills, 0–1 (null: automatic); a hidden bar wins over its fill.</summary>
    public double? BarDamage { get; set; }
    public double? BarDurability { get; set; }
    public double? BarEffectTime { get; set; }
    public bool HideBarDamage { get; set; }
    public bool HideBarDurability { get; set; }
    public bool HideBarEffectTime { get; set; }
    /// <summary>The value a full Durability / Effect Duration bar stands for; null: vanilla.</summary>
    public double? DurabilityMax { get; set; }
    public double? EffectTimeMax { get; set; }

    /// <summary>
    /// The attack a vanilla effect run on the item uses (SetSupportWeapon attackId): a <c>TppDamage.ATK_*</c> constant,
    /// or null for the throwAs item's. <see cref="OwnAttack"/>, when set, wins: the item's own copy of a damage row.
    /// </summary>
    public string? AttackId { get; set; }
    /// <summary>The item's own damage row (DeclareDamages + SetDamage), as <c>ATK_{<see cref="OwnSuffix"/>}</c>.</summary>
    public PartValues? OwnAttack { get; set; }
    /// <summary>The item's blast (SetSupportWeapon blastId): a <c>TppEquip.BLA_*</c> constant, or null for the throwAs item's.</summary>
    public string? BlastId { get; set; }
    /// <summary>The item's own blast row (DeclareBLAs + SetBlastParameter), as <c>BLA_{<see cref="OwnSuffix"/>}</c>; wins over <see cref="BlastId"/>.</summary>
    public BlastValues? OwnBlast { get; set; }
    /// <summary>For information: the vanilla attack and blast named for the throwAs item (ATK_Grenade, BLA_Grenade), when
    /// the game has them; what a copy of them starts from.</summary>
    public string? ThrowAsAttack { get; set; }
    public string? ThrowAsBlast { get; set; }
    /// <summary>The thrown-as item (EQP id) the user copied <see cref="OwnAttack"/> / <see cref="OwnBlast"/> from (#119);
    /// null for a row the port made. A later change of the thrown-as item keeps the copy and this name.</summary>
    public string? OwnAttackFrom { get; set; }
    public string? OwnBlastFrom { get; set; }
    /// <summary>The vanilla thrown item (EQP id) the user last copied with the Preset pick (#107), or null (Custom).
    /// The pick shows it only while the values still match it (Conversion.ThrownPresets.Shown).</summary>
    public string? Preset { get; set; }

    /// <summary>Throws it as another vanilla thrown item (issue #112), with the attack and blast named for that item
    /// (SupportWeaponConverter.ThrowAsRows). A default script swaps its effect step; an edited one stays.</summary>
    public void SetThrowAs(string throwAs, string? attack, string? blast)
    {
        Script.SwapDefaultEffect(ThrowAs, throwAs);
        ThrowAs = throwAs; ThrowAsAttack = attack; ThrowAsBlast = blast;
    }

    /// <summary>What the item does when thrown (V Framework runs no native effect on it).</summary>
    public ThrowScript Script { get; set; } = new();

    /// <summary>The ID part the item's own rows are named with: EQP_SWP_SVFM_Banana → SVFM_Banana.</summary>
    public string OwnSuffix => EquipConstant.StartsWith("EQP_SWP_", StringComparison.Ordinal) ? EquipConstant[8..] : EquipConstant;
    public string? OwnAttackConstant => OwnAttack != null ? $"ATK_{OwnSuffix}" : null;
    public string? OwnBlastConstant => OwnBlast != null ? $"BLA_{OwnSuffix}" : null;
    /// <summary>The attack and blast the item uses, as Lua (null: the throwAs item's).</summary>
    public string? Attack => OwnAttackConstant is { } a ? $"TppDamage.{a}" : AttackId;
    public string? Blast => OwnBlastConstant is { } b ? $"TppEquip.{b}" : BlastId;

    /// <summary>What p1 (<see cref="FuseTime"/>) means for an item thrown as <paramref name="throwAs"/>. V Framework writes
    /// fuseTime, sensorRange and decoyStrength to the same column (V_TppEquipLib.cpp); the vanilla mines read it as a
    /// sensor range and the decoys as a strength, every Throwing item as a fuse (.docs/Thrown_p1_p2.md, issue #118).</summary>
    public static P1Kind P1KindOf(string throwAs) =>
        throwAs.Contains("Decoy", StringComparison.Ordinal) ? P1Kind.DecoyStrength
        : throwAs.Contains("Mine", StringComparison.Ordinal) && !throwAs.EndsWith("Locator", StringComparison.Ordinal) ? P1Kind.SensorRange : P1Kind.Fuse;
    public P1Kind P1 => P1KindOf(ThrowAs);

    /// <summary>The fuse ends before a "hits a surface" step can run: the item goes off in the air, or in the hand at 0.</summary>
    public bool ShortFuse => P1 == P1Kind.Fuse && FuseTime < MinImpactFuse && Script.On.TryGetValue(ThrowEvent.Bounces, out var hit) && hit.Count > 0;
    /// <summary>The shortest fuse with no <see cref="ShortFuse"/> warning: the vanilla grenades' 3 seconds.</summary>
    public const double MinImpactFuse = 3;

    /// <summary>The script's context: the item's equip ID and name, and the attack and blast its steps use for "the item's"
    /// (its own or set ones, else the ones named for the throwAs item).</summary>
    public ThrowContext ScriptContext(string title) =>
        new($"TppEquip.{EquipConstant}", title, Attack ?? (ThrowAsAttack is { } ta ? $"TppDamage.{ta}" : null), Blast ?? (ThrowAsBlast is { } tb ? $"TppEquip.{tb}" : null));

    public string DevelopType { get; init; } = "Throwing";
    public string? DevelopGroup { get; init; }
    public required string NameLang { get; init; }
    public string? RealNameLang { get; init; }
    public string? InfoLang { get; init; }
    public required string IconFtexPath { get; set; }
    public DevelopRow Develop { get; set; } = new();
    public List<string> Header { get; } = [];
}

/// <summary>What the p1 column of a support weapon stands for (SetSupportWeaponParameter fuseTime / sensorRange / decoyStrength).</summary>
public enum P1Kind { Fuse, SensorRange, DecoyStrength }

/// <summary>One handler in the module's Messages() table: message class ("Throwing"), message ("OnStopThrowing"), the
/// handler's parameter list and its Lua body (not indented; the writer indents it).</summary>
public sealed record MessageHandler(string Class, string Message, string Parameters, string Body);

/// <summary>A support weapon's editable behaviour, as plain data (for undo and the project file).</summary>
public sealed class SupportSnapshot
{
    public int? DamageType { get; set; }
    public double? BarDamage { get; set; }
    public double? BarDurability { get; set; }
    public double? BarEffectTime { get; set; }
    public bool HideBarDamage { get; set; }
    public bool HideBarDurability { get; set; }
    public bool HideBarEffectTime { get; set; }
    public double? DurabilityMax { get; set; }
    public double? EffectTimeMax { get; set; }
    public string? AttackId { get; set; }
    public string? BlastId { get; set; }
    public Dictionary<string, string>? OwnAttack { get; set; }
    /// <summary>The vanilla row the own attack is a copy of (PartValues.Id): what a restore makes the row from when the
    /// item has none (#119). Null in a project file from before #119.</summary>
    public string? OwnAttackRow { get; set; }
    public BlastValues? OwnBlast { get; set; }
    public string? OwnAttackFrom { get; set; }
    public string? OwnBlastFrom { get; set; }
    public string Script { get; set; } = "[]";
    /// <summary>Null in a project file from before issue #112: the item keeps what it is thrown as.</summary>
    public string? ThrowAs { get; set; }
    public string? ThrowAsAttack { get; set; }
    public string? ThrowAsBlast { get; set; }
    /// <summary>#107: the grade (a preset sets it; null in an older project file: the item keeps its own) and the pick.</summary>
    public int? Grade { get; set; }
    public string? Preset { get; set; }

    public static SupportSnapshot Take(SupportWeaponSpec s) => new()
    {
        DamageType = s.DamageType, BarDamage = s.BarDamage, BarDurability = s.BarDurability, BarEffectTime = s.BarEffectTime,
        HideBarDamage = s.HideBarDamage, HideBarDurability = s.HideBarDurability, HideBarEffectTime = s.HideBarEffectTime,
        DurabilityMax = s.DurabilityMax, EffectTimeMax = s.EffectTimeMax, AttackId = s.AttackId, BlastId = s.BlastId,
        OwnAttack = s.OwnAttack?.Values.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()!), OwnAttackRow = s.OwnAttack?.Id, OwnBlast = s.OwnBlast, Script = s.Script.ToJson(),
        OwnAttackFrom = s.OwnAttackFrom, OwnBlastFrom = s.OwnBlastFrom,
        ThrowAs = s.ThrowAs, ThrowAsAttack = s.ThrowAsAttack, ThrowAsBlast = s.ThrowAsBlast, Grade = s.Grade, Preset = s.Preset,
    };

    /// <summary>Puts the values back into <paramref name="s"/>. Bad text (a hand-edited project file) throws
    /// InvalidDataException (or JsonException for the script) before any value changes.</summary>
    public void Restore(SupportWeaponSpec s)
    {
        var attack = OwnAttack?.ToDictionary(kv => kv.Key, kv => (kv.Value == null ? null : Formats.LuaLiteral.TryParse(kv.Value))
            ?? throw new InvalidDataException($"Bad attack value \"{kv.Value}\" for {kv.Key}."));
        var script = ThrowScript.FromJson(Script ?? throw new InvalidDataException("The support weapon has no script."));
        s.DamageType = DamageType; s.BarDamage = BarDamage; s.BarDurability = BarDurability; s.BarEffectTime = BarEffectTime;
        s.HideBarDamage = HideBarDamage; s.HideBarDurability = HideBarDurability; s.HideBarEffectTime = HideBarEffectTime;
        s.DurabilityMax = DurabilityMax; s.EffectTimeMax = EffectTimeMax; s.AttackId = AttackId; s.BlastId = BlastId;
        // #119: a saved own attack comes back on an item with none (a copy the user made); none saved removes the row.
        if (attack == null) s.OwnAttack = null;
        else
        {
            s.OwnAttack ??= new PartValues { Kind = WeaponPartKind.Damage, Id = OwnAttackRow ?? ThrowAsAttack ?? s.ThrowAsAttack ?? "" };
            s.OwnAttack.Values.Clear();
            foreach (var (k, v) in attack) s.OwnAttack.Values[k] = v;
        }
        s.OwnBlast = OwnBlast; s.OwnAttackFrom = OwnAttackFrom; s.OwnBlastFrom = OwnBlastFrom;
        s.Script.CopyFrom(script);
        if (ThrowAs != null) { s.ThrowAs = ThrowAs; s.ThrowAsAttack = ThrowAsAttack; s.ThrowAsBlast = ThrowAsBlast; }
        if (Grade is { } g) s.Grade = g;
        s.Preset = Preset;
    }
}
