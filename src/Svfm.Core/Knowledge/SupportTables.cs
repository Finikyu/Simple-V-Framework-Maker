using System.Text;
using Svfm.Core.Formats;

namespace Svfm.Core.Knowledge;

/// <summary>A support weapon's stats row (EquipParameterTables.lua SupportWeaponParameter: {SWP, ammo, p1, p2, grade}).</summary>
public sealed record SupportStats(int Ammo, double P1, double P2, int Grade);

/// <summary>
/// A blast row (EquipParameterTables.lua BlastParameter: {BLA, blastFlag, effectRange, optimalRange}; V Framework
/// SetBlastParameter has the same fields). <see cref="BlastFlag"/> is the noise enemies react to (0–4),
/// <see cref="EffectRange"/> the radius for the player's items, <see cref="OptimalRange"/> for everyone else's.
/// </summary>
public sealed record BlastValues(int BlastFlag, double EffectRange, double OptimalRange);

/// <summary>
/// The game's support weapon and blast tables, read from EquipParameterTables.lua (TppEquip.ReloadEquipParameterTables):
/// the vanilla file, or a mod's own copy of it (a mod that changes how many grenades are carried, or a blast radius).
/// Keys are the constants without their table (SWP_Grenade, BLA_Grenade).
/// </summary>
public sealed class SupportTables
{
    public const string Path = "/Assets/tpp/level_asset/weapon/ParameterTables/EquipParameterTables.lua";

    public Dictionary<string, SupportStats> Stats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, BlastValues> Blasts { get; } = new(StringComparer.Ordinal);

    /// <summary>Reads the file through <paramref name="read"/> (bytes by game path, null when absent); null when absent.</summary>
    public static SupportTables? Load(Func<string, byte[]?> read) =>
        read(Path) is { } d ? Parse(Encoding.UTF8.GetString(d)) : null;

    public static SupportTables Parse(string lua)
    {
        var root = LuaLiteral.CallArgument(lua, "TppEquip.ReloadEquipParameterTables") ?? throw new FormatException("No ReloadEquipParameterTables call.");
        var t = new SupportTables();
        foreach (var r in Rows(root["SupportWeaponParameter"]))
            if (r.Items.Count >= 5)
                t.Stats[EquipTables.Strip(r.Items[0].ToString()!)] = new SupportStats((int)(r.Items[1].AsNumber ?? 0), r.Items[2].AsNumber ?? 0, r.Items[3].AsNumber ?? 0, (int)(r.Items[4].AsNumber ?? 1));
        foreach (var r in Rows(root["BlastParameter"]))
            if (r.Items.Count >= 4)
                t.Blasts[EquipTables.Strip(r.Items[0].ToString()!)] = new BlastValues((int)(r.Items[1].AsNumber ?? 2), r.Items[2].AsNumber ?? 0, r.Items[3].AsNumber ?? 0);
        return t;
    }

    // Sections start with a flag number, then the rows.
    private static IEnumerable<LuaValue.Table> Rows(LuaValue? section) => (section as LuaValue.Table)?.Items.OfType<LuaValue.Table>() ?? [];
}
