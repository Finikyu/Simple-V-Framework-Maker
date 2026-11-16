namespace Svfm.Core.VFramework;

/// <summary>
/// A V Framework 1.7 buddy outfit (Buddy_Outfits wiki, RegisterBuddyOutfit): a new costume picked in Sortie Prep like the
/// vanilla ones. Its package is a copy of the vanilla buddy package whose .fox2 names the outfit's own .parts. Quiet
/// costumes only change the look. Needs an R&amp;D row with the same key (equipDevelopTypeID = EQP_DEV_TYPE_Quiet/Horse/DDog).
/// </summary>
public sealed class BuddyOutfitSpec
{
    public required string Key { get; init; }
    /// <summary>"quiet", "horse" or "dog".</summary>
    public required string Buddy { get; init; }
    public required string FpkPath { get; init; }
    /// <summary>Quiet only: show NAKED's bruises.</summary>
    public bool Bruise { get; set; }
    /// <summary>D-Dog only: how the gear plays (null = like NAKED, V Framework's default).</summary>
    public DogGearBehaviour? Dog { get; set; }
    /// <summary>D-Horse only: how the tack plays (null = like NORMAL, V Framework's default).</summary>
    public HorseTackBehaviour? Horse { get; set; }
    public required string NameLang { get; init; }
    /// <summary>The name in the detail panel (R&amp;D p30); vanilla Quiet costumes use the name itself. null = NameLang.</summary>
    public string? RealNameLang { get; init; }
    public string? InfoLang { get; init; }
    /// <summary>Ability lines in Sortie Prep (R&amp;D langPowerUpInfo0..; vanilla BATTLE DRESS: ability_1082).</summary>
    public List<string> PowerUpInfoLangs { get; init; } = [];
    public required string IconFtexPath { get; set; }
    public DevelopRow Develop { get; set; } = new();
    public List<string> Header { get; } = [];

    public string DevelopType => Buddy switch { "quiet" => "Quiet", "horse" => "Horse", "dog" => "DDog", _ => throw new ArgumentException(Buddy) };
}

/// <summary>
/// D-Dog gear behaviour, the dog branch fields of RegisterBuddyOutfit (V Framework 1.7 V_TppBuddyServiceLib.cpp
/// ReadDogEffects; unset fields play like NAKED). <see cref="Vanilla"/> gives the vanilla gear's values, so a port of a
/// gear replacer plays like the gear it replaced: the game tests the gear value for BATTLE DRESS (3) only, except the
/// soldiers' threat check (2–4) and the "Do it" order (BuddyOutfitRegistry kGearCommand); the BATTLE DRESS numbers are
/// the wiki's (damage 0.25, escape and hit retreat 10 m, retarget 15 m).
/// </summary>
public sealed record DogGearBehaviour
{
    /// <summary>V_DogCommandType name: KEEP_BUSY, FULTON, KNOCK_OUT, WOUND, KILL, KEEP_BUSY_NO_HOLD.</summary>
    public string? CommandType { get; init; }
    public double? DamageRate { get; init; }
    public bool? BulletFlinch { get; init; }
    public bool? ArmorHitEffect { get; init; }
    public bool? ArmorHitSound { get; init; }
    public double? EscapeDistance { get; init; }
    public double? HitRetreatDistance { get; init; }
    public double? RetargetRange { get; init; }
    public bool? SoldierThreat { get; init; }

    public static readonly string[] CommandTypes = ["KEEP_BUSY", "FULTON", "KNOCK_OUT", "WOUND", "KILL", "KEEP_BUSY_NO_HOLD"];

    /// <summary>
    /// The vanilla gear of a gear value (0 NAKED, 1 TACTICAL (FULTON), 2 SNEAKING (STUN), 3 BATTLE DRESS, 4 SNEAKING (KNIFE)).
    /// BATTLE DRESS takes a quarter of the damage only during orders in vanilla; V Framework's damageRate applies always,
    /// the closest it allows.
    /// </summary>
    public static DogGearBehaviour Vanilla(int gear) => gear switch
    {
        0 => new DogGearBehaviour(),
        1 => new DogGearBehaviour { CommandType = "FULTON" },
        2 => new DogGearBehaviour { CommandType = "KNOCK_OUT", SoldierThreat = true },
        3 => new DogGearBehaviour
        {
            CommandType = "WOUND", DamageRate = 0.25, BulletFlinch = false, ArmorHitEffect = true, ArmorHitSound = true,
            EscapeDistance = 10, HitRetreatDistance = 10, RetargetRange = 15, SoldierThreat = true,
        },
        4 => new DogGearBehaviour { CommandType = "KILL", SoldierThreat = true },
        _ => throw new ArgumentOutOfRangeException(nameof(gear), "Vanilla D-Dog gear values are 0–4."),
    };
}

/// <summary>
/// D-Horse tack behaviour, the horse branch fields of RegisterBuddyOutfit (V Framework 1.7 V_TppBuddyServiceLib.cpp
/// ReadHorseEffects; unset fields play like NORMAL). <see cref="Vanilla"/> gives the vanilla tack's values (wiki, "D-Horse
/// tack"; BuddyOutfitRegistry kTackDamageRate/kTackGallopRate/kTackBodyAttack/kTackSoundCode), so a port of a tack replacer
/// plays like the tack it replaced.
/// </summary>
public sealed record HorseTackBehaviour
{
    /// <summary>Share of damage taken: 1 = full, at most 1.275.</summary>
    public double? DamageRate { get; init; }
    /// <summary>Gallop speed multiplier, above 0.</summary>
    public double? GallopSpeedRate { get; init; }
    /// <summary>A TppDamage.ATK_* name (ATK_HorseBodyAttack, …Mil1–Mil3) hit when he rams soldiers.</summary>
    public string? BodyAttack { get; init; }
    /// <summary>Vanilla tack whose movement sounds play, 0–6 (see <see cref="TackNames"/>).</summary>
    public int? SoundType { get; init; }

    public static readonly string[] TackNames =
        ["NORMAL", "FURICORN", "BATTLE DRESS", "BATTLE DRESS (Defense Up [1])", "BATTLE DRESS (Defense Up [2])", "PARADE CLOTH", "WESTERN CLOTH"];

    /// <summary>The vanilla tack of a sound type value (0–6). Only the values that differ from NORMAL are set.</summary>
    public static HorseTackBehaviour Vanilla(int tack) => tack switch
    {
        0 => new HorseTackBehaviour(),
        1 => new HorseTackBehaviour { SoundType = 1 },
        2 => new HorseTackBehaviour { DamageRate = 0.895, GallopSpeedRate = 0.98, BodyAttack = "ATK_HorseBodyAttackMil1", SoundType = 2 },
        3 => new HorseTackBehaviour { DamageRate = 0.75, GallopSpeedRate = 0.96, BodyAttack = "ATK_HorseBodyAttackMil2", SoundType = 3 },
        4 => new HorseTackBehaviour { DamageRate = 0.5, GallopSpeedRate = 0.94, BodyAttack = "ATK_HorseBodyAttackMil3", SoundType = 4 },
        5 => new HorseTackBehaviour { SoundType = 5 },
        6 => new HorseTackBehaviour { SoundType = 6 },
        _ => throw new ArgumentOutOfRangeException(nameof(tack), "Vanilla D-Horse tack values are 0–6."),
    };
}
