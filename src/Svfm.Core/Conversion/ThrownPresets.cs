using System.Runtime.CompilerServices;
using Svfm.Core.Archives;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>
/// Issue #107: a preset copies the values of a vanilla thrown item to a thrown item in one step: what it is thrown as, the
/// ammo, fuse, effect time and grade, the attack and blast (the thrown-as item's, so no own rows), and the default script.
/// The model, name, description, icon and R&amp;D row stay. The pick shows the preset only while the values still match it.
/// </summary>
public static class ThrownPresets
{
    /// <summary>The vanilla thrown items a preset can copy (placed items are not in it), by EQP id.</summary>
    public static IEnumerable<string> All => GameKnowledge.SupportWeapons.Where(w => w.Value.EquipType == "Throwing").Select(w => w.Key);

    private static readonly ConditionalWeakTable<GameData, StrongBox<SupportTables?>> Tables = new();

    /// <summary>The stats of a vanilla thrown item: the game's table, else the knowledge (the same mapping as a port).</summary>
    public static SupportStats Stats(GameData? game, string eqp)
    {
        var v = GameKnowledge.SupportWeapons[eqp];
        var t = game == null ? null : Tables.GetValue(game, g => new(SupportTables.Load(g.TryRead))).Value;
        return t?.Stats.GetValueOrDefault(Swp(eqp)) ?? new SupportStats((int)(v.Ammo ?? 1), v.P1 ?? 0, v.P2 ?? 0, v.Grade ?? 1);
    }

    private static string Swp(string eqp) => GameKnowledge.SupportWeapons.TryGetValue(eqp, out var v) ? v.Swp.Replace("TppEquip.", "") : eqp.Replace("EQP_", "");

    /// <summary>The default script of a vanilla thrown item (as a port makes it).</summary>
    public static ThrowScript Script(string eqp) => ThrowScript.VanillaEffect(Swp(eqp));

    /// <summary>The script is the default of <paramref name="throwAs"/>, in either id form (an unedited script).</summary>
    public static bool IsDefault(ThrowScript s, string? throwAs) => throwAs == null ? s.IsEmpty
        : s.ToJson() is var j && (j == Script(throwAs).ToJson() || j == ThrowScript.VanillaEffect(throwAs).ToJson());

    /// <summary>The text of the confirmation before a preset replaces the script, or null when no question is needed.</summary>
    public static string? Confirm(ThrowScript? script, string? throwAs, bool locked, string name) =>
        locked ? $"This replaces the mod's own throw script with the default script of {name}. The tool cannot read the mod's script, so its steps are lost."
        : script != null && !IsDefault(script, throwAs) ? $"This replaces the edited throw script with the default script of {name}." : null;

    public static void Apply(SupportWeaponSpec s, GameData? game, string eqp)
    {
        var st = Stats(game, eqp);
        var (atk, bla) = game != null ? SupportWeaponConverter.ThrowAsRows(game, eqp) : (null, null);
        s.SetThrowAs(eqp, atk, bla);
        s.Ammo = st.Ammo; s.FuseTime = st.P1; s.ActiveTime = st.P2; s.Grade = st.Grade;
        s.AttackId = null; s.BlastId = null; s.OwnAttack = null; s.OwnBlast = null; s.OwnAttackFrom = null; s.OwnBlastFrom = null;
        s.Script.CopyFrom(Script(eqp));
        s.Preset = eqp;
    }

    /// <summary>The preset the pick shows: the stored one while every value it set still matches; null is "Custom".</summary>
    public static string? Shown(SupportWeaponSpec s, GameData? game) =>
        s.Preset is { } p && GameKnowledge.SupportWeapons.ContainsKey(p) && Stats(game, p) is var st && s.ThrowAs == p
        && s.Ammo == st.Ammo && s.FuseTime == st.P1 && s.ActiveTime == st.P2 && s.Grade == st.Grade
        && s is { AttackId: null, BlastId: null, OwnAttack: null, OwnBlast: null } && IsDefault(s.Script, p) ? p : null;

    /// <summary>The value of a preset for a field of an imported thrown item's SetSupportWeaponParameter call, or null.</summary>
    private static double? FieldValue(ExistingField f, SupportStats st) => f is { Group: "Thrown item", Path: [var k] } ? k switch
    {
        "ammo" => st.Ammo, "fuseTime" => st.P1, "activeTime" => st.P2, "grade" => st.Grade, _ => null,
    } : null;

    /// <summary>
    /// An imported thrown item: the values its script sets are spliced, the script is replaced and the lock lifted.
    /// shortcut: an attack or blast the mod names (attackId, blastId) is shown only and stays; upgrade when those become fields.
    /// </summary>
    public static void Apply(ExistingItem x, GameData? game, string eqp)
    {
        var st = Stats(game, eqp);
        foreach (var f in x.Fields.Where(f => f.Locked == null))
            if (FieldValue(f, st) is { } n) f.Value = n;
        if (x.ThrowAsField is { Locked: null } ta) ta.Value = eqp;
        x.Script?.CopyFrom(Script(eqp));
        if (x.ScriptLock != null) x.LockLifted = true;
        x.Preset = eqp;
    }

    public static string? Shown(ExistingItem x, GameData? game) =>
        x.Preset is { } p && GameKnowledge.SupportWeapons.ContainsKey(p) && Stats(game, p) is var st && x.ThrowAs == p && x.ScriptLock == null
        && x.Script is { } sc && IsDefault(sc, p) && x.Fields.All(f => f.Locked != null || FieldValue(f, st) is not { } n || Equals(f.Value, n)) ? p : null;
}
