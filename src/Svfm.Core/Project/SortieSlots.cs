using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>
/// Every change to the Sortie slots of an outfit: its own slots (the looks of each wearer), its bonus slots
/// (<see cref="OutfitSpec.AlsoUnder"/>) and the bonus slot order of the project (<see cref="ModProject.BonusOrder"/>).
/// The rules are here: a bonus slot keeps showing its look (#62), a vanilla-only outfit with no bonus slot left gets its
/// Main look back (#62), a new slot has a name of its own, and a cloned bonus slot goes directly after its source.
/// <para>
/// The slot names are in the text of <c>text</c>: the project item. For an existing V Framework mod this item is not
/// the outfit. A function makes no undo step: it calls <c>before</c> one time, directly before its first change, and
/// does not call it when it changes nothing. The caller makes the undo step there.
/// </para>
/// </summary>
public static class SortieSlots
{
    private static List<Look> Looks(OutfitBranch b) => [b.Base, .. b.Variants];
    private static OutfitBranch First(OutfitItem o) => o.Spec.Branches.Values.First();
    private static string Eng(ProjectItem text, string? id) =>
        id != null && text.Text.TryGetValue(id, out var per) && per.TryGetValue("eng", out var t) ? t : "";

    // ---- own slots ----

    /// <summary>True when an own slot can go. An existing V Framework mod keeps its last own slot.</summary>
    public static bool CanRemoveOwn(ModProject? p, OutfitItem o) => o.Spec.CanRemoveSlot(own: true, ownCanGo: p?.Existing == null);

    /// <summary>
    /// Moves the own slot at <paramref name="from"/> to <paramref name="to"/>; the slots between them shift. The look
    /// that is worn first stays the same look.
    /// </summary>
    /// <returns>For each new position, the old position of its look. null when nothing changed.</returns>
    public static IReadOnlyList<int>? MoveOwn(OutfitItem o, int from, int to, Action? before = null)
    {
        var old = Looks(First(o));
        if (from == to || from < 0 || to < 0 || from >= old.Count || to >= old.Count) return null;
        var list = old.ToList();
        list.RemoveAt(from);
        list.Insert(to, old[from]);
        return SetOwn(o, old, list, before);
    }

    /// <summary>
    /// Removes the own slot at <paramref name="index"/>. The last own slot goes only while a bonus slot stays: the
    /// outfit is then vanilla-only, and its look stays as the source of the bonus slots.
    /// </summary>
    /// <returns>For each new position, the old position of its look. null when nothing changed.</returns>
    public static IReadOnlyList<int>? RemoveOwn(ModProject? p, OutfitItem o, int index, Action? before = null)
    {
        if (!CanRemoveOwn(p, o)) return null;
        var old = Looks(First(o));
        if (old.Count < 2) return SetVanillaOnly(o, true, before) ? [0] : null;
        if (index < 0 || index >= old.Count) return null;
        var list = old.ToList();
        list.RemoveAt(index);
        return SetOwn(o, old, list, before);
    }

    /// <summary>Puts a copy of the own slot at <paramref name="index"/> after it, with a name of its own, for each wearer.</summary>
    /// <returns>For each new position, the old position of its look (-1 for the copy). null when nothing changed.</returns>
    public static IReadOnlyList<int>? CloneOwn(OutfitItem o, ProjectItem text, int index, Action? before = null)
    {
        var looks = Looks(First(o));
        if (index < 0 || index >= looks.Count) return null;
        return InsertCopy(o, text, index, index + 1, Eng(text, looks[index].DisplayName), before);
    }

    /// <summary>
    /// Adds an own slot at the end: a copy of the Main look with the name "New slot". A vanilla-only outfit gets its own
    /// slot back in place of that: its Main look.
    /// </summary>
    /// <returns>For each new position, the old position of its look (-1 for the new slot). null when nothing changed.</returns>
    public static IReadOnlyList<int>? AddOwn(OutfitItem o, ProjectItem text, Action? before = null)
    {
        if (o.Spec.VanillaOnly) return SetVanillaOnly(o, false, before) ? [.. Enumerable.Range(0, Looks(First(o)).Count)] : null;
        return InsertCopy(o, text, 0, int.MaxValue, "New slot", before);
    }

    /// <summary>Makes the own slot at <paramref name="index"/> the look that is worn first.</summary>
    /// <returns>For each position, the position of its look: no look moves.</returns>
    public static IReadOnlyList<int> SetWornFirst(OutfitItem o, int index, Action? before = null)
    {
        var old = Looks(First(o));
        return SetOwn(o, old, old.ToList(), before, index);
    }

    private static bool SetVanillaOnly(OutfitItem o, bool on, Action? before)
    {
        if (o.Spec.Branches.Values.All(b => b.VanillaOnly == on)) return false;
        before?.Invoke();
        foreach (var (who, b) in o.Spec.Branches.ToList()) o.Spec.Branches[who] = b with { VanillaOnly = on };
        return true;
    }

    private static IReadOnlyList<int> SetOwn(OutfitItem o, List<Look> old, List<Look> list, Action? before, int? wornFirst = null)
    {
        var order = list.Select(l => old.FindIndex(x => ReferenceEquals(x, l))).ToList();
        // The look worn first follows its look; when that look is gone, the Main look is worn first.
        int def = wornFirst ?? Math.Max(0, order.IndexOf(Math.Max(0, old.FindIndex(l => l.Default))));
        int worn = def >= 0 && def < order.Count ? order[def] : -1; // the old position of the look worn first
        before?.Invoke();
        // Each wearer keeps its own looks, in the new order. A wearer with fewer looks keeps the looks that it has.
        foreach (var (who, b) in o.Spec.Branches.ToList())
        {
            var all = Looks(b);
            var next = order.Where(k => k < all.Count).Select((k, i) => all[k] with { Default = k == worn && i > 0 }).ToList();
            if (next.Count > 0) o.Spec.Branches[who] = b with { Base = next[0], Variants = [.. next.Skip(1)] };
        }
        if (!order.SequenceEqual(Enumerable.Range(0, old.Count))) o.Spec.AlsoUnder = BonusSlots.FollowLooks(o.Spec.AlsoUnder, order);
        return order;
    }

    private static IReadOnlyList<int> InsertCopy(OutfitItem o, ProjectItem text, int from, int at, string name, Action? before)
    {
        int count = Looks(First(o)).Count;
        string id = NewLookName(o, text);
        var order = Enumerable.Range(0, count).ToList();
        order.Insert(Math.Min(at, count), -1);
        before?.Invoke();
        // A bonus slot keeps showing its look when the new look pushes it down a place.
        if (at < count) o.Spec.AlsoUnder = BonusSlots.FollowLooks(o.Spec.AlsoUnder, order);
        text.Text[id] = new(StringComparer.Ordinal) { ["eng"] = name };
        foreach (var (who, b) in o.Spec.Branches.ToList())
        {
            var all = Looks(b);
            if (from >= all.Count) continue;
            all.Insert(Math.Min(at, all.Count), all[from] with { DisplayName = id, Default = false });
            o.Spec.Branches[who] = b with { Base = all[0], Variants = [.. all.Skip(1)] };
        }
        return order;
    }

    /// <summary>A free name ID for a new look of the outfit: not in the text table and not used by a look of any wearer.</summary>
    internal static string NewLookName(OutfitItem o, ProjectItem text) =>
        NewName(text, $"{o.Spec.NameLang}_s", x => o.Spec.Branches.Values.SelectMany(Looks).Any(l => l.DisplayName == x));

    private static string NewName(ProjectItem text, string stem, Func<string, bool> used)
    {
        for (int n = 1; ; n++)
        {
            string id = stem + n;
            if (!text.Text.ContainsKey(id) && !used(id)) return id;
        }
    }

    // ---- bonus slots ----

    /// <summary>
    /// Replaces the bonus slots of the outfit. With no bonus slot left, a vanilla-only outfit gets its Main look back.
    /// </summary>
    public static void SetBonus(OutfitItem o, IReadOnlyList<AlsoUnder> list)
    {
        o.Spec.AlsoUnder = list;
        if (o.Spec.BonusSlotCount > 0) return;
        foreach (var (who, b) in o.Spec.Branches.ToList()) if (b.VanillaOnly) o.Spec.Branches[who] = b with { VanillaOnly = false };
    }

    /// <summary>
    /// #62: gives each vanilla outfit of <paramref name="list"/> that has no slot its first slot: the Main look for each
    /// wearer, with a name. Changes the list and gives it back.
    /// </summary>
    public static List<AlsoUnder> Seed(OutfitItem o, ProjectItem text, List<AlsoUnder> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Slots.Count > 0) continue;
            string id = NewBonusName(o, text, o.Spec.AlsoUnder); // the text entry of each round keeps the IDs apart
            text.Text[id] = new(StringComparer.Ordinal) { ["eng"] = FirstName(o, text) };
            list[i] = list[i] with { Slots = MainLookSlots(o, id) };
        }
        return list;
    }

    /// <summary>
    /// Puts a copy of the bonus slot after it, with a name of its own (a copy of the name). When the vanilla outfit has
    /// a bonus slot order with the source in it, the copy goes directly after the source there too. A bonus slot is named
    /// by three values: target (the position of the vanilla outfit in the bonus slots of the outfit), index (the look of
    /// the slot; -1 is the Main look) and lang (the name ID of the slot).
    /// </summary>
    /// <returns>The name ID of the copy. null when nothing changed.</returns>
    public static string? CloneBonus(ModProject? p, OutfitItem o, ProjectItem text, int target, int index, string lang, Action? before = null)
    {
        string id = NewBonusName(o, text, o.Spec.AlsoUnder);
        bool ok = Change(o, target, sl =>
        {
            var copy = sl.Where(x => x.Index == index && x.LangId == lang).Select(x => x with { LangId = id }).ToList();
            sl.InsertRange(sl.FindLastIndex(x => x.Index == index && x.LangId == lang) + 1, copy);
            return sl;
        }, () => { before?.Invoke(); text.Text[id] = new(text.Text.GetValueOrDefault(lang) ?? [], StringComparer.Ordinal); });
        if (!ok) return null;
        string camo = BonusSlots.CamoName(o.Spec.AlsoUnder[target].Outfit);
        if (p != null && p.BonusOrder.GetValueOrDefault(camo) is { } k && k.IndexOf(BonusSlots.Key(text.Id, lang)) is >= 0 and var i)
            p.BonusOrder = new(p.BonusOrder, StringComparer.OrdinalIgnoreCase) { [camo] = [.. k[..(i + 1)], BonusSlots.Key(text.Id, id), .. k[(i + 1)..]] };
        return id;
    }

    /// <summary>Removes the bonus slot. A slot can go while another slot of the outfit stays (#62).</summary>
    /// <returns>False when nothing changed.</returns>
    public static bool RemoveBonus(OutfitItem o, int target, int index, string lang, Action? before = null) =>
        o.Spec.CanRemoveSlot(own: false) && Change(o, target, sl => { sl.RemoveAll(x => x.Index == index && x.LangId == lang); return sl; }, before);

    /// <summary>Swaps the bonus slot with the slot <paramref name="delta"/> places on, under the same vanilla outfit (#62).</summary>
    /// <returns>False when the outfit has no vanilla outfit at <paramref name="target"/>.</returns>
    public static bool MoveBonus(OutfitItem o, int target, int index, string lang, int delta, Action? before = null) =>
        Change(o, target, sl =>
        {
            var gs = sl.GroupBy(x => (x.Index, x.LangId)).Select(g => g.ToList()).ToList(); // one slot: one (look, name) pair
            int i = gs.FindIndex(g => g[0].Index == index && g[0].LangId == lang), j = i + delta;
            if (i < 0 || j < 0 || j >= gs.Count) return sl;
            (gs[i], gs[j]) = (gs[j], gs[i]);
            return [.. gs.SelectMany(g => g)];
        }, before);

    /// <summary>Adds the Main look as a new bonus slot at the end, for each wearer, with a name.</summary>
    /// <returns>The name ID of the new slot. null when nothing changed.</returns>
    public static string? AddBonus(OutfitItem o, ProjectItem text, int target, Action? before = null)
    {
        string id = NewBonusName(o, text, o.Spec.AlsoUnder), name = FirstName(o, text);
        return Change(o, target, sl => { sl.AddRange(MainLookSlots(o, id)); return sl; },
            () => { before?.Invoke(); text.Text[id] = new(StringComparer.Ordinal) { ["eng"] = name }; }) ? id : null;
    }

    /// <summary>
    /// Swaps the bonus slots at <paramref name="i"/> and <paramref name="j"/> of <paramref name="keys"/> (the merged slots
    /// of the vanilla outfit, in the order of the build) and makes the result the bonus slot order of that outfit.
    /// </summary>
    public static void Reorder(ModProject p, string suit, IReadOnlyList<string> keys, int i, int j)
    {
        var k = keys.ToList(); (k[i], k[j]) = (k[j], k[i]);
        p.BonusOrder = new(p.BonusOrder, StringComparer.OrdinalIgnoreCase) { [BonusSlots.CamoName(suit)] = k };
    }

    private static bool Change(OutfitItem o, int target, Func<List<AlsoSlot>, List<AlsoSlot>> f, Action? before)
    {
        var list = o.Spec.AlsoUnder;
        if (target < 0 || target >= list.Count) return false;
        before?.Invoke();
        SetBonus(o, [.. list.Select((a, i) => i == target ? a with { Slots = f([.. a.Slots]) } : a)]);
        return true;
    }

    private static List<AlsoSlot> MainLookSlots(OutfitItem o, string id) => [.. o.Spec.Branches.Keys.OrderBy(k => k).Select(who => new AlsoSlot(who, -1, id))];
    private static string NewBonusName(OutfitItem o, ProjectItem text, IEnumerable<AlsoUnder> list) =>
        NewName(text, $"{o.Spec.NameLang}_u", x => list.Any(a => a.Slots.Any(s => s.LangId == x)));
    /// <summary>The name a new bonus slot starts with: the name of the Main look, or the name of the item.</summary>
    private static string FirstName(OutfitItem o, ProjectItem text) =>
        Eng(text, First(o).Base.DisplayName) is { Length: > 0 } n ? n : text.Title.Length > 0 ? text.Title : text.Id;
}
