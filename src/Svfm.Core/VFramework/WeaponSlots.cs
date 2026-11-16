namespace Svfm.Core.VFramework;

/// <summary>
/// One SetGunBasic slot of a weapon and every fact the port needs about it.
/// </summary>
/// <param name="Key">The gunBasic key, for example "scope1Id".</param>
/// <param name="Label">The name of the attachment in the UI.</param>
/// <param name="PartKind">The kind in ChimeraPartsPackageTable of the part that a mod's model can move into this slot; null when no model moves here.</param>
/// <param name="Stats">The stat table of the part in this slot; null when the part has none (a muzzle) or another slot gives it.</param>
/// <param name="Points">The connect points for the model merge, in the order of preference; empty for the receiver.</param>
/// <param name="MergeOrder">The position in the model merge. The barrel is first, because later parts can hang on its points.</param>
/// <param name="PartsType">The partsType of the slot in WeaponPartsCombinationSettings.lua; null when the game lists no choices.</param>
/// <param name="None">The empty value of the slot as the gunBasic table writes it; null when the slot is never empty in the lists.</param>
/// <param name="Swappable">True when the user can swap the part on the Stats tab.</param>
public sealed record WeaponSlot(string Key, string Label, string? PartKind, WeaponPartKind? Stats, string[] Points, int MergeOrder,
    int? PartsType = null, string? None = null, bool Swappable = false);

/// <summary>
/// The slots of a weapon, in the column order of the game's gunBasic table (column 1 is the weapon). This is the one
/// list of slots: the merge, the attachment list, the part choices and the tuning read it.
/// </summary>
public static class WeaponSlots
{
    // Swappable: only the sights. A V Framework custom weapon draws no separate attachment model (in game, Nov 10), so a
    // sight still gives its zoom, but a flashlight, laser or under-barrel does nothing.
    // shortcut: SIGHT_BARREL by the receiver flag sightMountMesh is not used (the selector is not confirmed); add it when an in-game test shows a sight in the wrong place.
    public static readonly IReadOnlyList<WeaponSlot> Columns =
    [
        new("receiverId", "Receiver", "receiver", WeaponPartKind.Receiver, [], -1),
        new("barrelId", "Barrel", "barrel", WeaponPartKind.Barrel, ["BARREL"], 0),
        new("ammoId", "Magazine", "magazine", WeaponPartKind.Magazine, ["AMMO"], 1),
        new("stockId", "Stock", "stock", WeaponPartKind.Stock, ["STOCK"], 2),
        new("muzzleId", "Muzzle", "muzzle", null, ["MUZZLE_OPTION"], 5),
        new("muzzleOptionId", "Muzzle attachment", "muzzleOption", WeaponPartKind.MuzzleOption, ["MUZZLE_OPTION"], 6),
        new("scope1Id", "Scope", "sight", WeaponPartKind.Sight, ["SIGHT_RECEIVER", "SIGHT_BARREL"], 3, 6, "ST_None", Swappable: true),
        new("scope2Id", "Scope 2", null, null, ["SIGHT_BARREL"], 4, 7, "ST_None", Swappable: true),
        new("underBarrelId", "Under-barrel", "underBarrel", WeaponPartKind.UnderBarrel, ["UNDER_BARREL"], 7, 10, "UD_None"),
        new("laserFlash1Id", "Laser sight 1", "option", WeaponPartKind.Option, ["SYSTEM_LASER"], 8, 8, "LT_None"),
        new("laserFlash2Id", "Laser sight 2", null, null, ["SYSTEM_LASER"], 9, 9, "LT_None"),
    ];

    /// <summary>The slots that hold attachments with packages of their own: every slot but the receiver.</summary>
    public static readonly IReadOnlyList<WeaponSlot> Attachments = Columns.Skip(1).ToList();

    /// <summary>The attachment slots in the order of the model merge.</summary>
    public static readonly IReadOnlyList<WeaponSlot> MergeOrder = Attachments.OrderBy(s => s.MergeOrder).ToList();

    /// <summary>The keys of the slots that the user can swap.</summary>
    public static readonly string[] Swappable = Columns.Where(s => s.Swappable).Select(s => s.Key).ToArray();

    /// <summary>The two slots that hold a flashlight or a laser sight.</summary>
    public static readonly string[] Lasers = ["laserFlash1Id", "laserFlash2Id"];

    /// <summary>
    /// The vanilla part in a slot. A part with changed stats becomes the copy's own part (WeaponTuning.Apply puts its
    /// constant, for example BA_SVFM_…, in spec.Parts), but its model, package and paint are those of the vanilla part.
    /// </summary>
    public static string? PartIn(WeaponSpec spec, string slot) => spec.Tuning?.GunBasic.GetValueOrDefault(slot) ?? spec.Parts.GetValueOrDefault(slot);
}
