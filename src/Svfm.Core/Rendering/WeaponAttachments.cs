using Svfm.Core.Knowledge;
using Svfm.Core.VFramework;

namespace Svfm.Core.Rendering;

/// <summary>One attachment of a weapon that the 3D preview and the icon can show or hide.</summary>
/// <param name="Key">A mesh group key ("suppressor", "light", "magazine") or the name of a merged attachment model without extension.</param>
/// <param name="Name">The label shown to the user.</param>
public sealed record WeaponAttachment(string Key, string Name);

/// <summary>
/// The attachments of a weapon (owner, Nov 7: the laser sights were not offered). A weapon has two kinds: mesh groups of
/// its own model (suppressor, flashlight, spare magazine) and the models of the attachment packages that the port merges
/// into its package (barrel, muzzle, scope, laser sight and so on, see WeaponConverter.AddPartPackages). Only the ones
/// the weapon has are listed.
/// </summary>
public static class WeaponAttachments
{
    public const string Suppressor = "suppressor", Light = "light", Magazine = "magazine";

    private static readonly (string Key, ulong Group, string Name, int Bit)[] Groups =
    [
        (Suppressor, OutlineIconRenderer.GroupSuppressor, "Suppressor", 1),
        (Light, OutlineIconRenderer.GroupLight, "Flashlight", 2),
        (Magazine, OutlineIconRenderer.GroupMagazine, "Spare magazine", 4),
    ];

    /// <summary>The model name of an attachment: the name of its .parts without "_vNN" (true for all 357 merged attachments of the game).</summary>
    public static string ModelKey(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        int v = stem.LastIndexOf("_v", StringComparison.Ordinal);
        return v > 0 && v + 2 < stem.Length && stem[(v + 2)..].All(char.IsAsciiDigit) ? stem[..v] : stem;
    }

    /// <summary>
    /// The model that the game draws for a custom gun: the one its receiver .parts names. V Framework 1.7 draws only this
    /// model (in game, Nov 10), so the preview and the icon draw only it too; the port merges the part models into it.
    /// Null when the package has no such .parts or model (then every model is drawn, as before).
    /// </summary>
    public static string? ReceiverModel(WeaponSpec spec, IReadOnlyDictionary<string, byte[]> files) => Receiver(spec, files)?.Path;

    /// <summary>The file of the receiver model (its path and its bytes) in the weapon's package files; null as <see cref="ReceiverModel"/>.</summary>
    public static Formats.PackageFile? Receiver(WeaponSpec spec, IReadOnlyDictionary<string, byte[]> files)
    {
        if (!spec.FpkPath.EndsWith(".fpk", StringComparison.Ordinal) || !files.TryGetValue(spec.FpkPath + "d", out var fpkd)
            || ReceiverPath(Formats.FoxPackage.Read(fpkd), spec.PartsPath) is not { } model) return null;
        return files.TryGetValue(spec.FpkPath, out var fpk) ? Formats.FoxPackage.Read(fpk).Find(model) : null;
    }

    /// <summary>The model path that the receiver .parts names (ModelDescription.modelFile), or null when the .parts is not in the package.</summary>
    public static string? ReceiverPath(Formats.FoxPackage fpkd, string partsPath) =>
        fpkd.Find(partsPath) is { } parts ? Formats.FoxDataSet.Read(parts.Data).ModelFiles.FirstOrDefault() : null;

    /// <summary>The attachments of a weapon.</summary>
    /// <param name="spec">The weapon spec (its SetGunBasic slots), or null.</param>
    /// <param name="modelPaths">The paths of the models in the weapon's package.</param>
    /// <param name="groups">The mesh group hashes of those models (48 bits).</param>
    /// <returns>The attachments, groups first.</returns>
    public static IReadOnlyList<WeaponAttachment> Of(WeaponSpec? spec, IEnumerable<string> modelPaths, IReadOnlySet<ulong> groups)
    {
        var list = Groups.Where(g => groups.Contains(g.Group)).Select(g => new WeaponAttachment(g.Key, g.Name)).ToList();
        if (spec == null) return list;
        var models = modelPaths.Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, name) in WeaponSlots.Attachments.Select(s => (s.Key, s.Label)))
        {
            if (WeaponSlots.PartIn(spec, slot) is not { } part || !GameKnowledge.ChimeraParts.TryGetValue(part, out var cp) || cp.Kind == "receiver") continue;
            string key = ModelKey(cp.Parts);
            // A flashlight sits in a laser slot too (LT_ parts in the "flt" folder).
            string n = cp.Parts.Contains("/flt/", StringComparison.Ordinal) ? name.Replace("Laser sight", "Flashlight") : name;
            if (models.Contains(key) && list.All(a => !a.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) list.Add(new WeaponAttachment(key, n));
        }
        return list;
    }

    /// <summary>The Parts bits of an icon (suppressor 1, light 2, magazine 4) for the hidden keys.</summary>
    public static int PartsBits(IEnumerable<string> hidden)
    {
        var h = hidden.ToHashSet();
        return Groups.Where(g => !h.Contains(g.Key)).Sum(g => g.Bit);
    }

    /// <summary>The hidden model keys of an icon, as one sorted text (compared by value in the icon settings), or null when none.</summary>
    public static string? HiddenModels(IEnumerable<string> hidden)
    {
        var m = hidden.Where(k => Groups.All(g => g.Key != k)).Order(StringComparer.Ordinal).ToList();
        return m.Count == 0 ? null : string.Join(';', m);
    }

    /// <summary>The hidden keys that an icon's Parts bits and hidden models give.</summary>
    public static HashSet<string> HiddenKeys(int parts, string? hiddenModels)
    {
        var h = Groups.Where(g => (parts & g.Bit) == 0).Select(g => g.Key).ToHashSet();
        if (hiddenModels != null) foreach (var k in hiddenModels.Split(';', StringSplitOptions.RemoveEmptyEntries)) h.Add(k);
        return h;
    }

    /// <summary>The mesh groups to hide for the hidden keys.</summary>
    public static HashSet<ulong> HiddenGroups(IReadOnlySet<string> hidden) => Groups.Where(g => hidden.Contains(g.Key)).Select(g => g.Group).ToHashSet();

    /// <summary>Whether a model of the package is hidden.</summary>
    public static bool IsHidden(string modelPath, IReadOnlySet<string> hidden) => hidden.Contains(Path.GetFileNameWithoutExtension(modelPath));
}
