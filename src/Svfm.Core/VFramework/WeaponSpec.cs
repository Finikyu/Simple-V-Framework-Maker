namespace Svfm.Core.VFramework;

/// <summary>
/// A V Framework 1.7 custom weapon assembled from parts (Custom_Weapons wiki): its own equip and weapon IDs, any mix of
/// vanilla part IDs (TppEquip.RC_/BA_/AM_/… written as code), its model and package, and an R&amp;D row. Changed
/// stats become the copy's own parts (<see cref="Tuning"/>); unchanged parts are the vanilla ones.
/// </summary>
public sealed class WeaponSpec
{
    /// <summary>R&amp;D registration key (saved by V Framework), e.g. "SVFM:WpnTest:Smg".</summary>
    public required string Key { get; init; }
    /// <summary>Constant names created by the module, e.g. "EQP_WP_SVFM_WpnTest_Smg" and "WP_SVFM_WpnTest_Smg".</summary>
    public required string EquipConstant { get; init; }
    public required string WeaponConstant { get; init; }
    /// <summary>TppEquip.EQP_TYPE_* name without the prefix, e.g. "Submachinegun".</summary>
    public required string EquipType { get; init; }
    /// <summary>TppMbDev.EQP_DEV_TYPE_* name without the prefix, e.g. "Submachinegun".</summary>
    public required string DevelopType { get; init; }
    public required string PartsPath { get; init; }
    public required string FpkPath { get; init; }
    public string Block { get; init; } = "EQP_BLOCK_MISSION";
    /// <summary>SetGunBasic fields: name → TppEquip constant (e.g. receiverId → "RC_20002"). ammoId is required.</summary>
    public Dictionary<string, string> Parts { get; } = new(StringComparer.Ordinal);
    public int WeaponGrade { get; set; } = 1;
    public required string NameLang { get; init; }
    public string? InfoLang { get; init; }
    /// <summary>Lang ID of the long real-world name (langEquipRealName); null = none.</summary>
    public string? RealNameLang { get; init; }
    /// <summary>Extra Declare calls written before SetGunBasic: (function such as "DeclareRCs", constant name).</summary>
    public List<(string function, string name)> Declarations { get; } = [];
    /// <summary>Lua written before SetGunBasic (custom SetReceiver, SetBullet … blocks), already indented by two spaces.</summary>
    public string? ExtraLua { get; init; }
    /// <summary>The editable stats of the copy (filled from the game's and the mod's tables); null when not made.</summary>
    public WeaponTuning? Tuning { get; set; }
    /// <summary>Lua of the copy's own parts (Set* calls) from <see cref="Tuning"/>, already indented.</summary>
    public string? TuningLua { get; set; }
    public required string IconFtexPath { get; set; }
    public DevelopRow Develop { get; set; } = new();
    public List<string> Header { get; } = [];
}
