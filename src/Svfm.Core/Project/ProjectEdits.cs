using System.Globalization;
using System.Text.Json;
using Svfm.Core.Formats;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>A mod opened together with the main one.</summary>
public sealed record ExtraMod(string Path, string? Inner);

/// <summary>One value that differs from the original: what Compare shows.</summary>
public sealed record DiffRow(string Label, string Was, string Now);

/// <summary>One group of editable values of an item that Compare shows together (one editor tab each).</summary>
public enum EditSection { Name, Texts, Wear, Slots, HeadWear, Faces, Abilities, Camo, Heads, Stats, Sortie, Attack, Script, Settings, Rd, Deploy }

/// <summary>A group of sections that Reset puts back to the original together.</summary>
public enum EditPart { Name, Texts, Rd, Stats, Outfit, Wearers, AlsoUnder, HeadBranches, Sortie, Attack, Script, Settings }

/// <summary>
/// The edits of one open project: the original of each item, the undo history, Reset and Compare against the
/// originals, and the project file (<c>.svfm</c>). Anything new that the user can edit must be in <see cref="ItemState"/>,
/// <see cref="SavedItem"/>, <see cref="Diff"/> and <see cref="Reset"/>; all four are in this file.
/// </summary>
public sealed class ProjectEdits
{
    private readonly IntakeResult _intake;
    private readonly TimeProvider _clock;
    private readonly List<Snapshot> _undo = [];
    private int _savedAt;
    private DateTimeOffset _lastEditAt;
    private readonly Dictionary<ProjectItem, ItemState> _orig = [];
    private Dictionary<string, string> _copyOf => Project.CopyOf;
    private readonly List<(ExtraMod Mod, List<ProjectItem> Items)> _merged = [];

    /// <summary>Starts the edits of a project that was just opened: every item as it is now is its original.</summary>
    /// <param name="intake">The open mod. Its project must not be null.</param>
    /// <param name="clock">The clock for merging typing into one undo step; null is the system clock.</param>
    public ProjectEdits(IntakeResult intake, TimeProvider? clock = null)
    {
        _intake = intake;
        _clock = clock ?? TimeProvider.System;
        if (intake.Project == null) throw new ArgumentException("The mod has no project.", nameof(intake));
        foreach (var i in Project.Items) RememberOriginal(i);
        SyncTwins();
        _origOrder = Project.BonusOrder.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, List<string>> _origOrder;

    /// <summary>The camos whose bonus slot order (now or in the original) holds a slot of the item.</summary>
    private List<string> OrderCamos(ProjectItem i) =>
        [.. Project.BonusOrder.Concat(_origOrder).Where(kv => kv.Value.Any(k => k.StartsWith(i.Id + "/", StringComparison.Ordinal))).Select(kv => kv.Key).Distinct(StringComparer.OrdinalIgnoreCase)];

    private ModProject Project => _intake.Project!;

    public bool CanUndo => _undo.Count > 0;
    /// <summary>True when the project changed since it was opened or last saved.</summary>
    public bool Dirty => _undo.Count != _savedAt;
    /// <summary>The field of the last checkpoint ("" after a one-off action or an undo).</summary>
    public string LastField { get; private set; } = "";

    /// <summary>Mods whose items were merged into the project and are still in it (listed in the project file).</summary>
    public IReadOnlyList<ExtraMod> Merged => _merged.Where(m => m.Items.Any(i => Project.Items.Contains(i))).Select(m => m.Mod).ToList();

    // ---- Undo ----

    public sealed record SpecState(DevelopRow Develop, Dictionary<PlayerBranch, OutfitBranch> Branches, Dictionary<PlayerBranch, HeadBranch>? Heads = null, IReadOnlyList<AlsoUnder>? Also = null);
    public sealed record BehaviourState(int Ammo = 1, double Fuse = 0, double Active = 0, bool Bruise = false, DogGearBehaviour? Dog = null, HorseTackBehaviour? Horse = null, bool InfiniteAmmo = false, Dictionary<string, string>? Weapon = null, SupportSnapshot? Support = null, string? Script = null, string? Preset = null, bool LockLifted = false);
    public sealed record ItemState(ProjectItem Item, string Title, Dictionary<string, Dictionary<string, string>> Text, IconSettings? Icon, SpecState? Spec, object[]? Fields = null, BehaviourState? Behaviour = null, IntakeResult.IconSnap? IconSnap = null, bool Synced = false);
    private sealed record Snapshot(string Name, string Version, string Author, string Website, string Description, List<ItemState> Items, Dictionary<string, List<string>> BonusOrder);

    /// <summary>Call before a change. Typing in one field is one undo step (a pause of 1.5 seconds starts another).</summary>
    /// <param name="field">Which field is being edited, or "" for a one-off action.</param>
    /// <returns>False when the change joins the last undo step.</returns>
    public bool Checkpoint(string field = "")
    {
        var now = _clock.GetUtcNow();
        if (field.Length > 0 && field == LastField && (now - _lastEditAt).TotalSeconds < 1.5) { _lastEditAt = now; return false; }
        LastField = field; _lastEditAt = now;
        _undo.Add(Take());
        if (_undo.Count > 100) { _undo.RemoveAt(0); _savedAt = Math.Max(0, _savedAt - 1); }
        return true;
    }

    /// <summary>Puts the project back as it was at the last checkpoint.</summary>
    /// <returns>False when there is nothing to undo.</returns>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        var p = Project;
        p.Name = s.Name; p.Version = s.Version; p.Author = s.Author; p.Website = s.Website; p.Description = s.Description; p.BonusOrder = s.BonusOrder;
        p.Items.Clear();
        foreach (var st in s.Items) { p.Items.Add(st.Item); Restore(st, true); }
        LastField = "";
        return true;
    }

    /// <summary>Puts an item back as it was in <paramref name="st"/>; <paramref name="icon"/> false leaves its icon as it is.</summary>
    private void Restore(ItemState st, bool icon)
    {
        st.Item.Title = st.Title;
        st.Item.Synced = st.Synced;
        st.Item.Text.Clear();
        foreach (var (k, v) in st.Text) st.Item.Text[k] = new Dictionary<string, string>(v);
        RestoreSpec(st.Item, st.Spec);
        RestoreFields(st.Item, st.Fields);
        RestoreBehaviour(st.Item, st.Behaviour);
        if (icon && st.IconSnap != null) _intake.RestoreIcon(st.Item, st.IconSnap);
        _intake.SyncSlotIcons(st.Item);
    }

    /// <summary>The number of undo steps now; the next change starts a step of its own.</summary>
    /// <returns>The depth to give to <see cref="UndoTo"/>.</returns>
    public int Mark() { LastField = ""; return _undo.Count; }

    /// <summary>Undoes every step made after <see cref="Mark"/> returned <paramref name="depth"/>.</summary>
    /// <returns>True when a step was undone.</returns>
    public bool UndoTo(int depth)
    {
        bool any = false;
        while (_undo.Count > depth && Undo()) any = true;
        return any;
    }

    /// <summary>Records that the project as it is now is saved.</summary>
    public void MarkSaved() => _savedAt = _undo.Count;

    private Snapshot Take()
    {
        var p = Project;
        return new Snapshot(p.Name, p.Version, p.Author, p.Website, p.Description, p.Items.Select(Snap).ToList(), p.BonusOrder);
    }

    private ItemState Snap(ProjectItem i) => new(i, i.Title, i.Text.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value)), _intake.IconOf(i), SpecOf(i), FieldsOf(i), BehaviourOf(i), _intake.SnapIcon(i), i.Synced);

    // The values of each item kind are on the item class (ProjectItem.RdRow, NameLangId, InfoLangId, TakeBehaviour, PutBehaviour).
    public static BehaviourState? BehaviourOf(ProjectItem i) => i.TakeBehaviour();
    private static void RestoreBehaviour(ProjectItem i, BehaviourState? b) { if (b != null) i.PutBehaviour(b); }
    private static object[]? FieldsOf(ProjectItem i) => (i as ExistingItem)?.Fields.Select(f => f.Value).ToArray();
    private static void RestoreFields(ProjectItem i, object[]? v, bool? parts = null) { if (i is ExistingItem e && v != null && v.Length == e.Fields.Count) for (int k = 0; k < v.Length; k++) if (parts == null || IsStatField(e, e.Fields[k]) == parts) e.Fields[k].Value = v[k]; }

    /// <summary>Whether a setting of an existing item is a weapon's gun or part value (the Stats tab), not one of the Settings tab.</summary>
    public static bool IsStatField(ExistingItem e, ExistingField f) => e.StatGroups.Contains(f.Group);

    /// <summary>The R&amp;D row of an item (null for looks without a row, and for an item of an existing mod whose script gives it none).</summary>
    public static DevelopRow? DevelopOf(ProjectItem i) => i.RdRow;
    public static void SetDevelop(ProjectItem i, DevelopRow d) => i.RdRow = d;
    /// <summary>Issue #64: gives an item the R&amp;D row of a vanilla item. Abilities are not in the vanilla data, so they stay.</summary>
    public static void CopyVanillaRow(ProjectItem i, Svfm.Core.Knowledge.VanillaDevelopRow row) { if (DevelopOf(i) is { } d) SetDevelop(i, row.Apply(d)); }
    /// <summary>The outfit whose branches the Abilities and Camo tabs edit: the item itself, or an existing outfit's model.</summary>
    public static OutfitItem? OutfitOf(ProjectItem i) => i as OutfitItem ?? (i as ExistingItem)?.Model as OutfitItem;
    /// <summary>The head option an item edits: the item itself, or the model of an existing head option.</summary>
    public static HeadOptionItem? HeadOf(ProjectItem i) => i as HeadOptionItem ?? (i as ExistingItem)?.Model as HeadOptionItem;
    /// <summary>The lang ID of the item's name (null when the item has none, e.g. a head option without an R&amp;D row).</summary>
    public static string? NameIdOf(ProjectItem i) => i.NameLangId;
    /// <summary>The lang ID of the item's description.</summary>
    public static string? InfoIdOf(ProjectItem i) => i.InfoLangId;

    private static SpecState? SpecOf(ProjectItem i) => i.TakeSpec();
    private static void RestoreSpec(ProjectItem i, SpecState? s) { if (s != null) i.PutSpec(s); }

    // ---- The mod's original: each item as it was when the tool made it (or when the copy was made) ----

    /// <summary>Remembers an item as it is now; Compare and Reset go back to this.</summary>
    public void RememberOriginal(ProjectItem i) => _orig[i] = Snap(i);
    public ItemState? OriginalOf(ProjectItem i) => _orig.GetValueOrDefault(i);

    /// <summary>Records a copy of an item that the intake made, after a checkpoint. The copy's original is the copy as it is now.</summary>
    public void AddCopy(ProjectItem source, ProjectItem copy)
    {
        _copyOf[copy.Id] = _copyOf.GetValueOrDefault(source.Id, source.Id);
        RememberOriginal(copy);
    }

    /// <summary>The ID of the item an item is a copy of, or null.</summary>
    /// <summary>
    /// Issue #106: records a new copy (<see cref="AddCopy"/>) and gives it the source's current state, under the copy's own
    /// IDs. The copy's original is the source's original: the maker made the copy from the unedited mod.
    /// </summary>
    public void TakeOver(ProjectItem source, ProjectItem copy)
    {
        AddCopy(source, copy);
        if (OriginalOf(source) is not { } orig) return;
        var fresh = JsonSerializer.SerializeToNode(SaveItem(copy), Json)!.AsObject();
        var now = Snap(source);
        Restore(orig, false);
        var was = JsonSerializer.SerializeToNode(SaveItem(source), Json)!.AsObject();
        Restore(now, false);
        var cur = JsonSerializer.SerializeToNode(SaveItem(source), Json)!.AsObject();
        string[] own = [nameof(SavedItem.Id), nameof(SavedItem.CopyOf), nameof(SavedItem.Title), nameof(SavedItem.Text), nameof(SavedItem.Icon), nameof(SavedItem.CustomIcon), nameof(SavedItem.SlotIcons)];
        // The IDs: the strings where the source's original and the fresh copy differ, side by side.
        // shortcut: a string found only in the current state (a new path the user picked) keeps the source's ID; upgrade when a copy needs it.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b)
        {
            if (a is System.Text.Json.Nodes.JsonObject oa && b is System.Text.Json.Nodes.JsonObject ob) { foreach (var (k, v) in oa) if (ob.TryGetPropertyValue(k, out var w)) Walk(v, w); }
            else if (a is System.Text.Json.Nodes.JsonArray aa && b is System.Text.Json.Nodes.JsonArray ab && aa.Count == ab.Count) { for (int n = 0; n < aa.Count; n++) Walk(aa[n], ab[n]); }
            else if (a is System.Text.Json.Nodes.JsonValue va && b is System.Text.Json.Nodes.JsonValue vb && va.TryGetValue(out string? sa) && vb.TryGetValue(out string? sb) && sa != sb) map.TryAdd(sa, sb);
        }
        foreach (var (k, v) in was) if (!own.Contains(k)) Walk(v, fresh[k]);
        void Rekey(System.Text.Json.Nodes.JsonNode? n)
        {
            System.Text.Json.Nodes.JsonNode? To(System.Text.Json.Nodes.JsonNode? v) =>
                v is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue(out string? s) && map.TryGetValue(s, out var t) ? t : v?.DeepClone();
            if (n is System.Text.Json.Nodes.JsonObject o) foreach (var k in o.Select(p => p.Key).ToList()) { var r = To(o[k]); o[k] = r; Rekey(r); }
            else if (n is System.Text.Json.Nodes.JsonArray a) for (int i = 0; i < a.Count; i++) { var r = To(a[i]); a[i] = r; Rekey(r); }
        }
        foreach (var k in own) cur.Remove(k);
        Rekey(cur);
        var si = cur.Deserialize<SavedItem>(Json)!;
        si.Id = copy.Id; si.CopyOf = _copyOf[copy.Id]; si.Title = source.Title + " (copy)";
        // The text IDs: the name and the description, else the copy's own prefix (or the copy's ID of an imported item).
        var freshText = copy.Text.Keys.ToList();
        si.Text = copy.Text.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        var lines = source.RdRow?.UpgradeLines ?? [];
        foreach (var (key, per) in source.Text)
        {
            if (lines.Contains(key)) continue;
            string? to = key == source.NameLangId ? copy.NameLangId : key == source.InfoLangId ? copy.InfoLangId
                : key.Replace(source.Prefix, copy.Prefix, StringComparison.Ordinal) is var byPrefix && byPrefix != key && (source is not ExistingItem || freshText.Contains(byPrefix)) ? byPrefix
                : freshText.FirstOrDefault(f => f.StartsWith(key + "_" + copy.Id, StringComparison.Ordinal));
            if (to == null) continue;
            si.Text[to] = per.ToDictionary(kv => kv.Key, kv => kv.Key == "eng" && key == source.NameLangId ? kv.Value + " (copy)" : kv.Value);
        }
        // Issue #123: each own upgrade line gets a new text ID of the copy, so that the two items' lines are independent.
        if (si.Develop is { } sd && lines.Count > 0)
            si.Develop = sd.WithUpgradeLines(lines.Select(l => source.Text.TryGetValue(l, out var per) ? NewLineText(si.Text, copy.Prefix, per) : l));
        ApplyItem(copy, si, []);
    }

    public string? CopySourceOf(ProjectItem i) => _copyOf.GetValueOrDefault(i.Id);

    /// <summary>Issue #123: puts the text of an own upgrade line under the first free ID "&lt;prefix&gt;_up_N" and returns that ID.</summary>
    public static string NewLineText(Dictionary<string, Dictionary<string, string>> text, string prefix, IReadOnlyDictionary<string, string> per)
    {
        int n = 0;
        while (text.ContainsKey($"{prefix}_up_{n}")) n++;
        text[$"{prefix}_up_{n}"] = new Dictionary<string, string>(per, StringComparer.Ordinal);
        return $"{prefix}_up_{n}";
    }

    /// <summary>The items of a merged mod that are in the project now.</summary>
    public IReadOnlyList<ProjectItem> ItemsMergedFrom(ExtraMod mod) =>
        _merged.Where(m => m.Mod == mod).SelectMany(m => m.Items).Where(Project.Items.Contains).Distinct().ToList();

    /// <summary>The copies of a body port for one wearer kind (<see cref="OutfitCopies"/>) that are in the project now.</summary>
    public IReadOnlyList<ProjectItem> CopiesFor(ProjectItem source, CopyKind kind) => kind == CopyKind.Quiet
        ? ItemsMergedFrom(OutfitCopies.QuietExtra(source))
        : Project.Items.Where(i => CopySourceOf(i) == source.Id && OutfitCopies.IsWornBy(i, kind)).ToList();

    /// <summary>Adds another mod's items to the project, after a checkpoint; undo takes them out again.</summary>
    public void Merge(ExtraMod mod, IntakeResult other)
    {
        _intake.Merge(other);
        _merged.Add((mod, [.. other.Project!.Items]));
        foreach (var i in other.Project!.Items) RememberOriginal(i);
        SyncTwins();
    }

    // ---- Twins and Sync (GLOSSARY.md): while Sync is on, the name, the description, the R&D row and the icon of two twins stay equal ----

    private sealed record TwinState(string Title, string Name, string Info, DevelopRow? Develop, RgbaImage? Icon, string Lines = "");
    /// <summary>Each twin as the last pass of <see cref="SyncTwins"/> left it: the twin that differs from it now is the one the user changed.</summary>
    private readonly Dictionary<ProjectItem, TwinState> _twinSeen = [];

    private TwinState TwinStateOf(ProjectItem i)
    {
        string T(string? id) => id != null && i.Text.TryGetValue(id, out var per) ? string.Join("\n", per.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)) : "";
        return new(i.Title, T(i.NameLangId), T(i.InfoLangId), i.RdRow, _intake.Icons.GetValueOrDefault(i.Id), string.Join("\n\n", (i.RdRow?.UpgradeLines ?? []).Select(T)));
    }

    /// <summary>The twin of an item that is in the project; null when it has none.</summary>
    public ProjectItem? TwinOf(ProjectItem i) => i.TwinId == null ? null : Project.Items.FirstOrDefault(x => x.Id == i.TwinId);

    /// <summary>
    /// Gives each twin with Sync on the values of the twin that changed since the last pass. Call it after each change;
    /// it makes no undo step, so the copy is part of the step of the change.
    /// </summary>
    /// <returns>True when a value was copied.</returns>
    public bool SyncTwins()
    {
        bool any = false;
        foreach (var a in Project.Items.Where(i => i.Synced).ToList())
        {
            if (TwinOf(a) is not { Synced: true } b || string.CompareOrdinal(a.Id, b.Id) > 0) continue;
            TwinState sa = TwinStateOf(a), sb = TwinStateOf(b);
            if (sa with { Icon = null } != sb with { Icon = null } || !IntakeResult.SameImage(sa.Icon, sb.Icon))
            {
                var (from, to) = _twinSeen.GetValueOrDefault(b) != sb && _twinSeen.GetValueOrDefault(a) == sa ? (b, a) : (a, b);
                _intake.CopyTwin(from, to);
                (sa, sb, any) = (TwinStateOf(a), TwinStateOf(b), true);
            }
            _twinSeen[a] = sa; _twinSeen[b] = sb;
        }
        return any;
    }

    /// <summary>Turns Sync of an item and its twin on or off (one undo step). Turned on, the twin gets the item's values.</summary>
    /// <returns>False when nothing changed.</returns>
    public bool SetSync(ProjectItem item, bool on)
    {
        if (TwinOf(item) is not { } twin || item.Synced == on) return false;
        Checkpoint();
        item.Synced = twin.Synced = on;
        if (on) { _intake.CopyTwin(item, twin); _twinSeen[item] = TwinStateOf(item); _twinSeen[twin] = TwinStateOf(twin); }
        return true;
    }

    /// <summary>The coarse state of an outfit's settings (abilities, camo, sortie slots, heads) for comparing: one text.</summary>
    public static string BranchesKey(ProjectItem i) => OutfitOf(i) is { } o ? BranchesKey(o.Spec.Branches) : "";
    public static string BranchesKey(Dictionary<PlayerBranch, OutfitBranch> b) => JsonSerializer.Serialize(b.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value), Json);
    static string SharedBranchesKey(Dictionary<PlayerBranch, OutfitBranch> b, Func<PlayerBranch, bool> keep) => BranchesKey(b.Where(k => keep(k.Key)).ToDictionary());
    static string WearersLabel(IEnumerable<PlayerBranch> b) => string.Join(", ", b.OrderBy(k => k).Select(PlayerBranches.Label));

    /// <summary>The part that Reset puts back for a section.</summary>
    public static EditPart PartOf(EditSection s) => s switch
    {
        EditSection.Name => EditPart.Name,
        EditSection.Texts => EditPart.Texts,
        EditSection.Abilities or EditSection.Camo or EditSection.Slots or EditSection.Heads => EditPart.Outfit,
        EditSection.Wear => EditPart.Wearers,
        EditSection.HeadWear or EditSection.Faces => EditPart.HeadBranches,
        EditSection.Rd or EditSection.Deploy => EditPart.Rd,
        EditSection.Stats => EditPart.Stats,
        EditSection.Sortie => EditPart.Sortie,
        EditSection.Attack => EditPart.Attack,
        EditSection.Script => EditPart.Script,
        _ => EditPart.Settings,
    };

    /// <summary>The sections that Compare of a tab shows. Name, text &amp; icon also holds the wearer card (issue #6).</summary>
    public static EditSection[] TabSections(EditSection tab) => tab == EditSection.Name ? [EditSection.Name, EditSection.Wear, EditSection.HeadWear] : [tab];

    /// <summary>The parts that Reset of a tab puts back. The slots tab also holds "also under other outfits".</summary>
    public static EditPart[] TabParts(EditSection tab) => tab == EditSection.Slots ? [PartOf(tab), EditPart.AlsoUnder] : [.. TabSections(tab).Select(PartOf).Distinct()];

    /// <summary>Puts parts of an item back as the original had them. Takes one checkpoint first, so it is one undo step.</summary>
    /// <returns>False when the item has no original (nothing changed).</returns>
    public bool Reset(ProjectItem i, params EditPart[] parts)
    {
        if (!_orig.TryGetValue(i, out var o)) return false;
        Checkpoint();
        foreach (var part in parts)
            switch (part)
            {
                case EditPart.Name:
                case EditPart.Texts:
                    i.Title = o.Title; i.Text.Clear();
                    if (part == EditPart.Name && TwinOf(i) is { } tw) i.Synced = tw.Synced = o.Synced;
                    foreach (var (k, v) in o.Text) i.Text[k] = new Dictionary<string, string>(v);
                    if (part == EditPart.Name && o.IconSnap != null) _intake.RestoreIcon(i, o.IconSnap);
                    // Issue #123: the upgrade lines are on the Name tab, with the texts of the item's own lines.
                    if (part == EditPart.Name && o.Spec?.Develop is { } od && DevelopOf(i) is { } cd) SetDevelop(i, cd with { UpgradeLineIds = od.UpgradeLineIds });
                    // The slot icons are icons too: each look gets back the icon path the original had.
                    if (part == EditPart.Name && i is OutfitItem ni && o.Spec != null)
                        foreach (var (k, b) in ni.Spec.Branches.ToList())
                            if (o.Spec.Branches.TryGetValue(k, out var ob)) ni.Spec.Branches[k] = b with { Base = b.Base with { IconFtexPath = ob.Base.IconFtexPath }, Variants = b.Variants.Select((v, n) => v with { IconFtexPath = n < ob.Variants.Count ? ob.Variants[n].IconFtexPath : null }).ToList() };
                    break;
                case EditPart.Rd: if (o.Spec != null) SetDevelop(i, o.Spec.Develop with { UpgradeLineIds = DevelopOf(i)?.UpgradeLineIds }); break;
                case EditPart.Stats when i is SupportWeaponItem st && o.Behaviour != null:
                    st.Spec.Ammo = o.Behaviour.Ammo; st.Spec.FuseTime = o.Behaviour.Fuse; st.Spec.ActiveTime = o.Behaviour.Active;
                    if (o.Behaviour.Support is { ThrowAs: { } ta } os0) st.Spec.SetThrowAs(ta, os0.ThrowAsAttack, os0.ThrowAsBlast);
                    if (o.Behaviour.Support is { } os1) { st.Spec.Preset = os1.Preset; if (os1.Grade is { } g1) st.Spec.Grade = g1; }
                    break;
                case EditPart.Stats when i is ExistingItem ex:
                {
                    // What it is thrown as goes back, and a default script with it (issue #112).
                    string? thrown = ex.ThrowAs;
                    RestoreFields(i, o.Fields, parts: true);
                    if (thrown != null && ex.ThrowAs is { } back && ex.Script != null && ex.ScriptLock == null) ex.Script.SwapDefaultEffect(thrown, back);
                    ex.Preset = o.Behaviour?.Preset;
                    break;
                }
                case EditPart.Stats: RestoreBehaviour(i, o.Behaviour); break;
                case EditPart.Script when i is ExistingItem { Script: { } rs, ScriptOriginal: { } ro } rx: rs.CopyFrom(ThrowScript.FromJson(ro)); rx.LockLifted = o.Behaviour?.LockLifted ?? false; break;
                case EditPart.Sortie or EditPart.Attack or EditPart.Script when i is SupportWeaponItem sw && o.Behaviour?.Support is { } os:
                {
                    var now = SupportSnapshot.Take(sw.Spec);
                    if (part == EditPart.Sortie) { now.DamageType = os.DamageType; now.BarDamage = os.BarDamage; now.BarDurability = os.BarDurability; now.BarEffectTime = os.BarEffectTime; now.HideBarDamage = os.HideBarDamage; now.HideBarDurability = os.HideBarDurability; now.HideBarEffectTime = os.HideBarEffectTime; now.DurabilityMax = os.DurabilityMax; now.EffectTimeMax = os.EffectTimeMax; }
                    else if (part == EditPart.Attack) { now.AttackId = os.AttackId; now.BlastId = os.BlastId; now.OwnAttack = os.OwnAttack; now.OwnAttackRow = os.OwnAttackRow; now.OwnBlast = os.OwnBlast; now.OwnAttackFrom = os.OwnAttackFrom; now.OwnBlastFrom = os.OwnBlastFrom; }
                    else now.Script = os.Script;
                    now.Restore(sw.Spec);
                    break;
                }
                // Outfit puts back the values of the current wearers; Wearers puts back the list, and a kept wearer keeps its values.
                case EditPart.Outfit:
                    if (OutfitOf(i) is { } oi && o.Spec != null)
                        foreach (var k in oi.Spec.Branches.Keys.ToList()) if (o.Spec.Branches.TryGetValue(k, out var ov)) oi.Spec.Branches[k] = ov;
                    break;
                case EditPart.Wearers:
                    if (OutfitOf(i) is { } wi && o.Spec != null)
                    {
                        var now = new Dictionary<PlayerBranch, OutfitBranch>(wi.Spec.Branches);
                        wi.Spec.Branches.Clear();
                        foreach (var (k, v) in o.Spec.Branches) wi.Spec.Branches[k] = now.GetValueOrDefault(k) ?? v;
                    }
                    break;
                case EditPart.AlsoUnder:
                    if (OutfitOf(i) is { } ao && o.Spec != null) ao.Spec.AlsoUnder = o.Spec.Also ?? [];
                    // A Move orders the whole camo, so Reset puts back the camo's order, the other items' slots included.
                    if (OrderCamos(i) is { Count: > 0 } camos)
                    {
                        var order = new Dictionary<string, List<string>>(Project.BonusOrder, StringComparer.OrdinalIgnoreCase);
                        foreach (var c in camos) if (_origOrder.TryGetValue(c, out var l)) order[c] = [.. l]; else order.Remove(c);
                        Project.BonusOrder = order;
                    }
                    break;
                case EditPart.HeadBranches:
                    if (HeadOf(i) is { } hi && o.Spec?.Heads != null) { hi.Spec.Branches.Clear(); foreach (var (k, v) in o.Spec.Heads) hi.Spec.Branches[k] = v; }
                    break;
                case EditPart.Settings: RestoreFields(i, o.Fields, i is ExistingItem { StatGroups.Count: > 0 } ? false : null); break;
            }
        _intake.SyncSlotIcons(i);
        return true;
    }

    // ---- Compare with the mod's original ----

    /// <summary>What differs from the original in one section of an item.</summary>
    public List<DiffRow> Diff(ProjectItem item, EditSection section)
    {
        var rows = new List<DiffRow>();
        var o = OriginalOf(item);
        if (o == null) return rows;
        string? nameId = NameIdOf(item), infoId = InfoIdOf(item);
        var lineIds = (o.Spec?.Develop?.UpgradeLines ?? []).Concat(DevelopOf(item)?.UpgradeLines ?? []).ToHashSet();
        void Text(bool main)
        {
            foreach (var id in o.Text.Keys.Union(item.Text.Keys).Distinct())
            {
                bool isMain = id == nameId || id == infoId || lineIds.Contains(id);
                if (isMain != main) continue;
                var was = o.Text.GetValueOrDefault(id) ?? []; var now = item.Text.GetValueOrDefault(id) ?? [];
                foreach (var lang in was.Keys.Union(now.Keys).Distinct())
                {
                    string a = was.GetValueOrDefault(lang) ?? "", b = now.GetValueOrDefault(lang) ?? "";
                    if (a != b) rows.Add(new DiffRow($"{(id == nameId ? "Name" : id == infoId ? "Description" : lineIds.Contains(id) ? "Upgrade line" : "Text")} · {lang}", a, b));
                }
            }
        }
        switch (section)
        {
            case EditSection.Name:
                Text(true);
                if (o.Spec?.Develop is { } ld0 && DevelopOf(item) is { } ld1 && ld0.UpgradeLineIds != ld1.UpgradeLineIds)
                    rows.Add(new DiffRow("Upgrade lines", $"{ld0.UpgradeLines.Count} lines", $"{ld1.UpgradeLines.Count} lines, changed"));
                // An item with no lang ID (a head option with no R&D row) keeps its name on Title alone, so the text
                // comparison above has nothing to look at. Compare the Title directly or a rename shows no change.
                if (nameId == null && o.Title != item.Title) rows.Add(new DiffRow("Name · eng", o.Title, item.Title));
                if (o.Synced != item.Synced) rows.Add(new DiffRow("Sync with twin", o.Synced ? "on" : "off", item.Synced ? "on" : "off"));
                if (o.Icon != null && _intake.IconOf(item) is { } cur && cur != o.Icon) rows.Add(new DiffRow("Icon", "as ported", "framing or style changed"));
                else if (o.Icon == null && item is ExistingItem && _intake.IconOf(item) is { Style: IconStyle.Custom } ci) rows.Add(new DiffRow("Icon", "packaged with the mod", ci.FromView ? "generated from the 3D preview" : "uploaded picture"));
                if (item is OutfitItem si && o.Spec != null && o.Spec.Branches.Count > 0)
                {
                    var was = o.Spec.Branches.OrderBy(k => k.Key).First().Value;
                    var wasLooks = was.Variants.Prepend(was.Base).ToList();
                    var now = IntakeResult.SlotLooks(si);
                    for (int n = 0; n < now.Count; n++)
                        if (now[n].IconFtexPath is { } sp && _intake.SlotIconImage(sp) != null && sp != (n < wasLooks.Count ? wasLooks[n].IconFtexPath : null))
                            rows.Add(new DiffRow($"Slot icon · slot {n + 1}", "the outfit icon", "a slot icon"));
                }
                break;
            case EditSection.Texts: Text(false); break;
            case EditSection.Rd or EditSection.Deploy:
                if (o.Spec?.Develop is { } d0 && DevelopOf(item) is { } d1)
                {
                    bool rd = section == EditSection.Rd;
                    if (rd && d0.Grade != d1.Grade) rows.Add(new DiffRow("Grade", d0.Grade?.ToString() ?? "none", d1.Grade?.ToString() ?? "none"));
                    if (rd && d0.GmpCost != d1.GmpCost) rows.Add(new DiffRow("Development cost", d0.GmpCost.ToString(), d1.GmpCost.ToString()));
                    if (!rd && d0.UsageGmpCost != d1.UsageGmpCost) rows.Add(new DiffRow("Deployment cost", d0.UsageGmpCost.ToString(), d1.UsageGmpCost.ToString()));
                    if (rd && d0.InitiallyAvailable != d1.InitiallyAvailable) rows.Add(new DiffRow("Already developed", d0.InitiallyAvailable ? "yes" : "no", d1.InitiallyAvailable ? "yes" : "no"));
                    if (rd && d0 with { Grade = d1.Grade, GmpCost = d1.GmpCost, UsageGmpCost = d1.UsageGmpCost, InitiallyAvailable = d1.InitiallyAvailable, UpgradeLineIds = d1.UpgradeLineIds } != d1)
                        rows.Add(new DiffRow("Staff, materials and unlock rules", "as installed", "changed"));
                    if (!rd && (d0.Usage1, d0.Usage1Count, d0.Usage2, d0.Usage2Count) != (d1.Usage1, d1.Usage1Count, d1.Usage2, d1.Usage2Count))
                        rows.Add(new DiffRow("Deployment resources", "as installed", "changed"));
                }
                break;
            case EditSection.Stats when item is ExistingItem sx:
                foreach (var f in sx.Fields.Where(f => IsStatField(sx, f) && f.Changed))
                    rows.Add(new DiffRow($"{f.Group} · {f.Label}", Convert.ToString(f.Original, CultureInfo.InvariantCulture) ?? "", Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? ""));
                break;
            case EditSection.Stats:
                if (item is WeaponItem && o.Behaviour?.Weapon is { } w0 && BehaviourOf(item)?.Weapon is { } w1)
                {
                    if (w0.GetValueOrDefault(WeaponTuning.CamoKey) is var c0 && w1.GetValueOrDefault(WeaponTuning.CamoKey) is var c1 && c0 != c1)
                        rows.Add(new DiffRow("Camo", c0 ?? "default paint", c1 ?? "default paint"));
                    foreach (var (key, now) in w1)
                        if (key != WeaponTuning.CamoKey && w0.GetValueOrDefault(key) != now)
                        {
                            int bar = key.IndexOf('|');
                            if (key[..bar] == WeaponTuning.SlotKey) { rows.Add(new DiffRow($"Part · {key[(bar + 1)..]}", w0.GetValueOrDefault(key) ?? "", now)); continue; }
                            var kind = Enum.Parse<WeaponPartKind>(key[..bar]);
                            // A swapped part's values follow the swap (its "Part" row); sights are not editable.
                            if (kind == WeaponPartKind.Sight || !w0.ContainsKey(key)) continue;
                            string label = WeaponPartFields.For(kind).FirstOrDefault(f => f.Path == key[(bar + 1)..])?.Label ?? key;
                            rows.Add(new DiffRow($"{kind} · {label}", w0.GetValueOrDefault(key) ?? "", now));
                        }
                }
                else if (o.Behaviour is { } b0 && BehaviourOf(item) is { } b1)
                    foreach (var pr in typeof(BehaviourState).GetProperties().Where(x => x.Name is not ("Support" or "Script" or "LockLifted")))
                    {
                        var x = pr.GetValue(b0); var y = pr.GetValue(b1);
                        if (!Equals(x, y)) rows.Add(new DiffRow(pr.Name, x?.ToString() ?? "default", y?.ToString() ?? "default"));
                    }
                if (o.Behaviour?.Support?.ThrowAs is { } t0 && item is SupportWeaponItem { Spec.ThrowAs: var t1 } && t0 != t1) rows.Add(new DiffRow("Thrown as", t0, t1));
                if (o.Behaviour?.Support is { } p0 && item is SupportWeaponItem { Spec: var p1 })
                {
                    if (p0.Preset != p1.Preset) rows.Add(new DiffRow("Preset", p0.Preset ?? "Custom", p1.Preset ?? "Custom"));
                    if (p0.Grade is { } g0 && g0 != p1.Grade) rows.Add(new DiffRow("Grade", g0.ToString(), p1.Grade.ToString()));
                }
                break;
            case EditSection.Wear:
                if (OutfitOf(item) is { } wo && o.Spec != null && (WearersLabel(o.Spec.Branches.Keys), WearersLabel(wo.Spec.Branches.Keys)) is var (l0, l1) && l0 != l1)
                    rows.Add(new DiffRow("Wearers", l0, l1));
                break;
            case EditSection.Abilities or EditSection.Camo or EditSection.Slots or EditSection.Heads:
                // Only the wearers in both lists: a wearer added or removed shows under "Wearers".
                if (OutfitOf(item) is { } bo && o.Spec != null && SharedBranchesKey(bo.Spec.Branches, o.Spec.Branches.ContainsKey) != SharedBranchesKey(o.Spec.Branches, bo.Spec.Branches.ContainsKey)) rows.Add(new DiffRow("Outfit settings", item is ExistingItem ? "as the mod has it" : "as ported", "changed (abilities, camo, sortie slots or heads)"));
                // The slots tab also holds "also under other outfits", so it reports that change too.
                if (section == EditSection.Slots && OutfitOf(item) is { } so && o.Spec != null)
                {
                    var was = o.Spec.Also ?? [];
                    static string K(IReadOnlyList<AlsoUnder> l) => JsonSerializer.Serialize(l, Json);
                    if (K(was) != K(so.Spec.AlsoUnder))
                        rows.Add(new DiffRow("Also under other outfits", T(was), T(so.Spec.AlsoUnder)));
                    // #84: each outfit with the looks of its slots, so a changed look shows too.
                    static string T(IReadOnlyList<AlsoUnder> l) => l.Count == 0 ? "none" : string.Join(", ", l.Select(a => a.Slots.Count == 0 ? a.Outfit
                        : $"{a.Outfit} ({string.Join(", ", a.Slots.DistinctBy(x => (x.Index, x.LangId)).Select(x => x.Index < 0 ? "main look" : $"look {x.Index + 2}"))})"));
                }
                // shortcut: compares the stored lists, so a Move and its Move back still shows; upgrade by comparing the merged order.
                if (section == EditSection.Slots)
                    foreach (var c in OrderCamos(item))
                        if (!(_origOrder.GetValueOrDefault(c) ?? []).SequenceEqual(Project.BonusOrder.GetValueOrDefault(c) ?? []))
                            rows.Add(new DiffRow($"Slot order · {c}", "as ported", "moved"));
                break;
            case EditSection.Script when item is ExistingItem { Script: { } ds } de:
                if (ds.ToJson() != de.ScriptOriginal) rows.Add(new DiffRow("Steps", "as the mod has it", "changed"));
                break;
            case EditSection.Sortie or EditSection.Attack or EditSection.Script:
                if (o.Behaviour?.Support is { } sa && BehaviourOf(item)?.Support is { } sb)
                {
                    string[] names = section switch
                    {
                        EditSection.Sortie => ["DamageType", "BarDamage", "BarDurability", "BarEffectTime", "HideBarDamage", "HideBarDurability", "HideBarEffectTime", "DurabilityMax", "EffectTimeMax"],
                        EditSection.Attack => ["AttackId", "BlastId", "OwnAttack", "OwnBlast"],
                        _ => ["Script"],
                    };
                    foreach (var pr in typeof(SupportSnapshot).GetProperties().Where(x => names.Contains(x.Name)))
                    {
                        static string Show(object? x) => x switch { null => "automatic", Dictionary<string, string> d => string.Join(", ", d.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")), _ => x.ToString() ?? "" };
                        string x0 = Show(pr.GetValue(sa)), x1 = Show(pr.GetValue(sb));
                        if (x0 != x1) rows.Add(new DiffRow(pr.Name == "Script" ? "Steps" : pr.Name, pr.Name == "Script" ? "as ported" : x0, pr.Name == "Script" ? "changed" : x1));
                    }
                }
                break;
            case EditSection.HeadWear or EditSection.Faces:
                if (HeadOf(item) is { } hi2 && o.Spec?.Heads is { } oh && !oh.Keys.OrderBy(k => k).SequenceEqual(hi2.Spec.Branches.Keys.OrderBy(k => k)))
                    rows.Add(new DiffRow("Worn with", string.Join(", ", oh.Keys.OrderBy(k => k).Select(PlayerBranches.Label)), string.Join(", ", hi2.Spec.Branches.Keys.OrderBy(k => k).Select(PlayerBranches.Label))));
                break;
            case EditSection.Settings:
                if (item is ExistingItem ex)
                    for (int k = 0; k < ex.Fields.Count; k++)
                        if (!Equals(ex.Fields[k].Value, ex.Fields[k].Original) && !IsStatField(ex, ex.Fields[k])) rows.Add(new DiffRow(ex.Fields[k].Label, Convert.ToString(ex.Fields[k].Original, CultureInfo.InvariantCulture) ?? "", Convert.ToString(ex.Fields[k].Value, CultureInfo.InvariantCulture) ?? ""));
                break;
        }
        return rows;
    }

    // ---- Project file (.svfm): the source mod's path plus what the user changed ----

    public sealed class SavedItem { public string Id { get; set; } = ""; public string? CopyOf { get; set; } public string Title { get; set; } = ""; public Dictionary<string, Dictionary<string, string>> Text { get; set; } = []; public IconSettings? Icon { get; set; } public DevelopRow? Develop { get; set; } public Dictionary<PlayerBranch, OutfitBranch>? Branches { get; set; } public List<AlsoUnder>? Also { get; set; } public List<PlayerBranch>? HeadOff { get; set; } public List<string>? FieldValues { get; set; } public BehaviourState? Behaviour { get; set; } public byte[]? CustomIcon { get; set; }
        /// <summary>The slot icons the looks point at (issue #88): icon path to PNG; null when every slot uses the outfit icon.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, byte[]>? SlotIcons { get; set; }
        /// <summary>Sync with the twin; null for an item with no twin.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? Sync { get; set; } }
    /// <summary>The slot icons the outfit's looks point at, as PNG, for the project file.</summary>
    private Dictionary<string, byte[]>? SlotIconsOf(ProjectItem i)
    {
        if (i is not OutfitItem o) return null;
        var d = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in o.Spec.Branches.Values.SelectMany(b => b.Variants.Prepend(b.Base)).Select(l => l.IconFtexPath).OfType<string>())
            if (_intake.SlotIconImage(path) is { } img) d[path] = Png.Encode(img);
        return d.Count == 0 ? null : new Dictionary<string, byte[]>(d);
    }

    /// <summary>A look that points at a slot icon the file did not bring (a damaged picture) uses the outfit icon.</summary>
    private void ClearMissingSlotIcons(OutfitItem o)
    {
        foreach (var (k, b) in o.Spec.Branches.ToList())
        {
            Look Fix(Look l) => l.IconFtexPath is { } p && IntakeResult.IsSlotIconPath(p) && _intake.SlotIconImage(p) == null ? l with { IconFtexPath = null } : l;
            o.Spec.Branches[k] = b with { Base = Fix(b.Base), Variants = b.Variants.Select(Fix).ToList() };
        }
    }

    /// <summary>The project file format this build writes. 2: slot icons (#88). 3: the values of #95 and #96.</summary>
    public const int LatestFormat = 3;

    public sealed class SavedProject
    {
        public int Format { get; set; } = LatestFormat;
        public string SourceMod { get; set; } = "";
        public string? SourceInner { get; set; }
        public List<ExtraMod> Extras { get; set; } = [];
        public List<ExtraMod> Merged { get; set; } = [];
        public string Name { get; set; } = ""; public string Version { get; set; } = ""; public string Author { get; set; } = ""; public string Website { get; set; } = ""; public string Description { get; set; } = "";
        public List<SavedItem> Items { get; set; } = [];
        /// <summary>The bonus slot order of each vanilla camo (<see cref="ModProject.BonusOrder"/>); null when none is set.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, List<string>>? BonusOrder { get; set; }
        /// <summary>Ids of the mod's own items that were removed (null in older files: every item not listed was removed).</summary>
        public List<string>? Removed { get; set; }
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    /// <summary>For reading a project file, which a user can edit: a null in a property that cannot be null, or an enum
    /// number, is a JsonException.</summary>
    private static readonly JsonSerializerOptions ReadJson = new() { IncludeFields = true, RespectNullableAnnotations = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) } };

    /// <summary>The project file text. Call <see cref="MarkSaved"/> after the file is written.</summary>
    /// <param name="sourceMod">The path of the mod the project was made from.</param>
    /// <param name="sourceInner">The .mgsv inside it when it is a zip.</param>
    /// <param name="extras">The mods opened together with it.</param>
    public string Save(string sourceMod, string? sourceInner, IReadOnlyList<ExtraMod> extras)
    {
        var p = Project;
        var sp = new SavedProject { Removed = _orig.Keys.Where(k => !_copyOf.ContainsKey(k.Id) && p.Items.All(x => x.Id != k.Id)).Select(k => k.Id).Distinct().ToList(), SourceMod = sourceMod, SourceInner = sourceInner, Extras = [.. extras], Merged = [.. Merged], Name = p.Name, Version = p.Version, Author = p.Author, Website = p.Website, Description = p.Description };
        foreach (var i in p.Items)
            sp.Items.Add(SaveItem(i));
        if (p.BonusOrder.Count > 0) sp.BonusOrder = p.BonusOrder.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        return JsonSerializer.Serialize(sp, Json);
    }

    private SavedItem SaveItem(ProjectItem i) => new SavedItem { Id = i.Id, CopyOf = _copyOf.GetValueOrDefault(i.Id), Title = i.Title, Text = i.Text.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value)), Icon = _intake.IconOf(i), Develop = DevelopOf(i), Branches = OutfitOf(i)?.Spec.Branches.ToDictionary(k => k.Key, k => k.Value), Also = OutfitOf(i)?.Spec.AlsoUnder.ToList(), HeadOff = HeadOf(i) is { } hi && OriginalOf(i)?.Spec?.Heads is { } oh ? oh.Keys.Where(k => !hi.Spec.Branches.ContainsKey(k)).ToList() : null, Behaviour = BehaviourOf(i), CustomIcon = _intake.IconOf(i) is { Style: IconStyle.Custom } ci ? Png.Encode(_intake.CustomIconImage(i, ci.Custom)!) : null, FieldValues = (i as ExistingItem)?.Fields.Select(f => Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? "").ToList(), SlotIcons = SlotIconsOf(i), Sync = i.TwinId != null ? i.Synced : null };

    /// <summary>Reads a project file. The caller opens the mods it names, then calls <see cref="Apply"/>.</summary>
    public static SavedProject Read(string json)
    {
        var sp = JsonSerializer.Deserialize<SavedProject>(json, ReadJson) ?? throw new InvalidDataException("Not a project file.");
        // A hand-edited file can hold null where the options cannot see it (in a list or a dictionary).
        static bool BadLook(Look? l) => l == null || l.HeadOptions.Any(h => h == null);
        static bool BadBranch(OutfitBranch? b) => b == null || BadLook(b.Base) || b.Variants.Any(BadLook) || b.HeadOptions.Any(h => h == null) || b.PrimaryColors.Any(c => c == null) || b.SecondaryColors.Any(c => c == null);
        if (sp.Items.Any(i => i == null || i.Text.Values.Any(v => v == null || v.Values.Any(t => t == null)) || i.Branches?.Values.Any(BadBranch) == true || i.Also?.Any(a => a == null || a.Slots == null || a.Slots.Any(s => s == null)) == true || i.SlotIcons?.Values.Any(v => v == null) == true)
            || sp.Extras.Any(e => e == null) || sp.Merged.Any(e => e == null) || sp.BonusOrder?.Values.Any(v => v == null) == true)
            throw new InvalidDataException("The project file has an empty (null) value where a value must be.");
        // An outfit needs one wearer at least; the editor shows its first branch.
        if (sp.Items.Any(i => i.Branches is { Count: 0 })) throw new InvalidDataException("The project file has an outfit with no wearers.");
        // A newer format can hold edits this build doesn't know; opening and saving it again would drop them.
        if (sp.Format > LatestFormat) throw new InvalidDataException("This project was made by a newer Simple V Framework Maker. Update the app to open it.");
        // Before Nov 14 2026, Duplicate of a head option made no copy: it put the same item in the list a second time and
        // gave it the name of the copy. A file saved after that has the item two times, each as a copy of itself. It is
        // one item and not a copy, and it gets back the name it had.
        foreach (var same in sp.Items.Where(i => i.CopyOf != null && i.CopyOf == i.Id).GroupBy(i => i.Id).ToList())
        {
            var keep = same.First();
            keep.CopyOf = null;
            if (keep.Title.EndsWith(" (copy)", StringComparison.Ordinal)) keep.Title = keep.Title[..^" (copy)".Length];
            foreach (var again in same.Skip(1)) sp.Items.Remove(again);
        }
        // Issue #26: a project saved with the old default sprint sparks path (not in the game) gets the new default.
        foreach (var b in sp.Items.Select(i => i.Branches).OfType<Dictionary<PlayerBranch, OutfitBranch>>())
            foreach (var k in b.Keys.ToList())
                if (b[k].Abilities.SprintSpark == SprintSpark.OldDefaultPath) b[k] = b[k] with { Abilities = b[k].Abilities with { SprintSpark = SprintSpark.DefaultPath } };
        return sp;
    }

    /// <summary>
    /// Applies a saved project's edits to this project, which was just opened from the mods the file names (merged
    /// mods included). The result has no undo history and counts as saved.
    /// </summary>
    /// <returns>The titles of the saved items whose edits could not be put back (the mod changed since the save).</returns>
    public List<string> Apply(SavedProject sp)
    {
        var notApplied = new List<string>();
        var p = Project;
        p.Name = sp.Name; p.Version = sp.Version; p.Author = sp.Author; p.Website = sp.Website; p.Description = sp.Description;
        p.BonusOrder = new(sp.BonusOrder ?? [], StringComparer.OrdinalIgnoreCase);
        // The bandanas that respect development are added by the Heads worn tab, not by the port: add them again.
        if (sp.Items.Any(si => si.Id is "bandana" or "infbandana" && si.CopyOf == null) && GatedBandanas.KeysIn(p).Count == 0) GatedBandanas.AddTo(p);
        var originals = p.Items.ToDictionary(i => i.Id);
        foreach (var si in sp.Items.Where(i => i.CopyOf != null))
            if (originals.TryGetValue(si.CopyOf!, out var src))
            {
                // A source that refuses (a lock rule that changed since the save, the mod's files changed) loses the copy only, not the whole project.
                try
                {
                    var c = _intake.Duplicate(src, si.Id); _copyOf[c.Id] = si.CopyOf!; RememberOriginal(c);
                    originals[c.Id] = c;
                }
                catch (Exception e) when (e is FileNotFoundException or InvalidDataException or InvalidOperationException or ArgumentException) { notApplied.Add(si.Title); }
            }
            else notApplied.Add(si.Title);
        // The "+ Bandana" heads (#126) are added by the Worn with card, not by the port: add them again.
        foreach (var si in sp.Items.Where(i => i.CopyOf == null && i.Id.EndsWith("_bandana", StringComparison.Ordinal) && !originals.ContainsKey(i.Id)))
            if (originals.GetValueOrDefault(si.Id[..^"_bandana".Length]) is HeadOptionItem h && _intake.Game is { } game)
                try { var c = BandanaCombo.Add(game, p, h); RememberOriginal(c); originals[c.Id] = c; }
                catch (Exception e) when (e is InvalidOperationException or InvalidDataException or FileNotFoundException) { }
        var keep = sp.Items.Select(i => i.Id).ToHashSet();
        var removed = sp.Removed?.ToHashSet();
        foreach (var gone in p.Items.Where(i => removed != null ? removed.Contains(i.Id) : !keep.Contains(i.Id)).ToList()) p.Items.Remove(gone);
        foreach (var si in sp.Items)
            if (originals.TryGetValue(si.Id, out var it)) ApplyItem(it, si, notApplied);
            else if (si.CopyOf == null) notApplied.Add(si.Title);
        var order = sp.Items.Select(i => i.Id).ToList();
        var sorted = p.Items.OrderBy(i => order.IndexOf(i.Id) is var ix && ix < 0 ? int.MaxValue : ix).ToList();
        p.Items.Clear(); p.Items.AddRange(sorted);
        SyncTwins();
        _undo.Clear(); _savedAt = 0; LastField = "";
        return notApplied;
    }

    /// <summary>Puts the saved edits of one item on it (a project file, or a copy that takes over its source: issue #106).</summary>
    private void ApplyItem(ProjectItem it, SavedItem si, List<string> notApplied)
    {
        it.Title = si.Title; it.Text.Clear();
        if (si.Sync is { } sync && it.TwinId != null) it.Synced = sync;
        foreach (var (k, v) in si.Text) it.Text[k] = new Dictionary<string, string>(v);
        bool badIcon = false;
        if (si.Icon is { Style: IconStyle.Custom } && si.CustomIcon != null)
        {
            // A damaged picture loses the icon only, not the whole project.
            try { _intake.LoadCustomIcon(it, si.Icon.Custom, Png.Decode(si.CustomIcon)); }
            catch (InvalidDataException) { badIcon = true; notApplied.Add(si.Title + " (its icon)"); }
        }
        if (si.Icon != null && !badIcon && _intake.IconOf(it) != si.Icon) _intake.ApplyIcon(it, si.Icon);
        // A hand-edited file can hold bad text (or NaN, which the Lua can't hold): then none of the settings are put back.
        if (it is ExistingItem ex && si.FieldValues is { } fv && fv.Count == ex.Fields.Count
            && fv.Select((t, k) => ex.Fields[k].Type switch
            {
                FieldType.Text => t,
                FieldType.Number => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null,
                // A switch can hold a dash attack's attack name (issue #96).
                _ => bool.TryParse(t, out var bo) ? bo : Abilities.IsDashAttackId(t) ? t : (object?)null,
            }).ToList() is var vals && vals.All(v => v != null))
            for (int k = 0; k < vals.Count; k++) ex.Fields[k].Value = vals[k]!;
        else if (it is ExistingItem && si.FieldValues is { Count: > 0 }) notApplied.Add(si.Title + " (its settings)");
        // Restore checks the hand-editable Lua and script text before it changes a value.
        try { RestoreBehaviour(it, si.Behaviour); }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            if (!notApplied.Contains(si.Title + " (its settings)")) notApplied.Add(si.Title + " (its settings)");
        }
        if (HeadOf(it) is { } hio && si.HeadOff != null) foreach (var k in si.HeadOff) hio.Spec.Branches.Remove(k);
        if (si.Develop != null && (it is not OutfitItem || si.Branches != null)) RestoreSpec(it, new SpecState(si.Develop, si.Branches ?? [], null, si.Also));
        else if (it is ExistingItem && si.Branches != null) RestoreSpec(it, new SpecState(new DevelopRow(), si.Branches, null, si.Also));
        if (si.SlotIcons != null)
        {
            // A damaged picture or a bad path loses the slot icons only: the slots then use the outfit icon.
            bool ok = true;
            foreach (var (path, png) in si.SlotIcons)
                try { ok &= _intake.LoadSlotIcon(path, Png.Decode(png)); }
                catch (InvalidDataException) { ok = false; }
            if (!ok) notApplied.Add(si.Title + " (its slot icons)");
        }
        if (it is OutfitItem so) ClearMissingSlotIcons(so);
        _intake.SyncSlotIcons(it);
    }
}
