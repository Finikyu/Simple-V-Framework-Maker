using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>Clone and Remove of one mod-added slot in the Sortie mockup (issue #30); Move one place left or right (issue #62).</summary>
/// <remarks>#62: <c>CanRemove</c> is the outfit's slot rule (<see cref="OutfitSpec.CanRemoveSlot"/>).</remarks>
public sealed record SlotEdit(Action Copy, Action Remove, Action<int> Move, Func<int, bool> CanMove, Func<bool> CanRemove);

/// <summary>#87: the name of a slot the mod adds: its text, the edit that sets it, and the cell text the row shows for it.</summary>
public sealed record SlotName(string Text, Action<string> Set, Func<string, string> Show);

/// <summary>One row of the Sortie Prep mockup: a vanilla suit the project adds looks to, or an outfit of the project.</summary>
public sealed class SortieRow : Observable
{
    private int _look;
    private string _name, _desc;
    public SortieRow(string key, string name, string description, int gmp, Bitmap? icon, IReadOnlyList<string> looks, int first)
    {
        Key = key; _name = name.ToUpperInvariant(); _desc = description; Gmp = gmp; Icon = icon; _looks = [.. looks];
        _look = Math.Clamp(first, 0, Math.Max(0, looks.Count - 1));
        Prev = new Command(() => LookIndex = (_look + Looks.Count - 1) % Looks.Count, () => HasCycle);
        Next = new Command(() => LookIndex = (_look + 1) % Looks.Count, () => HasCycle);
        CloneSlot = new Command(() => Edit?.Copy(), () => Edit != null);
        RemoveSlot = new Command(() => Edit?.Remove(), () => CanRemove);
        AddSlot = new Command(() => Add?.Invoke(), () => Add != null);
        MoveLeft = new Command(() => Edit?.Move(-1), () => Edit?.CanMove(-1) == true);
        MoveRight = new Command(() => Edit?.Move(1), () => Edit?.CanMove(1) == true);
    }

    public string Key { get; }
    public string Name => _name;
    /// <summary>The description pane's title keeps the case the text has (the game writes it in capitals itself).</summary>
    public string Title => Name;
    public string Description => _desc;
    /// <summary>Issue #49: only a mod row's name and description are edited here. A vanilla row shows the game's own text.</summary>
    public bool CanEditText => Item != null;
    /// <summary>Runs an edit without a rebuild of the rows: the box being typed in must keep the focus.</summary>
    public Action<Action> Guard { get; init; } = a => a();
    /// <summary>The same field as the Name tab (<see cref="ItemVm.Name"/>), so Undo, Compare, Reset and the project file cover it.</summary>
    public string EditName
    {
        get => Item?.Name ?? "";
        set { if (Item is not { } it || it.Name == value) return; Guard(() => it.Name = value); _name = (value.Trim() is { Length: > 0 } n ? n : it.Title).ToUpperInvariant(); Raise(); RaiseName(); }
    }
    public string EditDescription
    {
        get => Item?.Description ?? "";
        set { if (Item is not { } it || it.Description == value) return; Guard(() => it.Description = value); _desc = value; Raise(); Raise(nameof(Description)); }
    }
    private void RaiseName() { Raise(nameof(Name)); Raise(nameof(Title)); Raise(nameof(Letter)); }
    public int Gmp { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon != null;
    /// <summary>The placeholder when a vanilla suit's game icon is not found.</summary>
    public string Letter => Name.Length > 0 ? Name[..1] : "?";
    private readonly string[] _looks;
    public IReadOnlyList<string> Looks => _looks;
    /// <summary>The game shows ◀ ▶ only when the row has more than one look (owner, 3 Nov).</summary>
    public bool HasCycle => Looks.Count > 1;
    public int LookIndex
    {
        get => _look;
        set { if (Set(ref _look, value)) { Raise(nameof(Look)); Raise(nameof(IsGameLook)); Raise(nameof(SlotText)); Raise(nameof(CanRenameSlot)); Raise(nameof(IconName)); Raise(nameof(HasSlotEdit)); Raise(nameof(RemoveTip)); CloneSlot.Raise(); RemoveSlot.Raise(); MoveLeft.Raise(); MoveRight.Raise(); } }
    }
    public string Look => _looks.Length > 0 ? _looks[_look] : "";
    /// <summary>The shown look is a game part (NAKED, STANDARD, SCARF): it cannot be changed, so the cell is greyed.</summary>
    public bool IsGameLook => _look < GameSlots;
    /// <summary>#87: one entry per look; null for a game slot, which the mockup does not rename.</summary>
    public IReadOnlyList<SlotName?> Names { get; init; } = [];
    private SlotName? ShownName => _look < Names.Count ? Names[_look] : null;
    private readonly Dictionary<int, string> _typed = [];
    public bool CanRenameSlot => ShownName != null;
    /// <summary>#87: the name of the shown slot. A typed name changes the row's cell at once, and the box keeps the focus.</summary>
    public string SlotText
    {
        get => ShownName is { } s ? _typed.GetValueOrDefault(_look, s.Text) : "";
        set
        {
            if (ShownName is not { } s || SlotText == value) return;
            Guard(() => s.Set(value)); _typed[_look] = value; _looks[_look] = s.Show(value);
            Raise(); Raise(nameof(Look));
        }
    }
    /// <summary>#87: the file name of the icon each look shows on its sortie slot, as the game reads it.</summary>
    public IReadOnlyList<string> IconNames { get; init; } = [];
    public string IconName => _look < IconNames.Count ? IconNames[_look] : "";
    public Command Prev { get; }
    public Command Next { get; }
    /// <summary>The reason the bionic arm is not available, shown greyed out (issue #12); empty when Snake or Avatar is a wearer.</summary>
    public string ArmNote { get; init; } = "";
    public bool HasArmNote => ArmNote.Length > 0;
    /// <summary>The project item of a mod row; null on a vanilla row. Its icon sources change the row's icon (issue #29).</summary>
    public ItemVm? Item { get; init; }
    /// <summary>Only a mod row's icon opens the flyout of icon sources; a vanilla suit's game icon is not changed here.</summary>
    public bool CanChangeIcon => Item != null;
    /// <summary>Generate from the model: the item is selected first, because Generate mode uses the 3D panel of the selected item.</summary>
    public Command? Generate { get; init; }
    /// <summary>Issue #30: one entry per look; null for a game slot, or a slot the app cannot change (a script's own look).</summary>
    public IReadOnlyList<SlotEdit?> Edits { get; init; } = [];
    /// <summary>The number of the game's own slots at the start of a vanilla row; 0 on a mod row.</summary>
    public int GameSlots { get; init; }
    /// <summary>Issue #30: adds a new slot of the mod's own to this uniform; null when no outfit of the mod can add one here.</summary>
    public Action? Add { get; init; }
    private SlotEdit? Edit => _look < Edits.Count ? Edits[_look] : null;
    public bool HasSlotEdit => Edit != null;
    /// <summary>#62: an outfit keeps at least one slot, its own or under a vanilla suit. Game slots never go.</summary>
    public bool CanRemove => Edit != null && Edit.CanRemove();
    public string RemoveTip => CanRemove ? "Remove this slot" : "This is the last slot of the outfit. An outfit must keep one slot. Add a slot first.";
    public string AddTip => Add != null ? "Add a new slot of this mod" : "This mod adds these looks with its script. You cannot add a slot here.";
    public Command CloneSlot { get; }
    public Command RemoveSlot { get; }
    public Command AddSlot { get; }
    /// <summary>Issue #62: moves the shown slot one place. A slot under a vanilla outfit moves among that outfit's slots of the same mod item.</summary>
    public Command MoveLeft { get; }
    public Command MoveRight { get; }
    /// <summary>The arrows show on the selected row only; hidden by opacity so the row does not change height.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter Shown =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(b => b ? 1 : 0);
}

/// <summary>
/// A mockup of the game's Sortie Prep uniform list for the whole project (.docs/Research_Sortie_Menu_Mockup.md):
/// the vanilla suits the project adds looks to, in game order, then the project's own outfits at the bottom.
/// A mod that only adds looks to vanilla suits has no row of its own. Only a mod row's icon can be changed here (issue #29).
/// </summary>
public sealed class SortieVm : Observable
{
    private readonly EditorVm _ed;
    private SortieRow? _sel;
    private bool _editing;
    public SortieVm(EditorVm ed) { _ed = ed; }
    private void Guard(Action edit) { _editing = true; try { edit(); } finally { _editing = false; } }

    public ObservableCollection<SortieRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public SortieRow? Selected
    {
        get => _sel;
        set
        {
            var old = _sel;
            if (!Set(ref _sel, value)) return;
            if (old != null) { old.CloneSlot.CanExecuteChanged -= OnRowCan; old.RemoveSlot.CanExecuteChanged -= OnRowCan; }
            if (value != null) { value.CloneSlot.CanExecuteChanged += OnRowCan; value.RemoveSlot.CanExecuteChanged += OnRowCan; }
            Raise(nameof(HasSelected)); Prev.Raise(); Next.Raise(); OnRowCan(null, EventArgs.Empty);
        }
    }
    private void OnRowCan(object? s, EventArgs e) { CloneChosen.Raise(); RemoveChosen.Raise(); }
    public bool HasSelected => _sel != null;
    /// <summary>Left and Right cycle the looks of the selected row, as in the game.</summary>
    public Command Prev => _prev ??= new Command(() => _sel?.Prev.Execute(null), () => _sel?.HasCycle == true);
    public Command Next => _next ??= new Command(() => _sel?.Next.Execute(null), () => _sel?.HasCycle == true);
    private Command? _prev, _next;

    /// <summary>Issue #62: the rows picked with Ctrl or Shift; Clone and Remove act on each one's shown slot.</summary>
    public IReadOnlyList<SortieRow> Chosen { get; set; } = [];
    public Command CloneChosen => _cloneChosen ??= new Command(() => ApplyToChosen(false), () => _sel?.CloneSlot.CanExecute(null) == true);
    public Command RemoveChosen => _removeChosen ??= new Command(() => ApplyToChosen(true), () => _sel?.RemoveSlot.CanExecute(null) == true);
    private Command? _cloneChosen, _removeChosen;

    private void ApplyToChosen(bool remove)
    {
        // The rows are built again after each edit, so each target is found again by its key.
        var targets = (Chosen.Count > 0 ? Chosen : _sel is { } s ? [s] : []).Select(r => (r.Key, r.LookIndex)).ToList();
        foreach (var (key, look) in targets)
        {
            if (Rows.FirstOrDefault(r => r.Key == key) is not { } row) continue;
            row.LookIndex = look;
            var cmd = remove ? row.RemoveSlot : row.CloneSlot;
            if (cmd.CanExecute(null)) cmd.Execute(null);
        }
    }

    /// <summary>The vanilla parts in the order the game cycles them (owner: Naked - Standard - Scarf), and their labels.</summary>
    private static readonly string[] PartOrder = ["NAKED", "NORMAL", "NORMAL_SCARF"];
    private static string PartLabel(string p) => p switch
    {
        "NAKED" => "NAKED", "NORMAL" => "STANDARD", "NORMAL_SCARF" => "SCARF", _ => p.Replace('_', ' '),
    };

    // The game icons of vanilla suits (issue #28), decoded once per opened mod: the rows are built again on each edit.
    private IntakeResult? _iconsOf;
    private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private Bitmap? SuitIcon(string suit)
    {
        var intake = _ed.Session.Intake;
        if (!ReferenceEquals(intake, _iconsOf)) { _icons.Clear(); _iconsOf = intake; }
        if (_icons.TryGetValue(suit, out var b)) return b;
        var img = intake?.VanillaSuitIcon(suit);
        if (img != null) { using var ms = new MemoryStream(Png.Encode(img)); b = new Bitmap(ms); }
        return _icons[suit] = b;
    }

    private static string TextOf(ProjectItem i, string? id) =>
        id != null && i.Text.TryGetValue(id, out var per) && per.TryGetValue("eng", out var t) && t.Length > 0 ? t : id ?? "";

    /// <summary>
    /// Builds the rows again from the project; keeps each row's look where the row still exists. The selected row stays,
    /// unless <paramref name="followItem"/>: then the row of the item selected in the editor is selected.
    /// </summary>
    public void Rebuild(bool followItem = false)
    {
        if (_editing) return; // a name typed in the mockup (issue #49): the row already shows it
        var keep = Rows.ToDictionary(r => r.Key, r => r.LookIndex);
        string? selKey = followItem ? null : _sel?.Key;
        Rows.Clear();
        var items = _ed.Session.Project?.Items ?? [];

        // Vanilla suit name → the project's look names added to it (owner: they follow the suit's own parts). Each added
        // look has its Clone, Remove and Move (issues #30, #62), or null when the app cannot change it. A slot that the build
        // merges into one call for the camo has a key: those slots follow the project's BonusOrder and Move swaps them
        // across items (owner, Nov 6, Q8). An existing V Framework mod keeps its own calls, so Move stays within one outfit.
        bool merges = _ed.Session.Project?.Existing == null;
        // Each slot: its merge key, its cell text, its rename (#87, null when it has no lang id), its icon (null: the suit's), and its edits.
        var added = new Dictionary<string, List<(string? Key, string Name, SlotName? Rename, string? Icon, Func<int, List<string>, SlotEdit>? Make)>>(StringComparer.OrdinalIgnoreCase);
        var adders = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        // #87: a slot under a vanilla suit is named by its lang id, as the Sortie slots tab names it.
        SlotName? Rename(ItemVm? iv, string? lang) => iv == null || lang == null ? null
            : new SlotName(iv.GetText(lang, "eng", false), t => iv.SetText(lang, "eng", t), t => t.Length > 0 ? t : lang);
        void Add(string suit, ProjectItem it, ItemVm? iv, IEnumerable<(string? Key, string? Lang, string? Icon)> slots)
        {
            // A call that adds no looks (a port's head options on a vanilla suit) does not change the list.
            var list = slots.Select(n => (n.Key, Name: TextOf(it, n.Lang), n.Lang, n.Icon)).Where(n => n.Name.Length > 0).ToList();
            if (list.Count == 0) return;
            if (!added.TryGetValue(suit, out var l)) added[suit] = l = [];
            l.AddRange(list.Select(n => (n.Key, n.Name, Rename(iv, n.Lang), n.Icon, (Func<int, List<string>, SlotEdit>?)null)));
        }
        foreach (var it in items)
        {
            var ivm = _ed.Items.FirstOrDefault(v => v.Item == it);
            if (ProjectEdits.OutfitOf(it) is { } o)
            {
                var also = ivm?.Also;
                var first = o.Spec.Branches.Values.FirstOrDefault();
                for (int ai = 0; ai < o.Spec.AlsoUnder.Count; ai++)
                {
                    var a = o.Spec.AlsoUnder[ai]; int t = ai; string key = "v:" + a.Outfit, suit = a.Outfit;
                    // The outfit stays a row with no slot of the mod left, so Add slot can still add one (issue #30).
                    if (!added.TryGetValue(a.Outfit, out var l)) added[a.Outfit] = l = [];
                    // One slot per lang id, in list order: the order the build writes them when no BonusOrder is set.
                    var groups = a.Slots.GroupBy(x => (x.Index, x.LangId)).ToList();
                    for (int gi = 0; gi < groups.Count; gi++)
                    {
                        var (index, lang) = groups[gi].Key; int p = gi;
                        // The slot shows its look's icon; a look with none shows the vanilla suit's (ExtendVanillaOutfit writes no ICON).
                        var src = first == null ? null : index < 0 ? first.Base : index < first.Variants.Count ? first.Variants[index] : null;
                        l.Add((merges ? BonusSlots.Key(it.Id, lang) : null, TextOf(it, lang), Rename(ivm, lang), src?.IconFtexPath, also == null ? null : (at, keys) => new SlotEdit(
                            () => Do(key, at + 1, () => also.CloneSlot(t, index, lang), true),
                            () => Do(key, at, () => also.RemoveSlot(t, index, lang), true),
                            merges ? d => Do(key, at + d, () => Reorder(suit, keys, at, at + d), true) : d => Do(key, at + d, () => also.MoveSlot(t, index, lang, d), true),
                            merges ? d => at + d >= 0 && at + d < keys.Count : d => p + d >= 0 && p + d < groups.Count,
                            () => o.Spec.CanRemoveSlot(own: false))));
                    }
                    if (also != null) adders.TryAdd(a.Outfit, () => Do(key, int.MaxValue, () => also.AddSlot(t)));
                }
            }
            if (it is ExtraLooksItem x && (x.Spec.Outfit ?? (x.Spec.PartsType is { } pt ? GameKnowledge.PartsTypes.FirstOrDefault(p => p.PartsType == pt)?.Name : null)) is { } suit2)
                Add(suit2, it, ivm, x.Spec.Branches.Values.OrderByDescending(b => b.Variants.Count).FirstOrDefault()?.Variants
                    .Select(v => (merges && x.Spec.Outfit != null ? BonusSlots.Key(it.Id, v.DisplayName ?? "") : (string?)null, v.DisplayName, v.IconFtexPath)) ?? []);
            if (it is ExistingItem e)
                foreach (var (suitName, ids) in ExistingMod.VanillaLooks(e)) Add(suitName, it, ivm, ids.Select(id => ((string?)null, (string?)id, (string?)null)));
        }
        // The merged slots in the build's order, then the slots of calls the build does not merge.
        var order = _ed.Session.Project?.BonusOrder;
        var rowLooks = added.ToDictionary(kv => kv.Key, kv =>
        {
            var m = kv.Value.Where(n => n.Key != null).ToList();
            var sorted = BonusSlots.Ordered(order?.GetValueOrDefault(BonusSlots.CamoName(kv.Key)), [.. m.Select(n => n.Key!)]).Select(k => m[k]).ToList();
            List<string> keys = [.. sorted.Select(n => n.Key!)];
            return sorted.Concat(kv.Value.Where(n => n.Key == null)).Select((n, at) => (n.Name, n.Rename, n.Icon, Edit: n.Make?.Invoke(at, keys))).ToList();
        }, StringComparer.OrdinalIgnoreCase);

        var game = GameKnowledge.EnglishNamesGame;
        var camos = GameKnowledge.Camos;
        foreach (var (suit, names) in rowLooks.OrderBy(kv => camos.ToList().FindIndex(c => string.Equals(c.Name, kv.Key, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : int.MaxValue))
        {
            var c = GameKnowledge.CamoByName(suit);
            var dev = c?.DevelopId is { } d ? GameKnowledge.VanillaDevelop(d) : null;
            var parts = c == null ? ["NORMAL"] : c.PlayerParts.OrderBy(p => Array.IndexOf(PartOrder, p) is var k and >= 0 ? k : PartOrder.Length).ToList();
            var looks = parts.Select(PartLabel).Concat(names.Select(n => n.Name)).ToList();
            string key = "v:" + suit;
            Rows.Add(new SortieRow(key, GameKnowledge.EnglishName(game, c?.LangName) ?? c?.Description ?? suit, GameKnowledge.EnglishName(game, c?.LangInfo) ?? "",
                dev?.UsageGmp ?? 0, SuitIcon(suit), looks, keep.TryGetValue(key, out var li) ? li : Math.Max(0, parts.IndexOf("NORMAL")))
            {
                // The game's own slots have no Clone and no Remove.
                Edits = [.. parts.Select(_ => (SlotEdit?)null), .. names.Select(n => n.Edit)],
                // #87: the game's own slots keep their names; the mod's slots are renamed here.
                Names = [.. parts.Select(_ => (SlotName?)null), .. names.Select(n => n.Rename)],
                IconNames = [.. parts.Select(_ => IconName(c?.Icon)), .. names.Select(n => IconName(n.Icon ?? c?.Icon))],
                Guard = Guard,
                GameSlots = parts.Count,
                Add = adders.GetValueOrDefault(suit),
            });
        }

        // The project's own outfits go below every vanilla row (owner, 3 Nov), in project order.
        foreach (var iv in _ed.Items.Where(i => i.Item.Kind == ItemKind.Outfit && i.Outfit != null))
        {
            var ov = iv.Outfit!;
            // #62: a vanilla-only outfit has no slot of its own; Add slot gives it back its Main look.
            var looks = ov.VanillaOnly ? [BonusSlotsOnlyLook] : ov.LookRows.Select((r, k) => r.Label.Trim() is { Length: > 0 } t ? t.ToUpperInvariant() : $"LOOK {k + 1}").ToList();
            int def = Math.Max(0, ov.LookRows.ToList().FindIndex(r => r.IsDefault));
            string key = "m:" + iv.Item.Id;
            Rows.Add(new SortieRow(key, iv.Name.Trim() is { Length: > 0 } n ? n : iv.Title, iv.Description,
                (int)ov.UsageCost, iv.Icon, looks, keep.TryGetValue(key, out var li) ? li : def)
            {
                ArmNote = ov.ArmAllowed ? "" : "BIONIC ARM: " + PlayerBranches.NoBionicArmReason + ".",
                Item = iv,
                Guard = Guard,
                Generate = new Command(() => { _ed.Selected = iv; iv.PickGenerate.Execute(null); }, () => iv.PickGenerate.CanExecute(null)),
                Edits = ov.VanillaOnly ? [null] : [.. looks.Select((_, k) => (SlotEdit?)new SlotEdit(() => Do(key, k + 1, () => ov.CloneLook(k)), () => Do(key, k, () => ov.RemoveLook(k)),
                    d => Do(key, k + d, () => ov.MoveLook(k, d)), d => k + d >= 0 && k + d < looks.Count, () => ov.CanRemoveLook))],
                Add = () => Do(key, int.MaxValue, ov.AddLook),
                // #87: the same field as the slot name on the Sortie slots tab. A vanilla-only outfit has no own slot to name,
                // and the build writes no slot name for an outfit with one slot.
                Names = ov.VanillaOnly || !ov.CanNameLooks ? [null] : [.. ov.LookRows.Select((r, k) => (SlotName?)new SlotName(r.Label, t => r.Label = t, t => t.Trim() is { Length: > 0 } x ? x.ToUpperInvariant() : $"LOOK {k + 1}"))],
                // A look with no icon of its own shows its branch's: the base look's, else the outfit's.
                IconNames = ov.VanillaOnly ? [""] : [.. ov.LookRows.Select(r => IconName(r.Look.IconFtexPath ?? ov.LookRows[0].Look.IconFtexPath ?? ov.OutfitIcon))],
            });
        }

        string? want = selKey ?? (_ed.Selected is { } s ? "m:" + s.Item.Id : null);
        Selected = Rows.FirstOrDefault(r => r.Key == want) ?? Rows.LastOrDefault();
        Raise(nameof(IsEmpty));
    }

    /// <summary>#87: the file name of an icon path ("/Assets/.../ui_st_sna4_alp" gives "ui_st_sna4_alp"); "" for none.</summary>
    public static string IconName(string? path) => path is { Length: > 0 } p ? p[(p.LastIndexOf('/') + 1)..] : "";

    /// <summary>#62: the one cell of the row of a vanilla-only outfit (it shows only as bonus slots).</summary>
    public const string BonusSlotsOnlyLook = "BONUS SLOTS ONLY";

    /// <summary>Swaps the bonus slots at <paramref name="i"/> and <paramref name="j"/> of the camo's merged order.</summary>
    private void Reorder(string suit, List<string> keys, int i, int j)
    {
        _ed.Session.Checkpoint();
        SortieSlots.Reorder(_ed.Session.Project!, suit, keys, i, j);
        _ed.Session.NotifyChanged();
    }

    /// <summary>
    /// Runs a slot edit (issue #30), then shows look <paramref name="look"/> of the row, clamped: the clone, the slot
    /// after a removed one, or the added slot. <paramref name="afterGame"/> counts the look from the first added slot.
    /// </summary>
    private void Do(string key, int look, Action edit, bool afterGame = false)
    {
        edit();
        Rebuild();
        if (_sel is not { } r || r.Key != key) return;
        long at = afterGame ? (long)r.GameSlots + look : look;
        r.LookIndex = (int)Math.Clamp(at, 0, Math.Max(0, r.Looks.Count - 1));
    }
}
