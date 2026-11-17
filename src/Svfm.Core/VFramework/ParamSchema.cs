namespace Svfm.Core.VFramework;

/// <summary>Where the alias of a positional parameter comes from, so a reader knows how far to trust it.</summary>
public enum AliasSource
{
    /// <summary>No alias is known: the field can only be written positionally.</summary>
    None,
    /// <summary>The tool writes this alias itself (<see cref="LuaWriter"/>), and its builds have been run in game.</summary>
    Tool,
    /// <summary>Named in the V Framework 1.7 documentation (MGSV_KB\11_V_Framework_Lua_API.md), not written by the tool.</summary>
    VFrameworkDoc,
    /// <summary>Only in the wiki's table of MGO's unminified names (MGSV_KB\_wiki_raw\EquipDevelopSetting.md). V Framework may not accept it.</summary>
    MgoWiki,
}

/// <summary>
/// One parameter of an R&amp;D row: the game's positional name and the alias V Framework accepts for it.
/// <paramref name="InGameFiles"/> is false for a field that only V Framework has (p18..p21, and langResultRankLimited,
/// which has no positional name: its name is in <paramref name="Positional"/>).
/// </summary>
public sealed record ParamKey(string Positional, string? Alias, AliasSource Source, bool InGameFiles = true);

/// <summary>
/// The fields of one table of an R&amp;D row (<c>const</c> or <c>flow</c> of <c>AddToEquipDevelopTable</c>). A mod may spell a
/// field positionally (<c>p06</c>, as the game's own files do) or by its alias (<c>langEquipName</c>, as this tool writes
/// it), and real mods mix both inside one table. Read a row through <see cref="Formats.LuaValue.Table.Field"/> and the
/// spelling does not matter.
/// <para>
/// The positional names are the game's: EquipDevelopConstSetting.lua writes exactly p00..p17 and p30..p36, and
/// EquipDevelopFlowSetting.lua exactly p50..p74 (ParamSchemaTests checks both against the files on every run). The game
/// files have no aliases, so each alias records its source. Where the sources disagree V Framework's name is used: p08 is
/// <c>iconFtexPath</c> (MGO: ftexPath), p52 is <c>grade</c> (MGO: developRank), p51 is <c>sideGrade</c> (MGO:
/// derivationIndex) and p70 is <c>unk70</c> (MGO: developLevel). V Framework's names are in k_ConstFieldSpecs and
/// k_FlowFieldSpecs of EquipDevelop_AddToEquipDevelopTable.cpp.
/// </para>
/// </summary>
public sealed class ParamSchema
{
    public string Name { get; }
    public IReadOnlyList<ParamKey> Keys { get; }
    private readonly Dictionary<string, ParamKey> _byName = new(StringComparer.Ordinal);

    private ParamSchema(string name, IReadOnlyList<ParamKey> keys)
    {
        Name = name;
        Keys = keys;
        foreach (var k in keys)
        {
            _byName.Add(k.Positional, k);
            if (k.Alias != null) _byName.Add(k.Alias, k);
        }
        // Older SVFM ports used the MGO names. Find still reads them (full parity), and the writer uses the V Framework names.
        foreach (var (old, pos) in OldNames)
            if (_byName.TryGetValue(pos, out var k) && !_byName.ContainsKey(old)) _byName.Add(old, k);
    }

    private static readonly (string Old, string Positional)[] OldNames = [("derivationIndex", "p51"), ("developLevel", "p70")];

    /// <summary>The parameter a field name means, given either spelling; null when the name is not a parameter of this table.</summary>
    public ParamKey? Find(string name) => _byName.GetValueOrDefault(name);

    private static ParamKey P(int n, string? alias, AliasSource source) => new($"p{n:00}", alias, source);

    /// <summary>The <c>const</c> table: what the item is (p00..p17, p30..p36).</summary>
    public static ParamSchema Const { get; } = new("const",
    [
        // p00 is the row's own develop id. Vanilla rows give it; V Framework gives it from the key, so it has no alias.
        P(0, null, AliasSource.None),
        P(1, "equipID", AliasSource.Tool), P(2, "equipDevelopTypeID", AliasSource.Tool), P(3, "baseEquipDevelopId", AliasSource.Tool),
        P(4, "skill", AliasSource.Tool), P(5, "bluePrintId", AliasSource.Tool), P(6, "langEquipName", AliasSource.Tool),
        P(7, "langEquipInfo", AliasSource.Tool), P(8, "iconFtexPath", AliasSource.Tool), P(9, "equipDevelopGroupID", AliasSource.Tool),
        P(10, "langPowerUpInfo0", AliasSource.Tool), P(11, "langPowerUpInfo1", AliasSource.Tool), P(12, "langPowerUpInfo2", AliasSource.Tool),
        P(13, "langPowerUpInfo3", AliasSource.Tool), P(14, "langPowerUpInfo4", AliasSource.Tool), P(15, "langPowerUpInfo5", AliasSource.Tool),
        P(16, "langPowerUpInfo6", AliasSource.Tool), P(17, "langPowerUpInfo7", AliasSource.Tool),
        // The game's file goes from p17 to p30. V Framework 1.7 also reads p18..p21.
        P(18, "langPowerUpInfo8", AliasSource.VFrameworkDoc) with { InGameFiles = false }, P(19, "langPowerUpInfo9", AliasSource.VFrameworkDoc) with { InGameFiles = false },
        P(20, "langPowerUpInfo10", AliasSource.VFrameworkDoc) with { InGameFiles = false }, P(21, "langPowerUpInfo11", AliasSource.VFrameworkDoc) with { InGameFiles = false },
        P(30, "langEquipRealName", AliasSource.Tool), P(31, "isResultRankLimited", AliasSource.Tool),
        P(32, "isCustomEnable", AliasSource.VFrameworkDoc), P(33, "isColorChangeEnable", AliasSource.VFrameworkDoc),
        P(34, "unk34", AliasSource.VFrameworkDoc), P(35, "isSecurityStaffEquip", AliasSource.VFrameworkDoc), P(36, "unk36", AliasSource.VFrameworkDoc),
        // The text of a rank-limited row. V Framework reads it by this name only (kResultRankLimitedTextField).
        new("langResultRankLimited", null, AliasSource.Tool, InGameFiles: false),
    ]);

    /// <summary>The <c>flow</c> table: how the item is developed (p50..p74).</summary>
    public static ParamSchema Flow { get; } = new("flow",
    [
        // p50 is the vanilla row's index into the const file; the wiki gives it no name.
        P(50, null, AliasSource.None),
        P(51, "sideGrade", AliasSource.VFrameworkDoc), P(52, "grade", AliasSource.Tool), P(53, "developGmpCost", AliasSource.Tool),
        // The section names are V Framework's own (k_FlowFieldSpecs in EquipDevelop_AddToEquipDevelopTable.cpp): the
        // main slot p55 is a level only, and sectionIDForDevelop/developSectionLv are the third slot, p63/p64 (issue #38).
        P(54, "usageGmpCost", AliasSource.Tool), P(55, "sectionLvForDevelop", AliasSource.Tool), P(56, "sectionID2ForDevelop", AliasSource.Tool),
        P(57, "sectionLv2ForDevelop", AliasSource.Tool),
        P(58, "resourceType1", AliasSource.Tool), P(59, "resourceType1Count", AliasSource.Tool), P(60, "resourceType2", AliasSource.Tool),
        P(61, "resourceType2Count", AliasSource.Tool), P(62, "initialAvailable", AliasSource.Tool), P(63, "sectionIDForDevelop", AliasSource.Tool),
        P(64, "developSectionLv", AliasSource.Tool), P(65, "resourceUsageType1", AliasSource.Tool), P(66, "resourceUsageType1Count", AliasSource.Tool),
        P(67, "resourceUsageType2", AliasSource.Tool), P(68, "resourceUsageType2Count", AliasSource.Tool), P(69, "displayInfo", AliasSource.Tool),
        P(70, "unk70", AliasSource.VFrameworkDoc), P(71, "developTimeMinute", AliasSource.Tool), P(72, "isValidMbCoin", AliasSource.MgoWiki),
        P(73, "intimacyPoint", AliasSource.MgoWiki), P(74, "isFobAvailable", AliasSource.MgoWiki),
    ]);
}
