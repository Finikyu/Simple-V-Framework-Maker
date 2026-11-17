using System.Collections.ObjectModel;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

public sealed record AlsoChoice(string Id, string Name);

/// <summary>A vanilla outfit the item's slots also appear under: a row that opens to its slot ticks (issue #14).</summary>
public sealed class AlsoTargetChip(AlsoVm vm, int index, string name) : Observable
{
    public int Index { get; } = index;
    public string Outfit { get; } = name;
    public string Name { get; } = GameKnowledge.CamoByName(name) is { Description: { Length: > 0 } d } ? $"{name} · {d.Trim()}" : name;
    public bool Selected => vm.SelectedIndex == Index;
    public string Header => (Selected ? "▾  " : "▸  ") + Name;
    /// <summary>The slot rows, only for the open row: the other rows build no controls.</summary>
    public IEnumerable<AlsoSlotRow> Rows => Selected ? vm.Rows : [];
    public Command Select { get; } = new(() => vm.SelectedIndex = vm.SelectedIndex == index ? -1 : index);
    public Command Remove { get; } = new(() => vm.RemoveTarget(index));
    public Command AddSlot { get; } = new(() => { vm.AddSlot(index); vm.Refresh(); });
    public void Refresh() { Raise(nameof(Selected)); Raise(nameof(Header)); Raise(nameof(Rows)); }
}

/// <summary>#84: one bonus slot under the selected outfit: the look it shows, its name, its abilities and Remove.</summary>
public sealed class AlsoSlotRow(AlsoVm vm, int pos) : Observable
{
    /// <summary>The slot's place under the outfit; the row reads the slot there on each reload.</summary>
    public int Pos { get; } = pos;
    public int Index => vm.SlotAt(Pos)?.Index ?? -1; // -1 = the base look
    public bool IsMain => Index < 0;
    /// <summary>The outfit's looks by the mod's names, the base look first.</summary>
    public IReadOnlyList<string> Looks => vm.Looks;
    /// <summary>The place in <see cref="Looks"/> of the look the slot shows.</summary>
    public int Look { get => Index + 1; set { if (value >= 0) vm.SetSlotLook(Pos, value - 1); Raise(); Raise(nameof(IsMain)); } }
    public string Name { get => vm.RowName(Pos); set => vm.SetRowName(Pos, value); }
    // No arm choice (owner, Nov 6, Q12): V Framework 1.7 keeps the arm once for each vanilla suit, not for each bonus slot.
    public static IReadOnlyList<string> AbilityChoices { get; } = ["Abilities of the look", "As the vanilla suit"];
    /// <summary>0: the abilities of the look it shows (the default, #61 Q3); 1: the vanilla suit's.</summary>
    public int Abilities { get => vm.RowAbilities(Pos); set { vm.SetRowAbilities(Pos, value); Raise(); } }
    public Command Remove => new(() => vm.RemoveRow(Pos));
    public void Reload() { Raise(nameof(Looks)); Raise(nameof(Look)); Raise(nameof(IsMain)); Raise(nameof(Name)); Raise(nameof(Abilities)); }
}

/// <summary>"Other outfits": the item's slots added as extra looks under vanilla suits.</summary>
public sealed class AlsoVm : Observable
{
    private readonly Session _s;
    private readonly ItemVm _item;
    private readonly OutfitItem _o;
    private int _sel = -1;
    private string _query = "";
    private AlsoChoice? _pick;
    private readonly IReadOnlyList<AlsoInScript> _inScript;

    /// <param name="inScript">For an existing outfit: the vanilla suits its script already shows its looks under.</param>
    public AlsoVm(Session s, ItemVm item, OutfitItem o, IReadOnlyList<AlsoInScript>? inScript = null)
    {
        _s = s; _item = item; _o = o; _inScript = inScript ?? [];
        foreach (var a in _inScript) InScript.Add($"{a.Outfit}: {string.Join(", ", a.Slots.Select(x => x.Index).Distinct().Order().Select(LookName))}. Edited on the item \"{a.Holder}\".");
        Reload();
    }

    public ObservableCollection<AlsoTargetChip> Targets { get; } = [];
    public ObservableCollection<AlsoSlotRow> Rows { get; } = [];
    public ObservableCollection<AlsoChoice> Choices { get; } = [];
    /// <summary>The vanilla suits the script already shows this outfit's looks under (an existing outfit only), as text.</summary>
    public ObservableCollection<string> InScript { get; } = [];
    public bool HasInScript => InScript.Count > 0;
    private static string LookName(int index) => index < 0 ? "Main look" : $"Look {index + 2}";

    private IReadOnlyList<AlsoUnder> List => _o.Spec.AlsoUnder;
    private void Edit(string field, Func<List<AlsoUnder>, List<AlsoUnder>> f)
    {
        _s.Checkpoint(field);
        SortieSlots.SetBonus(_o, f([.. List]));
        _s.NotifyChanged();
    }

    public string Count => $"{Choices.Count} outfits match.";
    public bool NoTargets => Targets.Count == 0;
    public bool HasSelected => _sel >= 0 && _sel < List.Count;
    public int SelectedIndex { get => _sel; set { if (Set(ref _sel, value)) { RebuildRows(); foreach (var t in Targets) t.Refresh(); Raise(nameof(HasSelected)); } } }

    public string Query { get => _query; set { if (Set(ref _query, value)) RebuildChoices(); } }
    public AlsoChoice? Pick { get => _pick; set => Set(ref _pick, value); }
    public Command Add => new(() =>
    {
        if (_pick == null) return;
        Edit("also:add", l => { l.Add(new AlsoUnder(_pick.Id, [])); return Seed(l); });
        Reload(); SelectedIndex = List.Count - 1;
    });

    private List<AlsoUnder> Seed(List<AlsoUnder> l) => SortieSlots.Seed(_o, _item.Item, l);

    public void RemoveTarget(int i)
    {
        if (i < 0 || i >= List.Count) return;
        Edit("also:rm", l => { l.RemoveAt(i); return l; });
        _sel = Math.Min(_sel, List.Count - 1);
        Reload();
    }

    /// <summary>Issue #14: adds every fatigues outfit all wearers can wear; outfits already added, or already in the script, stay as they are.</summary>
    public Command AddAllFatigues => new(() =>
    {
        var all = GameKnowledge.WithAllFatigues(List, _o.Spec.Branches.Keys, _inScript.Select(a => a.Outfit));
        if (all.Count == List.Count) return; // nothing new: no empty undo step
        Edit("also:fatigues", _ => Seed([.. all]));
        Reload();
    });

    private bool Allowed(CamoInfo c) => GameKnowledge.AllCanWear(c, _o.Spec.Branches.Keys);

    private void RebuildChoices()
    {
        Choices.Clear();
        var q = _query.Trim();
        // A suit the script already shows the looks under is changed on its own item, so it is not offered again.
        foreach (var c in GameKnowledge.Camos.Where(Allowed).Where(c => !_inScript.Any(a => string.Equals(a.Outfit, c.Name, StringComparison.OrdinalIgnoreCase))))
        {
            string label = string.IsNullOrWhiteSpace(c.Description) ? c.Name : $"{c.Name} · {c.Description}";
            if (q.Length == 0 || label.Contains(q, StringComparison.OrdinalIgnoreCase)) Choices.Add(new AlsoChoice(c.Name, label));
        }
        _pick = Choices.FirstOrDefault(); Raise(nameof(Pick)); Raise(nameof(Count));
    }

    /// <summary>
    /// Every edit (each typed letter too) comes here. New rows would drop the focus of the name box after one letter
    /// (issue #18), so the rows are made again only when their number changes.
    /// </summary>
    private void RebuildRows()
    {
        var looks = Enumerable.Range(-1, _o.Spec.Branches.Count == 0 ? 0 : _o.Spec.Branches.Values.Max(b => b.Variants.Count) + 1)
            .Select(k => LookText(k) is { Length: > 0 } t ? t : LookName(k)).ToList();
        if (!looks.SequenceEqual(Looks)) Looks = looks; // a new list only on a change: the dropdowns keep their choice
        int n = HasSelected ? Groups(List[_sel].Slots).Count : 0;
        if (Rows.Count == n) { foreach (var r in Rows) r.Reload(); return; }
        Rows.Clear();
        for (int k = 0; k < n; k++) Rows.Add(new AlsoSlotRow(this, k));
    }

    /// <summary>The outfit's looks by the mod's names, for the look dropdown of each slot row (#84).</summary>
    public IReadOnlyList<string> Looks { get; private set; } = [];

    /// <summary>The name the mod gives look <paramref name="k"/> (-1 = the base look), or "".</summary>
    private string LookText(int k)
    {
        var first = _o.Spec.Branches.Values.First();
        var look = k < 0 ? first.Base : k < first.Variants.Count ? first.Variants[k] : null;
        return look?.DisplayName != null ? _item.GetText(look.DisplayName, "eng", false) : "";
    }

    /// <summary>The slots under one outfit: one (look, name) pair, shared by the wearers, is one slot (as the mockup shows them).</summary>
    private static List<List<AlsoSlot>> Groups(IEnumerable<AlsoSlot> sl) => [.. sl.GroupBy(x => (x.Index, x.LangId)).Select(g => g.ToList())];
    /// <summary>The slot at place <paramref name="pos"/> under the selected outfit (its first wearer), or null.</summary>
    public AlsoSlot? SlotAt(int pos) => HasSelected && Groups(List[_sel].Slots) is var g && pos >= 0 && pos < g.Count ? g[pos][0] : null;
    private static bool Same(AlsoSlot x, AlsoSlot s) => x.Index == s.Index && x.LangId == s.LangId;
    public int RowAbilities(int pos) => SlotAt(pos) is { } s && List[_sel].Slots.Any(x => Same(x, s) && x.SuitAbilities) ? 1 : 0;
    public void SetRowAbilities(int pos, int v)
    {
        if (v < 0 || SlotAt(pos) is not { } s || v == RowAbilities(pos)) return;
        ReplaceSlots("also:ab", sl => [.. sl.Select(x => Same(x, s) ? x with { SuitAbilities = v == 1 } : x)]);
    }
    public string RowName(int pos) => SlotAt(pos) is { } s ? _item.GetText(s.LangId, "eng", false) : "";

    /// <summary>#84: the slot shows look <paramref name="index"/> (-1 = the base look) for every wearer; the build writes its pl_parts.</summary>
    public void SetSlotLook(int pos, int index)
    {
        if (SlotAt(pos) is not { } s || s.Index == index) return;
        ReplaceSlots("also:look", sl => [.. sl.Select(x => Same(x, s) ? x with { Index = index } : x)]);
        Reload(false);
    }

    /// <summary>#84: the Remove button of a slot row.</summary>
    public void RemoveRow(int pos)
    {
        if (SlotAt(pos) is not { } s) return;
        RemoveSlot(_sel, s.Index, s.LangId);
        Reload();
    }

    private void ReplaceSlots(string field, Func<List<AlsoSlot>, List<AlsoSlot>> f)
    {
        int i = _sel;
        Edit(field, l => { l[i] = l[i] with { Slots = f([.. l[i].Slots]) }; return l; });
    }

    // ---- Issue #30: Clone, Remove and Add slot from the Sortie mockup. One slot is one lang id under one outfit. ----
    // No undo field: two clicks in a row are two undo steps.

    private void Changed(bool changed) { if (changed) _s.NotifyChanged(); }
    public void CloneSlot(int target, int index, string lang) => Changed(SortieSlots.CloneBonus(_s.Project, _o, _item.Item, target, index, lang, () => _s.Checkpoint()) != null);
    public void RemoveSlot(int target, int index, string lang) => Changed(SortieSlots.RemoveBonus(_o, target, index, lang, () => _s.Checkpoint()));
    public void MoveSlot(int target, int index, string lang, int delta) => Changed(SortieSlots.MoveBonus(_o, target, index, lang, delta, () => _s.Checkpoint()));
    public void AddSlot(int target) => Changed(SortieSlots.AddBonus(_o, _item.Item, target, () => _s.Checkpoint()) != null);

    public void SetRowName(int pos, string text) { if (SlotAt(pos) is { } s) _item.SetText(s.LangId, "eng", text); }

    private void Reload(bool rows = true)
    {
        // The chips stay while the outfits stay: new chips would drop the focus of a name box in the open row (issue #18).
        if (!Targets.Select(t => t.Outfit).SequenceEqual(List.Select(a => a.Outfit)))
        {
            Targets.Clear();
            for (int i = 0; i < List.Count; i++) Targets.Add(new AlsoTargetChip(this, i, List[i].Outfit));
        }
        if (_sel >= List.Count) _sel = List.Count - 1;
        foreach (var t in Targets) t.Refresh();
        if (rows) RebuildRows(); else foreach (var r in Rows) r.Reload();
        RebuildChoicesIfEmpty();
        Raise(nameof(NoTargets)); Raise(nameof(HasSelected)); Raise(nameof(SelectedIndex));
    }
    private void RebuildChoicesIfEmpty() { if (Choices.Count == 0) RebuildChoices(); }

    /// <summary>Called after undo, reset or any change from elsewhere.</summary>
    public void Refresh() { Reload(); }
}
