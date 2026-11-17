using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.VFramework;

namespace Svfm.Core.Knowledge;

public sealed record SnakeCamoFiles(
    [property: JsonPropertyName("normal_scarf")] string NormalScarf,
    [property: JsonPropertyName("naked")] string Naked,
    [property: JsonPropertyName("texture")] string Texture,
    [property: JsonPropertyName("nakedTexture")] string NakedTexture);

public sealed record DdCamoFiles(
    [property: JsonPropertyName("male")] string Male,
    [property: JsonPropertyName("female")] string Female,
    [property: JsonPropertyName("bodyTexture")] string? BodyTexture,
    [property: JsonPropertyName("itemTexture")] string? ItemTexture);

/// <summary>A vanilla camo type (IH playerCamoTypes), with its packages per body.</summary>
public sealed record CamoInfo
{
    [JsonPropertyName("playerCamoType")] public int PlayerCamoType { get; init; }
    /// <summary>IH name, also what V Framework's camoBonusType takes (e.g. "BLACK").</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("developId")] public int? DevelopId { get; init; }
    /// <summary>The c&lt;id&gt; / v&lt;id&gt; number in package names; null for suits that are not fatigues camos.</summary>
    [JsonPropertyName("fovaCamoId")] public int? FovaCamoId { get; init; }
    [JsonPropertyName("playerParts")] public List<string> PlayerParts { get; init; } = [];
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    [JsonPropertyName("langName")] public string? LangName { get; init; }
    [JsonPropertyName("langInfo")] public string? LangInfo { get; init; }
    [JsonPropertyName("snake")] public SnakeCamoFiles? Snake { get; init; }
    [JsonPropertyName("dd")] public DdCamoFiles? Dd { get; init; }
    [JsonPropertyName("iconStyle")] public string? IconStyle { get; init; }
    [JsonPropertyName("iconTextureOrigin")] public int[]? IconTextureOrigin { get; init; }
}

/// <summary>A vanilla parts type (suit model), with its package per body.</summary>
/// <summary>A named group of surfaces (issue #89): its surfaces, the vanilla outfits that hide on all of them, and its keywords.</summary>
public sealed record CamoEnvironment(string Title, string Note, IReadOnlyList<string> Surfaces, IReadOnlyList<string> FitCamos, Regex Keywords);

public sealed record PartsTypeInfo
{
    /// <summary>Null for IH entries without a vanilla parts type (HOSPITAL2 ...).</summary>
    [JsonPropertyName("partsType")] public int? PartsType { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("developId")] public int? DevelopId { get; init; }
    [JsonPropertyName("packages")] public Dictionary<string, string> Packages { get; init; } = [];
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    /// <summary>
    /// The head is part of the body model: the game loads no separate face for this parts type (SOLID SNAKE, Ninja,
    /// Raiden; IH InfPlayerParts gives them no needHead). Replacers of these bodies keep a built-in head.
    /// </summary>
    [JsonPropertyName("builtInHead")] public bool BuiltInHead { get; init; }
    /// <summary>
    /// The vanilla suit offers no head options (bandanas, balaclava …): the head is built in (SOLID SNAKE, Ninja,
    /// Raiden), covered (Parasite Suit mask), or the suit isn't a normal uniform (hospital gown, Avatar editor body).
    /// Source: the user's in-game check and the parts types Auditor's Debriefing mod extends with its head.
    /// </summary>
    [JsonPropertyName("noHeadOptions")] public bool NoHeadOptions { get; init; }
}

/// <summary>A vanilla numbered weapon built from chimera parts (IH EquipParameters gunBasic row).</summary>
public sealed record ChimeraWeapon
{
    [JsonPropertyName("wp")] public string Wp { get; init; } = "";
    [JsonPropertyName("equip")] public string Equip { get; init; } = "";
    [JsonPropertyName("receiverId")] public string ReceiverId { get; init; } = "";
    [JsonPropertyName("barrelId")] public string BarrelId { get; init; } = "";
    [JsonPropertyName("ammoId")] public string AmmoId { get; init; } = "";
    [JsonPropertyName("muzzleId")] public string MuzzleId { get; init; } = "MZ_None";
    [JsonPropertyName("muzzleOptionId")] public string MuzzleOptionId { get; init; } = "MO_None";
    [JsonPropertyName("stockId")] public string StockId { get; init; } = "SK_None";
    [JsonPropertyName("scope1Id")] public string Scope1Id { get; init; } = "ST_None";
    [JsonPropertyName("scope2Id")] public string Scope2Id { get; init; } = "ST_None";
    [JsonPropertyName("underBarrelId")] public string UnderBarrelId { get; init; } = "UD_None";
    [JsonPropertyName("laserFlash1Id")] public string LaserFlash1Id { get; init; } = "LT_None";
    [JsonPropertyName("laserFlash2Id")] public string LaserFlash2Id { get; init; } = "LT_None";
    [JsonPropertyName("grade")] public int Grade { get; init; }
    /// <summary>Name of TppMbDev.EQP_DEV_TYPE_* (also the TppEquip.EQP_TYPE_* name for these weapons).</summary>
    [JsonPropertyName("devType")] public string DevType { get; init; } = "";
    [JsonPropertyName("nameLang")] public string? NameLang { get; init; }
    [JsonPropertyName("infoLang")] public string? InfoLang { get; init; }
    [JsonPropertyName("realLang")] public string? RealLang { get; init; }
    [JsonPropertyName("icon")] public string? Icon { get; init; }
}

/// <summary>A chimera receiver package and the vanilla weapons that use its receiver.</summary>
public sealed record ChimeraPackage
{
    [JsonPropertyName("partsPath")] public string PartsPath { get; init; } = "";
    [JsonPropertyName("weapons")] public List<ChimeraWeapon> Weapons { get; init; } = [];
}

/// <summary>A "behaves like" preset for the ability controls.</summary>
public sealed record BehaviourPreset(string Id, string Label, Abilities Abilities, string? Note);

/// <summary>A vanilla R&amp;D row: name lang ID, grade, development and usage (deployment) GMP.</summary>
public sealed record VanillaDevelopRow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("grade")] int Grade,
    [property: JsonPropertyName("developGmp")] int DevelopGmp,
    [property: JsonPropertyName("usageGmp")] int UsageGmp)
{
    // The rest of the flow row (issue #64): what a copy of the row also fills in. Zero and empty mean left out.
    [JsonPropertyName("timeMinutes")] public int TimeMinutes { get; init; }
    [JsonPropertyName("sectionLv")] public int SectionLv { get; init; }
    [JsonPropertyName("section2Id")] public int Section2Id { get; init; }
    [JsonPropertyName("section2Lv")] public int Section2Lv { get; init; }
    [JsonPropertyName("sectionId")] public int SectionId { get; init; }
    [JsonPropertyName("sectionPairLv")] public int SectionPairLv { get; init; }
    [JsonPropertyName("res1")] public string? Res1 { get; init; }
    [JsonPropertyName("res1Count")] public int Res1Count { get; init; }
    [JsonPropertyName("res2")] public string? Res2 { get; init; }
    [JsonPropertyName("res2Count")] public int Res2Count { get; init; }
    [JsonPropertyName("use1")] public string? Use1 { get; init; }
    [JsonPropertyName("use1Count")] public int Use1Count { get; init; }
    [JsonPropertyName("use2")] public string? Use2 { get; init; }
    [JsonPropertyName("use2Count")] public int Use2Count { get; init; }
    /// <summary>Specialist skill (const p04) and blueprint (const p05, without its TppMotherBaseManagementConst. prefix).</summary>
    [JsonPropertyName("skill")] public string? Skill { get; init; }
    [JsonPropertyName("blueprint")] public string? Blueprint { get; init; }
    /// <summary>Flow p69 = 1: hidden until the skill or blueprint is obtained.</summary>
    [JsonPropertyName("hidden")] public bool Hidden { get; init; }

    /// <summary>The row as an R&amp;D row of a project item: the main staff section is the p63/p64 pair, else a p55 level of the R&amp;D section.</summary>
    public DevelopRow Apply(DevelopRow d)
    {
        int id = SectionId, lv = SectionPairLv;
        if (id == 0 && SectionLv > 0) (id, lv) = (DevelopRow.RdSection, SectionLv);
        return d with
        {
            Grade = Grade, GmpCost = DevelopGmp, UsageGmpCost = UsageGmp, TimeMinutes = TimeMinutes, SectionId = id, SectionLevel = lv,
            Section2Id = Section2Id, Section2Level = Section2Lv, Resource1 = Res1, Resource1Count = Res1Count, Resource2 = Res2, Resource2Count = Res2Count,
            Usage1 = Use1, Usage1Count = Use1Count, Usage2 = Use2, Usage2Count = Use2Count,
            Skill = Skill, BluePrint = Blueprint, HiddenUntilObtained = Hidden,
            // A copied blueprint replaces a mod script's own one; without one, the script's blueprint stays.
            OwnBluePrint = Blueprint != null ? null : d.OwnBluePrint,
            // The Unlock box keeps a mission unlock and a blueprint apart (choosing a mission clears the blueprint).
            AfterMission = Blueprint != null || Hidden ? null : d.AfterMission,
        };
    }
}

/// <summary>A vanilla support weapon: EquipIdTable row, SupportWeaponParameter (ammo, p1, p2, grade) and R&amp;D row.</summary>
public sealed record VanillaSupportWeapon(
    [property: JsonPropertyName("equipType")] string EquipType,
    [property: JsonPropertyName("swp")] string Swp,
    [property: JsonPropertyName("block")] string Block,
    [property: JsonPropertyName("parts")] string Parts,
    [property: JsonPropertyName("fpk")] string Fpk,
    [property: JsonPropertyName("ammo")] double? Ammo,
    [property: JsonPropertyName("p1")] double? P1,
    [property: JsonPropertyName("p2")] double? P2,
    [property: JsonPropertyName("grade")] int? Grade,
    [property: JsonPropertyName("develop")] VanillaSupportDevelop? Develop);

public sealed record VanillaSupportDevelop(
    [property: JsonPropertyName("developId")] int DevelopId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("info")] string Info,
    [property: JsonPropertyName("icon")] string Icon,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("parent")] int Parent);

/// <summary>One chimera part's package (ChimeraPartsPackageTable.lua packageInfos row).</summary>
public sealed record ChimeraPartPackage(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("parts")] string Parts,
    [property: JsonPropertyName("fpk")] string Fpk,
    [property: JsonPropertyName("paintFv2")] string PaintFv2,
    [property: JsonPropertyName("paintFpk")] string PaintFpk);

/// <summary>Tables gathered in the research (camos, parts types, presets), shipped with the tool.</summary>
/// <summary>Who wears a vanilla body package, and which look it is (issue #55). Reverses <see cref="PartsTypeInfo.Packages"/>.</summary>
public sealed record BodyRole(PlayerBranch Who, string Kind, PartsTypeInfo? Type);

public static class GameKnowledge
{
    private static readonly Lazy<IReadOnlyList<CamoInfo>> _camos = new(() => Load<List<CamoInfo>>("camos"));
    private static readonly Lazy<IReadOnlyList<PartsTypeInfo>> _parts = new(() => Load<List<PartsTypeInfo>>("parts_types"));
    private static readonly Lazy<IReadOnlyList<BehaviourPreset>> _presets = new(LoadPresets);

    private static readonly Lazy<IReadOnlyDictionary<string, ChimeraPackage>> _chimera =
        new(() => Load<Dictionary<string, ChimeraPackage>>("chimera_weapons"));
    /// <summary>Chimera receiver packages (…/chimera/receiver/*.fpk) → the vanilla weapons built on them.</summary>
    public static IReadOnlyDictionary<string, ChimeraPackage> ChimeraReceivers => _chimera.Value;

    private sealed record ChimeraPartsTable([property: JsonPropertyName("parts")] Dictionary<string, ChimeraPartPackage> Parts);
    private static readonly Lazy<ChimeraPartsTable> _chimeraParts = new(() => Load<ChimeraPartsTable>("chimera_parts"));
    /// <summary>
    /// The game's chimera part packages by part constant (RC_/BA_/ST_/LT_/LS_/MO_/SK_/UB_/AM_… without the table), from
    /// ChimeraPartsPackageTable.lua: the part's .parts, its package, and its default paint (.fv2 and package; empty for
    /// parts without paint). Parts with no package of their own (drawn by the receiver's model) are not listed.
    /// </summary>
    public static IReadOnlyDictionary<string, ChimeraPartPackage> ChimeraParts => _chimeraParts.Value.Parts;

    private sealed record DevelopTable([property: JsonPropertyName("rows")] Dictionary<string, VanillaDevelopRow> Rows);
    private static readonly Lazy<DevelopTable> _develop = new(() => Load<DevelopTable>("develop_rows"));
    /// <summary>A vanilla R&amp;D row by develop ID (EquipDevelopConst/FlowSetting.lua), or null.</summary>
    public static VanillaDevelopRow? VanillaDevelop(int developId) => _develop.Value.Rows.TryGetValue(developId.ToString(), out var r) ? r : null;
    /// <summary>Every vanilla R&amp;D row by develop ID, for the "copy from a vanilla item" picker.</summary>
    public static IReadOnlyDictionary<string, VanillaDevelopRow> VanillaDevelopRows => _develop.Value.Rows;

    /// <summary>
    /// The English name the game shows for a lang ID ("name_st_19001" → "Olive Drab"), read from the game's own
    /// language package. Null when the game isn't open or has no entry. Cached per game folder.
    /// </summary>
    public static string? EnglishName(GameData? game, string? langId)
    {
        if (game == null || string.IsNullOrEmpty(langId)) return null;
        var lang = _englishLang ??= ReadEnglishLang(game);
        return lang?[langId];
    }

    private static LangFile? _englishLang;
    private static bool _englishTried;
    /// <summary>The game whose text was read for <see cref="EnglishName"/> (set by the app when it opens the game).</summary>
    public static GameData? EnglishNamesGame { get; set; }
    private static readonly object EnglishLock = new();
    // The names are read on pool threads too (the icon search): a second thread waits for the first read, or it gets null.
    private static LangFile? ReadEnglishLang(GameData game) { lock (EnglishLock) return ReadEnglishLangLocked(game); }
    private static LangFile? ReadEnglishLangLocked(GameData game)
    {
        if (_englishTried) return _englishLang;
        _englishTried = true;
        try
        {
            if (game.TryRead("/Assets/tpp/pack/ui/lang/lang_default_data_eng.fpk") is not { } fpk) return null;
            var pkg = FoxPackage.Read(fpk);
            // The equipment names live in tpp_equip; weapons in tpp_weapon. Merge both so either kind resolves.
            var merged = new LangFile();
            foreach (var name in new[] { "tpp_equip", "tpp_weapon", "tpp_item" })
            {
                if (pkg.Find($"/Assets/tpp/lang/ui/{name}.eng.lng2") is not { } f) continue;
                foreach (var e in LangFile.Read(f.Data).Entries) merged.Entries.Add(e);
            }
            _englishLang = merged.Entries.Count > 0 ? merged : null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or KeyNotFoundException) { _englishLang = null; }
        return _englishLang;
    }

    /// <summary>
    /// The in-game name and category of each icon a vanilla item uses (issues #16, #24): extensionless icon path (any case) →
    /// "Name – Category", from the
    /// camos, suits, weapons and support weapons the tool knows. An icon used by more than one item gets the first name.
    /// An icon of no known item (a UI icon, a buddy costume) is not in the map. Without the game's text, a camo gets its
    /// description and the other items get no name.
    /// </summary>
    public static Dictionary<string, string> GameIconNames(GameData? game)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? icon, string? name, string category) { if (!string.IsNullOrEmpty(icon) && !string.IsNullOrEmpty(name)) names.TryAdd(ExtensionlessIcon(icon), $"{name} – {category}"); }
        foreach (var c in Camos) Add(c.Icon, EnglishName(game, c.LangName) ?? c.Description, "Camo");
        foreach (var p in PartsTypes) Add(p.Icon, p.DevelopId is { } d ? EnglishName(game, VanillaDevelop(d)?.Name) : null, "Outfit");
        foreach (var w in ChimeraReceivers.Values.SelectMany(r => r.Weapons)) Add(w.Icon, EnglishName(game, w.NameLang), "Weapon");
        foreach (var s in SupportWeapons.Values) Add(s.Develop?.Icon, EnglishName(game, s.Develop?.Name), "Support weapon");
        // Issue #24 follow-up: every other R&D row of the game (buddies, arms, heads, items, gadgets ...) names its icon too.
        if (game?.TryRead(DevelopConstPath) is { } lua)
            foreach (var row in LuaLiteral.CallArguments(System.Text.Encoding.UTF8.GetString(lua), "RegCstDev"))
                if (row["p08"] is LuaValue.Text icon && row["p06"] is LuaValue.Text name)
                    Add(icon.Value, EnglishName(game, name.Value), DevelopCategory(row["p02"] is LuaValue.Name t ? t.Value : ""));
        // Issue #53: an enemy weapon icon (ui_wp_<type>NN_m<K>_...) has no R&D row. It shows receiver <type>NN_main<K>.
        // The name is the receiver's own weapon name: the lowest name_wp_ number (a CS or SP variant has a higher one).
        foreach (var p in Archives.PathDictionary.Bundled.PathsUnder("/Assets/tpp/ui/texture/EquipIcon/"))
            if (WeaponIconReceiver.Match(p[(p.LastIndexOf('/') + 1)..]) is { Success: true } m
                && ReceiverWeaponName(game, m.Groups[1].Value, m.Groups[2].Value) is { } wn)
                names.TryAdd(p, $"{wn} – Weapon");
        // Icons that no game table names (issue #53).
        foreach (var (icon, name) in FixedIconNames) names.TryAdd(icon, name);
        return names;
    }

    private static readonly System.Text.RegularExpressions.Regex WeaponIconReceiver = new(@"^ui_wp_([a-z]{2}\d\d)_m(\d)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex BaseWeaponNameLang = new(@"^name_wp_(\d+)$");

    /// <summary>The English name of receiver <paramref name="type"/> main <paramref name="mark"/> ("ar01", "0"), or null.</summary>
    private static string? ReceiverWeaponName(GameData? game, string type, string mark) =>
        ChimeraReceivers.Where(r => Path.GetFileName(r.Key).StartsWith($"{type}_main{mark}_", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Value.Weapons)
            .Select(w => (w.NameLang, M: BaseWeaponNameLang.Match(w.NameLang ?? "")))
            .Where(x => x.M.Success)
            .OrderBy(x => int.Parse(x.M.Groups[1].Value))
            .Select(x => EnglishName(game, x.NameLang))
            .FirstOrDefault(n => n != null);

    /// <summary>Icons with no game table behind them: the D-Dog eye icons, an unused D-Dog icon and Quiet's Guilty Butterfly (names from the owner's testers, issue #53), and the 10 vehicle icons (issue #54: 4 from the testers; the rest from the icon picture and the vehicle text in tpp_common).</summary>
    private static readonly Dictionary<string, string> FixedIconNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/Assets/tpp/ui/texture/EquipIcon/buddy/ui_dd_ddg0_m5_alp"] = "Unused D-Dog Equipment – D-Dog",
        ["/Assets/tpp/ui/texture/EquipIcon/buddy/ui_dd_ddg_eye_1_alp"] = "Artificial Eye – D-Dog",
        ["/Assets/tpp/ui/texture/EquipIcon/buddy/ui_dd_ddg_eye_2_alp"] = "No Eyepatch – D-Dog",
        ["/Assets/tpp/ui/texture/EquipIcon/buddy/ui_dd_ddg_eye_3_alp"] = "Eyepatch – D-Dog",
        ["/Assets/tpp/ui/texture/EquipIcon/buddy/ui_qwp_sr02_01_20_alp"] = "GUILTY BUTTERFLY – Quiet",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_mbt00_alp"] = "TT77 NOSOROG – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_nbt00_alp"] = "M84A MAGLOADER – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_nmt00_alp"] = "BOAR-53CT – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_plv00_alp"] = "APE T-41LV – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_sav00_alp"] = "ZHUK BR-3 – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_sav01_alp"] = "ZHUK RS-ZO – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_slv00_alp"] = "ZaAZ-S84/4W – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_smt00_alp"] = "Zi-GRA 6T – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_wav00_alp"] = "STOUT IFV-FS – Vehicle",
        ["/Assets/tpp/ui/texture/EquipIcon/mecha/ui_eqp_vhcl_wav01_alp"] = "STOUT IFV-SC – Vehicle",
    };

    /// <summary>The game's R&amp;D table: one RegCstDev row per R&amp;D entry (p02 type, p06 name, p08 icon).</summary>
    public const string DevelopConstPath = "/Assets/tpp/motherbase/script/EquipDevelopConstSetting.lua";

    /// <summary>The category text of an R&amp;D type (TppMbDev.EQP_DEV_TYPE_Sniper → "Sniper rifle").</summary>
    public static string DevelopCategory(string type) => type[(type.LastIndexOf('_') + 1)..] switch
    {
        "ArtificialArm" => "Arm",
        "Assault" => "Assault rifle",
        "BattleGear" => "Battle Gear",
        "DDog" => "D-Dog",
        "Equip" => "Item",
        "GrenadeLauncher" => "Grenade launcher",
        "Heli" => "Helicopter",
        "Horse" => "D-Horse",
        "Machinegun" => "Machine gun",
        "Placed" => "Placed weapon",
        "SecurityGadgets" => "Security device",
        "Sniper" => "Sniper rifle",
        "Submachinegun" => "Submachine gun",
        "Suit" => "Outfit",
        "Throwing" => "Throwing weapon",
        "WalkerGear" => "D-Walker",
        "" => "Item",
        var other => other,
    };

    private static string ExtensionlessIcon(string p) => p.EndsWith(".ftex", StringComparison.OrdinalIgnoreCase) ? p[..^5] : p;

    /// <summary>Forgets the cached English names (a different game folder was chosen).</summary>
    public static void ForgetEnglishNames() { lock (EnglishLock) { _englishLang = null; _englishTried = false; } }
    /// <summary>Deployment (usage) GMP of the vanilla Bandana (37001): 3400.</summary>
    public static int BandanaUsageGmp => VanillaDevelop(37001)?.UsageGmp ?? 0;

    private sealed record SupportTable([property: JsonPropertyName("weapons")] Dictionary<string, VanillaSupportWeapon> Weapons);
    private static readonly Lazy<SupportTable> _support = new(() => Load<SupportTable>("support_weapons"));
    /// <summary>Vanilla support weapons by equip constant (EQP_SWP_*): equip row, stats, R&amp;D row.</summary>
    public static IReadOnlyDictionary<string, VanillaSupportWeapon> SupportWeapons => _support.Value.Weapons;

    private sealed record AttackTable([property: JsonPropertyName("attacks")] Dictionary<string, int> Attacks);
    private static readonly Lazy<AttackTable> _attacks = new(() => Load<AttackTable>("attack_ids"));
    /// <summary>The game's attack numbers by TppDamage.ATK_* name (issue #94; sources in attack_ids.json).</summary>
    public static IReadOnlyDictionary<string, int> AttackIds => _attacks.Value.Attacks;
    /// <summary>The last attack number V Framework reads for an outfit's dashAttack (OutfitRegistry.h kSprintAttackLast).</summary>
    public const int DashAttackLast = 0x1BC;
    /// <summary>The attack number limit V Framework reads for a horse bodyAttackId (BuddyOutfitRegistry.h kAttackIdLimit).</summary>
    public const int AttackIdLimit = 0x400;

    public static IReadOnlyList<CamoInfo> Camos => _camos.Value;
    public static IReadOnlyList<PartsTypeInfo> PartsTypes => _parts.Value;
    public static IReadOnlyList<BehaviourPreset> Presets => _presets.Value;

    private sealed record CamoValueTable(
        [property: JsonPropertyName("materials")] List<string> Materials,
        [property: JsonPropertyName("camos")] Dictionary<string, Dictionary<string, int>> Camos);
    private static readonly Lazy<CamoValueTable> _camoValues = new(() => Load<CamoValueTable>("camo_values"));

    /// <summary>The 82 ground materials concealment is rated on (MTR_*, game order).</summary>
    public static IReadOnlyList<string> CamoMaterials => _camoValues.Value.Materials;

    /// <summary>
    /// The game's concealment values for a vanilla camo (player2_camouf_param.lua camoTable): material → bonus,
    /// non-zero entries only (vanilla uses 50). Null when the camo is unknown.
    /// </summary>
    public static IReadOnlyDictionary<string, int>? CamoValues(string camoName) =>
        _camoValues.Value.Camos.TryGetValue(camoName.ToUpperInvariant(), out var v) ? v : null;

    /// <summary>
    /// The camoBonusValues key that V Framework 1.7 needs so that a value reaches <paramref name="material"/>. Issue #89:
    /// the game keeps 83 slots per camo and its InitCamoufTable (exe 0x140B10D70) puts Lua column n (1-based) in slot n,
    /// so slot = 0-based column + 1. V Framework reads row[slot] with names at the 0-based column, thus one name later.
    /// Null for the last material: its slot (82) is past the V Framework row.
    /// </summary>
    public static string? CamoValueKey(string material) =>
        _camoValues.Value.Materials.IndexOf(material) is var i and >= 0 && i + 1 < CamoMaterials.Count ? CamoMaterials[i + 1] : null;

    /// <summary>The material that a camoBonusValues key reaches in game (inverse of <see cref="CamoValueKey"/>). Null for
    /// the first key (slot 0, which the game never uses) and an unknown key.</summary>
    public static string? CamoValueMaterial(string key) =>
        _camoValues.Value.Materials.IndexOf(key) is var i and >= 1 ? CamoMaterials[i - 1] : null;

    // Issue #89: each Environment is the non-zero row of one vanilla camo; the 6 rows do not overlap.
    private static readonly (string Title, string Camo, string Note, string Words)[] EnvDefs =
    [
        ("Greenery", "OLIVEDRAB", "Leaves, wood, plants, moss and turf.", @"greenery|jungles?|forests?|foliage|woodlands?"),
        ("Rocky", "TIGERSTRIPE", "Rock and cliff faces, and stony soil.", @"rocky|rocks?|cliffs?"),
        ("Sand & soil", "FOXTROT", "Sand, gravel and dry soil.", @"sand|sandy|deserts?|gravel"),
        ("Red soil", "GOLDTIGER", "The red soil of Africa.", @"red soil"),
        ("Metal & concrete", "SQUARE", "Metal, pipes, fences, concrete and tiles: the urban and FOB grounds.", @"metal|concrete|urban|steel"),
        ("River & swamp", "WETWORK", "Wet soil, mud and shallow water.", @"rivers?|swamps?|marsh(es)?|wetlands?"),
    ];
    private static readonly Lazy<IReadOnlyList<CamoEnvironment>> _environments = new(() => EnvDefs.Select(d =>
    {
        var surfaces = CamoMaterials.Where(CamoValues(d.Camo)!.ContainsKey).ToList();
        var fits = Camos.Where(c => CamoValues(c.Name) is { } v && surfaces.All(v.ContainsKey) && c.Description.Length > 0)
            .Select(c => c.Description.Trim()).Distinct().ToList();
        return new CamoEnvironment(d.Title, d.Note, surfaces, fits, new Regex($@"\b({d.Words})\b", RegexOptions.IgnoreCase));
    }).ToList());

    /// <summary>The Environments of the camo tab (issue #89), from the game's camo table.</summary>
    public static IReadOnlyList<CamoEnvironment> CamoEnvironments => _environments.Value;

    // The 7 camo words of issue #89, each a whole word with its endings: "spotlight" and "hideous" are no camo words.
    private static readonly Regex[] CamoWords = [.. new[] { "blend(s|ed|ing)?", "camo", "camouflage[sd]?", "spot(s|ted|ting)?", "detect(s|ed|ing|ion|able)?", "conceal(s|ed|ing|ment)?", "hid(e|es|den|ing)" }
        .Select(w => new Regex($@"\b{w}\b", RegexOptions.IgnoreCase))];

    /// <summary>
    /// The Environments a description names (issue #89): a keyword of the Environment and at least two of the camo words
    /// (blend, camo, camouflage, spot, detection, conceal, hide), so one word alone is no match. "Red soil" wins over sand.
    /// </summary>
    public static IReadOnlyList<CamoEnvironment> EnvironmentsNamedIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        int camoWords = CamoWords.Count(w => w.IsMatch(text));
        if (camoWords < 2) return [];
        var named = CamoEnvironments.Where(e => e.Keywords.IsMatch(text)).ToList();
        if (named.Any(e => e.Title == "Red soil")) named.RemoveAll(e => e.Title == "Sand & soil");
        return named;
    }

    /// <summary>
    /// One row of a <c>player2_camouf_param.lua</c> (Player.InitCamoufTable, one row per playerCamoType, columns in
    /// <see cref="CamoMaterials"/> order): material → value, non-zero only. Null when the file has no such row.
    /// </summary>
    public static Dictionary<string, int>? CamoRowFromTable(string lua, int playerCamoType)
    {
        lua = Formats.LuaLiteral.BlankComments(lua);
        int at = lua.IndexOf("InitCamoufTable", StringComparison.Ordinal);
        if (at < 0) return null;
        var rows = Regex.Matches(lua[at..], @"\{([-+\d.,\s]*)\}").Select(m => m.Groups[1].Value).ToList();
        if (playerCamoType < 0 || playerCamoType >= rows.Count) return null;
        var cells = rows[playerCamoType].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var d = new Dictionary<string, int>();
        for (int i = 0; i < cells.Length && i < CamoMaterials.Count; i++)
            if (double.TryParse(cells[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) && (int)Math.Round(x) is var v and not 0) d[CamoMaterials[i]] = v;
        return d;
    }

    /// <summary>
    /// The camo values of a vanilla suit (body replacers, issue #89): the row that every camo of that parts type shares,
    /// or null when they differ (the fatigues) or none is known.
    /// </summary>
    public static IReadOnlyDictionary<string, int>? CamoValuesOfParts(string partsType)
    {
        var rows = Camos.Where(c => c.PlayerParts.Contains(partsType) || c.PlayerParts.Count == 0 && c.Name == partsType)
            .Select(c => CamoValues(c.Name)).Where(v => v != null).ToList();
        return rows.Count > 0 && rows.All(r => r!.Count == rows[0]!.Count && r.All(kv => rows[0]!.GetValueOrDefault(kv.Key) == kv.Value)) ? rows[0] : null;
    }

    /// <summary>
    /// Auto-fill (issue #89): gives 50 on every surface of each Environment the description names. It only adds: a
    /// surface that already has a value keeps it. Returns the Environments it ticked.
    /// </summary>
    public static List<string> FillCamoFromDescription(Dictionary<string, int> values, string? description)
    {
        var ticked = new List<string>();
        foreach (var e in EnvironmentsNamedIn(description))
        {
            if (e.Surfaces.All(s => values.GetValueOrDefault(s) > 0)) continue;
            foreach (var s in e.Surfaces) if (values.GetValueOrDefault(s) <= 0) values[s] = 50;
            ticked.Add(e.Title);
        }
        return ticked;
    }

    /// <summary>
    /// The camo preset that a set of concealment values still is (issue #22): <paramref name="preset"/> when the values
    /// are exactly its table, else null (Custom). Many vanilla camos share one table, so only the chosen preset can match.
    /// </summary>
    public static string? CamoPresetFor(string? preset, IReadOnlyDictionary<string, int> values) =>
        preset != null && CamoValues(preset) is { } t && t.Count == values.Count(kv => kv.Value != 0)
            && t.All(kv => values.GetValueOrDefault(kv.Key) == kv.Value) ? preset : null;

    /// <summary>
    /// The vanilla suit icons a look can point its <c>iconFtexPath</c> at, as (file name, extensionless path). Read
    /// from the game's own EquipIcon/suit folder: ui_st_sna0_c00..c60 are the fatigues, sna2..sna7 the named suits
    /// (SV-SNEAKING SUIT, BATTLE DRESS, ...), nin0 the Ninja, rai0 Raiden, dla..dle the DLC suits, plus the bandanas.
    /// The path is what V Framework wants: no .ftex suffix.
    /// </summary>
    public static IReadOnlyList<(string Name, string Path)> VanillaSuitIcons { get; } =
    [
        .. Enumerable.Range(0, 61).Select(i => $"c{i:00}").Where(c => c != "c15" && c != "c21" && c != "c34")
            .Select(c => ($"ui_st_sna0_{c}_alp", $"/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna0_{c}_alp")),
        ("ui_st_sna2_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna2_alp"),
        ("ui_st_sna3_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna3_alp"),
        ("ui_st_sna4_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna4_alp"),
        ("ui_st_sna5_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna5_alp"),
        ("ui_st_sna6_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna6_alp"),
        ("ui_st_sna7_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna7_alp"),
        ("ui_st_sna0_gld_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna0_gld_alp"),
        ("ui_st_sna0_sil_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna0_sil_alp"),
        ("ui_st_nin0_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_nin0_alp"),
        ("ui_st_rai0_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_rai0_alp"),
        ("ui_st_dla_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_dla_alp"),
        ("ui_st_dlb_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_dlb_alp"),
        ("ui_st_dlc_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_dlc_alp"),
        ("ui_st_dld_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_dld_alp"),
        ("ui_st_dle_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_dle_alp"),
        ("ui_st_bandana_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_bandana_alp"),
        ("ui_st_infinity_bandana_alp", "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_infinity_bandana_alp"),
    ];

    public static CamoInfo? CamoByName(string name) => Camos.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the vanilla suit (camo name or number) has more than one grade in R&amp;D (Sneaking Suit, Battle Dress,
    /// Parasite Suit...): its Sortie Prep row shows grade stars, not a camo name (#90).
    /// </summary>
    public static bool HasGrades(string outfit)
    {
        var c = int.TryParse(outfit, out int n) ? Camos.FirstOrDefault(x => x.PlayerCamoType == n) : CamoByName(outfit);
        if (c?.DevelopId is not { } id || VanillaDevelop(id)?.Name is not { } name) return false;
        return VanillaDevelopRows.Values.Where(r => r.Name == name).Select(r => r.Grade).Distinct().Count() > 1;
    }

    /// <summary>
    /// True when every wearer can wear the vanilla suit. A fatigues camo: Snake and Avatar need its Snake files, DD
    /// soldiers its DD files. A special suit (SNEAKING_SUIT_TPP, BATTLEDRESS, PARASITE and the like): it is in the Sortie
    /// Prep list (it has an R&amp;D row), and each wearer has a body package of its parts type. V Framework 1.7 takes each
    /// vanilla camo name in ExtendVanillaOutfit and asks the game for the parts type (OutfitLuaBindings.cpp,
    /// l_ExtendVanillaOutfit); it has no rule against a special suit.
    /// </summary>
    public static bool AllCanWear(CamoInfo c, IEnumerable<PlayerBranch> wearers)
    {
        if (c.FovaCamoId != null || c.Snake != null || c.Dd != null)
            return wearers.All(k => k switch
            {
                PlayerBranch.Snake or PlayerBranch.Avatar => c.Snake != null,
                PlayerBranch.DdMale or PlayerBranch.DdFemale => c.Dd != null,
                _ => false, // Ocelot and Quiet wear no fatigues
            });
        // Ocelot and Quiet have one suit of their own in Sortie Prep, with no R&D row (V Framework 1.7,
        // UniqueCharacterPartsTypePin.cpp: parts types 26 and 27). Each other suit of the list has an R&D row.
        if (c.DevelopId == null && c.Name is not ("OCELOT" or "QUIET")) return false;
        // The parts type with the name of the suit, else the parts types that the camo table names (SOLIDSNAKE: MGS1).
        var parts = PartsTypes.Where(p => p.Name == c.Name).ToList();
        if (parts.Count == 0) parts = PartsTypes.Where(p => c.PlayerParts.Contains(p.Name)).ToList();
        bool Has(string key) => parts.Any(p => p.Packages.ContainsKey(key));
        return parts.Count > 0 && wearers.All(k => k switch
        {
            PlayerBranch.Snake => Has("SNAKE"),
            PlayerBranch.Avatar => Has("AVATAR") || Has("SNAKE"), // the Avatar follows Snake (V Framework)
            PlayerBranch.DdMale => Has("DD_MALE"),
            PlayerBranch.DdFemale => Has("DD_FEMALE"),
            PlayerBranch.Ocelot => Has("OCELOT"),
            PlayerBranch.Quiet => Has("QUIET"),
            _ => false,
        });
    }

    /// <summary>
    /// "Add all fatigues" (issue #14): the list with each fatigues outfit (a camo with a fatigues camo number) that every
    /// wearer can wear added at the end, with no slots. An outfit already in the list or in <paramref name="skip"/> is not added again.
    /// </summary>
    public static List<AlsoUnder> WithAllFatigues(IEnumerable<AlsoUnder> list, IEnumerable<PlayerBranch> wearers, IEnumerable<string> skip)
    {
        var l = list.ToList();
        var have = new HashSet<string>(l.Select(a => a.Outfit).Concat(skip), StringComparer.OrdinalIgnoreCase);
        l.AddRange(Camos.Where(c => c.FovaCamoId != null && AllCanWear(c, wearers) && have.Add(c.Name)).Select(c => new AlsoUnder(c.Name, [])));
        return l;
    }
    public static BehaviourPreset Preset(string id) => Presets.First(p => p.Id == id);
    public static PartsTypeInfo PartsType(string name) => PartsTypes.First(p => p.Name == name);

    // ---- Body roles (issue #55): the one table behind "which character wears this vanilla body package" ----
    private static readonly (string Key, PlayerBranch Who)[] BranchKeys =
        [("SNAKE", PlayerBranch.Snake), ("AVATAR", PlayerBranch.Avatar), ("DD_MALE", PlayerBranch.DdMale),
         ("DD_FEMALE", PlayerBranch.DdFemale), ("OCELOT", PlayerBranch.Ocelot), ("QUIET", PlayerBranch.Quiet)];

    // The table lists Snake's Venom suit as pl_venom; mods and the game files use plparts_venom.
    private static string RoleKey(string package)
    {
        var p = package.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase) ? package[..^4] : package;
        return p.EndsWith("/plparts_venom", StringComparison.OrdinalIgnoreCase) ? p[..^"plparts_venom".Length] + "pl_venom" : p;
    }

    private static readonly Lazy<Dictionary<string, BodyRole>> _roles = new(() =>
    {
        var d = new Dictionary<string, BodyRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in PartsTypes)
            foreach (var (key, who) in BranchKeys)   // Snake before Avatar: a shared package gives Snake
                if (t.Packages.TryGetValue(key, out var file) && file.EndsWith(".fpk", StringComparison.Ordinal))
                    d.TryAdd(file[..^4], new BodyRole(who, BodyKind(file[..^4]), t));
        return d;
    });

    /// <summary>The look kind of a body package: its file name without "plparts_"/"pl_" and the DD "ddm_"/"ddf_" prefix (battledress, parasite, normal, venom ...). plparts_dd_male/dd_female are the DD "normal".</summary>
    private static string BodyKind(string package)
    {
        var n = package[(package.LastIndexOf('/') + 1)..].ToLowerInvariant();
        foreach (var pre in new[] { "plparts_", "pl_" }) if (n.StartsWith(pre, StringComparison.Ordinal)) { n = n[pre.Length..]; break; }
        if (n is "dd_male" or "dd_female") return "normal";
        return n.StartsWith("ddm_", StringComparison.Ordinal) || n.StartsWith("ddf_", StringComparison.Ordinal) ? n[4..] : n;
    }

    /// <summary>Character, look kind and parts type of a vanilla body package (with or without ".fpk"), or null when no parts type lists it.</summary>
    public static BodyRole? BodyRoleOf(string vanillaPackage) =>
        string.IsNullOrEmpty(vanillaPackage) ? null : _roles.Value.GetValueOrDefault(RoleKey(vanillaPackage));

    /// <summary>The DD branch of a package (the ddm_/ddf_ rule of Intake), or null for a body that is no DD body.</summary>
    public static PlayerBranch? DdBranchOf(string vanillaPackage) =>
        BodyRoleOf(vanillaPackage)?.Who is PlayerBranch.DdMale or PlayerBranch.DdFemale ? BodyRoleOf(vanillaPackage)!.Who
        : vanillaPackage.Contains("plparts_ddm_") ? PlayerBranch.DdMale
        : vanillaPackage.Contains("plparts_ddf_") ? PlayerBranch.DdFemale : null;

    /// <summary>True when only DD women wear the package (the package part of BodyConverter.FemaleOnly).</summary>
    public static bool IsFemaleOnlyPackage(string vanillaPackage) =>
        BodyRoleOf(vanillaPackage)?.Who == PlayerBranch.DdFemale
        || System.Text.RegularExpressions.Regex.IsMatch(vanillaPackage, @"/plparts_(ddf_|dd_female|dl[a-z]\d_plyf)");

    private static T Load<T>(string name)
    {
        using var s = typeof(GameKnowledge).Assembly.GetManifestResourceStream($"Svfm.Knowledge.{name}.json")
            ?? throw new InvalidOperationException($"knowledge table {name} missing from the build");
        return JsonSerializer.Deserialize<T>(s) ?? throw new InvalidDataException(name);
    }

    private static IReadOnlyList<BehaviourPreset> LoadPresets()
    {
        using var s = typeof(GameKnowledge).Assembly.GetManifestResourceStream("Svfm.Knowledge.presets.json")!;
        using var doc = JsonDocument.Parse(s);
        var list = new List<BehaviourPreset>();
        foreach (var p in doc.RootElement.GetProperty("presets").EnumerateArray())
        {
            var a = p.GetProperty("abilities");
            bool B(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            string? S(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            list.Add(new BehaviourPreset(
                p.GetProperty("id").GetString()!,
                p.GetProperty("label").GetString()!,
                Abilities.Read(B, n => a.TryGetProperty(n, out var v) ? v.GetDouble() : null, S),
                p.TryGetProperty("note", out var note) ? note.GetString() : null));
        }
        return list;
    }
}
