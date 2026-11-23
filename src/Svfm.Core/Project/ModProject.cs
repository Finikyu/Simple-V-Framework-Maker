using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Mods;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>What kind of thing an item adds to the game.</summary>
public enum ItemKind { Outfit, ExtraLooks, HeadOption, Weapon, Item, BuddyOutfit }

/// <summary>
/// One thing in a project: an outfit, extra looks on a vanilla suit, a head option, a weapon or an item.
/// Its <see cref="Key"/> and <see cref="Prefix"/> are fixed when the item is created and saved with the
/// project, so rebuilding (or adding more items later) never changes them: V Framework keeps the IDs it
/// assigned under the key, and the prefix names every file the item adds.
/// </summary>
public abstract class ProjectItem
{
    /// <summary>Short ID, unique in the project (letters, digits, _).</summary>
    public required string Id { get; init; }
    /// <summary>V Framework registration key (outfits, heads, weapons); null for extra looks.</summary>
    public string? Key { get; init; }
    /// <summary>File-name prefix of everything the item adds, e.g. "svfm_tbo" or "svfm_pack_ninja".</summary>
    public required string Prefix { get; init; }
    public abstract ItemKind Kind { get; }
    /// <summary>Display name for lists and the module header.</summary>
    public string Title { get; set; } = "";
    /// <summary>Archive files (path → data) this item adds. Lang packages are built by the project, not here.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    /// <summary>Text: lang ID → language → text. IDs should start with the prefix.</summary>
    public Dictionary<string, Dictionary<string, string>> Text { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = [];

    // What the Edits (undo, Reset, Compare, the project file) read of an item. Each item kind states it once, here.
    /// <summary>The R&amp;D row; null for an item with no row. Setting it does nothing on an item with no row.</summary>
    public virtual DevelopRow? RdRow { get => null; set { } }
    /// <summary>The lang ID of the item's name; null when it has none (a head option with no R&amp;D row).</summary>
    public virtual string? NameLangId => null;
    /// <summary>The lang ID of the item's description.</summary>
    public virtual string? InfoLangId => null;
    /// <summary>The behaviour values of the item (the Stats tab and the like) for a snapshot; null when it has none.</summary>
    public virtual ProjectEdits.BehaviourState? TakeBehaviour() => null;
    /// <summary>Puts the values of <see cref="TakeBehaviour"/> back.</summary>
    public virtual void PutBehaviour(ProjectEdits.BehaviourState b) { }
    /// <summary>The R&amp;D row and, for an outfit or a head option, the wearers with their values, for a snapshot; null for an item with none of them.</summary>
    public virtual ProjectEdits.SpecState? TakeSpec() => RdRow is { } d ? new(d, []) : null;
    /// <summary>Puts the values of <see cref="TakeSpec"/> back. The state can come from a project file: see the item kinds for what an empty part of it means.</summary>
    public virtual void PutSpec(ProjectEdits.SpecState s) => RdRow = s.Develop;
    /// <summary>The path of the item's icon (an .ftex path with no extension); null for an item with no icon of its own.</summary>
    public virtual string? OwnIconPath { get => null; set { } }
    /// <summary>What the port check (PortCheck) found wrong: the build refuses the item.</summary>
    public List<string> Problems { get; } = [];
    /// <summary>The ID of the twin (see GLOSSARY.md, Twin); set at intake, null for an item with no twin.</summary>
    public string? TwinId { get; set; }
    /// <summary>Sync with the twin is on (both twins hold the same value).</summary>
    public bool Synced { get; set; }

    public void SetText(string langId, string english) => SetText(langId, "eng", english);

    public void SetText(string langId, string language, string text)
    {
        if (!Text.TryGetValue(langId, out var per)) Text[langId] = per = new(StringComparer.Ordinal);
        per[language] = text;
    }

    /// <summary>The item's calls for the module's LoadLibraries.</summary>
    public abstract string LuaBlock();
    /// <summary>Handlers the item adds to the module's Messages() (e.g. a support weapon's scripted effect).</summary>
    public List<MessageHandler> Messages { get; } = [];
    /// <summary>Every handler the item adds: <see cref="Messages"/>, and those it makes from its own data (a support weapon's script).</summary>
    public virtual IEnumerable<MessageHandler> AllMessages => Messages;
    /// <summary>The module announces every Throwing message it gets (a test build's diagnostics).</summary>
    public virtual bool DiagnoseMessages => false;
    /// <summary>Lua the item runs every frame in game (the module's Update), not indented.</summary>
    public List<string> Updates { get; } = [];
    /// <summary>Every per-frame Lua the item adds: <see cref="Updates"/>, and what it makes from its own data.</summary>
    public virtual IEnumerable<string> AllUpdates => Updates;
    /// <summary>Module-level Lua (functions) the item's handlers and updates call; written once per module.</summary>
    public virtual IEnumerable<string> ModuleHelpers => [];
    /// <summary>Lua the item runs when a mission starts (the module's Init), not indented.</summary>
    public List<string> Inits { get; } = [];

    /// <summary>Look selectors this item takes from the 138 shared by all custom outfits.</summary>
    public virtual int LookSelectors => 0;
    /// <summary>Custom parts types (suit slots) this item takes from the 223 available.</summary>
    public virtual int PartsTypes => 0;
}

public sealed class OutfitItem : ProjectItem
{
    public required OutfitSpec Spec { get; init; }
    /// <summary>The Environments that the port ticked from the mod's description (issue #89). Set at intake, not an edit.</summary>
    public List<string> CamoFromDescription { get; } = [];
    public override ItemKind Kind => ItemKind.Outfit;
    public override string? OwnIconPath { get => Spec.IconFtexPath; set => Spec.IconFtexPath = value!; }
    public override ProjectEdits.SpecState? TakeSpec() => new(Spec.Develop, new Dictionary<PlayerBranch, OutfitBranch>(Spec.Branches), null, Spec.AlsoUnder);
    public override void PutSpec(ProjectEdits.SpecState s)
    {
        Spec.Develop = s.Develop ?? Spec.Develop; Spec.Branches.Clear();
        foreach (var (k, v) in s.Branches) Spec.Branches[k] = v;
        if (s.Also != null) Spec.AlsoUnder = s.Also;
    }
    public override DevelopRow? RdRow { get => Spec.Develop; set => Spec.Develop = value!; }
    public override string? NameLangId => Spec.NameLang;
    public override string? InfoLangId => Spec.InfoLang;
    public override string LuaBlock() => OwnBlock() + string.Concat(Spec.AlsoUnder.Select(a => "\n" + LuaWriter.ExtendBlock(AlsoUnderSpec(a))));
    /// <summary>The outfit's own call; the build merges its bonus slots with those of other items (<see cref="BonusSlots"/>).</summary>
    public string OwnBlock() => Spec.VanillaOnly ? "" : LuaWriter.OutfitBlock(Spec);
    /// <summary>The ExtendVanillaOutfit that puts the chosen slots under one vanilla suit.</summary>
    public ExtendVanillaSpec AlsoUnderSpec(AlsoUnder a)
    {
        var e = new ExtendVanillaSpec { Outfit = a.Outfit };
        e.Header.Add($"{Title}: also shown under {a.Outfit}");
        foreach (var g in a.Slots.GroupBy(x => x.Who))
        {
            if (!Spec.Branches.TryGetValue(g.Key, out var br)) continue;
            var looks = new List<Look>();
            foreach (var sl in g)
            {
                var src = sl.Index < 0 ? br.Base : sl.Index < br.Variants.Count ? br.Variants[sl.Index] : null;
                if (src == null) continue;
                // #61 Q3: the slot has the abilities of the look it shows (its own, else the outfit's), or the vanilla suit's.
                // shortcut: an outfit with no abilities writes none, so the slot falls back to the camo's; write an empty set if that proves wrong in game.
                var ab = sl.SuitAbilities ? null : (sl.Index < 0 ? null : src.Abilities) ?? (br.Abilities.IsEmpty ? null : br.Abilities);
                bool noCamo = BonusSlots.NeedsNoCamo(a.Outfit, src);
                looks.Add(src with
                {
                    DisplayName = sl.LangId, EnableArm = null, EnableHead = null, Default = false, HeadOptions = [], Abilities = ab,
                    CamoFpk = noCamo ? BonusSlots.NoCamoFpk(Prefix) : src.CamoFpk, CamoFv2 = noCamo ? BonusSlots.NoCamoFv2(Prefix) : src.CamoFv2,
                });
            }
            // V Framework 1.7 reads enableArm of ExtendVanillaOutfit for the branch only (one value for the vanilla outfit), not per variant.
            if (looks.Count > 0) e.Branches[g.Key] = new VanillaBranch { Variants = looks, EnableArm = g.Any(x => x.EnableArm == false) ? false : null };
        }
        return e;
    }
    /// <summary>The empty camo package (<see cref="BonusSlots.NoCamoFiles"/>) when a bonus slot points at it, else none.</summary>
    public Dictionary<string, byte[]> BonusFiles() =>
        Spec.AlsoUnder.Select(AlsoUnderSpec).SelectMany(e => e.Branches.Values).SelectMany(b => b.Variants).Any(l => l.CamoFpk == BonusSlots.NoCamoFpk(Prefix))
            ? BonusSlots.NoCamoFiles(Prefix) : [];
    /// <summary>One selector for the outfit plus one per variant (the widest branch counts), plus the slots shown under other suits.</summary>
    public override int LookSelectors => (Spec.VanillaOnly ? 0 : 1 + (Spec.Branches.Count == 0 ? 0 : Spec.Branches.Values.Max(b => b.Variants.Count)))
        + Spec.AlsoUnder.Sum(a => a.Slots.GroupBy(x => x.Who).Select(g => g.Count()).DefaultIfEmpty(0).Max());
    public override int PartsTypes => Spec.VanillaOnly ? 0 : 1;
}

public sealed class ExtraLooksItem : ProjectItem
{
    public required ExtendVanillaSpec Spec { get; init; }
    public override ItemKind Kind => ItemKind.ExtraLooks;
    public override string LuaBlock() => LuaWriter.ExtendBlock(Spec);
    /// <summary>Each extra look takes a selector from the shared pool; the slot is shared by every character
    /// (V Framework OutfitRegistry: variantSelectorCodes per vanilla parts type, 29 at most).</summary>
    public override int LookSelectors => Spec.Branches.Count == 0 ? 0 : Spec.Branches.Values.Max(b => b.Variants.Count);
}

public sealed class HeadOptionItem : ProjectItem
{
    public required HeadOptionSpec Spec { get; init; }
    public override ItemKind Kind => ItemKind.HeadOption;
    public override string? OwnIconPath { get => Spec.IconFtexPath; set => Spec.IconFtexPath = value!; }
    public override ProjectEdits.SpecState? TakeSpec() => new(Spec.Develop, [], new Dictionary<PlayerBranch, HeadBranch>(Spec.Branches));
    public override void PutSpec(ProjectEdits.SpecState s)
    {
        Spec.Develop = s.Develop ?? Spec.Develop;
        if (s.Heads == null) return;
        Spec.Branches.Clear();
        foreach (var (k, v) in s.Heads) Spec.Branches[k] = v;
    }
    public override DevelopRow? RdRow { get => Spec.Develop; set => Spec.Develop = value!; }
    public override string? NameLangId => Spec.NameLang;
    public override string? InfoLangId => Spec.InfoLang;
    public override ProjectEdits.BehaviourState? TakeBehaviour() => new(InfiniteAmmo: Spec.InfiniteAmmo);
    public override void PutBehaviour(ProjectEdits.BehaviourState b) => Spec.InfiniteAmmo = b.InfiniteAmmo;
    public override string LuaBlock() => LuaWriter.HeadBlock(Spec);
}

/// <summary>A custom weapon assembled from parts (V Framework 1.7 Custom Weapons).</summary>
public sealed class WeaponItem : ProjectItem
{
    public required WeaponSpec Spec { get; init; }
    /// <summary>
    /// Paint textures of the game (default and camo-ready, by path code) that the mod replaces, each with the code of the
    /// port's copy of the mod's version (InstalledTextures). WeaponConverter.Repaint paints with the copies, so a camo
    /// uses the mod's own camo textures and a retextured part still takes a camo. Filled at import; a duplicate is made again.
    /// </summary>
    internal Dictionary<ulong, ulong> PaintCopies { get; } = [];
    /// <summary>
    /// The receiver model, its connect points (.fcnp) and its .parts as they were before the part models were merged in,
    /// by package path. WeaponConverter.Current starts from them when a part is swapped on the Stats tab. Filled at import.
    /// </summary>
    internal Dictionary<string, byte[]> Unmerged { get; } = [];
    /// <summary>The slots and parts of the last merge (WeaponConverter.MergeSignature); a build merges again when they differ.</summary>
    internal string MergedSlots { get; set; } = "";
    /// <summary>The port copies the mod's own weapon package (a Zeta EquipIdTable row), which is the whole gun: a build adds the package of a swapped part only. Filled at import.</summary>
    public bool OwnPackage { get; internal set; }
    public override ItemKind Kind => ItemKind.Weapon;
    public override string? OwnIconPath { get => Spec.IconFtexPath; set => Spec.IconFtexPath = value!; }
    public override DevelopRow? RdRow { get => Spec.Develop; set => Spec.Develop = value!; }
    public override string? NameLangId => Spec.NameLang;
    public override string? InfoLangId => Spec.InfoLang;
    public override ProjectEdits.BehaviourState? TakeBehaviour() => Spec.Tuning == null ? null : new(Weapon: Spec.Tuning.Snapshot());
    public override void PutBehaviour(ProjectEdits.BehaviourState b) { if (Spec.Tuning != null && b.Weapon != null) Spec.Tuning.Restore(b.Weapon); }
    public override string LuaBlock() => LuaWriter.WeaponBlock(Spec);
}

/// <summary>A buddy outfit: a Quiet costume, D-Horse tack or D-Dog gear.</summary>
public sealed class BuddyOutfitItem : ProjectItem
{
    public required BuddyOutfitSpec Spec { get; init; }
    public override ItemKind Kind => ItemKind.BuddyOutfit;
    public override string? OwnIconPath { get => Spec.IconFtexPath; set => Spec.IconFtexPath = value!; }
    public override DevelopRow? RdRow { get => Spec.Develop; set => Spec.Develop = value!; }
    public override string? NameLangId => Spec.NameLang;
    public override string? InfoLangId => Spec.InfoLang;
    public override ProjectEdits.BehaviourState? TakeBehaviour() => new(Bruise: Spec.Bruise, Dog: Spec.Dog, Horse: Spec.Horse);
    public override void PutBehaviour(ProjectEdits.BehaviourState b) { Spec.Bruise = b.Bruise; Spec.Dog = b.Dog; Spec.Horse = b.Horse; }
    public override string LuaBlock() => LuaWriter.BuddyBlock(Spec);
}

/// <summary>A custom support weapon (thrown item) with its own model.</summary>
public sealed class SupportWeaponItem : ProjectItem
{
    public required SupportWeaponSpec Spec { get; init; }
    public override ItemKind Kind => ItemKind.Weapon;
    public override string? OwnIconPath { get => Spec.IconFtexPath; set => Spec.IconFtexPath = value!; }
    public override DevelopRow? RdRow { get => Spec.Develop; set => Spec.Develop = value!; }
    public override string? NameLangId => Spec.NameLang;
    public override string? InfoLangId => Spec.InfoLang;
    public override ProjectEdits.BehaviourState? TakeBehaviour() => new(Ammo: Spec.Ammo, Fuse: Spec.FuseTime, Active: Spec.ActiveTime, Support: SupportSnapshot.Take(Spec));
    public override void PutBehaviour(ProjectEdits.BehaviourState b) { b.Support?.Restore(Spec); Spec.Ammo = b.Ammo; Spec.FuseTime = b.Fuse; Spec.ActiveTime = b.Active; }
    public override string LuaBlock() => LuaWriter.SupportWeaponBlock(Spec);
    /// <summary>The item's script, written for its current name (announcements use it).</summary>
    public override IEnumerable<MessageHandler> AllMessages => Messages.Concat(Spec.Script.Handlers(Spec.ScriptContext(Title)));
    public override bool DiagnoseMessages => Spec.Script.On.Values.Any(l => l.Any(a => a is AnnounceAction));
    /// <summary>The script's per-frame check (V Framework's Throwing messages did not reach modules in game, Oct 27).</summary>
    public override IEnumerable<string> AllUpdates => Spec.Script.Poller(Spec.ScriptContext(Title)) is { } poll ? Updates.Append(poll) : Updates;
    public override IEnumerable<string> ModuleHelpers => Spec.Script.IsEmpty ? [] : [ThrowScript.Helpers];
}

/// <summary>
/// A weapon or item, carried as its registration Lua (as in an existing V Framework mod, e.g. Lost
/// Weapons' RegisterConstantEquipId … AddToEquipDevelopTable). The weapon converter will fill these;
/// until then they come from opened V Framework mods. The Lua must be indented for LoadLibraries.
/// </summary>
public sealed class LuaItem : ProjectItem
{
    public required ItemKind ItemType { get; init; }
    public required string Lua { get; init; }
    public override ItemKind Kind => ItemType;
    public override string LuaBlock() => Lua.EndsWith('\n') ? Lua : Lua + "\n";
}

/// <summary>
/// A mod being made: several items built into one .mgsv, with one Lua module and one text file per
/// language (the Lost Weapons layout). Several projects can be installed side by side in SnakeBite and
/// DMM as long as their file names differ, which the per-project and per-item prefixes guarantee.
/// </summary>
/// <summary>A mod the player must install too: its name and author, and the textures it provides.</summary>
public sealed record RequiredMod(string Name, string Author, IReadOnlySet<ulong> Textures)
{
    public static RequiredMod From(Mods.MgsvMod mod) => new(mod.Metadata.Name, mod.Metadata.Author, TextureCheck.Provided(mod.ArchiveFiles));
}

public sealed class ModProject
{
    /// <summary>Short lower-case ID; names the module (SVFM_&lt;Id&gt;.lua) and the text files (svfm_&lt;id&gt;.lng2).</summary>
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string Version { get; set; } = "1.0";
    public string Author { get; set; } = "";
    public string Website { get; set; } = "";
    public string Description { get; set; } = "";
    public List<ProjectItem> Items { get; } = [];
    /// <summary>Copy ID → the ID of the first source of its Copy family (Duplicate). <see cref="ProjectEdits"/> keeps it; <see cref="CopyFamily"/> reads it.</summary>
    public Dictionary<string, string> CopyOf { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The order of the bonus slots under each vanilla camo (camo name to <see cref="BonusSlots.Key"/>s), set by Move in
    /// the Sortie mockup. Slots not listed follow, in project order. Replaced whole on each change (undo keeps a reference).
    /// </summary>
    public Dictionary<string, List<string>> BonusOrder { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Mods the player installs alongside this one (required dependencies, e.g. a texture download the port reads but
    /// does not copy). Their textures count as provided in the build checks, and the description says they are needed.
    /// </summary>
    public List<RequiredMod> Requires { get; } = [];
    /// <summary>
    /// When only part of the game is given: missing textures without a dictionary name become warnings instead of
    /// errors (they may be unnamed vanilla files in the archives not given). Off by default; no effect with the full game.
    /// </summary>
    public bool UnnamedTexturesUnverified { get; init; }

    /// <summary>Set when the project edits an existing V Framework mod (see <see cref="ExistingMod"/>): the build changes only what was edited.</summary>
    public ExistingModData? Existing { get; set; }

    public string ModulePath => $"/mod/modules/SVFM_{Naming.Pascal(Id)}.lua";
    public string TextFileBase => $"svfm_{Id}";

    /// <summary>The key and prefix a new item gets. The first item takes the project's own (so a one-item
    /// project looks like a plain port); later ones add their item ID.</summary>
    public (string key, string prefix) NewItemNames(string itemId)
    {
        Naming.CheckId(itemId);
        return Items.Count == 0 || itemId == Id
            ? ($"SVFM:{Naming.Pascal(Id)}", $"svfm_{Id}")
            : ($"SVFM:{Naming.Pascal(Id)}:{Naming.Pascal(itemId)}", $"svfm_{Id}_{itemId}");
    }
}

public static class Naming
{
    public static void CheckId(string id)
    {
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            throw new ArgumentException($"\"{id}\": IDs are lower-case letters, digits or _.");
    }

    public static string Pascal(string id) =>
        string.Concat(id.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));

    /// <summary>
    /// The path of a new model in Snake's Scenes folder. Issue #126: the game crashes when it sets up a head part whose
    /// model path is long (84 characters crashed in game, 58 loaded). A longer path gets a short hashed name.
    /// </summary>
    public static string SnaModel(string prefix, string suffix)
    {
        const string dir = "/Assets/tpp/chara/sna/Scenes/";
        string path = $"{dir}{prefix}_{suffix}.fmdl";
        return path.Length <= MaxModelPath ? path : $"{dir}svfm_{Hashing.FoxHash.StrCode32(prefix):x8}_{suffix}.fmdl";
    }

    /// <summary>The longest model path proven to load in game (#126).</summary>
    public const int MaxModelPath = 58;
}

/// <summary>Limits shared by every V Framework mod installed together.</summary>
public static class VFrameworkLimits
{
    public const int LookSelectors = 138;
    public const int CustomPartsTypes = 223;
    public const int VariantsPerVanillaPartsType = 29;
}

public sealed class ProjectBuildResult
{
    public required MgsvBuilder Mod { get; init; }
    public required string Lua { get; init; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public int LookSelectors { get; init; }
    public int PartsTypes { get; init; }
    public bool Ok => Errors.Count == 0;
}

public static class ProjectBuilder
{
    /// <summary>
    /// True when the build draws the infinity mark on the item's icon: a head option with infinite ammo whose icon file
    /// the project ships and no other item uses. A head of an existing V Framework mod gets it only when the edit turns
    /// infinite ammo on (issue #133): a head that had it already keeps its icon as the mod ships it.
    /// </summary>
    public static bool GetsInfinityMark(ModProject p, ProjectItem item) => InfinityIconOf(p, item) != null;

    /// <summary>The icon path (as the item's files spell it, no extension) that the build draws the infinity mark on, or null.</summary>
    public static string? InfinityIconOf(ModProject p, ProjectItem item)
    {
        string? icon = item switch
        {
            HeadOptionItem { Spec: { InfiniteAmmo: true, IconFtexPath: { } i } } => i,
            ExistingItem { Model: HeadOptionItem } x when x.Fields.Any(f => f.Label == "Infinite ammo" && f.Changed && f.Value is true) => IntakeResult.IconPathOf(x),
            _ => null,
        };
        if (icon == null || p.Items.Any(i => i != item && string.Equals(IntakeResult.IconPathOf(i), icon, StringComparison.OrdinalIgnoreCase))) return null;
        return item.Files.Keys.FirstOrDefault(k => k.Equals(icon + ".ftex", StringComparison.OrdinalIgnoreCase)) is { } key ? key[..^".ftex".Length] : null;
    }

    /// <param name="p">The project.</param>
    /// <param name="game">When given, checks that nothing but the shared language packages lands on a vanilla path.</param>
    public static ProjectBuildResult Build(ModProject p, GameData? game = null)
    {
        if (p.Existing != null) return ExistingMod.Build(p, game);
        Naming.CheckId(p.Id);
        // A part swapped on the Stats tab needs its package in the port's package (a custom weapon loads only its own).
        // The swapped part's model is merged into the receiver model (the game draws only that model).
        // Then the camo chosen on the Stats tab is painted into the models (the swapped parts included).
        if (game != null) foreach (var w in p.Items.OfType<WeaponItem>().Where(w => w.Spec.Tuning != null)) Conversion.WeaponConverter.MakeCurrent(game, w);
        // New entries in the resident common package, merged by SnakeBite; the vanilla entries stay (SupportWeaponConverter.AddTo).
        static bool IsCommonPart(string path) => path is Conversion.SupportWeaponConverter.CommonPackage + ".fpk" or Conversion.SupportWeaponConverter.CommonPackage + ".fpkd";
        var errors = new List<string>();
        var warnings = new List<string>();

        // Unique IDs, keys, prefixes and files across items.
        foreach (var g in p.Items.GroupBy(i => i.Id).Where(g => g.Count() > 1)) errors.Add($"Two items have the ID \"{g.Key}\".");
        foreach (var g in p.Items.Where(i => i.Key != null).GroupBy(i => i.Key).Where(g => g.Count() > 1)) errors.Add($"Two items have the key \"{g.Key}\".");
        foreach (var g in p.Items.GroupBy(i => i.Prefix).Where(g => g.Count() > 1)) errors.Add($"Two items have the file prefix \"{g.Key}\".");
        errors.AddRange(CopyFamily.Errors(p));
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in p.Items)
            foreach (var path in item.Files.Keys.Where(k => !IsCommonPart(k)))
            {
                // Items may share a file (textures from one mod used by several outfits) when it is the same file.
                if (owner.TryGetValue(path, out var other) && other != item.Id
                    && !p.Items.First(i => i.Id == other).Files[path].AsSpan().SequenceEqual(item.Files[path]))
                    errors.Add($"{path} is in both \"{other}\" and \"{item.Id}\" with different contents.");
                owner[path] = item.Id;
                if (path.StartsWith("/Assets/tpp/pack/ui/lang/", StringComparison.Ordinal))
                    errors.Add($"\"{item.Id}\": {path} — language files are built by the project from the items' text.");
                else if (game != null && game.Contains(path))
                    errors.Add($"\"{item.Id}\": {path} would overwrite a vanilla file.");
                // The path list stands in for the game only when not all of the game is open. It also names files of other
                // Fox Engine games (Metal Gear Survive /Assets/ssd/, P.T. /Assets/sh/, MGO /Assets/mgo/), which TPP does not
                // have: Retali8 Quiet Suits ships Survive textures at their Survive paths.
                else if (game?.IsComplete != true && !path.StartsWith("/Assets/ssd/", StringComparison.Ordinal) && !path.StartsWith("/Assets/sh/", StringComparison.Ordinal)
                    && !path.StartsWith("/Assets/mgo/", StringComparison.Ordinal) && Archives.PathDictionary.Bundled.TryGetPath(Hashing.FoxHash.PathCode64(path)) == path)
                    errors.Add($"\"{item.Id}\": {path} is a vanilla game path (bundled path list); it would overwrite a vanilla file.");
            }

        // Text: one file per language for the whole project.
        var text = new ModText { FileBase = p.TextFileBase };
        foreach (var item in p.Items)
            foreach (var (id, per) in item.Text)
            {
                if (text.Texts.ContainsKey(id)) errors.Add($"Text ID {id} is used by two items.");
                foreach (var (lang, t) in per) text.Set(id, lang, t);
            }

        // Limits.
        int selectors = p.Items.Sum(i => i.LookSelectors), parts = p.Items.Sum(i => i.PartsTypes);
        if (selectors > VFrameworkLimits.LookSelectors)
            errors.Add($"The items need {selectors} look selectors; V Framework has {VFrameworkLimits.LookSelectors} for all custom outfits together.");
        else if (selectors > VFrameworkLimits.LookSelectors / 2)
            warnings.Add($"{selectors} of the {VFrameworkLimits.LookSelectors} look selectors shared by all installed V Framework outfits.");
        var bonus = BonusSlots.Merge(p.Items, warnings, errors, p.BonusOrder);

        foreach (var o in p.Items.OfType<OutfitItem>().Where(o => o.Spec.VanillaOnly))
            warnings.Add($"\"{o.Id}\": the outfit has no slot of its own; it is written only as looks under vanilla suits, with no suit cell and no R&D row.");
        foreach (var x in p.Items.OfType<ExtraLooksItem>().Where(x => x.Spec.Branches.Values.Any(b => b.Variants.Count > 0)))
            // Why no head options: heads are matched to the suit's own camo, not the look's selector. The missing name was
            // seen in game on Oct 23.
            warnings.Add($"\"{x.Id}\": looks added to a vanilla suit have no Heads worn, no icon of their own, and no " +
                "name in the Sortie Prep UNIFORMS box.");

        // Heads on a look whose head is hidden would sit on a body with a built-in or covered head.
        foreach (var o in p.Items.OfType<OutfitItem>())
            foreach (var (who, br) in o.Spec.Branches)
            {
                bool bad = (!br.ShowsHead(br.Base) && (br.HeadOptions.Count > 0 || br.Base.HeadOptions.Count > 0))
                           || br.Variants.Any(v => !br.ShowsHead(v) && v.HeadOptions.Count > 0);
                if (bad) warnings.Add($"\"{o.Id}\" ({LuaWriter.BranchName(who)}): Heads worn lists heads on a look with the head hidden (enableHead = false).");
                // V Framework 1.7 reads look abilities only on variants (ReadVariantsArrayInto), so the main look uses the outfit's.
                if (br.Base.Abilities is { } ba && ba != br.Abilities)
                    warnings.Add($"\"{o.Id}\" ({LuaWriter.BranchName(who)}): the first look has abilities of its own; the first look always uses the outfit's abilities, so its own are not written.");
            }

        // V Framework 1.7 lists vanilla heads without checking development (see VanillaHeads).
        foreach (var o in p.Items.OfType<OutfitItem>())
        {
            var listed = o.Spec.Branches.Values.SelectMany(b => b.HeadOptions.Concat(new[] { b.Base }.Concat(b.Variants).SelectMany(l => l.HeadOptions)))
                .Where(VanillaHeads.DevelopIds.ContainsKey).Distinct().ToList();
            if (listed.Count > 0)
                warnings.Add($"\"{o.Id}\": {string.Join(", ", listed)} can be worn with this outfit even before it is developed (V Framework 1.7 doesn't check development for vanilla heads).");
        }

        // Avatar reads Snake's branch of a custom outfit (V Framework 1.7 GetPTData), so heads built on Snake's own face
        // would put Snake's face on Avatar: such outfits get an Avatar branch without them. An outfit's own Avatar branch
        // (headOptions are per branch in 1.7) loses them too (issue #65).
        var snakeFace = p.Items.OfType<HeadOptionItem>().Where(h => h.Spec.AvatarWouldWearSnakeFace).Select(h => h.Spec.Key).ToHashSet(StringComparer.Ordinal);
        if (snakeFace.Count > 0)
            foreach (var o in p.Items.OfType<OutfitItem>())
            {
                bool own = o.Spec.Branches.TryGetValue(PlayerBranch.Avatar, out var src);
                if (!own && !o.Spec.Branches.TryGetValue(PlayerBranch.Snake, out src)) continue;
                var dropped = src!.HeadOptions.Concat(src.Base.HeadOptions).Concat(src.Variants.SelectMany(v => v.HeadOptions)).Where(snakeFace.Contains).Distinct().ToList();
                if (dropped.Count == 0) continue;
                o.Spec.Branches[PlayerBranch.Avatar] = PlayerBranches.AvatarFrom(src, snakeFace);
                warnings.Add($"\"{o.Id}\": Avatar" + (own ? "'s branch leaves out" : " gets his own branch without") + $" the heads built on Snake's face ({string.Join(", ", dropped)}).");
            }

        // A custom head is offered only to the characters it was made for: it needs a branch for that character (Avatar may
        // wear a Snake head unless it is built on Snake's own face). Tested Oct 25: male DD heads don't sit on DD women.
        var customHeads = p.Items.OfType<HeadOptionItem>().ToDictionary(h => h.Spec.Key, StringComparer.Ordinal);
        static bool Fits(HeadOptionSpec h, PlayerBranch who) => ModHeads.Fits(h, who);
        void CheckHeads(string itemId, PlayerBranch who, IEnumerable<string> keys)
        {
            foreach (var k in keys.Distinct())
                if (customHeads.TryGetValue(k, out var h) && !Fits(h.Spec, who))
                    errors.Add($"\"{itemId}\" ({LuaWriter.BranchName(who)}): head \"{h.Title}\" was not made for {LuaWriter.BranchName(who)}" +
                        (h.Spec.AvatarWouldWearSnakeFace && who == PlayerBranch.Avatar ? " (it is built on Snake's face)." : " (its mod has no model for that character)."));
        }
        foreach (var o in p.Items.OfType<OutfitItem>())
            foreach (var (who, br) in o.Spec.Branches)
                CheckHeads(o.Id, who, br.HeadOptions.Concat(br.Base.HeadOptions).Concat(br.Variants.SelectMany(v => v.HeadOptions)));
        foreach (var x in p.Items.OfType<ExtraLooksItem>())
            foreach (var (who, vb) in x.Spec.Branches)
                CheckHeads(x.Id, who, vb.HeadOptions.Concat(vb.Variants.SelectMany(v => v.HeadOptions)));

        var header = new List<string> { $"{p.Name} {p.Version}" + (p.Author.Length > 0 ? $" by {p.Author}" : "") };
        header.AddRange(p.Items.Select(i => $"  {i.Kind}: {(i.Title.Length > 0 ? i.Title : i.Id)}" + (i.Key != null ? $" [{i.Key}]" : "")));
        // Every item kind with an R&D row can follow a vanilla row (issue #98). An existing mod's script keeps its own table;
        // a head with no name has no row.
        var follow = p.Items.Where(i => i is not ExistingItem && i.Key != null && i.NameLangId != null && i.RdRow?.FollowsVanillaDevelopId != null)
            .ToDictionary(i => i.Key!, i => i.RdRow!.FollowsVanillaDevelopId!.Value);
        // Outfits first, then one call for each vanilla camo with the bonus slots of every item.
        var blocks = p.Items.Where(i => i is not ExtraLooksItem { Spec.Outfit: not null }).Select(i => i is OutfitItem o ? o.OwnBlock() : i.LuaBlock())
            .Where(b => b.Length > 0).Concat(bonus.Select(LuaWriter.ExtendBlock)).ToList();
        string lua = LuaWriter.Module(header, blocks, follow,
            p.Items.SelectMany(i => i.AllMessages).ToList(), p.Items.SelectMany(i => i.AllUpdates).ToList(), p.Items.SelectMany(i => i.Inits).ToList(),
            p.Items.Any(i => i.DiagnoseMessages), p.Items.SelectMany(i => i.ModuleHelpers).ToList());

        string description = p.Description;
        if (p.Requires.Count > 0)
        {
            string needs = string.Join(", ", p.Requires.Select(r => $"\"{r.Name}\"" + (r.Author.Length > 0 ? $" by {r.Author}" : "")));
            description = (description.Length > 0 ? description + "\n" : "") + $"Requires {needs}: install it as well.";
            warnings.Add($"The build needs {needs} installed with it (its files are not copied).");
        }
        var b = new MgsvBuilder { Name = p.Name, Version = p.Version, Author = p.Author, Website = p.Website, Description = description };
        // A head option with infinite ammo shows the infinity mark of the vanilla Infinity Bandana on its icon, in the same spot.
        var mark = game != null && p.Items.OfType<HeadOptionItem>().Any(h => h.Spec.InfiniteAmmo) ? Rendering.HeadIcon.InfinityMark(game) : null;
        foreach (var item in p.Items)
        {
            var files = item.Files;
            if (mark != null && InfinityIconOf(p, item) is { } icon) files = Rendering.HeadIcon.WithInfinityMark(files, icon, mark);
            // The Avatar package of a bandana that waits for development is made from the game files (GatedBandanas).
            if (item is HeadOptionItem gh && GatedBandanas.IsGated(gh) && gh.Spec.Branches.ContainsKey(PlayerBranch.Avatar))
            {
                if (game != null) foreach (var (path, data) in GatedBandanas.AvatarFiles(game, gh, warnings)) b.AddArchiveFile(path, data);
                else if (!gh.Files.ContainsKey(gh.Spec.Branches[PlayerBranch.Avatar].Fpk ?? ""))
                    warnings.Add($"\"{gh.Title}\": no game files, so the Avatar's bandana package is not built.");
            }
            foreach (var (path, data) in files.Where(f => !IsCommonPart(f.Key))) b.AddArchiveFile(path, data);
        }
        // Partial col_common_tpp packages (thrown items' models): one package with the new entries of every item.
        foreach (var g in p.Items.SelectMany(i => i.Files).Where(f => IsCommonPart(f.Key)).GroupBy(f => f.Key))
        {
            var merged = Formats.FoxPackage.Read(g.First().Value);
            foreach (var f in g.Skip(1).SelectMany(f => Formats.FoxPackage.Read(f.Value).Files))
                if (merged.Find(f.Path) == null) merged.Files.Add(f);
                else if (!merged.Find(f.Path)!.Data.AsSpan().SequenceEqual(f.Data)) errors.Add($"{f.Path} is in two items' {g.Key} with different contents.");
            b.AddArchiveFile(g.Key, merged.Write());
        }
        foreach (var o in p.Items.OfType<OutfitItem>()) foreach (var (path, data) in o.BonusFiles()) b.AddArchiveFile(path, data);
        foreach (var o in p.Items.OfType<OutfitItem>()) errors.AddRange(SprintSpark.Pack(b, $"\"{o.Id}\"", o.Spec.Branches.Values, game));
        if (text.Texts.Count > 0) foreach (var (path, data) in text.BuildPackages()) b.AddArchiveFile(path, data);
        b.AddGameFile(p.ModulePath, System.Text.Encoding.UTF8.GetBytes(lua));
        foreach (var issue in b.Check()) errors.Add($"{issue.Path}: {issue.Message}");

        foreach (var item in p.Items) errors.AddRange(item.Problems.Select(x => $"\"{item.Id}\": {x}"));
        // A support weapon's script as it is now (it can be edited after the port).
        foreach (var sw in p.Items.OfType<SupportWeaponItem>()) errors.AddRange(sw.Spec.Script.Problems(sw.Spec.ScriptContext(sw.Title)).Select(x => $"\"{sw.Id}\": {x}"));

        // Every texture the models use must come with the build or be in the game; a missing one flickers in game.
        var allFiles = p.Items.SelectMany(i => i.Files).ToList();
        var provided = TextureCheck.Provided(allFiles);
        foreach (var rq in p.Requires) provided.UnionWith(rq.Textures);
        foreach (var item in p.Items)
        {
            var missing = TextureCheck.Find(item.Files.ToList(), game, alsoProvided: provided);
            // With only part of the game given (tests), an unnamed texture (no dictionary name) can't be told from a vanilla
            // file in the archives that weren't given. Missing stays an error (a texture mod's new paths are unnamed too:
            // Tactical Outfit's Main Textures); UnnamedTexturesUnverified makes them warnings. The full game checks every one.
            var unverifiable = game == null || game.IsComplete || !p.UnnamedTexturesUnverified ? []
                : missing.Where(m => PathDictionary.Bundled.TryGetPath(m.PathCode) == null).ToList();
            missing = missing.Except(unverifiable).ToList();
            // Only a missing base-colour map is an error (the model shows other textures there); missing normal, specular
            // or detail maps change the shading only, as in the original mod.
            var shading = missing.Where(m => !m.Base).ToList();
            missing = missing.Where(m => m.Base).ToList();
            if (missing.Count > 0) errors.Add($"\"{item.Id}\": " + TextureCheck.Describe(missing));
            if (shading.Count > 0)
                warnings.Add($"\"{item.Id}\": " + TextureCheck.Describe(shading, shadingOnly: true));
            if (unverifiable.Count > 0)
                warnings.Add($"\"{item.Id}\": {unverifiable.Count} unnamed texture(s) not found in the game data given ({string.Join(", ", unverifiable.Take(4).Select(m => m.PathCode.ToString("x16")))}{(unverifiable.Count > 4 ? ", …" : "")}); with only part of the game this can't be checked. Open the full game to check them.");
        }

        // An icon path the script names needs a file, from this build or the game: a missing icon shows another item's icon in game.
        foreach (var item in p.Items)
            if (IntakeResult.IconPathOf(item) is { Length: > 0 } icon && !allFiles.Any(f => f.Key.Equals(icon + ".ftex", StringComparison.OrdinalIgnoreCase)) && game?.Contains(icon + ".ftex") != true)
            {
                string missingIcon = $"\"{item.Id}\": the script names the icon {icon}, and the build has no file for it.";
                if (game is { IsComplete: true }) errors.Add(missingIcon + " Generate or upload an icon for this item.");
                else warnings.Add(missingIcon + " With only part of the game this can't be checked. Open the full game to check it.");
            }

        var r = new ProjectBuildResult { Mod = b, Lua = lua, LookSelectors = selectors, PartsTypes = parts };
        r.Errors.AddRange(errors);
        r.Warnings.AddRange(warnings);
        foreach (var item in p.Items) r.Warnings.AddRange(item.Notes.Select(n => $"{item.Id}: {n}"));
        return r;
    }
}
