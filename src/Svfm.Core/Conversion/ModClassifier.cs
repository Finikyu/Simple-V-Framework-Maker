using Svfm.Core.Archives;
using Svfm.Core.Mods;

namespace Svfm.Core.Conversion;

public enum ModFileKind { Body, BodyPhysics, Camo, SnakeFace, DdHead, FaceOrHead, WeaponPackage, ItemPackage, Icon, Lang, ZetaScript, VFrameworkModule, Texture, MissionOrCutscene, Ui, Buddy, AvatarPart, Other }

/// <summary>One file of a mod: what it is, and whether it replaces a vanilla file or adds a new one.</summary>
public sealed record ClassifiedFile(string Path, ModFileKind Kind, bool ReplacesVanilla);

public sealed record ModClassification(IReadOnlyList<ClassifiedFile> Files, IReadOnlyList<string> Routes);

/// <summary>
/// Sorts a mod's files by kind and finds what each replaces (handover step 1), then names the port routes that fit.
/// Archive files are checked against the game; game-folder files (Zeta scripts, modules) are always "new".
/// </summary>
public static class ModClassifier
{
    public static ModClassification Classify(GameData? game, MgsvMod mod)
    {
        var files = new List<ClassifiedFile>();
        foreach (var path in mod.ArchiveFiles.Keys.OrderBy(p => p, StringComparer.Ordinal))
            files.Add(new ClassifiedFile(path, KindOf(path, mod), game?.Contains(path) ?? false));
        foreach (var path in mod.GameFiles.Keys.OrderBy(p => p, StringComparer.Ordinal))
            files.Add(new ClassifiedFile(path, KindOf(path), false));

        var routes = new List<string>();
        var weapons = WeaponConverter.Detect(mod);
        if (weapons.Count > 0)
            routes.Add($"Weapon model replacer ({string.Join(", ", weapons.Select(w => w[(w.LastIndexOf('/') + 1)..]))}): port as a new V Framework weapon (WeaponConverter).");
        if (files.Any(f => f.Kind == ModFileKind.Body && f.ReplacesVanilla))
            routes.Add("Body replacer: port as a new outfit (BodyConverter).");
        var hidden = files.Where(f => f.Kind == ModFileKind.Body && f.Path.EndsWith(".fpk", StringComparison.Ordinal))
            .SelectMany(f => FilesOf(mod.ArchiveFiles[f.Path]) ?? []).Select(x => x.Path)
            .Where(x => BodyConverter.HeadModelBody(x) != null).Distinct().ToList();
        if (hidden.Count > 0)
            routes.Add($"Head gear inside the body ({string.Join(", ", hidden.Select(h => h[(h.LastIndexOf('/') + 1)..]))}: the balaclava model at its vanilla path): " +
                       "taken out of the body and offered as a DD head option with its own model.");
        var buddies = game != null ? BuddyConverter.Detect(game, mod) : [];
        foreach (var w in buddies.Where(b => b.Warnings.Count > 0))
            routes.Add($"Buddy replacer {w.VanillaPackage[(w.VanillaPackage.LastIndexOf('/') + 1)..]}: portable, with warnings. {string.Join(" ", w.Warnings)}");
        if (buddies.Count > 0)
            routes.Add($"Buddy replacer ({string.Join(", ", buddies.Select(b => b.VanillaPackage[(b.VanillaPackage.LastIndexOf('/') + 1)..]))}): port each as a new " +
                       $"{string.Join("/", buddies.Select(b => b.Buddy).Distinct())} buddy outfit with its own package.");
        if (files.Any(f => f.Kind == ModFileKind.SnakeFace))
            routes.Add("Snake face replacer: port as Snake head options, one per face set (plain / bandana), with the Demon stages." +
                       (files.Any(f => f.Kind == ModFileKind.BodyPhysics) ? " The hair physics it puts in the body packages goes with the head (attached by its .fv2)." : ""));
        if (files.Any(f => f.Kind == ModFileKind.DdHead))
            routes.Add("DD head model replacer (balaclava/headgear): port as a DD head option with its own model.");
        if (files.Any(f => f.Kind == ModFileKind.MissionOrCutscene))
            routes.Add("Story mission/cutscene packages: not convertible (they only matter in those scenes); left out.");
        if (files.Any(f => f.Kind == ModFileKind.Ui))
            routes.Add("UI pictures (staff portrait …): global UI, not part of an item; left out.");
        // DD fatigues camo packages that go with a DD fatigues body the mod also replaces are that body's colours.
        var bodyCamos = game == null ? [] : BodyConverter.Detect(game, mod)
            .SelectMany(b => new[] { VFramework.PlayerBranch.DdMale, VFramework.PlayerBranch.DdFemale }.SelectMany(w => BodyConverter.FatiguesCamos(b, w)))
            .Select(c => c.package).ToHashSet(StringComparer.Ordinal);
        bool CamoOfBody(ClassifiedFile f) => f.Path.LastIndexOf('.') is int d and > 0 && bodyCamos.Contains(f.Path[..d]);
        if (files.Any(f => f.Kind == ModFileKind.Camo && f.ReplacesVanilla && CamoOfBody(f)))
            routes.Add("DD fatigues camos for the replaced body: offered as the outfit's colours in Sortie Prep.");
        if (files.Any(f => f.Kind == ModFileKind.Camo && f.ReplacesVanilla && !CamoOfBody(f)))
            routes.Add("Camo replacer: port as a new camo outfit.");
        var zetaBuddies = BuddyConverter.DetectZeta(mod);
        if (zetaBuddies.Count > 0)
            routes.Add($"Zeta buddy packages ({string.Join(", ", zetaBuddies.Select(b => b.VanillaPackage[(b.VanillaPackage.LastIndexOf('/') + 1)..]))}): each becomes its own " +
                       $"{zetaBuddies[0].Buddy} buddy outfit, its package copied as installed under the outfit's name.");
        if (files.Any(f => f.Kind == ModFileKind.ZetaScript) && zetaBuddies.Count == 0)
            routes.Add("Zeta scripts: translate the parameter tables to a V Framework weapon (new weapon, custom receiver/bullet/magazine rows); " +
                       "the model is the mod's own package, or a copy of the vanilla gun with its part models merged in.");
        var items = files.Where(f => f.Kind == ModFileKind.ItemPackage && f.ReplacesVanilla).ToList();
        var support = game != null ? SupportWeaponConverter.Detect(game, mod) : [];
        foreach (var sw in support)
            routes.Add(sw.Thrown
                ? $"Thrown support weapon model replacer ({SupportWeaponConverter.Family(sw.VanillaEquip)}): a new item thrown like it, with the mod's model."
                : $"Placed support weapon model replacer ({SupportWeaponConverter.Family(sw.VanillaEquip)}): not portable. {sw.Reason}");
        if (items.Count > 0 && weapons.Count == 0 && support.Count == 0)
            // Not tried: replacing a vanilla item id through AddToEquipIdTable (a risk to every mission).
            routes.Add("Item re-skin: not portable. V Framework 1.7 can't add a new item type or change the look of a game item.");
        foreach (var vp in VoicePackages.Find(mod))
            routes.Add($"Voice package ({vp.Fpk[(vp.Fpk.LastIndexOf('/') + 1)..]}, banks {string.Join(", ", vp.Banks)}): an outfit voice; " +
                       "the tool can give it to its outfits or to vanilla suits, with the voice mod installed alongside.");
        var banks = files.Where(f => f.Path.StartsWith("/Assets/tpp/sound/asset/", StringComparison.Ordinal) && f.Path.EndsWith(".sbp", StringComparison.Ordinal) && f.ReplacesVanilla).ToList();
        if (banks.Count > 0)
            routes.Add($"Vanilla sound bank replacer ({string.Join(", ", banks.Select(b => b.Path[(b.Path.LastIndexOf('/') + 1)..]))}): not portable as it is, because it replaces a vanilla file. " +
                       "A voice bank can only become an outfit voice inside a voice package of its own, which this tool does not build yet.");
        // Only new-path textures (no package, no script): a texture download another mod needs ("Main Textures").
        var retex = game != null && files.All(f => f.Kind == ModFileKind.Texture) ? BodyConverter.DetectRetexture(game, mod) : [];
        foreach (var r in retex)
            routes.Add($"Retexture of a vanilla suit ({r.VanillaPackage[(r.VanillaPackage.LastIndexOf('/') + 1)..]}): port as a new outfit; the vanilla package is copied and its textures swapped for the mod's.");
        if (retex.Count == 0 && files.Count > 0 && files.All(f => f.Kind == ModFileKind.Texture && !f.ReplacesVanilla))
            routes.Add("Texture download (only new textures): textures for another mod's model. Open that mod together with these textures.");
        // The avatar's own parts (the horn, the avatar face). These belong to the avatar the player builds in the avatar
        // creator, not to a player outfit, and V Framework 1.7 has no hook for them: RegisterOutfit/RegisterHeadOption
        // both act on the player's suit and head, never on /pack/player/avatar/. So there is nothing to port to.
        var avatarParts = files.Where(f => f.Kind == ModFileKind.AvatarPart).ToList();
        if (avatarParts.Count > 0)
            routes.Add($"Avatar part replacer ({string.Join(", ", avatarParts.Select(a => a.Path[(a.Path.LastIndexOf('/') + 1)..]).Distinct())}): " +
                       "not portable. These are the avatar's own parts (the horn, the avatar face), which the avatar creator " +
                       "uses, not a player outfit; V Framework has no hook for them. Install it as a plain mod instead.");
        if (routes.Count == 0 && files.Any(f => f.Kind == ModFileKind.VFrameworkModule))
            routes.Add("V Framework module script: a mod whose script calls V Framework opens for editing in place, not for a port.");
        if (routes.Count == 0) routes.Add("No route known for these files.");
        return new ModClassification(files, routes);
    }

    public static ModFileKind KindOf(string path) => KindOf(path, null);

    /// <summary>The kind of one file. With the mod, a body package whose only change is physics (.sim) is BodyPhysics.</summary>
    public static ModFileKind KindOf(string path, MgsvMod? mod)
    {
        if (path.Contains("/pack/mission2/", StringComparison.Ordinal) || path.Contains("/pack/location/", StringComparison.Ordinal)) return ModFileKind.MissionOrCutscene;
        if (path.Contains("/ui/texture/EquipIcon/", StringComparison.Ordinal)) return ModFileKind.Icon;
        if (path.StartsWith("/Assets/tpp/ui/", StringComparison.Ordinal) || path.Contains("/pack/ui/", StringComparison.Ordinal) && !path.Contains("/pack/ui/lang/", StringComparison.Ordinal)) return ModFileKind.Ui;
        if (System.Text.RegularExpressions.Regex.IsMatch(path, @"/pack/player/fova/plfova_sna[09]_face\d_v\d\d\.")) return ModFileKind.SnakeFace;
        if (path.Contains("/chara/cm_head/", StringComparison.Ordinal) || path.Contains("_head_z_", StringComparison.Ordinal)) return ModFileKind.DdHead;
        if (mod != null && path.Contains("/pack/player/parts/plparts_", StringComparison.Ordinal) && path.EndsWith(".fpkd", StringComparison.Ordinal)
            && mod.ArchiveFiles.TryGetValue(path, out var d) && !mod.ArchiveFiles.ContainsKey(path[..^1])
            && FilesOf(d) is { } inner && inner.All(f => f.Extension is "sim" or "clo"))
            return ModFileKind.BodyPhysics;
        if (path.StartsWith("/mod/zeta/", StringComparison.OrdinalIgnoreCase)) return ModFileKind.ZetaScript;
        if (path.StartsWith("/mod/modules/", StringComparison.OrdinalIgnoreCase)) return ModFileKind.VFrameworkModule;
        // The avatar's own parts (the horn, the face): /pack/player/avatar/<part>/plfova_avm_<part>_vNN. The owner's
        // "Avatar Horn Removal" mod replaces plfova_avm_hone_v00..02, which matched no route at all.
        if (path.Contains("/pack/player/avatar/", StringComparison.Ordinal)) return ModFileKind.AvatarPart;
        if (path.Contains("/pack/player/parts/plparts_", StringComparison.Ordinal)) return ModFileKind.Body;
        if (path.Contains("/pack/player/fova/", StringComparison.Ordinal)) return ModFileKind.Camo;
        if (path.Contains("/pack/buddy/", StringComparison.Ordinal)) return ModFileKind.Buddy;
        if (path.Contains("/pack/collectible/chimera/", StringComparison.Ordinal)) return ModFileKind.WeaponPackage;
        if (path.Contains("/pack/collectible/", StringComparison.Ordinal)) return ModFileKind.ItemPackage;
        if (path.Contains("/pack/ui/lang/", StringComparison.Ordinal)) return ModFileKind.Lang;
        if (path.Contains("/ui/texture/EquipIcon/", StringComparison.Ordinal)) return ModFileKind.Icon;
        if (path.Contains("/chara/sna/") && path.Contains("face", StringComparison.OrdinalIgnoreCase)) return ModFileKind.FaceOrHead;
        if (path.Contains(".ftex", StringComparison.Ordinal)) return ModFileKind.Texture;
        return ModFileKind.Other;
    }

    /// <summary>The files of a package, or null when it is damaged: a damaged package must not stop the whole mod's intake.</summary>
    private static List<Formats.PackageFile>? FilesOf(byte[] package)
    {
        try { return Formats.FoxPackage.Read(package).Files; } catch (InvalidDataException) { return null; }
    }
}
