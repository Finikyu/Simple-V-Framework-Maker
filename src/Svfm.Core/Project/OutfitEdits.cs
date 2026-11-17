using Svfm.Core.Knowledge;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>
/// The changes of an outfit that have a rule: its wearers, its camo values, the prosthetic arm, the head lists, and
/// the values of one own slot. The slot list is in <see cref="SortieSlots"/>.
/// <para>
/// A function makes no undo step: it calls <c>before</c> one time, directly before its first change, and does not call
/// it when it changes nothing. The caller makes the undo step there. A function gives back false (or null) when it
/// changed nothing.
/// </para>
/// </summary>
public static class OutfitEdits
{
    private static IEnumerable<Look> Looks(OutfitBranch b) => b.Variants.Prepend(b.Base);

    /// <summary>
    /// Replaces each branch with the result of <paramref name="f"/>. A result equal to the branch is no change: a
    /// control can write back the value that it shows.
    /// </summary>
    public static bool Change(OutfitItem o, Func<PlayerBranch, OutfitBranch, OutfitBranch> f, Action? before = null)
    {
        var next = o.Spec.Branches.ToDictionary(kv => kv.Key, kv => f(kv.Key, kv.Value));
        if (next.All(kv => Equals(kv.Value, o.Spec.Branches[kv.Key]))) return false;
        before?.Invoke();
        foreach (var (k, v) in next) o.Spec.Branches[k] = v;
        return true;
    }

    /// <summary>Changes the look at <paramref name="look"/> (0 is the Main look) or, with -1, every look, for each wearer.</summary>
    private static bool ChangeLooks(OutfitItem o, int look, Func<Look, Look> f, Action? before) => Change(o, (_, b) =>
    {
        var old = Looks(b).ToList();
        var next = old.Select((l, i) => look < 0 || i == look ? f(l) : l).ToList();
        // The same looks give the same branch: a new list is not equal to the old one.
        return next.SequenceEqual(old) ? b : b with { Base = next[0], Variants = [.. next.Skip(1)] };
    }, before);

    // ---- wearers ----

    /// <summary>
    /// Adds or removes a wearer. The last wearer stays (#15). A wearer that comes back gets its original branch. Avatar
    /// with no original branch gets a branch made from Snake's, without the heads built on Snake's face (#45, #65).
    /// </summary>
    /// <param name="p">The project: its head options tell which heads are built on Snake's face.</param>
    /// <param name="o">The outfit.</param>
    /// <param name="who">The wearer.</param>
    /// <param name="on">True to add the wearer.</param>
    /// <param name="original">The branches of the outfit as the mod was opened.</param>
    /// <param name="before">Called one time, directly before the change.</param>
    public static bool SetWearer(ModProject p, OutfitItem o, PlayerBranch who, bool on, IReadOnlyDictionary<PlayerBranch, OutfitBranch> original, Action? before = null)
    {
        var bs = o.Spec.Branches;
        bool wears = bs.ContainsKey(who);
        if (on == wears) return false;
        if (!on)
        {
            if (PlayerBranches.IsLastWearer(wears, bs.Count)) return false;
            before?.Invoke();
            bs.Remove(who);
            return true;
        }
        if (!original.TryGetValue(who, out var b))
        {
            if (who != PlayerBranch.Avatar || (bs.GetValueOrDefault(PlayerBranch.Snake) ?? original.GetValueOrDefault(PlayerBranch.Snake)) is not { } snake) return false;
            b = PlayerBranches.AvatarFrom(snake, p.Items.Select(ProjectEdits.HeadOf).OfType<HeadOptionItem>().Where(h => h.Spec.AvatarWouldWearSnakeFace).Select(h => h.Spec.Key).ToHashSet(StringComparer.Ordinal));
        }
        before?.Invoke();
        bs[who] = b;
        return true;
    }

    // ---- camo ----

    /// <summary>The values a branch hides with: the table of its preset, or its own values (Custom).</summary>
    public static IReadOnlyDictionary<string, int> CamoValues(OutfitBranch b) =>
        (b.CamoBonusType != null ? GameKnowledge.CamoValues(b.CamoBonusType) : null) ?? b.CamoBonusValues ?? new Dictionary<string, int>();

    /// <summary>
    /// Sets one value on several surfaces, from the values that each branch hides with now. A value from 1 to 100 is
    /// set; 0 or less removes the surface. A preset whose values change becomes Custom (#22). A change that leaves the
    /// values of the preset as they are keeps the preset.
    /// </summary>
    public static bool SetCamoValues(OutfitItem o, IEnumerable<string> surfaces, int value, Action? before = null)
    {
        var names = surfaces.ToList();
        value = Math.Min(value, 100);
        return Change(o, (_, b) =>
        {
            var d = new Dictionary<string, int>(CamoValues(b));
            foreach (var n in names) if (value <= 0) d.Remove(n); else d[n] = value;
            if (GameKnowledge.CamoPresetFor(b.CamoBonusType, d) != null) return b;
            // Custom with the same values is no change.
            return b.CamoBonusType == null && b.CamoBonusValues is { } have && have.Count == d.Count && d.All(kv => have.TryGetValue(kv.Key, out int v) && v == kv.Value)
                ? b : b with { CamoBonusType = null, CamoBonusValues = d };
        }, before);
    }

    /// <summary>
    /// Picks a camo preset, or Custom (null). Custom starts with the values of the first wearer: the values of its
    /// preset, or its own values. A preset removes the own values.
    /// </summary>
    public static bool SetCamoPreset(OutfitItem o, string? preset, Action? before = null)
    {
        if (preset != null) return Change(o, (_, b) => b with { CamoBonusType = preset, CamoBonusValues = null }, before);
        var from = CamoValues(o.Spec.Branches.Values.First());
        return Change(o, (_, b) => b with { CamoBonusType = null, CamoBonusValues = new Dictionary<string, int>(from) }, before);
    }

    // ---- the prosthetic arm ----

    /// <summary>True when no look in scope has <c>enableArm = false</c>. The scope is one look, or every look (-1).</summary>
    public static bool ProstheticOn(OutfitItem o, int look = -1) => ArmScope(o, look).All(l => l.EnableArm != false);

    /// <summary>
    /// "Enable prosthetic" for one look, or for every look (-1), for each wearer. V Framework 1.7 has no ability-only
    /// key: off writes <c>enableArm = false</c>, and the game then loads no prosthetic arm. On removes only the false
    /// values. The edit is not refused for an outfit with no Snake or Avatar wearer: an existing mod can have the key.
    /// </summary>
    public static bool SetProsthetic(OutfitItem o, bool on, int look = -1, Action? before = null)
    {
        // Not "on == ProstheticOn": with some looks on and some off that function says off, and off must still reach each look.
        if (ArmScope(o, look).All(l => (l.EnableArm != false) == on)) return false;
        return ChangeLooks(o, look, l => l with { EnableArm = on ? (l.EnableArm == false ? null : l.EnableArm) : false }, before);
    }

    private static IEnumerable<Look> ArmScope(OutfitItem o, int look) =>
        o.Spec.Branches.Values.SelectMany(b => look < 0 ? Looks(b) : Looks(b).Skip(look).Take(1));

    // ---- one own slot ----

    /// <summary>Sets <c>enableArm</c> (on, off, or null: not set) on the look at <paramref name="index"/>, for each wearer.</summary>
    public static bool SetLookArm(OutfitItem o, int index, bool? arm, Action? before = null) => ChangeLooks(o, index, l => l with { EnableArm = arm }, before);

    /// <summary>
    /// Sets the icon of the look at <paramref name="index"/>, for each wearer: a .ftex path with no extension. With
    /// null, the look shows the icon of the outfit.
    /// </summary>
    public static bool SetLookLogo(OutfitItem o, int index, string? logo, Action? before = null) => ChangeLooks(o, index, l => l with { IconFtexPath = logo }, before);

    /// <summary>
    /// Gives a look that has no name ID its first name (#18): a new name ID on the look of each wearer, and the name in
    /// the text of <paramref name="text"/> (the project item; for an existing V Framework mod this is not the outfit).
    /// <paramref name="before"/> gets the new name ID.
    /// </summary>
    /// <returns>The new name ID. null when nothing changed: the look has a name ID, the name is empty, or no such look.</returns>
    public static string? NameLook(OutfitItem o, ProjectItem text, int index, string name, Action<string>? before = null)
    {
        if (name.Length == 0 || index < 0 || Looks(o.Spec.Branches.Values.First()).ElementAtOrDefault(index) is not { DisplayName: null }) return null;
        string id = SortieSlots.NewLookName(o, text);
        // Only the first wearer's look is checked above. A look of another wearer that has its own name keeps it.
        if (!ChangeLooks(o, index, l => l.DisplayName == null ? l with { DisplayName = id } : l, () => before?.Invoke(id))) return null;
        text.Text[id] = new(StringComparer.Ordinal) { ["eng"] = name };
        return id;
    }

    // ---- heads ----

    /// <summary>True when the wearer lists the head.</summary>
    public static bool HasHead(OutfitItem o, PlayerBranch who, string head) =>
        o.Spec.Branches.TryGetValue(who, out var b) && (b.Base.HeadOptions.Contains(head) || b.HeadOptions.Contains(head));

    /// <summary>
    /// Lists a head for one wearer, or removes it. The list is the same on the branch and on each look of the wearer:
    /// V Framework 1.7 gives a look without its own list no HEAD row.
    /// </summary>
    public static bool SetHead(OutfitItem o, PlayerBranch who, string head, bool on, Action? before = null)
    {
        if (!o.Spec.Branches.TryGetValue(who, out var b) || on == HasHead(o, who, head)) return false;
        var heads = b.HeadOptions.Concat(b.Base.HeadOptions).Distinct().ToList();
        if (on) heads.Add(head); else heads.Remove(head);
        before?.Invoke();
        o.Spec.Branches[who] = b with { HeadOptions = [.. heads], Base = b.Base with { HeadOptions = [.. heads] }, Variants = [.. b.Variants.Select(v => v with { HeadOptions = [.. heads] })] };
        return true;
    }

    /// <summary>True when no look of any wearer shows the head of the wearer.</summary>
    public static bool HeadHidden(OutfitItem o) => o.Spec.Branches.Count > 0 && o.Spec.Branches.Values.All(b => !b.AnyLookShowsHead());

    /// <summary>
    /// "Hide the wearer's head": on writes <c>enableHead = false</c> on each look of each wearer, so V Framework loads
    /// no face. Off removes the values.
    /// </summary>
    public static bool HideHead(OutfitItem o, bool on, Action? before = null) =>
        on != HeadHidden(o) && ChangeLooks(o, -1, l => l with { EnableHead = on ? false : null }, before);
}
