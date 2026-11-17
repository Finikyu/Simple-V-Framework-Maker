using System.Text;
using System.Text.RegularExpressions;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>What kind of value an editable setting of an existing mod holds.</summary>
public enum FieldType { Bool, Number, Text, Flag }

/// <summary>
/// One setting of an existing mod's script that the tool can change (an ability, a part of the R&amp;D row), and where it
/// is written in the script. A field the script doesn't have yet is added when it is set.
/// </summary>
public sealed class ExistingField
{
    public required string Group { get; init; }
    public required string Label { get; init; }
    public required FieldType Type { get; init; }
    /// <summary>The value the script gives it (bool, double or string); the default when the script leaves it out.</summary>
    public required object Original { get; init; }
    /// <summary>The value now (same type as <see cref="Original"/>).</summary>
    public object Value { get; set; } = null!;
    /// <summary>False when the script doesn't set it at all.</summary>
    public bool Present { get; init; }
    public required string Module { get; init; }
    public required string CallPath { get; init; }
    /// <summary>Which written call of <see cref="CallPath"/> in the module holds it (an index into <see cref="LuaSource.FindCalls"/>).</summary>
    public required int Site { get; init; }
    public required int ArgIndex { get; init; }
    public required string[] Path { get; init; }
    /// <summary>A Flag the script writes as true/false rather than 1/0.</summary>
    public bool WrittenAsBool { get; init; }
    /// <summary>Why the tool can't change this one in this script (null = it can).</summary>
    public string? Locked { get; init; }
    /// <summary>
    /// A value the script gives by a name (a local, a parameter) is replaced by the new literal at this place. The sandbox
    /// gives the value the name stands for, so the field shows it; the name and its other uses are left as they are.
    /// </summary>
    public bool OverName { get; init; }
    public bool Changed => !Equals(Value, Original);

    /// <summary>The value as Lua source text.</summary>
    public object ForLua => Value is LuaSource.Code ? Value : Type switch
    {
        // A switch with text is a dash attack by its attack name (Abilities dashAttack key).
        FieldType.Bool when Value is string atk => new LuaSource.Code(LuaWriter.DashAttackLua(atk)),
        _ when Value is string => Value,
        FieldType.Flag => WrittenAsBool ? (bool)Value : (object)((bool)Value ? 1 : 0),
        FieldType.Number => (double)Value,
        _ => Value,
    };
}

/// <summary>A vanilla suit that an ExtendVanillaOutfit call of the script shows an outfit's looks under, and the item that holds the call.</summary>
public sealed record AlsoInScript(string Outfit, string Holder, IReadOnlyList<AlsoSlot> Slots);

/// <summary>
/// A thing an existing V Framework mod adds, as its script registers it: an outfit, a head option, looks on a vanilla suit,
/// a buddy costume or a weapon/item. The script, the files and every call the tool doesn't understand stay as they are;
/// only the text (names and descriptions) and the settings listed in <see cref="Fields"/> can be changed.
/// </summary>
public sealed class ExistingItem : ProjectItem
{
    public required ItemKind ItemType { get; init; }
    public override ItemKind Kind => ItemType;
    // An item whose script gives it no R&D row keeps none.
    public override DevelopRow? RdRow { get => Develop; set { if (Develop != null) Develop = value; } }
    public override string? NameLangId => NameId;
    public override string? InfoLangId => InfoId;
    public override ProjectEdits.BehaviourState? TakeBehaviour() => Script is { } sc ? new(Script: sc.ToJson()) : null;
    public override void PutBehaviour(ProjectEdits.BehaviourState b) { if (Script is { } es && b.Script != null) es.CopyFrom(ThrowScript.FromJson(b.Script)); }
    // The script's own path is not changed: the build writes this one in its place (null when it is the same).
    public override string? OwnIconPath { get => IconPathWritten ?? IntakeResult.ShippedIconOf(this); set => IconPathWritten = IconPath is { } p && ExistingMod.IconStem(p) == value ? null : value; }
    public override ProjectEdits.SpecState? TakeSpec() => Model switch
    {
        OutfitItem o => new(Develop ?? new DevelopRow(), new Dictionary<PlayerBranch, OutfitBranch>(o.Spec.Branches), null, o.Spec.AlsoUnder),
        HeadOptionItem h => new(Develop ?? new DevelopRow(), [], new Dictionary<PlayerBranch, HeadBranch>(h.Spec.Branches)),
        _ => base.TakeSpec(),
    };
    public override void PutSpec(ProjectEdits.SpecState s)
    {
        RdRow = s.Develop;
        switch (Model)
        {
            // No heads is a saved project: keep what the script has, less the characters the file takes out (HeadOff).
            case HeadOptionItem h when s.Heads != null:
                h.Spec.Branches.Clear();
                foreach (var (k, v) in s.Heads) h.Spec.Branches[k] = v;
                break;
            case OutfitItem o:
                // An empty list is a saved project without branches: keep what the script has.
                if (s.Branches.Count > 0) { o.Spec.Branches.Clear(); foreach (var (k, v) in s.Branches) o.Spec.Branches[k] = v; }
                if (s.Also != null) o.Spec.AlsoUnder = s.Also;
                break;
        }
    }
    public string? NameId { get; init; }
    public string? RealNameId { get; init; }
    public string? InfoId { get; init; }
    /// <summary>Other text this item's script names (look names, power-up lines): a label and the text ID.</summary>
    public List<(string Label, string Id)> OtherText { get; } = [];
    public List<ExistingField> Fields { get; } = [];
    /// <summary>The groups of <see cref="Fields"/> that the item's own tab shows, in this order: a weapon's gun and parts, or a
    /// buddy costume's behaviour. The Settings tab leaves them out.</summary>
    public List<string> StatGroups { get; } = [];
    /// <summary>The values a weapon's gun and part calls set by a name (a part, a damage row, an ammo kind): shown, not changed.</summary>
    public List<(string Group, string Label, string Value)> PartNames { get; } = [];
    /// <summary>The equip constant of the item's R&amp;D row (TppEquip.EQP_WP_x), or null. It leads to a weapon's gun and parts.</summary>
    public string? EquipId { get; internal set; }
    /// <summary>The two sight slots of a weapon's SetGunBasic, as the script writes them. Null when the script leaves one out.</summary>
    public string? Scope1Id { get; internal set; }
    public string? Scope2Id { get; internal set; }
    /// <summary>The mod's own SetSight values for those slots, by id without the table.</summary>
    public Dictionary<string, PartValues> SightRows { get; } = new(StringComparer.Ordinal);
    /// <summary>The sight line of the Stats tab, with the edits made to the first sight's values.</summary>
    public string SightText(EquipTables? vanilla)
    {
        var rows = SightRows.ToDictionary(r => r.Key, r => r.Value.Copy());
        if (Scope1Id != null && rows.TryGetValue(EquipTables.Strip(Scope1Id), out var row))
            foreach (var f in Fields.Where(f => f.Group == "Sight" || f.Group.StartsWith("Sight · ", StringComparison.Ordinal)))
                if (WeaponPartFields.Sight.FirstOrDefault(p => p.Label == f.Label) is { } part)
                    row.Values[part.Path] = f.Value switch { double d => new LuaValue.Number(d), bool b => new LuaValue.Number(b ? 1 : 0), _ => new LuaValue.Text(f.Value?.ToString() ?? "") };
        return WeaponSight.Describe(Scope1Id, Scope2Id, vanilla, rows);
    }
    /// <summary>Whether the item is a thrown or placed item (support weapon calls), not a gun: its Stats tab shows those calls.</summary>
    public bool Support { get; internal set; }
    /// <summary>
    /// What a thrown item does ("What it does"), read from the mod's Throwing handlers (<see cref="ThrowScriptReader"/>).
    /// Null for other items. A changed script is built as a new module, and the mod's own blocks for the item are switched off.
    /// </summary>
    public ThrowScript? Script { get; internal set; }
    /// <summary>The script as the mod has it (<see cref="ThrowScript.ToJson"/>).</summary>
    public string? ScriptOriginal { get; internal set; }
    /// <summary>Why the script can't be changed, or null.</summary>
    public string? ScriptLock { get; internal set; }
    /// <summary>What the item is thrown as (EQP_SWP_...), or null.</summary>
    public string? ThrowAs { get; internal set; }
    private ThrowContext? _scriptContext;
    /// <summary>What the script's steps need from the item (its name as it is now), or null for an item that is not thrown.</summary>
    public ThrowContext? ScriptContext { get => _scriptContext is { } c ? c with { Title = Title } : null; internal set => _scriptContext = value; }
    /// <summary>
    /// The item's R&amp;D row, read from the script, as the tool's own <see cref="DevelopRow"/> (the R&amp;D and Deployment
    /// tabs edit it). Null when the script gives the item no row. A change is written back by splice, key by key, through
    /// <see cref="DevelopFields"/>.
    /// </summary>
    public DevelopRow? Develop { get; set; }
    /// <summary>The R&amp;D row as the script has it.</summary>
    public DevelopRow? DevelopOriginal { get; internal set; }
    /// <summary>
    /// Where each key of the R&amp;D row is written in the script (the label is the key's alias, e.g. "grade"). Kept out of
    /// <see cref="Fields"/>, so the Settings tab does not show the R&amp;D row a second time.
    /// </summary>
    public List<ExistingField> DevelopFields { get; } = [];
    /// <summary>The icon's path in the mod (as the script writes it), or null.</summary>
    public string? IconPath { get; init; }
    /// <summary>
    /// The icon path (without extension) the build writes into the script in place of <see cref="IconPath"/>: a game
    /// icon, or the item's own new icon file when the mod does not ship one. Null: the script keeps its icon path.
    /// </summary>
    public string? IconPathWritten { get; internal set; }
    /// <summary>Each place in the item's calls where the script writes <see cref="IconPath"/> (the R&amp;D row, the looks).</summary>
    internal List<ExistingField> IconFields { get; } = [];
    /// <summary>The module that registers it.</summary>
    public string Module { get; init; } = "";
    /// <summary>
    /// When one written call registers it several times (a loop, or a function the script calls again): what each time
    /// registers, e.g. the vanilla suits that get the looks. Empty when the call is made once. A setting edits the one
    /// written call, so it changes every instance.
    /// </summary>
    public List<string> Instances { get; } = [];
    /// <summary>
    /// The item as the tool's own kind (an <see cref="OutfitItem"/>, <see cref="HeadOptionItem"/>, <see cref="WeaponItem"/>
    /// or <see cref="BuddyOutfitItem"/>), read from its script, for the 3D preview. It shares the item's files and is never
    /// built itself. For an outfit, the Abilities and Camo concealment tabs edit its branches, and a change is written back
    /// by splice through <see cref="SpecFields"/>. Null when the script names no model the tool can find.
    /// </summary>
    public ProjectItem? Model { get; internal set; }
    /// <summary>The branches of <see cref="Model"/> as the script has them. Null when <see cref="Model"/> is not an outfit.</summary>
    public Dictionary<PlayerBranch, OutfitBranch>? OutfitOriginal { get; internal set; }
    /// <summary>
    /// Where each outfit setting of <see cref="Model"/> (abilities, camo) is written in the script. The label is the key;
    /// the path starts at the branch, e.g. snake.abilities.silentFootsteps. Kept out of <see cref="Fields"/>, so the
    /// Settings tab does not show them a second time.
    /// </summary>
    public List<ExistingField> SpecFields { get; } = [];
    /// <summary>The branches of an outfit model that are looks only (looks on a vanilla suit): the script gives them no look of their own, so all their looks are in variants.</summary>
    public HashSet<PlayerBranch> LooksOnly { get; } = [];
    /// <summary>The branches of a head option model as the script has them. Null when <see cref="Model"/> is not a head option.</summary>
    public Dictionary<PlayerBranch, HeadBranch>? HeadOriginal { get; internal set; }
    /// <summary>
    /// The vanilla suits the script already shows this outfit's looks under: each ExtendVanillaOutfit call of the mod whose
    /// looks have the same model files as a look of this outfit. Read only; such a call is its own item (Holder), and its
    /// looks are edited there. What the user adds in "Also under other outfits" is in the model's AlsoUnder.
    /// </summary>
    public List<AlsoInScript> AlsoInScript { get; } = [];
    /// <summary>The written call of the item (module, call, site), and why its tables can't be replaced (null: they can).</summary>
    internal (string module, string callPath, int site, string? locked) Call { get; set; }
    /// <summary>The recorded calls of the item: what the script registers each time it makes the call.</summary>
    internal List<RecordedCall> Calls { get; set; } = [];
    /// <summary>The recorded R&amp;D row calls of the item (AddToEquipDevelopTable), one per time the script makes the row.</summary>
    internal List<RecordedCall> Rows { get; set; } = [];
    /// <summary>Makes the field for a key of the outfit call that the script did not have when the mod opened.</summary>
    internal Func<string[], FieldType, ExistingField>? FieldAt { get; set; }
    /// <summary>Whether the loose textures of <see cref="Model"/> are added (ExistingMod.LoadPreviewTextures).</summary>
    internal bool TexturesLoaded { get; set; }
    public override string LuaBlock() => "";
}

/// <summary>The opened mod behind a project that edits an existing V Framework mod.</summary>
public sealed partial class ExistingModData
{
    public required MgsvMod Source { get; init; }
    /// <summary>Module path → the script's text as the mod has it.</summary>
    public Dictionary<string, string> Modules { get; } = new(StringComparer.Ordinal);
    /// <summary>Calls to V Framework the tool doesn't turn into items (kept untouched): path → how many.</summary>
    public Dictionary<string, int> OtherCalls { get; } = new(StringComparer.Ordinal);
    /// <summary>Why a module could not be read completely.</summary>
    public List<string> Notes { get; } = [];
    /// <summary>Every item the mod had when it was opened. One that the project no longer has was removed, and the build takes its calls out of the script.</summary>
    public List<ExistingItem> Opened { get; } = [];
}

/// <summary>Opens an existing V Framework mod for editing, and builds it back with only the edited text and settings changed.</summary>
public static partial class ExistingMod
{
    private static readonly (string field, string label)[] BranchNames =
    [
        ("snake", "Snake / Avatar"), ("avatar", "Avatar"), ("ddMale", "DD male"), ("ddFemale", "DD female"), ("ocelot", "Ocelot"), ("quiet", "Quiet"),
    ];

    private const string Develop = "V_TppMotherBaseManagement.AddToEquipDevelopTable";

    /// <summary>The language of a language package path (.../lang_default_data_eng.fpk → "eng").</summary>
    private static string? PackageLanguage(string path)
    {
        var m = Regex.Match(path, @"lang_default_data_(\w+)\.fpk$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private sealed class LangSlot(string package, string inner, LangFile file)
    {
        public string Package { get; } = package;
        public string Inner { get; } = inner;
        public LangFile File { get; } = file;
        public bool Dirty { get; set; }
        /// <summary>The file's name without the language and extension (svfm_x for svfm_x.eng.lng2).</summary>
        public string Base => Regex.Replace(System.IO.Path.GetFileName(Inner), @"\.\w+\.lng2$", "");
    }

    private static Dictionary<string, List<LangSlot>> ReadLanguages(MgsvMod mod)
    {
        var all = new Dictionary<string, List<LangSlot>>(StringComparer.Ordinal);
        foreach (var (path, data) in mod.ArchiveFiles)
        {
            if (PackageLanguage(path) is not { } lang || !path.EndsWith(".fpk", StringComparison.Ordinal)) continue;
            FoxPackage pkg;
            try { pkg = FoxPackage.Read(data); } catch (InvalidDataException) { continue; }
            foreach (var f in pkg.Files.Where(f => f.Path.EndsWith(".lng2", StringComparison.Ordinal)))
            {
                try
                {
                    if (!all.TryGetValue(lang, out var list)) all[lang] = list = [];
                    list.Add(new LangSlot(path, f.Path, LangFile.Read(f.Data)));
                }
                catch (InvalidDataException) { /* a text file the tool can't read stays as it is */ }
            }
        }
        return all;
    }

    private static string StringOf(LuaValue? v) => v is LuaValue.Text t ? t.Value : "";

    /// <summary>The vanilla suit an ExtendVanillaOutfit call adds to, by its name (not its internal parts number).</summary>
    private static string TargetOf(LuaValue.Table a) => a["outfit"] is { } o ? o.ToString().Trim('"') : a["partsType"] is { } pt ? SuitNameOf(pt) : "a vanilla suit";

    /// <summary>
    /// The vanilla suits a "looks on a vanilla suit" item adds looks to, one per ExtendVanillaOutfit call, with the lang IDs
    /// of the look names in call order (the first character that has looks). The Sortie Prep mockup lists them.
    /// </summary>
    public static IEnumerable<(string Outfit, IReadOnlyList<string> NameIds)> VanillaLooks(ExistingItem item) =>
        item.ItemType != ItemKind.ExtraLooks ? [] : item.Calls.Select(c => c.Arguments.FirstOrDefault()).OfType<LuaValue.Table>().Select(a =>
            (TargetOf(a), (IReadOnlyList<string>)(Branches.Keys.Select(f => a[f]).OfType<LuaValue.Table>().Select(b => b["variants"]).OfType<LuaValue.Table>()
                .FirstOrDefault()?.Items.OfType<LuaValue.Table>().Select(v => StringOf(v["displayName"])).ToList() ?? [])));

    /// <summary>
    /// Fills <see cref="ExistingItem.AlsoInScript"/>: for each outfit, the ExtendVanillaOutfit calls of the mod that show
    /// one of its looks (the same fpkPath and partsPath) under a vanilla suit. The tool's own "also under other outfits"
    /// is written this way, so a mod the tool made opens with them listed again.
    /// </summary>
    private static void BindAlso(ModProject project)
    {
        var items = project.Items.OfType<ExistingItem>().ToList();
        foreach (var outfit in items.Where(i => i.ItemType == ItemKind.Outfit && i.Model is OutfitItem))
        {
            var branches = ((OutfitItem)outfit.Model!).Spec.Branches;
            foreach (var holder in items.Where(i => i.ItemType == ItemKind.ExtraLooks))
                foreach (var call in holder.Calls)
                {
                    if (call.Arguments.FirstOrDefault() is not LuaValue.Table a) continue;
                    var slots = new List<AlsoSlot>();
                    foreach (var (field, who) in Branches)
                    {
                        if (a[field] is not LuaValue.Table b || b["variants"] is not LuaValue.Table vs || !branches.TryGetValue(who, out var br)) continue;
                        var looks = new List<Look> { br.Base };
                        looks.AddRange(br.Variants);
                        foreach (var v in vs.Items.OfType<LuaValue.Table>())
                        {
                            int k = looks.FindIndex(l => l.FpkPath == StringOf(v["fpkPath"]) && l.PartsPath == StringOf(v["partsPath"]));
                            bool? arm = v["enableArm"] is LuaValue.Name { Value: "true" or "false" } n ? n.Value == "true" : null;
                            if (k >= 0) slots.Add(new AlsoSlot(who, k - 1, StringOf(v["displayName"]), arm));
                        }
                    }
                    if (slots.Count > 0) outfit.AlsoInScript.Add(new AlsoInScript(TargetOf(a), holder.Title, slots));
                }
        }
    }

    /// <summary>
    /// Writes the vanilla suits the user added in "Also under other outfits" for an existing outfit: one ExtendVanillaOutfit
    /// call per suit, the same as for the tool's own outfits, put just after the outfit's RegisterOutfit call so that the
    /// script makes both at the same time. A suit with no look ticked is not written.
    /// </summary>
    /// <returns>Why the calls can't be added, or null.</returns>
    private static string? InsertAlso(Dictionary<string, string> sources, ExistingItem item, Func<string, string, int, int> siteNow)
    {
        if (item.ItemType != ItemKind.Outfit || item.Model is not OutfitItem m) return null;
        var blocks = m.Spec.AlsoUnder.Select(m.AlsoUnderSpec).Where(e => e.Branches.Count > 0).Select(LuaWriter.ExtendBlock).ToList();
        if (blocks.Count == 0) return null;
        if (item.Call.locked is { } l) return $"also under other outfits: the looks can't be added ({l})";
        var (module, callPath, site, _) = item.Call;
        site = siteNow(module, callPath, site);
        string src = sources[module];
        var sites = LuaSource.FindCalls(src, callPath);
        if (site >= sites.Count) return "also under other outfits: the place in the script was not found";
        int start = sites[site].Start, end = sites[site].End;
        // "return V_Player.RegisterOutfit(...)" ends its block: nothing can follow it there.
        if (src[..start].TrimEnd().EndsWith("return", StringComparison.Ordinal)) return "also under other outfits: the script returns the outfit's call, so no call can follow it";
        int after = end;
        while (after < src.Length && src[after] is ' ' or '\t') after++;
        if (after < src.Length && src[after] == ';') end = after + 1;
        // The call must end its line (a comment may follow): one inside another expression can't be followed by a call.
        int next = end;
        while (next < src.Length && src[next] is ' ' or '\t') next++;
        if (next < src.Length && src[next] is not ('\r' or '\n') && (!src[next..].StartsWith("--", StringComparison.Ordinal) || src[next..].StartsWith("--[", StringComparison.Ordinal)))
            return "also under other outfits: the outfit's call is part of a longer line, so no call can follow it";
        if (next < src.Length && src[next] == '-') { while (next < src.Length && src[next] is not ('\r' or '\n')) next++; end = next; }
        int lineStart = src.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        string indent = new(src[lineStart..start].TakeWhile(c => c is ' ' or '\t').ToArray());
        string nl = src.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // ExtendBlock indents its lines by two spaces; the call's own indent takes their place.
        var lines = string.Concat(blocks).TrimEnd('\n').Split('\n').Select(x => indent + (x.StartsWith("  ", StringComparison.Ordinal) ? x[2..] : x));
        sources[module] = src[..end] + nl + string.Join(nl, lines) + src[end..];
        return null;
    }

    private static void Strings(LuaValue v, string path, List<(string path, string text)> found)
    {
        switch (v)
        {
            case LuaValue.Text t: found.Add((path, t.Value)); break;
            case LuaValue.Table t:
                for (int i = 0; i < t.Items.Count; i++) Strings(t.Items[i], $"{path}[{i + 1}]", found);
                foreach (var kv in t.Fields) Strings(kv.Value, path.Length == 0 ? kv.Key : path + "." + kv.Key, found);
                break;
        }
    }

    private static string Slug(string s)
    {
        var t = new string(s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (t.Contains("__", StringComparison.Ordinal)) t = t.Replace("__", "_", StringComparison.Ordinal);
        if (t.Length == 0) t = "item";
        return char.IsAsciiDigit(t[0]) ? "m" + t : t;
    }

    /// <summary>The item's icon as the mod ships it, or null when the mod has none that can be read.</summary>
    /// <param name="mod">The mod.</param>
    /// <param name="item">The item.</param>
    /// <returns>The picture.</returns>
    public static RgbaImage? IconOf(MgsvMod mod, ExistingItem item)
    {
        if (item.IconPath is not { Length: > 0 } p) return null;
        string ftex = p.EndsWith(".ftex", StringComparison.Ordinal) ? p : p + ".ftex";
        if (!mod.ArchiveFiles.TryGetValue(ftex, out var data)) return null;
        string stem = ftex[..^5];
        var mips = new Dictionary<int, byte[]>();
        for (int k = 1; k <= 6; k++) if (mod.ArchiveFiles.TryGetValue($"{stem}.{k}.ftexs", out var m)) mips[k] = m;
        try { return FtexTexture.Read(data).DecodeLargestAvailable(mips); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Reads an existing V Framework mod: runs each of its scripts in the sandbox, turns what the script registers into items,
    /// and recovers the names and descriptions from the mod's language files by hashing the IDs the script names.
    /// </summary>
    /// <param name="mod">The opened mod.</param>
    /// <param name="findings">What was found, for the Found screen.</param>
    /// <returns>A project that edits the mod, or null when the mod is not a V Framework mod.</returns>
    public static ModProject? TryOpen(MgsvMod mod, List<IntakeFinding> findings)
    {
        var runs = new List<(string path, string src, SandboxResult run)>();
        foreach (var (path, bytes) in mod.GameFiles.Where(g => g.Key.StartsWith("/mod/modules/", StringComparison.OrdinalIgnoreCase) && g.Key.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
        {
            string src = new UTF8Encoding(false).GetString(bytes);
            if (!src.Contains("V_", StringComparison.Ordinal)) continue;
            runs.Add((path, src, LuaSandbox.Run(src)));
        }
        if (runs.All(r => !r.run.Calls.Any(c => c.Path.StartsWith("V_", StringComparison.Ordinal)))) return null;

        var data = new ExistingModData { Source = mod };
        var project = new ModProject
        {
            Id = Intake.IdFrom(mod.Metadata.Name), Name = mod.Metadata.Name, Version = mod.Metadata.Version, Author = mod.Metadata.Author,
            Website = mod.Metadata.Website, Description = mod.Metadata.Description, Existing = data,
        };
        var langs = ReadLanguages(mod);
        bool HasKey(string id) => langs.Values.Any(l => l.Any(s => s.File.Get(FoxHash.StrCode32(id)) != null));
        string? EnglishOf(string id) => langs.TryGetValue("eng", out var e) ? e.Select(s => s.File.Get(FoxHash.StrCode32(id))).FirstOrDefault(t => t != null) : null;

        var stems = Stems(mod);
        var equip = EquipPackages(runs.Select(r => r.run));
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        string UniqueId(string wanted) { string id = wanted, n = ""; for (int i = 2; !usedIds.Add(id + n); i++) n = "_" + i; return id + n; }

        var modules = new List<ModuleRun>();
        var others = new List<RecordedCall>();
        foreach (var (modulePath, src, run) in runs)
        {
            data.Modules[modulePath] = src;
            if (run.Error != null) data.Notes.Add($"{modulePath}: {run.Error}" + (run.Calls.Count > 0 ? " Everything it registered before that is shown." : ""));
            var written = run.Calls.Select(c => c.Path).Distinct().ToDictionary(p => p, p => LuaSource.FindCalls(src, p));
            modules.Add(new ModuleRun(modulePath, src, run, written));
            var develop = run.Calls.Where(c => c.Path == Develop && c.Arguments.Count >= 2 && c.Arguments[0] is LuaValue.Text && c.Arguments[1] is LuaValue.Table).ToList();
            var usedDevelop = new HashSet<RecordedCall>();

            foreach (var calls in Registrations(run.Calls))
            {
                var call = calls[0];
                (ItemKind kind, string what)? kindOf = call.Path switch
                {
                    "V_Player.RegisterOutfit" => (ItemKind.Outfit, "Outfit"),
                    "V_Player.RegisterHeadOption" => (ItemKind.HeadOption, "Head option"),
                    "V_Player.ExtendVanillaOutfit" => (ItemKind.ExtraLooks, "Looks added to a vanilla suit"),
                    "V_TppBuddyService.RegisterBuddyOutfit" => (ItemKind.BuddyOutfit, "Buddy costume"),
                    _ => null,
                };
                if (kindOf is not { } ko) continue;
                if (calls.Any(c => c.Arguments.Count == 0 || c.Arguments[0] is not LuaValue.Table)) continue;
                // Each instance's R&D row, found by its key.
                var rows = new List<RecordedCall>();
                foreach (var c in calls)
                {
                    string k = StringOf(((LuaValue.Table)c.Arguments[0])["key"]);
                    var row = k.Length > 0 ? develop.FirstOrDefault(d => ((LuaValue.Text)d.Arguments[0]).Value == k && !usedDevelop.Contains(d)) : null;
                    if (row != null) { usedDevelop.Add(row); rows.Add(row); }
                }
                BuildItem(project, data, mod, modulePath, src, calls, rows, ko.kind, ko.what, StringOf(((LuaValue.Table)call.Arguments[0])["key"]), written, UniqueId, HasKey, EnglishOf, langs, findings, stems, equip);
            }

            // R&D rows nothing above registered (weapons and items made with the weapon calls).
            foreach (var rows in Registrations(develop.Where(d => !usedDevelop.Contains(d))))
            {
                var tbl = (LuaValue.Table)rows[0].Arguments[1];
                string key = ((LuaValue.Text)rows[0].Arguments[0]).Value;
                var type = (tbl["const"] as LuaValue.Table)?.Field(ParamSchema.Const, "equipDevelopTypeID")?.Value is LuaValue.Name tn ? tn.Value : "";
                var kind = type.Contains("Suit", StringComparison.Ordinal) ? ItemKind.Outfit
                    : type.Contains("Quiet", StringComparison.Ordinal) || type.Contains("DDog", StringComparison.Ordinal) || type.Contains("Horse", StringComparison.Ordinal) ? ItemKind.BuddyOutfit : ItemKind.Weapon;
                BuildItem(project, data, mod, modulePath, src, [], rows, kind, kind == ItemKind.Weapon ? "Weapon or item" : kind == ItemKind.Outfit ? "Outfit" : "Buddy costume", key, written, UniqueId, HasKey, EnglishOf, langs, findings, stems, equip);
            }

            others.AddRange(run.Calls.Where(c => c.Path.StartsWith("V_", StringComparison.Ordinal)
                && c.Path is not ("V_Player.RegisterOutfit" or "V_Player.RegisterHeadOption" or "V_Player.ExtendVanillaOutfit" or "V_TppBuddyService.RegisterBuddyOutfit" or Develop)));
        }
        // A Set* call that a weapon's Stats tab reads is not an "other" call (gap G8): its values are settings now.
        var read = BindWeapons(project, modules);
        foreach (var g in others.Where(c => !read.Contains(c)).GroupBy(c => c.Path))
            data.OtherCalls[g.Key] = g.Count();
        BindAlso(project);
        if (data.OtherCalls.Count > 0)
            findings.Add(new IntakeFinding("Other V Framework calls in the script: " + string.Join(", ", data.OtherCalls.Select(kv => kv.Value > 1 ? $"{kv.Key} ×{kv.Value}" : kv.Key)), true,
                "Kept exactly as they are; the tool changes only the names, descriptions and settings it lists."));
        foreach (var n in data.Notes) findings.Add(new IntakeFinding("Script problem", false, n));
        if (project.Items.Count == 0) return null;
        data.Opened.AddRange(project.Items.OfType<ExistingItem>());
        return project;
    }

    /// <summary>
    /// The calls grouped by the written call that made them, in the order the script first made each. A call written once
    /// and made once is a group of one; a call in a loop is one group of all its runs. A call the text does not show
    /// (<see cref="RecordedCall.Site"/> is -1) is a group of its own, so nothing is merged on a guess.
    /// </summary>
    private static IEnumerable<List<RecordedCall>> Registrations(IEnumerable<RecordedCall> calls) =>
        calls.GroupBy(c => (c.Path, c.Site >= 0 ? c.Site : -1 - c.Occurrence)).Select(g => g.ToList());

    /// <summary>The value at a field path in a table (null when the path is not there).</summary>
    private static LuaValue? At(LuaValue? v, IEnumerable<string> path)
    {
        foreach (var name in path) v = (v as LuaValue.Table)?[name];
        return v;
    }

    /// <summary>
    /// The vanilla suit's own name for a parts type number, so an item reads "Looks on SNEAKING SUIT_TPP" rather than
    /// "Looks on parts type 8". Falls back to the number when the knowledge tables do not know it.
    /// </summary>
    private static string SuitNameOf(LuaValue pt)
    {
        string raw = pt.ToString().Trim('"');
        if (!int.TryParse(raw, out int n)) return "parts type " + raw;
        try
        {
            var info = GameKnowledge.PartsTypes.FirstOrDefault(p => p.PartsType == n);
            return info == null ? "parts type " + n : info.Name;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException) { return "parts type " + n; }
    }

    /// <summary>The vanilla R&amp;D row the key's row follows, from the FOLLOW_VANILLA table an SVFM build writes (LuaWriter.WriteFollowVanillaDevelop).</summary>
    private static int? FollowOf(string src, string key)
    {
        var table = Regex.Match(src, @"local FOLLOW_VANILLA = \{([^}]*)\}");
        var m = table.Success ? Regex.Match(table.Groups[1].Value, @"\[""" + Regex.Escape(key) + @"""\]\s*=\s*(\d+)") : Match.Empty;
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    // calls: the registration call's runs (one, or one per time a loop made it); empty for an R&D row on its own.
    // rows: the R&D rows of those runs, or the runs of an R&D row on its own.
    private static void BuildItem(ModProject project, ExistingModData data, MgsvMod mod, string modulePath, string src, List<RecordedCall> calls, List<RecordedCall> rows,
        ItemKind kind, string what, string key, Dictionary<string, List<LuaCallSite>> written, Func<string, string> uniqueId,
        Func<string, bool> hasKey, Func<string, string?> english, Dictionary<string, List<LangSlot>> langs, List<IntakeFinding> findings,
        Dictionary<string, List<string>> stems, Dictionary<string, (string parts, string fpk)> equip)
    {
        var args = calls.Count > 0 ? (LuaValue.Table)calls[0].Arguments[0] : null;
        var row = rows.FirstOrDefault();
        var rowTable = row != null ? (LuaValue.Table)row.Arguments[1] : null;
        var constTable = rowTable?["const"] as LuaValue.Table;
        // Every R&D field is read through the schema: a mod may write p06 or langEquipName, and real mods mix both in one row.
        string? Lang(LuaValue.Table? t, string field) => t?.Field(ParamSchema.Const, field)?.Value is LuaValue.Text s && hasKey(s.Value) ? s.Value : null;
        string? nameId = Lang(constTable, "langEquipName"), realId = Lang(constTable, "langEquipRealName"), infoId = Lang(constTable, "langEquipInfo");

        // Every other string in the script's calls for this item that is a text ID of the mod.
        var found = new List<(string path, string text)>();
        if (args != null) Strings(args, "", found);
        if (rowTable != null) Strings(rowTable, "row", found);
        var assigned = new HashSet<string?> { nameId, realId, infoId };
        var other = new List<(string, string)>();
        foreach (var (path, text) in found)
        {
            if (!hasKey(text) || !assigned.Add(text)) continue;
            if (nameId == null && path.EndsWith("displayName", StringComparison.Ordinal)) { nameId = text; continue; }
            other.Add((Pretty(path), text));
        }

        string title = (nameId != null ? english(nameId) : null) ?? (key.Length > 0 ? key : what);
        // Name the suit the looks are added to, not its internal parts number. "Looks on parts type 8" told the
        // owner nothing; "Looks on SNEAKING SUIT_TPP" says which suit in the game it changes.
        // A loop's item lists what each run registers: the suit for looks, otherwise the key.
        var made = calls.Count > 0 ? calls : rows;
        var instances = made.Count < 2 ? [] : made.Select(c => kind == ItemKind.ExtraLooks ? TargetOf((LuaValue.Table)c.Arguments[0])
            : c.Arguments[0] is LuaValue.Table a && StringOf(a["key"]) is { Length: > 0 } k ? k
            : c.Arguments[0] is LuaValue.Text t ? t.Value : $"#{c.Occurrence + 1}").ToList();
        if (kind == ItemKind.ExtraLooks) title = instances.Count > 0 ? $"Looks on {instances.Count} vanilla suits" : $"Looks on {TargetOf(args!)}";
        string id = uniqueId(Slug(key.Length > 0 ? key : title));
        var item = new ExistingItem
        {
            Id = id, Key = key.Length > 0 ? key : null, Prefix = id, ItemType = kind, Title = title, NameId = nameId, RealNameId = realId, InfoId = infoId, Module = modulePath,
            IconPath = StringOf(constTable?.Field(ParamSchema.Const, "iconFtexPath")?.Value) is { Length: > 0 } ip ? ip : found.Select(f => f.text).FirstOrDefault(t => t.Contains("EquipIcon", StringComparison.Ordinal) || t.EndsWith("_ico.ftex", StringComparison.Ordinal)),
        };
        item.OtherText.AddRange(other);
        item.Instances.AddRange(instances);
        foreach (var textId in new[] { nameId, realId, infoId }.Concat(other.Select(x => x.Item2)).OfType<string>().Distinct())
        {
            foreach (var (lang, slots) in langs)
                foreach (var s in slots)
                    if (s.File.Get(FoxHash.StrCode32(textId)) is { } t) { item.SetText(textId, lang, t); break; }
        }

        if (rowTable != null) ReadDevelop(item, modulePath, src, rows, rowTable, written);
        if (item.Develop != null && key.Length > 0 && FollowOf(src, key) is { } follow)
            item.Develop = item.DevelopOriginal = item.Develop with { FollowsVanillaDevelopId = follow };
        // Files: what the script names for this item, plus a weapon's package from its AddToEquipIdTable entry.
        var equipId = constTable?.Field(ParamSchema.Const, "equipID")?.Value is LuaValue.Name eq ? eq.Value : null;
        item.EquipId = equipId;
        var weapon = equipId != null && equip.TryGetValue(equipId, out var e) ? e.fpk : null;
        CollectFiles(item, mod, stems, found.Select(f => f.text).Append(item.IconPath ?? "").Append(weapon ?? ""));
        CollectCommonTextures(item, mod, stems);
        item.Model = ModelOf(item, args, constTable, equip);
        // Every place the item's calls write its icon path, so a game icon or a new icon file can be named there.
        if (item.IconPath is { Length: > 0 } iconText)
            foreach (var (path, text) in found.Where(f => f.text == iconText))
            {
                bool inRow = path.StartsWith("row.", StringComparison.Ordinal);
                var on = inRow ? rows : calls;
                if (on.Count == 0) continue;
                var keys = System.Text.RegularExpressions.Regex.Split(inRow ? path[4..] : path, @"\.|(?=\[)").Where(k => k.Length > 0).ToArray();
                item.IconFields.Add(MakeField("Icon", "iconFtexPath", FieldType.Text, modulePath, on, inRow ? 1 : 0, keys, src, written, writtenAsFlag: false, overName: true));
            }
        item.Calls = calls;
        item.Rows = rows;
        if (calls.Count > 0)
            item.Call = (modulePath, calls[0].Path, calls[0].Site,
            calls.Any(c => c.Site < 0) ? "the script makes this call in a way the tool can't follow"
            : calls.Select(c => c.Site).Distinct().Count() > 1 ? "the script writes these calls in more than one place" : null);
        // Settings. The abilities and camo of a branch the outfit model has are edited on the model (Abilities and Camo
        // concealment tabs); the abilities of any other branch are listed in the Settings tab.
        if (args != null)
        {
            if (item.Model is OutfitItem outfit)
                BindOutfit(item, outfit, args, (path, type) => MakeField("Outfit", path[^1], type, modulePath, calls, 0, path, src, written, writtenAsFlag: false));
            if (kind is ItemKind.Outfit or ItemKind.ExtraLooks)
                foreach (var (field, display) in BranchNames)
                    if (args[field] is LuaValue.Table && !(item.Model is OutfitItem o && o.Spec.Branches.ContainsKey(Branches[field])))
                        foreach (var k in Abilities.Keys)
                            item.Fields.Add(MakeField($"Abilities · {display}", k.Label, k.Type, modulePath, calls, 0, [field, "abilities", k.Name], src, written, writtenAsFlag: false));
            if (kind == ItemKind.HeadOption)
                item.Fields.Add(MakeField("Abilities", "Infinite ammo", FieldType.Bool, modulePath, calls, 0, ["abilities", "infiniteAmmo"], src, written, false));
            if (kind == ItemKind.BuddyOutfit) BindBuddy(item, args, modulePath, calls, src, written);
        }
        project.Items.Add(item);
        findings.Add(new IntakeFinding($"{what}: {title}", true, "Already a V Framework mod: opened as it is; only what you change is rewritten."));
    }

    /// <summary>
    /// The keys of an R&amp;D row the tool edits, in the table that holds them, and the value <see cref="LuaWriter"/> gives
    /// each from a <see cref="DevelopRow"/> (null: the key is left out). AfterMission and FollowsVanillaDevelopId are
    /// functions in the script, so they are not here: they can't be set (see <see cref="Build"/>). AfterMission reads as
    /// null; FollowsVanillaDevelopId is read from an SVFM build's FOLLOW_VANILLA table (<see cref="FollowOf"/>).
    /// </summary>
    private static readonly (string table, string key, FieldType type, Func<DevelopRow, object?> get)[] DevelopKeys =
    [
        ("flow", "grade", FieldType.Number, d => d.Grade), ("flow", "developGmpCost", FieldType.Number, d => d.GmpCost),
        ("flow", "usageGmpCost", FieldType.Number, d => d.UsageGmpCost), ("flow", "initialAvailable", FieldType.Flag, d => d.InitiallyAvailable),
        ("flow", "developTimeMinute", FieldType.Number, d => d.TimeMinutes),
        // The R&D section's level is p55 (sectionLvForDevelop); any other main section is the id and level pair p63/p64.
        ("flow", "sectionLvForDevelop", FieldType.Number, d => d.SectionId == DevelopRow.RdSection ? d.SectionLevel : 0),
        ("flow", "sectionIDForDevelop", FieldType.Number, d => d.SectionId == DevelopRow.RdSection ? 0 : d.SectionId),
        ("flow", "developSectionLv", FieldType.Number, d => d.SectionId == DevelopRow.RdSection ? 0 : d.SectionLevel),
        ("flow", "sectionID2ForDevelop", FieldType.Number, d => d.Section2Id), ("flow", "sectionLv2ForDevelop", FieldType.Number, d => d.Section2Level),
        ("flow", "resourceType1", FieldType.Text, d => NullIfEmpty(d.Resource1)), ("flow", "resourceType1Count", FieldType.Number, d => d.Resource1Count),
        ("flow", "resourceType2", FieldType.Text, d => NullIfEmpty(d.Resource2)), ("flow", "resourceType2Count", FieldType.Number, d => d.Resource2Count),
        ("flow", "resourceUsageType1", FieldType.Text, d => NullIfEmpty(d.Usage1)), ("flow", "resourceUsageType1Count", FieldType.Number, d => d.Usage1Count),
        ("flow", "resourceUsageType2", FieldType.Text, d => NullIfEmpty(d.Usage2)), ("flow", "resourceUsageType2Count", FieldType.Number, d => d.Usage2Count),
        ("flow", "displayInfo", FieldType.Flag, d => d.HiddenUntilObtained),
        ("const", "baseEquipDevelopId", FieldType.Number, d => d.UpgradeOfDevelopId), ("const", "skill", FieldType.Text, d => d.Skill),
        // Only a game blueprint is set here; a mod's own one (OwnBluePrint) is never written, so the script keeps it.
        ("const", "bluePrintId", FieldType.Text, d => d.BluePrint is { } bp ? new LuaSource.Code(DevelopRow.BluePrintLua(bp)) : null),
        ("const", "isResultRankLimited", FieldType.Flag, d => d.RankLimited), ("const", "langResultRankLimited", FieldType.Text, d => NullIfEmpty(d.ResultRankLang)),
        // The look-name group: not in the row, so ApplyDevelop leaves it; ApplyOutfit sets it from the look count.
        ("const", "equipDevelopGroupID", FieldType.Number, _ => null),
    ];

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Reads the R&amp;D row into <see cref="ExistingItem.Develop"/> and records where each key is written.</summary>
    private static void ReadDevelop(ExistingItem item, string modulePath, string src, List<RecordedCall> rows, LuaValue.Table rowTable, Dictionary<string, List<LuaCallSite>> written)
    {
        var at = new Dictionary<string, ExistingField>(StringComparer.Ordinal);
        foreach (var (table, key, keyType, _) in DevelopKeys)
        {
            var type = keyType;
            // Edit the key under the name the script gives it (p52 or grade); one it leaves out is added under the alias.
            var t = rowTable[table] as LuaValue.Table;
            var have = t?.Field(table == "const" ? ParamSchema.Const : ParamSchema.Flow, key);
            string name = have?.Key ?? key;
            // A skill is a staff skill name (text); a script that gives a number keeps it as a number.
            // bluePrintId = 65535 (none) is a number too.
            if (key is "skill" or "bluePrintId" && have?.Value is LuaValue.Number) type = FieldType.Number;
            var f = MakeField("R&D", key, type, modulePath, rows, 1, [table, name], src, written, writtenAsFlag: true);
            item.DevelopFields.Add(f);
            at[key] = f;
        }
        // A key the script leaves out, or sets in a way the tool can't read, is null (or 0).
        int? N(string k) => at[k] is { Present: true, Locked: null, Original: double d } ? (int)Math.Round(d) : null;
        bool B(string k) => at[k] is { Locked: null, Original: true };
        string? S(string k) => at[k] is { Locked: null, Original: string { Length: > 0 } s } ? s : null;
        // The main section is the p63/p64 pair when the script sets one, else a p55 level (the R&D section).
        int mainId = N("sectionIDForDevelop") ?? 0, mainLv = N("developSectionLv") ?? 0;
        if (mainId == 0 && N("sectionLvForDevelop") is > 0 and var rd) (mainId, mainLv) = (DevelopRow.RdSection, rd);
        // The script's own blueprint (65535 is none): shown as text and kept as it is.
        var bp = (rowTable["const"] as LuaValue.Table)?.Field(ParamSchema.Const, "bluePrintId")?.Value;
        string? own = bp switch
        {
            null or LuaValue.Name { Value: "nil" } or LuaValue.Number { Value: 65535d } => null,
            LuaValue.Text t => t.Value,
            LuaValue.Number n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            LuaValue.Name { Value: "<function>" } => "A function in the script",
            LuaValue.Name nm => nm.Value,
            _ => "A value in the script",
        };
        var row = new DevelopRow
        {
            OwnBluePrint = own,
            Grade = N("grade"), GmpCost = N("developGmpCost") ?? 0, UsageGmpCost = N("usageGmpCost") ?? 0, InitiallyAvailable = B("initialAvailable"),
            TimeMinutes = N("developTimeMinute") ?? 0, SectionId = mainId, SectionLevel = mainLv,
            Section2Id = N("sectionID2ForDevelop") ?? 0, Section2Level = N("sectionLv2ForDevelop") ?? 0,
            Resource1 = S("resourceType1"), Resource1Count = N("resourceType1Count") ?? 0, Resource2 = S("resourceType2"), Resource2Count = N("resourceType2Count") ?? 0,
            Usage1 = S("resourceUsageType1"), Usage1Count = N("resourceUsageType1Count") ?? 0, Usage2 = S("resourceUsageType2"), Usage2Count = N("resourceUsageType2Count") ?? 0,
            HiddenUntilObtained = B("displayInfo"), UpgradeOfDevelopId = N("baseEquipDevelopId"),
            Skill = S("skill") ?? (at["skill"] is { Present: true, Locked: null, Original: double sk } && sk != 0 ? sk.ToString(System.Globalization.CultureInfo.InvariantCulture) : null),
            RankLimited = B("isResultRankLimited"), ResultRankLang = S("langResultRankLimited"),
        };
        item.Develop = item.DevelopOriginal = row;
    }

    /// <summary>
    /// Puts the edited R&amp;D row into <see cref="ExistingItem.DevelopFields"/>, key by key: a key whose value is as the
    /// script has it stays untouched. A key taken out (an upgrade, a skill, a resource) goes into <paramref name="remove"/>,
    /// and the build takes it out of the script. Returns why a change can't be written.
    /// </summary>
    private static List<string> ApplyDevelop(ExistingItem item, List<ExistingField> remove)
    {
        var why = new List<string>();
        if (item.Develop is not { } d || item.DevelopOriginal is not { } o) return why;
        if (d.AfterMission != o.AfterMission || d.FollowsVanillaDevelopId != o.FollowsVanillaDevelopId)
            why.Add("R&D unlock: a story-mission or vanilla-row unlock can't be added to an existing script (it is a function there)");
        foreach (var f in item.DevelopFields)
        {
            var get = DevelopKeys.First(k => k.key == f.Label).get;
            object? now = get(d);
            f.Value = f.Original;
            if (Equals(now, get(o))) continue;
            if (now == null)
            {
                if (f.Locked != null) why.Add($"R&D {f.Label}: can't be taken out of the script ({f.Locked})");
                else if (f.Present) remove.Add(f);
                continue;
            }
            if (now is LuaSource.Code) { f.Value = now; continue; }
            if (now is double n && !double.IsFinite(n)) { why.Add($"R&D {f.Label}: {n} is not a number a script can hold"); continue; }
            if (f.Type == FieldType.Number && now is string s && !DevelopRow.IsNumber(s))
            {
                // skill = 0 means no specialist, so a skill name can replace it.
                if (f.Original is 0d) f.Value = s;
                else why.Add($"R&D {f.Label}: the script gives a number here, so the tool can't change it to a skill name");
                continue;
            }
            f.Value = f.Type == FieldType.Number ? Convert.ToDouble(now, System.Globalization.CultureInfo.InvariantCulture) : now;
        }
        return why;
    }

    /// <summary>
    /// Reads the abilities and camo of each branch of the outfit model from the script, and records where each key is
    /// written (<see cref="ExistingItem.SpecFields"/>). A key the tool can't read (an expression) reads as its default.
    /// </summary>
    private static void BindOutfit(ExistingItem item, OutfitItem model, LuaValue.Table args, Func<string[], FieldType, ExistingField> at)
    {
        item.FieldAt = at;
        foreach (var (field, who) in Branches)
        {
            if (!model.Spec.Branches.TryGetValue(who, out var branch)) continue;
            var f = new Dictionary<string, ExistingField>(StringComparer.Ordinal);
            foreach (var k in Abilities.Keys) item.SpecFields.Add(f[k.Name] = at([field, "abilities", k.Name], k.Type));
            // V Framework takes the camo by its name or by its number (0 to 116, the same list as GameKnowledge.Camos). A
            // number shows as its name. It stays in the script until the user makes a new choice, which is written as a name.
            bool camoNumber = (args[field] as LuaValue.Table)?["camoBonusType"] is LuaValue.Number;
            item.SpecFields.Add(f["camoBonusType"] = at([field, "camoBonusType"], camoNumber ? FieldType.Number : FieldType.Text));
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            if ((args[field] as LuaValue.Table)?["camoBonusValues"] is LuaValue.Table cv)
                foreach (var (ground, _) in cv.Fields)
                {
                    var v = at([field, "camoBonusValues", ground], FieldType.Number);
                    item.SpecFields.Add(v);
                    // Issue #89: a key reaches the material one before it in game (GameKnowledge.CamoValueKey).
                    if (v is { Locked: null, Original: double d } && d != 0 && GameKnowledge.CamoValueMaterial(ground) is { } mat) values[mat] = (int)Math.Round(d);
                }
            string? S(string k) => f[k] is { Locked: null, Original: string { Length: > 0 } s } ? s : null;
            model.Spec.Branches[who] = branch with
            {
                // A key that the script does not set has the number 0 (ExistingField.Original), which is no own sprint speed.
                Abilities = Abilities.Read(k => f[k] is { Locked: null, Original: true }, k => f[k] is { Locked: null, Original: double d } ? d : null, S),
                CamoBonusType = f["camoBonusType"] is { Locked: null, Original: double camo }
                    ? Knowledge.GameKnowledge.Camos.FirstOrDefault(c => c.PlayerCamoType == camo)?.Name : S("camoBonusType"),
                CamoBonusValues = values.Count > 0 ? values : null,
            };
        }
        item.OutfitOriginal = new Dictionary<PlayerBranch, OutfitBranch>(model.Spec.Branches);
    }

    /// <summary>
    /// A table of an outfit branch (variants, headOptions, colors, voiceFpk) as <see cref="LuaWriter"/> writes it for
    /// <paramref name="b"/>, or the whole branch when <paramref name="sub"/> is null. Null when the writer leaves it out.
    /// A looks-only branch has all its looks in variants.
    /// </summary>
    private static string? Written(PlayerBranch who, OutfitBranch b, bool looksOnly, string? sub, bool? multiLook = null)
    {
        if (looksOnly) b = b with { Variants = [b.Base, .. b.Variants] };
        var spec = new OutfitSpec { Key = "k", NameLang = "n", IconFtexPath = "" };
        spec.Branches[who] = b;
        string lua = LuaWriter.OutfitBlock(spec, multiLook);
        var t = LuaSource.FindCalls(lua, "V_Player.RegisterOutfit")[0].Arguments[0].Table!;
        var e = t.Find(LuaWriter.BranchName(who))!;
        if (sub != null) e = e.Table!.Find(sub);
        return e == null ? null : lua[e.ValueStart..e.ValueEnd];
    }

    /// <summary>A value as text with the fields in key order, so two tables with the same content compare equal.</summary>
    private static string Canon(LuaValue? v) => v switch
    {
        null => "nil",
        LuaValue.Table t when t.Items.Count == 0 && t.Fields.Count == 0 => "nil",
        LuaValue.Table t => CanonTable(t),
        LuaValue.Number n => LuaLiteral.FormatNumber(n.Value),
        _ => v.ToString(),
    };

    /// <summary>
    /// A table in one form: keys [1] to [n] written out (<c>[1] = { ... }</c>) are the same list as items in order, so the
    /// script's reader and the writer's reader give the same text for both.
    /// </summary>
    private static string CanonTable(LuaValue.Table t)
    {
        var items = t.Items.ToList();
        var fields = t.Fields.ToList();
        for (int n = items.Count + 1; fields.FindIndex(f => f.Key == n.ToString(System.Globalization.CultureInfo.InvariantCulture)) is var i and >= 0; n++)
        {
            items.Add(fields[i].Value);
            fields.RemoveAt(i);
        }
        return "{" + string.Join(",", items.Select(Canon)) + ";" + string.Join(",", fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "=" + Canon(f.Value))) + "}";
    }

    /// <summary>
    /// Why the table at <paramref name="path"/> can't be replaced with the writer's text, or null when it can: the writer
    /// must give back what the script has (every call), so nothing the tool does not read is lost.
    /// </summary>
    private static string? CantReplace(ExistingItem item, string[] path, string? writtenBefore)
    {
        if (item.Call.locked is { } l) return l;
        string before = writtenBefore == null ? "nil" : Canon(LuaLiteral.Parse(writtenBefore));
        if (item.Calls.Any(c => Canon(At(c.Arguments[0], path)) != before))
            return "the script writes it with keys or values the tool doesn't read, or works it out each time";
        return null;
    }

    /// <summary>
    /// Puts the edited outfit model into the script, key by key: abilities, camo, voice type and the main look's keys go
    /// into <see cref="ExistingItem.SpecFields"/>; variants, headOptions, colors and voiceFpk, and a branch added or taken
    /// out (who wears it), are table changes in <paramref name="ops"/> (path, Lua text; null text takes the key out).
    /// A key or table whose value is as the script has it stays untouched. Returns why a change can't be written.
    /// </summary>
    private static List<string> ApplyOutfit(ExistingItem item, List<(string[] path, string? lua)> ops, List<ExistingField> remove)
    {
        var why = new List<string>();
        foreach (var f in item.SpecFields) f.Value = f.Original;
        if (item.Model is not OutfitItem m || item.OutfitOriginal is not { } orig) return why;
        foreach (var (field, who) in Branches)
        {
            orig.TryGetValue(who, out var was);
            m.Spec.Branches.TryGetValue(who, out var now);
            if (was == null && now == null) continue;
            if (now == null)
            {
                if (item.Call.locked is { } l) why.Add($"{field}: who can wear it can't be changed ({l})");
                else ops.Add(([field], null));
                continue;
            }
            if (was == null)
            {
                // A new wearer is written as the writer writes a branch, with the outfit's icon on its main look: the R&D
                // icon, or else the icon of a wearer the script has (a port gives every wearer the item's one icon).
                if (item.Call.locked is { } l) { why.Add($"{field}: who can wear it can't be changed ({l})"); continue; }
                string? icon = IconText(item) ?? orig.Values.Select(o => o.Base.IconFtexPath).FirstOrDefault(i => i != null);
                var b = now.Base.IconFtexPath == null ? now with { Base = now.Base with { IconFtexPath = icon } } : now;
                if (b.Base.IconFtexPath == null) { why.Add($"{field}: a new wearer needs an icon, and the script names none"); continue; }
                try { ops.Add(([field], Written(who, b, false, null, LuaWriter.IsMultiLook(m.Spec.Branches)))); }
                catch (ArgumentException e) { why.Add($"{field}: {e.Message}"); }
                continue;
            }
            bool looksOnly = item.LooksOnly.Contains(who);
            void Set(string[] path, FieldType type, object? value, object? before)
            {
                if (Equals(value, before)) return;
                var f = item.SpecFields.FirstOrDefault(x => x.Path.SequenceEqual(path));
                if (f == null) item.SpecFields.Add(f = item.FieldAt!(path, type));
                if (value == null)
                {
                    // A key taken out (a camo type, a sprint spark): the build takes it out of the script.
                    if (f.Locked != null) why.Add($"{string.Join(".", path)}: can't be taken out of the script ({f.Locked})");
                    else if (f.Present) remove.Add(f);
                    return;
                }
                f.Value = value;
            }
            foreach (var k in Abilities.Keys) Set([field, "abilities", k.Name], k.Type, k.Value(now.Abilities), k.Value(was.Abilities));
            Set([field, "camoBonusType"], FieldType.Text, now.CamoBonusType, was.CamoBonusType);
            // V Framework uses the type over the values, so the values are written only when the branch has no type.
            if (now.CamoBonusType == null)
            {
                var a = was.CamoBonusValues ?? new Dictionary<string, int>();
                var c = now.CamoBonusValues ?? new Dictionary<string, int>();
                foreach (var ground in a.Keys.Union(c.Keys))
                    if (GameKnowledge.CamoValueKey(ground) is { } key)
                        Set([field, "camoBonusValues", key], FieldType.Number, (double)c.GetValueOrDefault(ground), (double)a.GetValueOrDefault(ground));
            }
            // The main look's keys, one by one (a looks-only branch has its main look in variants).
            // Down to 1 look: the main look's name goes, as the writer leaves it out (it is drawn over the grade bar).
            var nowBase = LookCountNow(item) == false ? now.Base with { DisplayName = null } : now.Base;
            if (!looksOnly)
                foreach (var (key, _, get, _) in Look.Keys)
                {
                    object? v = get(nowBase), o = get(was.Base);
                    if (Equals(v, o)) continue;
                    if (v == null)
                    {
                        if (item.Call.locked is { } l) why.Add($"{field}.{key}: can't be taken out ({l})");
                        else ops.Add(([field, key], null));
                    }
                    // A switch the script leaves out reads as off, so Set would see no change; V Framework's default is on
                    // (enableArm: the bionic arm and prosthetics stay on the look, owner Nov 17). Write it out.
                    else if (v is bool on && o == null)
                    {
                        if (item.Call.locked is { } l) why.Add($"{field}.{key}: can't be added ({l})");
                        else ops.Add(([field, key], on ? "true" : "false"));
                    }
                    else Set([field, key], v is bool ? FieldType.Bool : FieldType.Text, v, o);
                }
            if (now.Voice?.Type == null && was.Voice?.Type != null)
            {
                if (item.Call.locked is { } l) why.Add($"{field}.voiceType: can't be taken out ({l})");
                else ops.Add(([field, "voiceType"], null));
            }
            else Set([field, "voiceType"], FieldType.Text, now.Voice?.Type, was.Voice?.Type);
            // The tables, whole: only when the writer gives back what the script has.
            foreach (var sub in new[] { "variants", "headOptions", "colors", "voiceFpk" })
            {
                string? before, after;
                try { before = Written(who, was, looksOnly, sub); after = Written(who, now, looksOnly, sub); }
                catch (ArgumentException e) { why.Add($"{field}.{sub}: {e.Message}"); continue; }
                if (before == after) continue;
                if (CantReplace(item, [field, sub], before) is { } no) { why.Add($"{field}.{sub}: can't be changed ({no})"); continue; }
                ops.Add(([field, sub], after));
            }
        }
        // The look count crossed 1: the R&D row's look-name group follows it, as LuaWriter.OutfitBlock gives it (M5).
        if (LookCountNow(item) is { } multi && item.DevelopFields.FirstOrDefault(f => f.Label == "equipDevelopGroupID") is { } g)
        {
            // Any group the script sets is the look-name group (a name such as V_TppMbDev.EQP_OUTFIT_VARIANT_NAME locks it).
            if (!multi && g.Present) remove.Add(g);
            else if (multi && !g.Present)
            {
                if (g.Locked != null) why.Add($"R&D equipDevelopGroupID: can't be added for the new look count ({g.Locked})");
                else g.Value = new LuaSource.Code("V_TppMbDev.EQP_OUTFIT_VARIANT_NAME");
            }
        }
        return why;
    }

    /// <summary>When an edit takes an outfit from 1 look to 2 or more (true) or back (false); else null.</summary>
    private static bool? LookCountNow(ExistingItem item) =>
        item.DevelopFields.Count > 0 && item.Model is OutfitItem m && item.OutfitOriginal is { } orig && LuaWriter.IsMultiLook(m.Spec.Branches) is var now && now != LuaWriter.IsMultiLook(orig) ? now : null;

    /// <summary>A character's table of a head option as <see cref="LuaWriter.HeadBlock"/> writes it.</summary>
    private static string HeadWritten(PlayerBranch who, HeadBranch b)
    {
        var spec = new HeadOptionSpec { Key = "k" };
        spec.Branches[who] = b;
        string lua = LuaWriter.HeadBlock(spec);
        var e = LuaSource.FindCalls(lua, "V_Player.RegisterHeadOption")[0].Arguments[0].Table!.Find(LuaWriter.BranchName(who))!;
        return lua[e.ValueStart..e.ValueEnd];
    }

    /// <summary>
    /// Puts the edited head option model into the script: a character who no longer wears the head is taken out (the
    /// head's table for that character goes), a new character gets a table as the writer writes it, and a changed
    /// character's table is written again when the writer gives back what the script has. Returns why a change can't be
    /// written.
    /// </summary>
    private static List<string> ApplyHeads(ExistingItem item, List<(string[] path, string? lua)> ops)
    {
        var why = new List<string>();
        if (item.Model is not HeadOptionItem h || item.HeadOriginal is not { } orig) return why;
        string Key(HeadBranch b) => System.Text.Json.JsonSerializer.Serialize(b);
        if (orig.Count == h.Spec.Branches.Count && orig.All(kv => h.Spec.Branches.TryGetValue(kv.Key, out var n) && Key(n) == Key(kv.Value))) return why;
        if (item.Call.locked is { } l) { why.Add($"who wears it can't be changed ({l})"); return why; }
        foreach (var (who, was) in orig)
        {
            string name = LuaWriter.BranchName(who);
            if (!h.Spec.Branches.TryGetValue(who, out var now)) ops.Add(([name], null));
            else if (Key(now) != Key(was))
            {
                if (CantReplace(item, [name], HeadWritten(who, was)) is { } no) why.Add($"{name}: the head can't be changed ({no})");
                else ops.Add(([name], HeadWritten(who, now)));
            }
        }
        foreach (var (who, now) in h.Spec.Branches.Where(kv => !orig.ContainsKey(kv.Key)).OrderBy(kv => kv.Key))
            ops.Add(([LuaWriter.BranchName(who)], HeadWritten(who, now)));
        return why;
    }

    /// <summary>A text's place for display: "row.const.p10" reads "R&amp;D · const.langPowerUpInfo0".</summary>
    private static string Pretty(string path) => Regex.Replace(path, @"^row\.(const|flow)\.(p\d\d)$",
        m => $"row.{m.Groups[1].Value}.{(m.Groups[1].Value == "const" ? ParamSchema.Const : ParamSchema.Flow).Find(m.Groups[2].Value)?.Alias ?? m.Groups[2].Value}")
        .Replace("row.", "R&D · ", StringComparison.Ordinal);

    /// <summary>One setting, read from the first of <paramref name="calls"/>. It can be changed only when one written call makes them all and gives it the same value each time.</summary>
    private static ExistingField MakeField(string group, string label, FieldType type, string module, List<RecordedCall> calls, int argIndex, string[] path,
        string src, Dictionary<string, List<LuaCallSite>> written, bool writtenAsFlag, bool overName = true)
    {
        var call = calls[0];
        var current = At(call.Arguments[argIndex], path);
        object original = type switch { FieldType.Bool => false, FieldType.Number => 0d, FieldType.Flag => false, _ => "" };
        bool present = false, asBool = false;
        string? locked = null;
        // V Framework reads dashAttack as on/off or as an attack ID (issue #96): an ID reads as its attack name.
        if (type == FieldType.Bool && path[^1] == "dashAttack" && Abilities.DashAttackOf(current) is { } attack) { present = true; original = attack; }
        else if (current is LuaValue.Number n)
        {
            present = true;
            if (type == FieldType.Text) locked = "the script gives a number where text is expected";
            else if (type == FieldType.Bool && n.Value is not (0 or 1)) locked = "the script gives a number (an ID) where the tool has only on and off";
            else original = type is FieldType.Bool or FieldType.Flag ? n.Value != 0 : n.Value;
        }
        else if (current is LuaValue.Name { Value: "true" or "false" } b)
        {
            present = true; asBool = true;
            if (type == FieldType.Text) locked = "the script gives true/false where text is expected";
            else original = type == FieldType.Number ? (b.Value == "true" ? 1d : 0d) : b.Value == "true";
        }
        else if (current is LuaValue.Text t)
        {
            present = true;
            if (type == FieldType.Text) original = t.Value; else locked = "the script gives text where a number or switch is expected";
        }
        else if (current != null && current is not LuaValue.Name { Value: "nil" }) { present = true; locked = "the script sets it with a name or an expression that the tool can't work out"; }
        if (locked == null && calls.Any(c => c.Site < 0)) locked = "the script makes this call in a way the tool can't follow";
        if (locked == null && calls.Select(c => c.Site).Distinct().Count() > 1) locked = "the script writes these calls in more than one place";
        if (locked == null && calls.Skip(1).Any(c => !Equals(At(c.Arguments[argIndex], path), current)))
            locked = $"it is not the same each of the {calls.Count} times the script makes this call (the script works it out each time)";
        if (locked == null)
        {
            var sites = written[call.Path];
            var table = call.Site < sites.Count && argIndex < sites[call.Site].Arguments.Count ? sites[call.Site].Arguments[argIndex].Table : null;
            if (table == null) locked = "the script passes this table in a way the tool can't follow";
            else
            {
                object trial = type switch { FieldType.Bool => true, FieldType.Number => 1d, FieldType.Flag => 1, _ => "x" };
                if (LuaSource.SetField(src, table, path, trial, out var err, overName) == null) locked = err;
            }
        }
        // Compare with what the text writes: a field the run found but the text writes by another route stays locked.
        return new ExistingField
        {
            Group = group, Label = label, Type = type, Original = original, Value = original, Present = present, Module = module, CallPath = call.Path,
            Site = call.Site, ArgIndex = argIndex, Path = path, WrittenAsBool = writtenAsFlag && asBool, Locked = locked, OverName = overName,
        };
    }

    // ---- Building it back ----

    /// <summary>
    /// Writes an uploaded icon over the icon the mod ships, at the same archive path (its spelling, not the script's).
    /// The .ftexs mips of the shipped icon that the new one does not have are taken out. A new icon file at a path of the
    /// item's own is added (the script is then pointed at it, see <see cref="ApplyIconPath"/>).
    /// An icon whose files are still the opened mod's own arrays is left as it is, so an untouched build stays byte-identical.
    /// </summary>
    private static void WriteIcon(MgsvBuilder b, MgsvMod source, ExistingItem item)
    {
        if (IntakeResult.IconPathOf(item) is not { } stem) return;
        bool Under(string k) => k.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
        var now = item.Files.Where(f => Under(f.Key)).ToList();
        var shipped = source.ArchiveFiles.Where(f => Under(f.Key)).ToList();
        // A path of the item's own that the mod does not ship (a new icon file): its files are added. A game icon has none.
        if (shipped.Count == 0) { foreach (var (k, v) in now) b.AddArchiveFile(k, v); return; }
        if (item.IconPathWritten != null && now.Count == 0) return;
        bool same = now.Count == shipped.Count && now.All(f => source.ArchiveFiles.Keys.FirstOrDefault(k => k.Equals(f.Key, StringComparison.OrdinalIgnoreCase)) is { } k && ReferenceEquals(source.ArchiveFiles[k], f.Value));
        if (same) return;
        string spelled = shipped[0].Key[..stem.Length];
        foreach (var (k, _) in shipped) b.RemoveArchiveFile(k);
        foreach (var (k, v) in now) b.AddArchiveFile(spelled + k[stem.Length..], v);
    }

    /// <summary>The icon path stem: no leading-slash difference and no .ftex extension.</summary>
    internal static string IconStem(string p)
    {
        if (!p.StartsWith('/')) p = "/" + p;
        return p.EndsWith(".ftex", StringComparison.OrdinalIgnoreCase) ? p[..^5] : p;
    }

    /// <summary>
    /// Puts a changed icon path into <see cref="ExistingItem.IconFields"/>: every place the script writes the old path gets
    /// the new one, spelled as the script spells paths (with or without .ftex). Returns why it can't be written.
    /// </summary>
    private static List<string> ApplyIconPath(ExistingItem item)
    {
        foreach (var f in item.IconFields) f.Value = f.Original;
        if (item.IconPathWritten is not { } now || item.IconPath is not { } was || IconStem(was) == now) return [];
        if (item.IconFields.Count == 0) return ["icon: the script names its icon in a way the tool can't change"];
        string text = IconText(item)!;
        foreach (var f in item.IconFields) f.Value = text;
        return [];
    }

    /// <summary>The icon path the build writes for the item, spelled as the script spells its own (null: none).</summary>
    private static string? IconText(ExistingItem item)
    {
        if (item.IconPathWritten is not { } now || item.IconPath is not { } was) return item.IconPath;
        string text = was.EndsWith(".ftex", StringComparison.OrdinalIgnoreCase) ? now + ".ftex" : now;
        return !was.StartsWith('/') && text.StartsWith('/') ? text[1..] : text;
    }

    /// <summary>Builds the mod again: the original files and script, with only the edited text and settings changed, and the
    /// items added from plain mods built next to them (<see cref="BuildAdded"/>).</summary>
    /// <param name="p">The project (from <see cref="TryOpen"/>).</param>
    /// <param name="game">When given, the build of the added items checks their files against the game.</param>
    /// <returns>The build, with any setting that could not be written reported as an error.</returns>
    public static ProjectBuildResult Build(ModProject p, GameData? game = null)
    {
        var data = p.Existing ?? throw new InvalidOperationException("Not an existing-mod project.");
        var errors = new List<string>();
        var warnings = new List<string>();
        var b = MgsvBuilder.From(data.Source);
        b.Name = p.Name; b.Version = p.Version; b.Author = p.Author; b.Website = p.Website; b.Description = p.Description;
        foreach (var item in p.Items.OfType<ExistingItem>()) WriteIcon(b, data.Source, item);

        // Scripts: change only the edited settings.
        var sources = new Dictionary<string, string>(data.Modules, StringComparer.Ordinal);
        // The script of a copy that the project no longer has (removed, or undone) is left out.
        foreach (var (module, copy) in data.Copies) if (!p.Items.Contains(copy)) sources.Remove(module);
        foreach (var item in p.Items.OfType<ExistingItem>())
        {
            var ops = new List<(string[] path, string? lua)>();
            var remove = new List<ExistingField>();
            foreach (var why in ApplyDevelop(item, remove).Concat(ApplyOutfit(item, ops, remove)).Concat(ApplyHeads(item, ops)).Concat(ApplyIconPath(item))) errors.Add($"\"{item.Title}\" · {why}.");
            foreach (var f in item.Fields.Concat(item.DevelopFields).Concat(item.SpecFields).Concat(item.IconFields).Where(f => f.Changed))
            {
                string who = $"\"{item.Title}\" · {(f.Group == "Outfit" ? string.Join(".", f.Path) : f.Label)}";
                if (f.Locked != null) { errors.Add($"{who}: can't be changed ({f.Locked})."); continue; }
                var sites = LuaSource.FindCalls(sources[f.Module], f.CallPath);
                var table = f.Site < sites.Count && f.ArgIndex < sites[f.Site].Arguments.Count ? sites[f.Site].Arguments[f.ArgIndex].Table : null;
                if (table == null) { errors.Add($"{who}: the place in the script was not found."); continue; }
                var updated = LuaSource.SetField(sources[f.Module], table, f.Path, f.ForLua, out var err, f.OverName);
                if (updated == null) errors.Add($"{who}: {err}.");
                else sources[f.Module] = updated;
            }
            // Keys taken out of the R&D row or an outfit branch, then tables: after the values, each against the script as it now stands.
            foreach (var f in remove)
            {
                string who = $"\"{item.Title}\" · {string.Join(".", f.Path)}";
                var sites = LuaSource.FindCalls(sources[f.Module], f.CallPath);
                var table = f.Site < sites.Count && f.ArgIndex < sites[f.Site].Arguments.Count ? sites[f.Site].Arguments[f.ArgIndex].Table : null;
                if (table == null) { errors.Add($"{who}: the place in the script was not found."); continue; }
                var updated = LuaSource.RemoveField(sources[f.Module], table, f.Path, out var err);
                if (updated == null) errors.Add($"{who}: {err}.");
                else sources[f.Module] = updated;
            }
            foreach (var (path, lua) in ops)
            {
                string who = $"\"{item.Title}\" · {string.Join(".", path)}";
                var (module, callPath, site, _) = item.Call;
                var sites = LuaSource.FindCalls(sources[module], callPath);
                var table = site < sites.Count && sites[site].Arguments.Count > 0 ? sites[site].Arguments[0].Table : null;
                if (table == null) { errors.Add($"{who}: the place in the script was not found."); continue; }
                var updated = lua == null ? LuaSource.RemoveField(sources[module], table, path, out var err) : LuaSource.ReplaceTable(sources[module], table, path, lua, out err, overName: true);
                if (updated == null) errors.Add($"{who}: {err}.");
                else sources[module] = updated;
            }
            // Down to 1 look: the GetOutfitInfo line after the R&D row, as the writer gives a one-look outfit.
            if (LookCountNow(item) == false && item.DevelopFields.FirstOrDefault() is { } rd && !sources[rd.Module].Contains("GetOutfitInfo"))
            {
                string src = sources[rd.Module];
                var sites = LuaSource.FindCalls(src, rd.CallPath);
                var call = rd.Site < sites.Count && sites[rd.Site].Arguments.Count > 1 ? sites[rd.Site] : null;
                if (call == null || StatementOf(src, call, out _) is not (_, var end))
                    errors.Add($"\"{item.Title}\" · GetOutfitInfo: the R&D row in the script was not found or is not a statement of its own.");
                else
                {
                    string nl = src.Contains("\r\n") ? "\r\n" : "\n";
                    sources[rd.Module] = src[..end] + LuaWriter.OutfitInfoLine(src[call.Arguments[0].Start..call.Arguments[0].End].Trim()).Replace("\n", nl) + src[end..];
                }
            }
        }
        // Removed items: their calls come out after every value is written (the fields count the calls as the mod has them).
        var cut = new List<(string module, string path, int site)>();
        foreach (var gone in data.Opened.Where(o => p.Items.All(i => i.Id != o.Id)))
        {
            if (RemoveLockOf(data, gone) is { } why) { errors.Add($"\"{gone.Title}\" · can't be taken out of the mod: {why}."); continue; }
            cut.AddRange(CallsOf(gone));
        }
        foreach (var (module, list) in cut.GroupBy(c => c.module).Select(g => (g.Key, g.ToList())))
        {
            string src = sources[module];
            var ranges = list.Select(c => StatementOf(src, LuaSource.FindCalls(src, c.path)[c.site], out _)!.Value).OrderByDescending(r => r.start);
            foreach (var (start, end) in ranges) src = src[..start] + src[end..];
            sources[module] = src;
        }
        // A call that comes after a removed call of the same name in its module is now one place nearer the start.
        int SiteNow(string module, string path, int site) => site - cut.Count(c => c.module == module && c.path == path && c.site < site);
        // New calls last: they would move the place of every later ExtendVanillaOutfit call that a field points to.
        foreach (var item in p.Items.OfType<ExistingItem>())
        {
            if (InsertAlso(sources, item, SiteNow) is { } why) errors.Add($"\"{item.Title}\" · {why}.");
            else if (item.ItemType == ItemKind.Outfit && item.Model is OutfitItem m) foreach (var (path, bytes) in m.BonusFiles()) b.AddArchiveFile(path, bytes);
        }
        // The mod's own script is not merged (owner, Nov 6): two calls with slots for one camo overwrite each other in game.
        var slotCalls = p.Items.OfType<ExistingItem>().SelectMany(i => VanillaLooks(i).Where(v => v.NameIds.Count > 0).Select(v => (v.Outfit, i.Title))
            .Concat(i.Model is OutfitItem m && i.ItemType == ItemKind.Outfit ? m.Spec.AlsoUnder.Where(a => a.Slots.Count > 0).Select(a => (a.Outfit, i.Title)) : []));
        foreach (var g in slotCalls.GroupBy(c => BonusSlots.CamoName(c.Outfit), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            warnings.Add($"{g.Key}: {string.Join(", ", g.Select(c => $"\"{c.Title}\"").Distinct())} each add slots in a call of their own; in game the last call replaces the slots of the earlier ones.");
        // A changed thrown-item script: the mod's blocks for the item never run, and a new module runs the steps.
        foreach (var item in p.Items.OfType<ExistingItem>())
        {
            if (item.Script is not { } script || item.ScriptContext is not { } ctx || script.ToJson() == item.ScriptOriginal) continue;
            if (item.ScriptLock != null) { errors.Add($"\"{item.Title}\" · What it does: can't be changed ({item.ScriptLock})."); continue; }
            if (script.Problems(ctx) is { Count: > 0 } problems) { errors.AddRange(problems.Select(x => $"\"{item.Title}\" · What it does: {x}")); continue; }
            foreach (var module in sources.Keys.ToList()) sources[module] = ThrowScriptReader.SwitchOff(sources[module], ctx.EquipConstant);
            if (script.IsEmpty) continue;
            string stem = $"/mod/modules/SVFM_{p.Id}_{item.Id}_throw", path = stem + ".lua";
            for (int n = 2; data.Source.GameFiles.ContainsKey(path) || sources.ContainsKey(path); n++) path = $"{stem}_{n}.lua";
            sources[path] = LuaWriter.Module([$"What \"{item.Title}\" does when it is thrown, changed with Simple V Framework Maker.", script.Summary() ?? ""],
                [], null, script.Handlers(ctx).ToList(), [script.Poller(ctx)!], null, false, [ThrowScript.Helpers]);
        }
        foreach (var (path, text) in sources) b.AddGameFile(path, new UTF8Encoding(false).GetBytes(text));

        // Text.
        try { foreach (var (path, bytes) in PatchText(data, p.Items.OfType<ExistingItem>().ToList())) b.AddArchiveFile(path, bytes); }
        catch (InvalidDataException e) { errors.Add("The language files could not be rebuilt: " + e.Message); }

        // A sprint sparks effect set in the editor goes into the outfit's package (issue #26). An unchanged one stays as the mod has it.
        foreach (var x in p.Items.OfType<ExistingItem>())
            if (x.Model is OutfitItem o && x.OutfitOriginal is { } was)
                errors.AddRange(SprintSpark.Pack(b, $"\"{x.Title}\"", o.Spec.Branches.Where(kv => kv.Value.Abilities.SprintSpark != was.GetValueOrDefault(kv.Key)?.Abilities.SprintSpark).Select(kv => kv.Value), game));

        // Items added from a plain mod (merge): built as a plain port with a module and text files of its own.
        string? addedLua = BuildAdded(p, data, b, game, errors, warnings, out int selectors, out int parts);

        foreach (var n in data.Notes) warnings.Add(n);
        // A damaged package of the mod made the save throw (the metadata lists each package's files); it is an error here.
        foreach (var issue in b.Check().Where(i => i.Message == "not a valid package")) errors.Add($"{issue.Path}: {issue.Message}");
        var result = new ProjectBuildResult { Mod = b, Lua = string.Join("\n", sources.Values.Append(addedLua ?? "")), LookSelectors = selectors, PartsTypes = parts };
        result.Errors.AddRange(errors);
        result.Warnings.AddRange(warnings);
        return result;
    }

    /// <summary>
    /// Builds the items that were added from a plain mod (they are not <see cref="ExistingItem"/>s) with the normal
    /// pipeline, as a project of their own, and puts the result into the existing mod: a new module next to the mod's
    /// scripts, their archive files, and their text files in the mod's language packages. The project ID of that part is
    /// the mod's ID, or the first one after it (_2, _3, ...) whose module and text file names the mod doesn't use yet (a mod
    /// that SVFM built has them). A file that the mod already has with other contents is an error.
    /// </summary>
    /// <returns>The new module's text, or null when nothing was added.</returns>
    private static string? BuildAdded(ModProject p, ExistingModData data, MgsvBuilder b, GameData? game, List<string> errors, List<string> warnings, out int selectors, out int parts)
    {
        selectors = parts = 0;
        var added = p.Items.Where(i => i is not ExistingItem).ToList();
        if (added.Count == 0) return null;
        foreach (var a in added)
            foreach (var x in p.Items.OfType<ExistingItem>().Where(x => x.Id == a.Id || (a.Key != null && x.Key == a.Key)))
                errors.Add($"\"{a.Title}\" has the same ID or key as \"{x.Title}\" of the mod ({a.Key ?? a.Id}).");

        bool Taken(string id)
        {
            var probe = new ModProject { Id = id, Name = "" };
            string lng = $"/Assets/tpp/lang/ui/{probe.TextFileBase}.";
            return b.GameFiles.ContainsKey(probe.ModulePath) || data.Modules.ContainsKey(probe.ModulePath)
                || b.ArchiveFiles.Where(f => f.Key.StartsWith(LangPackages, StringComparison.Ordinal))
                    .Any(f => Lists(f.Value, lng));
        }
        // A damaged package counts as not using the name; counting it as taken would never end the loop below.
        static bool Lists(byte[] pkg, string lng)
        {
            try { return FoxPackage.Read(pkg).Files.Any(x => x.Path.StartsWith(lng, StringComparison.Ordinal)); }
            catch (InvalidDataException) { return false; }
        }
        string id = p.Id;
        for (int n = 2; Taken(id); n++) id = $"{p.Id}_{n}";
        var sub = new ModProject { Id = id, Name = p.Name, Version = p.Version, Author = p.Author, UnnamedTexturesUnverified = p.UnnamedTexturesUnverified };
        sub.Items.AddRange(added);
        sub.Requires.AddRange(p.Requires);
        var r = ProjectBuilder.Build(sub, game);
        errors.AddRange(r.Errors);
        warnings.AddRange(r.Warnings);
        selectors = r.LookSelectors; parts = r.PartsTypes;

        foreach (var (path, bytes) in r.Mod.GameFiles)
            if (b.GameFiles.ContainsKey(path)) errors.Add($"{path}: the mod already has this file.");
            else b.AddGameFile(path, bytes);
        foreach (var (path, bytes) in r.Mod.ArchiveFiles)
        {
            if (!b.ArchiveFiles.TryGetValue(path, out var mine)) { b.AddArchiveFile(path, bytes); continue; }
            if (mine.AsSpan().SequenceEqual(bytes)) continue;
            if (!path.StartsWith(LangPackages, StringComparison.Ordinal)) { errors.Add($"{path}: the mod already has this file with other contents."); continue; }
            // A language package: the mod's own text files stay, the added items' text file goes in next to them.
            FoxPackage pkg;
            try { pkg = FoxPackage.Read(mine); } catch (InvalidDataException) { errors.Add($"{path}: the mod's language package is damaged, so the added items' text file can't go in."); continue; }
            foreach (var f in FoxPackage.Read(bytes).Files)
                if (pkg.Find(f.Path) != null) errors.Add($"{path}: the mod already has the text file {f.Path}.");
                else pkg.Files.Add(f);
            b.AddArchiveFile(path, pkg.Write());
        }
        return r.Lua;
    }

    private const string LangPackages = "/Assets/tpp/pack/ui/lang/";

    /// <summary>The written calls the item's registration and R&amp;D row are made by: (module, call, site).</summary>
    private static IEnumerable<(string module, string path, int site)> CallsOf(ExistingItem item) =>
        item.Calls.Concat(item.Rows).Select(c => (item.Module, c.Path, c.Site)).Distinct();

    /// <summary>
    /// Why the item can't be taken out of its mod, or null when it can. Removing an item takes its registration call and
    /// its R&amp;D row out of the script, so each must be a statement of its own that the script makes once: a call in a
    /// loop or in a function the script calls again registers other things too, and a call inside a longer expression
    /// can't be cut out without breaking that expression. A weapon's gun and part calls stay: without its R&amp;D row the
    /// weapon is not in the game's lists, and those calls set only values.
    /// </summary>
    /// <param name="data">The opened mod.</param>
    /// <param name="item">One of its items.</param>
    /// <returns>The reason, or null.</returns>
    public static string? RemoveLockOf(ExistingModData data, ExistingItem item)
    {
        if (item.Calls.Count == 0 && item.Rows.Count == 0) return "the script has no call of its own for it";
        if (item.Instances.Count > 0 || item.Calls.Count > 1 || item.Rows.Count > 1)
            return "one call in the script registers it more than once, together with other things";
        if (item.Calls.Concat(item.Rows).Any(c => c.Site < 0)) return "the script makes its call in a way the tool can't follow";
        if (!data.Modules.TryGetValue(item.Module, out var src)) return "its script is not in the mod";
        // Another item made by the same written call (two items from one loop) would go with it.
        foreach (var other in data.Opened.Where(o => o != item && o.Module == item.Module))
            if (CallsOf(other).Intersect(CallsOf(item)).Any()) return $"its call also registers \"{other.Title}\"";
        foreach (var (_, path, site) in CallsOf(item))
        {
            var sites = LuaSource.FindCalls(src, path);
            if (site >= sites.Count) return "the place in the script was not found";
            if (StatementOf(src, sites[site], out var why) == null) return why;
        }
        return null;
    }

    /// <summary>
    /// The text of the statement that is only this call, from the start of its line to the end of it (a trailing ';' and
    /// a line comment go with it), or null when the call is part of a longer line or expression.
    /// </summary>
    private static (int start, int end)? StatementOf(string src, LuaCallSite call, out string why)
    {
        why = "";
        int lineStart = src.LastIndexOf('\n', Math.Max(0, call.Start - 1)) + 1;
        if (call.Start == 0) lineStart = 0;
        if (src[lineStart..call.Start].Any(c => c is not (' ' or '\t'))) { why = "its call is part of a longer line"; return null; }
        // The line before must not leave an expression open ("local x =", "return", "f(a,").
        string before = src[..lineStart].TrimEnd();
        int prevStart = before.LastIndexOf('\n') + 1;
        string prev = before[prevStart..];
        int comment = prev.IndexOf("--", StringComparison.Ordinal);
        if (comment >= 0 && prev.IndexOf('"') < 0 && prev.IndexOf('\'') < 0) prev = prev[..comment];
        prev = prev.TrimEnd();
        if (prev.Length > 0 && (prev[^1] is '=' or '(' or ',' or '{' or '[' or '+' or '-' or '*' or '/' or '<' or '>' or '~' or '.'
            || System.Text.RegularExpressions.Regex.IsMatch(prev, @"(^|[^\w])(return|and|or|not|local|in)$")))
        { why = "its call is part of a longer expression"; return null; }
        int end = call.End;
        while (end < src.Length && src[end] is ' ' or '\t') end++;
        if (end < src.Length && src[end] == ';') end++;
        while (end < src.Length && src[end] is ' ' or '\t') end++;
        if (end < src.Length && src[end..].StartsWith("--", StringComparison.Ordinal) && !src[end..].StartsWith("--[", StringComparison.Ordinal))
            while (end < src.Length && src[end] is not ('\r' or '\n')) end++;
        if (end < src.Length && src[end] is not ('\r' or '\n')) { why = "its call is part of a longer line"; return null; }
        if (end < src.Length && src[end] == '\r') end++;
        if (end < src.Length && src[end] == '\n') end++;
        return (lineStart, end);
    }

    /// <summary>The text in a language package; the app's Chinese row ("cht") is the "chn" package.</summary>
    private static string? TextIn(Dictionary<string, string> per, string package) =>
        per.TryGetValue(package, out var t) && t.Length > 0 ? t : package == "chn" && per.TryGetValue("cht", out var c) ? c : null;

    private static List<(string path, byte[] data)> PatchText(ExistingModData data, List<ExistingItem> items)
    {
        var langs = ReadLanguages(data.Source);
        var ids = items.SelectMany(i => i.Text.Keys).Distinct().ToList();
        var texts = items.SelectMany(i => i.Text).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
        string engBase = langs.TryGetValue("eng", out var eng) && eng.Count > 0 ? eng[0].Base : "svfm_text";
        var created = new Dictionary<string, List<LangSlot>>(StringComparer.Ordinal);
        // An ID that no language file has yet (a name added in the tool) gets the English text in every language file the
        // mod already ships, so the game shows a name there and not the bare ID. An ID that the mod has is left out where
        // the mod leaves it out.
        var isNew = ids.Where(id => !langs.Values.SelectMany(l => l).Any(s => s.File.Get(FoxHash.StrCode32(id)) != null)).ToHashSet(StringComparer.Ordinal);

        foreach (var (package, suffix, _) in ModText.Languages)
        {
            langs.TryGetValue(package, out var slots);
            slots ??= [];
            foreach (var id in ids)
            {
                uint key = FoxHash.StrCode32(id);
                var own = TextIn(texts[id], package);
                texts[id].TryGetValue("eng", out var engText);
                var holder = slots.FirstOrDefault(s => s.File.Get(key) != null);
                if (holder != null)
                {
                    string orig = holder.File.Get(key)!;
                    string? desired = !string.IsNullOrEmpty(own) ? own : package == "eng" ? orig : engText ?? orig;
                    if (desired != orig) { holder.File.Set(key, desired, holder.File.Entries.First(e => e.Key == key).Color); holder.Dirty = true; }
                }
                else if (string.IsNullOrEmpty(own) && isNew.Contains(id) && !string.IsNullOrEmpty(engText) && slots.FirstOrDefault(s => s.Base == engBase) is { } shipped)
                {
                    shipped.File.Set(key, engText); shipped.Dirty = true;
                }
                else if (!string.IsNullOrEmpty(own))
                {
                    var target = slots.FirstOrDefault(s => s.Base == engBase);
                    if (target == null)
                    {
                        target = new LangSlot($"/Assets/tpp/pack/ui/lang/lang_default_data_{package}.fpk", $"/Assets/tpp/lang/ui/{engBase}.{suffix}.lng2", new LangFile());
                        // A new language file holds every ID of the items, English where there is no text of its own.
                        foreach (var other in ids)
                        {
                            var t = TextIn(texts[other], package);
                            if (string.IsNullOrEmpty(t)) texts[other].TryGetValue("eng", out t);
                            if (!string.IsNullOrEmpty(t)) target.File.Set(FoxHash.StrCode32(other), t);
                        }
                        slots.Add(target); langs[package] = slots;
                        if (!created.TryGetValue(package, out var cl)) created[package] = cl = [];
                        cl.Add(target);
                    }
                    target.File.Set(key, own); target.Dirty = true;
                }
            }
        }

        var output = new List<(string, byte[])>();
        foreach (var group in langs.Values.SelectMany(l => l).Where(s => s.Dirty).GroupBy(s => s.Package))
        {
            FoxPackage pkg;
            if (data.Source.ArchiveFiles.TryGetValue(group.Key, out var original)) pkg = FoxPackage.Read(original);
            else pkg = new FoxPackage();
            foreach (var slot in group)
            {
                var file = pkg.Find(slot.Inner);
                if (file != null) file.Data = slot.File.Write();
                else pkg.Files.Add(new PackageFile(slot.Inner, slot.File.Write()));
            }
            output.Add((group.Key, pkg.Write()));
        }
        return output;
    }
}
