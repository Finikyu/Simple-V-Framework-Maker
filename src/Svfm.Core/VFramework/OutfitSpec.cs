namespace Svfm.Core.VFramework;

/// <summary>V Framework player-type branches.</summary>
public enum PlayerBranch { Snake, Avatar, DdMale, DdFemale, Ocelot, Quiet }

public static class PlayerBranches
{
    /// <summary>The name of a branch as the head screens and Compare show it.</summary>
    public static string Label(PlayerBranch who) => who switch { PlayerBranch.Snake => "Snake", PlayerBranch.Avatar => "Avatar", PlayerBranch.DdMale => "DD soldier (male)", PlayerBranch.DdFemale => "DD soldier (female)", PlayerBranch.Ocelot => "Ocelot", _ => "Quiet" };

    /// <summary>True when Snake or Avatar is a wearer: only they have the prosthetic (bionic) arm that enableArm sets.</summary>
    public static bool HasBionicArm(IEnumerable<PlayerBranch> wearers) => wearers.Any(w => w is PlayerBranch.Snake or PlayerBranch.Avatar);

    /// <summary>Why the arm choice is greyed out when no wearer has the prosthetic arm (issue #12).</summary>
    public const string NoBionicArmReason = "Only Snake and Avatar have the prosthetic arm";

    /// <summary>True for the only ticked wearer: its tick box is locked, so an outfit or a head option keeps a wearer (issue #15).</summary>
    public static bool IsLastWearer(bool wears, int wearerCount) => wears && wearerCount < 2;
    public const string LastOutfitWearerReason = "An outfit needs at least one wearer";
    public const string LastHeadWearerReason = "A head option needs at least one wearer";

    /// <summary>
    /// Avatar's branch from Snake's. Snake and Avatar wear the same outfit files (no vanilla parts type has a separate
    /// Avatar package), so only the lists are copied. The heads in <paramref name="dropHeads"/> (heads built on Snake's own
    /// face) are left out.
    /// </summary>
    public static OutfitBranch AvatarFrom(OutfitBranch snake, IReadOnlySet<string>? dropHeads = null)
    {
        List<string> Heads(List<string> h) => h.Where(k => dropHeads?.Contains(k) != true).ToList();
        return snake with
        {
            HeadOptions = Heads(snake.HeadOptions),
            Base = snake.Base with { HeadOptions = Heads(snake.Base.HeadOptions) },
            Variants = snake.Variants.Select(v => v with { HeadOptions = Heads(v.HeadOptions) }).ToList(),
            PrimaryColors = [.. snake.PrimaryColors],
            SecondaryColors = [.. snake.SecondaryColors],
        };
    }
}

/// <summary>
/// One key of a V Framework <c>abilities</c> table. <paramref name="Label"/> and <paramref name="Type"/> are for the
/// settings of an existing mod. <paramref name="Get"/> gives the property of <see cref="Abilities"/> as it is (bool,
/// float?, int or string). <paramref name="Set"/> takes the value as a reader gives it: bool, double? or string?.
/// </summary>
public sealed record AbilityKey(string Name, string Label, Project.FieldType Type, Func<Abilities, object?> Get, Func<Abilities, object?, Abilities> Set)
{
    /// <summary>The value as a field of an existing mod holds it: a number is a double. Through decimal, so 1.3f is 1.3 and not 1.2999999523.</summary>
    public object? Value(Abilities a) => Get(a) switch { float f => (double)(decimal)f, int i => (double)i, var v => v };
}

/// <summary>One key of a look that holds one value: text, or true/false when <paramref name="IsBool"/>. <paramref name="Set"/> takes string? or bool?.</summary>
public sealed record LookKey(string Name, bool IsBool, Func<Look, object?> Get, Func<Look, object?, Look> Set);

/// <summary>
/// Outfit abilities (V Framework 1.7 <c>abilities</c> table). Null/false fields are left out of
/// the Lua so V Framework uses its defaults.
/// </summary>
public sealed record Abilities
{
    public bool SilentFootsteps { get; init; }
    public bool QuietMovement { get; init; }
    /// <summary>0.1–10. Cyborg Ninja 1.3, Raiden 1.5.</summary>
    public float? SprintSpeed { get; init; }
    public bool RaidenRun { get; init; }
    public bool DashAttack { get; init; }
    /// <summary>The TppDamage.ATK_ name the dash attack uses (number 1 to <see cref="Knowledge.GameKnowledge.DashAttackLast"/>); null: the default attack. Set only with <see cref="DashAttack"/>.</summary>
    public string? DashAttackId { get; init; }
    public bool SprintSound { get; init; }
    /// <summary>.vfx path, packed with the outfit.</summary>
    public string? SprintSpark { get; init; }
    /// <summary>0–9; level N copies BATTLE DRESS grade N's damage rate. 7–9 untested.</summary>
    public int Defense { get; init; }
    /// <summary>0–9; level N copies SNEAKING SUIT grade N's health regeneration. 7–9 untested.</summary>
    public int LifeRecovery { get; init; }
    /// <summary>Cloth sound: nom, bony, snk, amr.</summary>
    public string? RattleSuit { get; init; }
    /// <summary>Hit sound: battledress or default.</summary>
    public string? DamageSe { get; init; }

    public bool IsEmpty => this == new Abilities();

    /// <summary>
    /// The abilities that a source gives for the keys of a V Framework <c>abilities</c> table. Each reader (a script of an
    /// existing mod, a look's table, the presets file) gives the three functions for its own form of a value.
    /// A sprint speed of 0 or none is no own sprint speed.
    /// </summary>
    /// <param name="flag">True when the key is on.</param>
    /// <param name="number">The number of the key; null when the source has none.</param>
    /// <param name="text">The text of the key; null when the source has none.</param>
    public static Abilities Read(Func<string, bool> flag, Func<string, double?> number, Func<string, string?> text) =>
        // dashAttack can give text: the attack name of a dash attack. Other text, and the other switches, read through the flag.
        Keys.Aggregate(new Abilities(), (a, k) => k.Set(a, k.Type switch
        {
            Project.FieldType.Bool => k.Name == "dashAttack" && text(k.Name) is { } id && IsDashAttackId(id) ? id : flag(k.Name),
            Project.FieldType.Number => number(k.Name),
            _ => text(k.Name),
        }));

    /// <summary>
    /// The attack name of a dashAttack value that V Framework reads as an attack ID: <c>TppDamage.ATK_x</c> or its number,
    /// 1 to <see cref="Knowledge.GameKnowledge.DashAttackLast"/> (OutfitLuaBindings.cpp). Null for any other value. The
    /// number 1 is an attack ID too (ATK_PushSlideInterp), as V Framework reads it; only true and false are on and off.
    /// </summary>
    public static string? DashAttackOf(Formats.LuaValue? v)
    {
        var ids = Knowledge.GameKnowledge.AttackIds;
        string? name = v switch
        {
            Formats.LuaValue.Name { Value: var n } when n.StartsWith("TppDamage.", StringComparison.Ordinal) => n["TppDamage.".Length..],
            Formats.LuaValue.Number { Value: var d } when d >= 1 && d == Math.Floor(d) => ids.FirstOrDefault(kv => kv.Value == d).Key,
            _ => null,
        };
        return name != null && IsDashAttackId(name) ? name : null;
    }

    /// <summary>True for a TppDamage.ATK_ name with a number up to <see cref="Knowledge.GameKnowledge.DashAttackLast"/>: a name that a dash attack can use.</summary>
    public static bool IsDashAttackId(string name) =>
        Knowledge.GameKnowledge.AttackIds.TryGetValue(name, out int id) && id >= 1 && id <= Knowledge.GameKnowledge.DashAttackLast;

    private static AbilityKey Flag(string name, string label, Func<Abilities, bool> get, Func<Abilities, bool, Abilities> set) =>
        new(name, label, Project.FieldType.Bool, a => get(a), (a, v) => set(a, v is true));
    private static AbilityKey Level(string name, string label, Func<Abilities, int> get, Func<Abilities, int, Abilities> set) =>
        new(name, label, Project.FieldType.Number, a => get(a), (a, v) => set(a, (int)Math.Round(v as double? ?? 0)));
    private static AbilityKey Text(string name, string label, Func<Abilities, string?> get, Func<Abilities, string?, Abilities> set) =>
        new(name, label, Project.FieldType.Text, get, (a, v) => set(a, v as string));

    /// <summary>
    /// The keys of a V Framework <c>abilities</c> table, in the order <see cref="LuaWriter"/> writes them. The writer, the
    /// readers (<see cref="Read"/>) and the write-back of an existing mod all use this list, so a new ability is added
    /// here only (and in <see cref="Ticked"/>). A field, not a property: the tests that list the properties do not see it.
    /// </summary>
    public static readonly IReadOnlyList<AbilityKey> Keys =
    [
        Flag("silentFootsteps", "Silent footsteps", a => a.SilentFootsteps, (a, v) => a with { SilentFootsteps = v }),
        Flag("quietMovement", "Quiet's moves", a => a.QuietMovement, (a, v) => a with { QuietMovement = v }),
        new("sprintSpeed", "Sprint speed (0 = normal)", Project.FieldType.Number, a => a.SprintSpeed, (a, v) => a with { SprintSpeed = v is double s && s != 0 ? (float)s : null }),
        Flag("raidenRun", "Raiden's run", a => a.RaidenRun, (a, v) => a with { RaidenRun = v }),
        // Three states: off (false), on (true) or a named attack (its ATK_ name as text).
        new("dashAttack", "Dash attack", Project.FieldType.Bool, a => a.DashAttackId ?? (object)a.DashAttack,
            (a, v) => v is string id ? a with { DashAttack = true, DashAttackId = id } : a with { DashAttack = v is true, DashAttackId = null }),
        Flag("sprintSound", "Sprint sound", a => a.SprintSound, (a, v) => a with { SprintSound = v }),
        Text("sprintSpark", "Sprint sparks (.vfx)", a => a.SprintSpark, (a, v) => a with { SprintSpark = v }),
        Level("defense", "Defense (0-9)", a => a.Defense, (a, v) => a with { Defense = v }),
        Level("lifeRecovery", "Life recovery (0-9)", a => a.LifeRecovery, (a, v) => a with { LifeRecovery = v }),
        Text("rattleSuit", "Cloth sound", a => a.RattleSuit, (a, v) => a with { RattleSuit = v }),
        Text("damageSe", "Hit sound", a => a.DamageSe, (a, v) => a with { DamageSe = v }),
    ];

    /// <summary>
    /// The settings that are on, in the words of the Abilities tab. The preset line is made from this, so it cannot
    /// differ from the preset. A new field must be added here (the test AbilityTicks_CoverEveryField fails if not).
    /// </summary>
    public IReadOnlyList<string> Ticked()
    {
        var f = System.Globalization.CultureInfo.InvariantCulture;
        var l = new List<string>();
        if (SilentFootsteps) l.Add("Silent footsteps");
        if (QuietMovement) l.Add("Quiet movement");
        if (SprintSpeed != null) l.Add("Own sprint speed " + SprintSpeed.Value.ToString("0.0#", f));
        if (RaidenRun) l.Add("Raiden run");
        if (SprintSound) l.Add("Sprint sound");
        if (DashAttack) l.Add(DashAttackId is { } id ? "Dash attack " + id : "Dash attack");
        if (SprintSpark != null) l.Add("Sprint sparks");
        if (Defense != 0) l.Add("Defense " + Defense);
        if (LifeRecovery != 0) l.Add("Life recovery " + LifeRecovery);
        if (RattleSuit != null) l.Add("Cloth sound " + RattleSuit);
        if (DamageSe != null) l.Add("Hit sound " + DamageSe);
        return l;
    }
}

/// <summary>One look on the cycle button (a V Framework variant), or the branch's base look.</summary>
public sealed record Look
{
    public required string PartsPath { get; init; }
    public required string FpkPath { get; init; }
    public string? CamoFpk { get; init; }
    public string? CamoFv2 { get; init; }
    /// <summary>Lang ID of the label on the cycle button.</summary>
    public string? DisplayName { get; init; }
    /// <summary>
    /// The icon this look shows in the suit list and on the sortie slot: an .ftex path without its extension, e.g.
    /// "/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_sna4_alp". Null means "inherit": the base look falls back to the
    /// outfit's own icon (the writer's ICON local) and a variant falls back to its branch's icon, which is what
    /// V Framework does when the key is absent.
    /// </summary>
    /// <remarks>
    /// This is the only per-look icon key V Framework has. There is no per-look "logo" key: the V_CallMenuIcon
    /// constants (KNOCK, INTERROGATION, BUDDY, SONAR) belong to V_Player.AddCallMenuItem's `icon` field and are not
    /// accepted here, so a constant written into this field would be loaded as a file path and fail.
    /// </remarks>
    public string? IconFtexPath { get; set; }
    public bool? EnableArm { get; init; }
    public bool? EnableHead { get; init; }
    public bool Default { get; init; }
    /// <summary>
    /// Variant only: this look's heads. V Framework 1.7 does not fall back to the branch's list: a variant
    /// without its own headOptions has no HEAD row at all (OutfitEntry::GetHeadOptionsForVariant), so every
    /// variant repeats the list. Ignored on looks added to a vanilla suit (ReadVanillaExtVariants doesn't read it).
    /// </summary>
    public List<string> HeadOptions { get; init; } = [];
    /// <summary>
    /// Variant only: this look's own abilities; null = the branch's ("Same as the outfit"). V Framework 1.6 and 1.7 read
    /// a variant's abilities table and use it in place of the branch's, whole (OutfitAbilities.cpp 586-595), so this is
    /// the full set. A vanilla suit's own look (index 0) has none.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Abilities? Abilities { get; init; }

    /// <summary>
    /// The keys of a look that hold one value, in the order <see cref="LuaWriter"/> writes them. The writer, the reader
    /// and the write-back of an existing mod use this list. The tables (headOptions, abilities) and <c>default</c> are not here.
    /// </summary>
    public static readonly IReadOnlyList<LookKey> Keys =
    [
        new("partsPath", false, l => l.PartsPath, (l, v) => l with { PartsPath = v as string ?? "" }),
        new("fpkPath", false, l => l.FpkPath, (l, v) => l with { FpkPath = v as string ?? "" }),
        new("camoFpk", false, l => l.CamoFpk, (l, v) => l with { CamoFpk = v as string }),
        new("camoFv2", false, l => l.CamoFv2, (l, v) => l with { CamoFv2 = v as string }),
        new("iconFtexPath", false, l => l.IconFtexPath, (l, v) => l with { IconFtexPath = v as string }),
        new("displayName", false, l => l.DisplayName, (l, v) => l with { DisplayName = v as string }),
        new("enableArm", true, l => l.EnableArm, (l, v) => l with { EnableArm = v as bool? }),
        new("enableHead", true, l => l.EnableHead, (l, v) => l with { EnableHead = v as bool? }),
    ];
}

/// <summary>The vanilla heads each character can wear with a normal suit (V Framework head aliases).</summary>
public static class VanillaHeads
{
    /// <summary>
    /// The vanilla heads a port lists by default. Custom outfits get only the heads they list (V Framework keeps
    /// "none" itself). V Framework 1.7 checks development only for custom heads, not vanilla ones
    /// (TryInjectHeadOptionList), and module Lua runs at boot before the save is loaded, so a port can't check it
    /// either: whatever is listed can be worn undeveloped. Both of Snake's bandanas are R&amp;D items (Bandana 37001,
    /// Infinity Bandana 37002), so by default none is listed; <see cref="Optional"/> lists them on request.
    /// DD soldiers' balaclava and headgears are listed by default.
    /// </summary>
    public static List<string> For(PlayerBranch who) => who switch
    {
        PlayerBranch.DdMale or PlayerBranch.DdFemale => ["balaclava", "spheadgear", "hpheadgear"],
        _ => [],
    };

    /// <summary>Heads the user can add for this character (each can be worn before it is developed).</summary>
    public static List<string> Optional(PlayerBranch who) => who switch
    {
        PlayerBranch.Snake or PlayerBranch.Avatar => ["bandana", "infinitebandana"],
        _ => [],
    };

    /// <summary>Vanilla heads that V Framework 1.7 lists without checking they are developed.</summary>
    public static readonly IReadOnlyDictionary<string, int> DevelopIds = new Dictionary<string, int>
    {
        ["bandana"] = 37001,
        ["infinitebandana"] = 37002,
    };
}

/// <summary>One character's branch: a base look, optional extra looks, abilities and camo bonus.</summary>
/// <summary>
/// An outfit's voice (V Framework voiceFpk / voiceType): a voice package per voice type, and the voice the player is
/// made to speak with. A voice package's .fpkd holds a .fox2 with a SoundPackage naming a .sdf, which loads a sound
/// bank (.sbp) by name (Raiden Voice, Yazed0071: vox_player_raiden). Not applied in FOB.
/// </summary>
public sealed record OutfitVoice
{
    /// <summary>Voice type → voice package (.fpk), e.g. "snak" → "/Assets/tpp/pack/player/parts/V_plvoice_raiden.fpk".</summary>
    public Dictionary<string, string> Fpks { get; init; } = new(StringComparer.Ordinal);
    /// <summary>One voice package for all voice types (voiceFpk as text, M8); null = a package for each voice type (<see cref="Fpks"/>).</summary>
    public string? Fpk { get; init; }
    /// <summary>The voice type the player speaks with ("snak", "ddmsoldiera"…); null = the character's own.</summary>
    public string? Type { get; init; }

    /// <summary>This voice with one package for all types: the package of its voice type, else the first by type name.</summary>
    public OutfitVoice OneForAll() => Fpk != null || Fpks.Count == 0 ? this : this with
    {
        Fpk = Type != null && Fpks.TryGetValue(Type, out var f) ? f : Fpks.OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Value,
        Fpks = new(StringComparer.Ordinal),
    };

    /// <summary>
    /// This voice with a package for each voice type: the one package goes to the voice type, else to each type of the
    /// wearer. Not to all 11 types: V Framework keeps 8 entries at most (kMaxVoiceFpkByType).
    /// </summary>
    public OutfitVoice EachType(PlayerBranch who) => Fpk == null ? this : this with
    {
        Fpk = null,
        Fpks = (Type != null ? [Type] : TypesOf(who)).ToDictionary(t => t, _ => Fpk, StringComparer.Ordinal),
    };

    /// <summary>The voice types a wearer speaks with by default.</summary>
    private static IEnumerable<string> TypesOf(PlayerBranch who) => who switch
    {
        PlayerBranch.DdMale => Types.Where(t => t.StartsWith("ddm", StringComparison.Ordinal)),
        PlayerBranch.DdFemale => Types.Where(t => t.StartsWith("ddf", StringComparison.Ordinal)),
        PlayerBranch.Ocelot => ["ocelot"],
        PlayerBranch.Quiet => ["quiet"],
        _ => ["snak"],
    };

    /// <summary>The voice types (Custom_Outfits wiki): Snake/Avatar snak, DD male ddmsoldiera–d, DD female ddfsoldiera–d, ocelot, quiet.</summary>
    public static readonly IReadOnlyList<string> Types =
        ["snak", "ddmsoldiera", "ddmsoldierb", "ddmsoldierc", "ddmsoldierd", "ddfsoldiera", "ddfsoldierb", "ddfsoldierc", "ddfsoldierd", "ocelot", "quiet"];
}

public sealed record OutfitBranch
{
    /// <summary>The outfit's own voice; null = the character's voice.</summary>
    public OutfitVoice? Voice { get; init; }
    public required Look Base { get; init; }
    public List<Look> Variants { get; init; } = [];

    /// <summary>
    /// True when <paramref name="look"/> shows the wearer's head. A look with no value (enableHead absent) takes the value
    /// of the Main look, and the Main look shows the head unless it says not. False for a body with its head in the model.
    /// </summary>
    public bool ShowsHead(Look look) => look.EnableHead ?? Base.EnableHead != false;

    /// <summary>True when the Main look or another look shows the wearer's head.</summary>
    public bool AnyLookShowsHead() => ShowsHead(Base) || Variants.Any(ShowsHead);
    public Abilities Abilities { get; init; } = new();
    /// <summary>Vanilla camo name for concealment, e.g. "BLACK". Wins over <see cref="CamoBonusValues"/> in V Framework.</summary>
    public string? CamoBonusType { get; init; }
    /// <summary>
    /// Concealment per ground material (MTR_* name → bonus; vanilla camos use 0 or 50). Materials left out are 0.
    /// The game's own values per vanilla camo are in GameKnowledge.CamoValues.
    /// </summary>
    public Dictionary<string, int>? CamoBonusValues { get; init; }
    public List<string> HeadOptions { get; init; } = [];
    /// <summary>Sortie Prep colour palette (V Framework colors): fv2s applied over the outfit, e.g. vanilla camos.</summary>
    public List<OutfitColor> PrimaryColors { get; init; } = [];
    public List<OutfitColor> SecondaryColors { get; init; } = [];
    /// <summary>
    /// #62: the user removed the outfit's last own slot. While a bonus slot remains the outfit has no Main look: the build
    /// writes only its slots under vanilla suits (<see cref="OutfitSpec.VanillaOnly"/>). The looks stay as their source.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool VanillaOnly { get; init; }
}

/// <summary>One colour in the Sortie Prep palette: an fv2 applied over the outfit, its package and swatch icon.</summary>
public sealed record OutfitColor(string Fv2, string? Fpk, string? Icon);

/// <summary>The R&amp;D row (AddToEquipDevelopTable).</summary>
public sealed record DevelopRow
{
    /// <summary>Grade 1–12 (an old value above 12 is kept), or null to leave it out (V Framework picks one; costumes without a grade, camo outfits).</summary>
    public int? Grade { get; init; } = 1;
    public int GmpCost { get; init; }
    /// <summary>GMP paid each time it is deployed (usageGmpCost); 0 = free. The vanilla Bandana costs 3400.</summary>
    public int UsageGmpCost { get; init; }
    /// <summary>True: already developed when installed.</summary>
    public bool InitiallyAvailable { get; init; } = true;
    /// <summary>
    /// The row follows this vanilla R&amp;D row: it never shows in R&amp;D, and the module copies the vanilla row's
    /// development state onto it (after the save is loaded, and whenever V Framework re-checks the row). Used for
    /// heads that stand in for vanilla ones, so the player never develops the same item twice. Needs a registration key.
    /// </summary>
    public int? FollowsVanillaDevelopId { get; init; }
    /// <summary>Minutes to develop (developTimeMinute); 0 = left out.</summary>
    public int TimeMinutes { get; init; }
    /// <summary>Required R&amp;D-style staff section (1 Combat, 2 R&amp;D, 3 Base Dev, 4 Support, 5 Intel, 6 Medical; 0 = none) and its level.</summary>
    public int SectionId { get; init; }
    /// <summary>
    /// The R&amp;D section. Its level has its own field, p55 (sectionLvForDevelop), with no section id, as in most vanilla
    /// rows; the other sections go in an id and level pair (p63/p64, p56/p57). No vanilla row puts this id in p56.
    /// </summary>
    public const int RdSection = 2;
    public int SectionLevel { get; init; }
    public int Section2Id { get; init; }
    public int Section2Level { get; init; }
    /// <summary>Resources spent to develop (names such as CommonMetal) and their counts.</summary>
    public string? Resource1 { get; init; }
    public int Resource1Count { get; init; }
    public string? Resource2 { get; init; }
    public int Resource2Count { get; init; }
    /// <summary>Resources spent on each deployment.</summary>
    public string? Usage1 { get; init; }
    public int Usage1Count { get; init; }
    public string? Usage2 { get; init; }
    public int Usage2Count { get; init; }
    /// <summary>
    /// The specialist skill (p04): a Mother Base staff skill name such as SuppressorEngineer, or null for none. A number
    /// from an old project or an existing mod stays as its digits and is written as a number.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(SkillJson))]
    public string? Skill { get; init; }
    public static bool IsNumber(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>Reads an old project's numeric skill as its digits, so it loads and is written back unchanged.</summary>
    private sealed class SkillJson : System.Text.Json.Serialization.JsonConverter<string?>
    {
        public override string? Read(ref System.Text.Json.Utf8JsonReader r, Type t, System.Text.Json.JsonSerializerOptions o) =>
            r.TokenType == System.Text.Json.JsonTokenType.Number ? System.Text.Encoding.UTF8.GetString(r.ValueSpan) : r.GetString();
        public override void Write(System.Text.Json.Utf8JsonWriter w, string? v, System.Text.Json.JsonSerializerOptions o) => w.WriteStringValue(v);
    }
    /// <summary>Hidden until its blueprint (or skill) is obtained: displayInfo = 1.</summary>
    public bool HiddenUntilObtained { get; init; }
    /// <summary>
    /// The game's blueprint (p05) the player must obtain, by its TppMotherBaseManagementConst name (DESIGN_2001), or
    /// null for none. Written as bluePrintId = TppMotherBaseManagementConst.&lt;name&gt;.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] // keeps old .svfm files as they are
    public string? BluePrint { get; init; }
    /// <summary>
    /// The blueprint an existing mod's script gives (a mod key, an expression or a function), as text to show. Never
    /// written: the script keeps it as it is.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnBluePrint { get; init; }
    /// <summary>
    /// Issue #123: the upgrade lines in R&amp;D (langPowerUpInfo0..7), as lang IDs joined by '\n', in order; null = none.
    /// A vanilla line is a game ID (ability_0300); the item's own line is a key of its text. One string, not a list,
    /// so that the record keeps value equality (undo, Compare) and old .svfm files stay as they are.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UpgradeLineIds { get; init; }
    /// <summary>The game shows at most 8 upgrade lines (p10 to p17).</summary>
    public const int MaxUpgradeLines = 8;
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> UpgradeLines => UpgradeLineIds is { Length: > 0 } s ? s.Split('\n') : [];
    public DevelopRow WithUpgradeLines(IEnumerable<string> ids) =>
        this with { UpgradeLineIds = ids.Where(i => i.Length > 0).Take(MaxUpgradeLines).ToList() is { Count: > 0 } l ? string.Join('\n', l) : null };
    /// <summary>
    /// "Hidden until obtained" needs something to obtain: a blueprint or a specialist. Without one the row stays hidden
    /// for ever.
    /// </summary>
    public bool CanBeHidden() => BluePrint != null || OwnBluePrint != null || Skill != null;
    public static string BluePrintLua(string id) => "TppMotherBaseManagementConst." + id;
    /// <summary>
    /// The 79 blueprints (p05) that vanilla R&amp;D rows use, each with the first develop id that uses it (for its name).
    /// From EquipDevelopConstSetting.lua. ANIMAL_ ids are iDroid animal entries that vanilla rows also use as p05.
    /// </summary>
    public static IReadOnlyList<(string Id, int DevelopId)> VanillaBluePrints { get; } =
    [
        ("ANIMAL_130", 16008), ("ANIMAL_2250", 18010), ("DESIGN_2000", 1031), ("DESIGN_2001", 1041), ("DESIGN_2002", 1061),
        ("DESIGN_2003", 1071), ("DESIGN_2005", 2011), ("DESIGN_2006", 2021), ("DESIGN_2007", 3051), ("DESIGN_2008", 3071),
        ("DESIGN_2009", 4041), ("DESIGN_2010", 4051), ("DESIGN_2011", 5021), ("DESIGN_2012", 5031), ("DESIGN_2013", 6021),
        ("DESIGN_2014", 6031), ("DESIGN_2015", 6041), ("DESIGN_2016", 7011), ("DESIGN_2017", 7021), ("DESIGN_2018", 8011),
        ("DESIGN_2019", 10061), ("DESIGN_2020", 18002), ("DESIGN_2021", 38011), ("DESIGN_2022", 38014), ("DESIGN_2023", 27006),
        ("DESIGN_2024", 38030), ("DESIGN_2025", 9001), ("DESIGN_2026", 31002), ("DESIGN_2027", 2041), ("DESIGN_3000", 10080),
        ("DESIGN_3001", 24005), ("DESIGN_3002", 25005), ("DESIGN_3003", 28004), ("DESIGN_3005", 31007), ("DESIGN_3006", 16003),
        ("DESIGN_3007", 12043), ("DESIGN_3008", 19070), ("DESIGN_3009", 37001), ("DESIGN_3010", 37002), ("DESIGN_3011", 12003),
        ("DESIGN_3012", 12013), ("DESIGN_3013", 1080), ("DESIGN_3014", 51004), ("DESIGN_3015", 51005), ("DESIGN_3016", 19072),
        ("DESIGN_3017", 19073), ("DESIGN_3018", 17031), ("DESIGN_3019", 19024), ("EXTRA_4000", 2030), ("EXTRA_4001", 4060),
        ("EXTRA_4002", 9010), ("EXTRA_4003", 9011), ("EXTRA_4004", 9012), ("EXTRA_4005", 9013), ("EXTRA_4006", 12010),
        ("EXTRA_4007", 12011), ("EXTRA_4008", 12012), ("EXTRA_4009", 12014), ("EXTRA_4010", 13040), ("EXTRA_4011", 19040),
        ("EXTRA_4012", 19071), ("EXTRA_4015", 19032), ("EXTRA_4016", 19030), ("EXTRA_4017", 19031), ("EXTRA_4018", 19033),
        ("EXTRA_4019", 19080), ("EXTRA_4020", 24006), ("EXTRA_4021", 24007), ("EXTRA_4022", 19081), ("EXTRA_4023", 19084),
        ("EXTRA_4024", 1090), ("EXTRA_4025", 1091), ("EXTRA_4026", 19085), ("EXTRA_4027", 19086), ("EXTRA_4028", 25006),
        ("EXTRA_5000", 38033), ("EXTRA_5001", 38034), ("EXTRA_5002", 38043), ("EXTRA_6000", 17032),
    ];
    /// <summary>Parent R&amp;D row (baseEquipDevelopId): a vanilla develop id this row upgrades.</summary>
    public int? UpgradeOfDevelopId { get; init; }
    /// <summary>
    /// "Grade before" (issue #121): the registration key of the project item that must be developed first. It is written
    /// as baseEquipDevelopId through V_TppMotherBaseManagement.GetDevelopId, so it wins over <see cref="UpgradeOfDevelopId"/>.
    /// </summary>
    // Left out of the project file when none, so a file without a link stays as format 3 wrote it.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GradeBefore { get; init; }
    /// <summary>The Lua that gives the develop ID of a project item's row.</summary>
    public static string GradeBeforeLua(string key) => "V_TppMotherBaseManagement.GetDevelopId(" + LuaWriter.Str(key) + ")";
    /// <summary>The key in a value that <see cref="GradeBeforeLua"/> wrote (as the sandbox names it), or null.</summary>
    public static string? GradeBeforeOf(string lua) =>
        System.Text.RegularExpressions.Regex.Match(lua, """^V_TppMotherBaseManagement\.GetDevelopId\("([^"\\]+)"\)$""") is { Success: true } m ? m.Groups[1].Value : null;
    /// <summary>
    /// True when the game never shows the row in R&amp;D: an upgrade that is not developed and costs 0 GMP to develop
    /// (V Framework develops 0-cost rows on its own only when their parent row can show; an upgrade row stays hidden).
    /// Seen in game, 18 Nov 2026 (Smoke Pellet grades). 1 GMP or more shows the row.
    /// </summary>
    public bool IsHiddenUpgrade() => (UpgradeOfDevelopId != null || GradeBefore != null) && !InitiallyAvailable && GmpCost == 0 && FollowsVanillaDevelopId == null;
    /// <summary>Using the item caps the mission rank (isResultRankLimited).</summary>
    public bool RankLimited { get; init; }
    /// <summary>
    /// Lang id of the line the results screen shows when the rank is capped (langResultRankLimited), or null for the
    /// V Framework default, the item's name. Written only with <see cref="RankLimited"/>.
    /// </summary>
    public string? ResultRankLang { get; init; }
    /// <summary>
    /// Story mission code (e.g. 10086, Lingua Franca): the row stays hidden in R&amp;D until that mission is cleared.
    /// Written as a live bluePrintId condition (TppStory.IsMissionCleard) with displayInfo = 1. Null: no condition.
    /// </summary>
    public int? AfterMission { get; init; }
}

/// <summary>Everything that goes into one V Framework outfit's Lua.</summary>
public sealed class OutfitSpec
{
    /// <summary>Registration key; keep it the same across rebuilds (V Framework saves IDs under it).</summary>
    public required string Key { get; init; }
    public required string NameLang { get; init; }
    public string? RealNameLang { get; init; }
    public string? InfoLang { get; init; }
    public required string IconFtexPath { get; set; }
    public Dictionary<PlayerBranch, OutfitBranch> Branches { get; } = [];
    /// <summary>The vanilla suits this outfit's slots also appear under.</summary>
    public IReadOnlyList<AlsoUnder> AlsoUnder { get; set; } = [];
    public DevelopRow Develop { get; set; } = new();
    /// <summary>Comment lines written at the top of the module.</summary>
    public List<string> Header { get; } = [];

    /// <summary>
    /// #62: no own slot, only bonus slots. V Framework 1.7 has no RegisterOutfit without a suit cell and an R&amp;D row, so
    /// the build writes only the ExtendVanillaOutfit calls. Takes effect only while a bonus slot remains.
    /// </summary>
    public bool VanillaOnly => Branches.Count > 0 && Branches.Values.All(b => b.VanillaOnly) && BonusSlotCount > 0;
    /// <summary>The outfit's own Sortie slots: the Main look and the variants of the widest branch.</summary>
    public int OwnSlots => VanillaOnly || Branches.Count == 0 ? 0 : 1 + Branches.Values.Max(b => b.Variants.Count);
    /// <summary>The bonus slots under vanilla suits; one slot is one (look, name) pair shared by the wearers.</summary>
    public int BonusSlotCount => AlsoUnder.Sum(a => a.Slots.Select(x => (x.Index, x.LangId)).Distinct().Count());
    /// <summary>
    /// #62: a slot can go while one other slot of the outfit remains. The last own slot can go only while a bonus slot
    /// stays (<paramref name="ownCanGo"/> false: never, as for an existing V Framework mod). #84: the last bonus slot of a
    /// vanilla-only outfit can go too, because the outfit then gets its own slot back.
    /// </summary>
    public bool CanRemoveSlot(bool own, bool ownCanGo = true) =>
        own ? OwnSlots > 1 || (ownCanGo && OwnSlots == 1 && BonusSlotCount > 0) : OwnSlots + BonusSlotCount > 1 || VanillaOnly;
}

/// <summary>
/// "Also appear under other outfits": the outfit's slots added as extra looks under a vanilla suit (ExtendVanillaOutfit,
/// no new R&amp;D row). Lists are replaced whole on every edit, never changed in place, so undo can keep the old one.
/// </summary>
public sealed record AlsoUnder(string Outfit, IReadOnlyList<AlsoSlot> Slots);

/// <summary>One slot of the item shown under a vanilla suit: Index -1 is the base look, otherwise the variant number.</summary>
/// <remarks>SuitAbilities true: the slot has the vanilla suit's abilities, not the abilities of the look it shows (#61 Q3).</remarks>
public sealed record AlsoSlot(PlayerBranch Who, int Index, string LangId, bool? EnableArm = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool SuitAbilities = false);

/// <summary>Extra looks, heads or abilities added to a vanilla suit (V_Player.ExtendVanillaOutfit). No R&amp;D row.</summary>
public sealed class ExtendVanillaSpec
{
    /// <summary>Vanilla camo name ("TIGERSTRIPE") or number 0–116. Variants need this (not a raw parts type).</summary>
    public string? Outfit { get; init; }
    /// <summary>Raw parts type; variants do not work with it.</summary>
    public int? PartsType { get; init; }
    public Dictionary<PlayerBranch, VanillaBranch> Branches { get; } = [];
    public List<string> Header { get; } = [];
}

/// <summary>What ExtendVanillaOutfit changes for one character. Only fields set are written.</summary>
public sealed record VanillaBranch
{
    /// <summary>A voice for the vanilla suit (Raiden Voice gives the RAIDEN suit Raiden's voice this way); null = unchanged.</summary>
    public OutfitVoice? Voice { get; init; }
    /// <summary>Extra looks; a vanilla parts type holds 29 in total.</summary>
    public List<Look> Variants { get; init; } = [];
    public List<string> HeadOptions { get; init; } = [];
    public bool? EnableArm { get; init; }
    public bool? EnableHead { get; init; }
    public Abilities Abilities { get; init; } = new();
}

/// <summary>A selectable head (V_Player.RegisterHeadOption) and, when it has a name or icon, its R&amp;D row.</summary>
public sealed class HeadOptionSpec
{
    /// <summary>Max 63 characters; no line break, double quote or backslash.</summary>
    public required string Key { get; init; }
    public Dictionary<PlayerBranch, HeadBranch> Branches { get; } = [];
    public bool ShowInDevelopMenu { get; init; }
    public bool InfiniteAmmo { get; set; }
    /// <summary>
    /// The Snake branch wears Snake's own face (bandana faces, accessory heads, face mods). V Framework 1.7 has Avatar
    /// read Snake's branch of a custom outfit, so Avatar would wear Snake's face; the builder gives such outfits an
    /// Avatar branch without these heads, unless the head has its own Avatar branch.
    /// </summary>
    public bool SnakeFaceOnly { get; init; }
    /// <summary>Avatar would wear Snake's face: the head is built on it and has no Avatar branch of its own.</summary>
    public bool AvatarWouldWearSnakeFace => SnakeFaceOnly && !Branches.ContainsKey(PlayerBranch.Avatar);
    /// <summary>Why the Avatar can't wear this head (an orange warning in the app); null when the Avatar can.</summary>
    public string? NoAvatarReason { get; init; }
    /// <summary>R&amp;D row: name lang ID and icon. Without a name no row is written.</summary>
    public string? NameLang { get; init; }
    /// <summary>R&amp;D row: real name and description lang IDs (shown when the head is picked).</summary>
    public string? RealNameLang { get; init; }
    public string? InfoLang { get; init; }
    public string? IconFtexPath { get; set; }
    public DevelopRow Develop { get; set; } = new();
    public List<string> Header { get; } = [];
}

public sealed record HeadBranch
{
    public string? Fv2 { get; init; }
    public string? Fpk { get; init; }
    /// <summary>Snake only: own face per Demon Point stage (1 Normal, 2 Grown horn, 3 Demon Snake).</summary>
    public Dictionary<int, (string fv2, string fpk)> FaceStages { get; init; } = [];
    /// <summary>DD only: a soldier face worn as the head, e.g. "TppEnemyFaceId.svs_balaclava" (written as code).</summary>
    public string? EnemyFaceId { get; init; }
}
