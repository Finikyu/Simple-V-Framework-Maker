using System.Globalization;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;

namespace Svfm.Core.VFramework;

/// <summary>
/// The kind of sight a weapon has, as one line of text for the Stats tab: what the zoom does and which overlay the game
/// draws. The rules and their evidence are in .docs/Research_Weapon_Sight_Kind.md.
/// </summary>
public static class WeaponSight
{
    /// <summary>
    /// The line for the two sight slots of a gun. A mod's own row (<paramref name="mod"/>, key without the table) wins over
    /// the game's row. An id with no row is "not known": this function does not guess.
    /// </summary>
    public static string Describe(string? scope1, string? scope2, EquipTables? vanilla, IReadOnlyDictionary<string, PartValues>? mod = null)
    {
        if (vanilla == null && (mod == null || mod.Count == 0)) return "Sight: not known.";
        bool none1 = IsNone(scope1), none2 = IsNone(scope2);
        if (none1 && none2) return "Sight: no sight part, no scope overlay.";
        string text = $"Sight: {(none1 ? "no first sight" : Slot(scope1!, vanilla, mod))}.";
        return none2 ? text : $"{text} Second sight: {Slot(scope2!, vanilla, mod)}.";
    }

    /// <summary>One sight part as short text for a list of choices, e.g. "scope, 4x (ST_60304)".</summary>
    public static string Name(string id, EquipTables? vanilla) => IsNone(id) ? "none" : Slot(id, vanilla, null);

    // An absent slot is 0 in V Framework's SetGunBasic (GunBasicInject.cpp, kNoneValue).
    private static bool IsNone(string? id) =>
        string.IsNullOrEmpty(id) || id == "0" || EquipTables.Strip(id).Equals("ST_None", StringComparison.OrdinalIgnoreCase);

    private static string Slot(string slot, EquipTables? vanilla, IReadOnlyDictionary<string, PartValues>? mod)
    {
        string id = EquipTables.Strip(slot);
        var game = vanilla?.Part(WeaponPartKind.Sight, id);
        var row = mod?.GetValueOrDefault(id) ?? game;
        return row == null ? $"not known ({id})" : $"{Kind(row, game == null)} ({id})";
    }

    private static string Kind(PartValues row, bool custom)
    {
        double[] zooms = [.. new[] { "zoom1", "zoom2", "zoom3" }.Select(k => row.Values.GetValueOrDefault(k)?.AsNumber ?? 0).Where(z => z != 0)];
        string list = string.Join(" / ", zooms.Select(z => z.ToString("0.##", CultureInfo.InvariantCulture) + "x"));
        string ui = row.Values.TryGetValue("scopeUiId", out var u) ? EquipTables.Strip(u.ToString()!) : "";
        bool ownReticle = Text(row, "windowFactoryName") != "" && Text(row, "scopeUiPath") != "";
        // V Framework reads an absent scopeUiId as 0: no overlay, unless the sight names its own window.
        bool noOverlay = ui is "" or "0" or "SCOPE_UI_NONE" && !ownReticle;

        string kind;
        if (zooms.Length == 0) kind = "no zoom";
        else if (ui == "SCOPE_UI_DOT") kind = zooms.Max() == 1 ? "dot sight, no zoom" : "dot sight, " + list;
        else if (On(row, "nvg")) kind = "night vision scope, no zoom";
        else if (noOverlay) kind = "no scope overlay, " + list;
        // Only the two vanilla boosters: a custom sight with zoom 1, 2 is a scope with two steps of its own.
        else if (!custom && zooms is [1, 2] or [1, 2, 4]) kind = "booster (magnifier), " + list;
        else kind = "scope, " + list;

        if (On(row, "rangeFinder")) kind += ", range finder";
        if (custom && ownReticle) kind += ", custom reticle";
        else if (custom && !noOverlay && Text(row, "windowFactoryName") == "") kind += ", borrowed reticle";
        return kind;
    }

    private static bool On(PartValues row, string key) =>
        row.Values.GetValueOrDefault(key) is { } v && (v.AsNumber is > 0 || v is LuaValue.Name { Value: "true" });

    private static string Text(PartValues row, string key) => row.Values.GetValueOrDefault(key) is LuaValue.Text t ? t.Value : "";
}
