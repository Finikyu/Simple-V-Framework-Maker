using Svfm.Core.Knowledge;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>
/// The bonus slots of a project as one ExtendVanillaOutfit call for each vanilla camo (owner, Nov 6). V Framework 1.7
/// writes the looks of each call from the start of the camo's block, so two calls for one camo overwrite each other
/// (.docs/Research_Bonus_Slot_Order.md, Q1 and Q8).
/// </summary>
public static class BonusSlots
{
    /// <summary>
    /// Merges the slots that outfits put under vanilla camos and the extra-looks items into one call for each camo. In each
    /// wearer's branch the looks keep the project order. When two items set one per-outfit value (arm, head, Heads worn,
    /// voice; abilities only from items without slots, #61 Q4) differently, the last item in project order wins and a warning names both (Q11). The slots then
    /// follow <paramref name="order"/> (<see cref="ModProject.BonusOrder"/>).
    /// </summary>
    public static List<ExtendVanillaSpec> Merge(IEnumerable<ProjectItem> items, List<string> warnings, List<string> errors, IReadOnlyDictionary<string, List<string>>? order = null)
    {
        var calls = new List<(string Item, string Id, ExtendVanillaSpec Spec)>();
        foreach (var i in items)
        {
            string name = i.Title.Length > 0 ? i.Title : i.Id;
            if (i is OutfitItem o)
            {
                calls.AddRange(o.Spec.AlsoUnder.Select(o.AlsoUnderSpec).Where(e => e.Branches.Count > 0).Select(e => (name, i.Id, e)));
                foreach (var a in o.Spec.AlsoUnder) ArmWarnings(o, name, a, warnings);
            }
            else if (i is ExtraLooksItem { Spec.Outfit: not null } x) calls.Add((name, i.Id, x.Spec));
        }
        var merged = new List<ExtendVanillaSpec>();
        foreach (var g in calls.GroupBy(c => CamoName(c.Spec.Outfit!), StringComparer.OrdinalIgnoreCase))
        {
            var e = new ExtendVanillaSpec { Outfit = g.First().Spec.Outfit };
            var setBy = new Dictionary<(PlayerBranch, string), string>();
            var keys = new Dictionary<PlayerBranch, List<string>>();
            foreach (var (item, id, s) in g)
            {
                e.Header.AddRange(s.Header);
                foreach (var (who, b) in s.Branches)
                {
                    var m = e.Branches.GetValueOrDefault(who) ?? new VanillaBranch();
                    (keys.TryGetValue(who, out var ks) ? ks : keys[who] = []).AddRange(b.Variants.Select(v => Key(id, v.DisplayName ?? "")));
                    bool Take(string field, bool set, bool same)
                    {
                        if (!set) return false;
                        if (setBy.TryGetValue((who, field), out var was) && was != item && !same)
                            warnings.Add($"{g.Key} ({LuaWriter.BranchName(who)}): \"{was}\" and \"{item}\" both set {field}; \"{item}\" wins (the last in the project).");
                        setBy[(who, field)] = item;
                        return true;
                    }
                    // #61 Q4: an item with slots puts its abilities on its own slots; one without slots sets them for the camo.
                    bool slots = b.Variants.Count > 0;
                    var mine = slots && !b.Abilities.IsEmpty ? b.Variants.Select(v => v with { Abilities = v.Abilities ?? b.Abilities }) : b.Variants;
                    e.Branches[who] = m with
                    {
                        Variants = [.. m.Variants, .. mine],
                        EnableArm = Take("the arm", b.EnableArm != null, b.EnableArm == m.EnableArm) ? b.EnableArm : m.EnableArm,
                        EnableHead = Take("the head", b.EnableHead != null, b.EnableHead == m.EnableHead) ? b.EnableHead : m.EnableHead,
                        HeadOptions = Take("Heads worn", b.HeadOptions.Count > 0, b.HeadOptions.SequenceEqual(m.HeadOptions)) ? b.HeadOptions : m.HeadOptions,
                        Voice = Take("the voice", b.Voice != null, b.Voice == m.Voice) ? b.Voice : m.Voice,
                        Abilities = Take("the abilities", !slots && !b.Abilities.IsEmpty, b.Abilities == m.Abilities) ? b.Abilities : m.Abilities,
                    };
                }
            }
            var want = order?.GetValueOrDefault(g.Key);
            foreach (var (who, ks) in keys)
                e.Branches[who] = e.Branches[who] with { Variants = [.. Ordered(want, ks).Select(k => e.Branches[who].Variants[k])] };
            merged.Add(e);
        }

        // One selector slot for each look, shared by every wearer: the widest branch counts, all camos of a parts type together.
        foreach (var pg in merged.GroupBy(e => PartsOf(e.Outfit!), StringComparer.OrdinalIgnoreCase))
        {
            int n = pg.Sum(e => e.Branches.Values.Max(b => b.Variants.Count));
            if (n > VFrameworkLimits.VariantsPerVanillaPartsType)
                errors.Add($"The bonus slots under {string.Join(", ", pg.Select(e => CamoName(e.Outfit!)))} need {n} slots; the vanilla suit holds {VFrameworkLimits.VariantsPerVanillaPartsType} for all its camos together.");
            // V Framework keeps one per-outfit value for each parts type and wearer, for the camo of the last call (Research_Bonus_Slot_Order.md, Q9).
            foreach (var who in pg.SelectMany(e => e.Branches.Keys).Distinct())
                foreach (var (field, set) in PerOutfit)
                {
                    var camos = pg.Where(e => e.Branches.TryGetValue(who, out var b) && set(b)).Select(e => CamoName(e.Outfit!)).ToList();
                    if (camos.Count > 1)
                        warnings.Add($"{string.Join(", ", camos)} ({LuaWriter.BranchName(who)}) are one vanilla suit and all set {field}; V Framework keeps only the last ({camos[^1]}), so {string.Join(", ", camos.SkipLast(1))} get none.");
                }
        }
        // shortcut: counts one native cell for each row; a row with more native cells shows fewer added slots (AddListSuit.cpp 1441).
        foreach (var e in merged)
            foreach (var (who, b) in e.Branches.Where(kv => kv.Value.Variants.Count > RowCells - 1))
                warnings.Add($"{CamoName(e.Outfit!)} ({LuaWriter.BranchName(who)}) has {b.Variants.Count} bonus slots; a Sortie Prep row shows {RowCells} cells, so the slots after the {RowCells - 1}th do not show.");
        return merged;
    }

    /// <summary>
    /// Under a vanilla outfit, V Framework 1.7 has no working arm switch. enableArm = false only hides the arm model: the arm
    /// ability stays, and using it softlocks the character (BlockControllerImpl_LoadPartsNew.cpp, the CaseD arm table is set
    /// for custom parts types only; in game Nov 8). So a look that has no arm on its own slot shows the arm there.
    /// </summary>
    private static void ArmWarnings(OutfitItem o, string name, AlsoUnder a, List<string> warnings)
    {
        foreach (var s in a.Slots.Where(s => PlayerBranches.HasBionicArm([s.Who])))
        {
            if (!o.Spec.Branches.TryGetValue(s.Who, out var br)) continue;
            var look = s.Index < 0 ? br.Base : s.Index < br.Variants.Count ? br.Variants[s.Index] : null;
            string where = $"{CamoName(a.Outfit)} ({LuaWriter.BranchName(s.Who)})";
            string? w = s.EnableArm == false
                ? $"{where}: \"{name}\" turns the prosthetic arm off. Under a vanilla outfit V Framework 1.7 hides only the arm model; the arm ability stays, and using it softlocks the character."
                : look?.EnableArm == false
                    ? $"{where}: a look of \"{name}\" has no prosthetic arm (own head or own left arm). Under a vanilla outfit V Framework 1.7 cannot turn the arm off, so the arm shows on it."
                    : null;
            if (w != null && !warnings.Contains(w)) warnings.Add(w);
        }
    }

    private static readonly (string Field, Func<VanillaBranch, bool> Set)[] PerOutfit =
    [
        ("the arm", b => b.EnableArm != null), ("the head", b => b.EnableHead != null), ("Heads worn", b => b.HeadOptions.Count > 0),
        ("the voice", b => b.Voice != null), ("the abilities", b => !b.Abilities.IsEmpty),
    ];

    /// <summary>Names one bonus slot in <see cref="ModProject.BonusOrder"/>: the item and the slot's name id.</summary>
    public static string Key(string itemId, string langId) => itemId + "/" + langId;

    /// <summary>The positions of <paramref name="keys"/> sorted by <paramref name="order"/>; keys not in it follow, in their own order.</summary>
    public static List<int> Ordered(IReadOnlyList<string>? order, IReadOnlyList<string> keys) =>
        [.. Enumerable.Range(0, keys.Count).OrderBy(k => order?.ToList().IndexOf(keys[k]) is int n and >= 0 ? n : int.MaxValue)];

    /// <summary>
    /// #62: the bonus slots after the outfit's own looks are removed or moved. <paramref name="order"/>[new position] is the
    /// old position (position 0 is the Main look, Index -1). A slot whose look is gone shows the new Main look: removing an
    /// own slot never removes a bonus slot. Returns new lists.
    /// </summary>
    public static IReadOnlyList<AlsoUnder> FollowLooks(IReadOnlyList<AlsoUnder> list, IReadOnlyList<int> order) =>
        [.. list.Select(a => a with { Slots = [.. a.Slots.Select(s => s with { Index = Math.Max(0, order.ToList().IndexOf(s.Index + 1)) - 1 })] })];

    /// <summary>
    /// #85: V Framework 1.7 loads the camo's vanilla camo for a slot without an installed camoFpk (camoFpk = false does the
    /// same, BlockControllerImpl_LoadPartsNew.cpp hkLoadPlayerCamoFpk). A slot under a fatigues camo (one with a fova camo
    /// package) whose look has no camo of its own thus points at this empty package (.docs/Research_Bonus_Slot_Camo.md).
    /// </summary>
    public static bool NeedsNoCamo(string outfit, Look look) =>
        look.CamoFpk == null && look.CamoFv2 == null && GameKnowledge.CamoByName(CamoName(outfit))?.FovaCamoId != null;
    public static string NoCamoFpk(string prefix) => $"/Assets/tpp/pack/player/fova/plfova_{prefix}_nocamo.fpk";
    public static string NoCamoFv2(string prefix) => $"/Assets/tpp/fova/chara/sna/{prefix}_nocamo.fv2";
    /// <summary>The empty camo package: an .fpk with an empty .fv2 (as hyu0_main0_v00.fpk) and an empty .fpkd (as
    /// pldfrm_avm0_type0_v00.fpkd, 48 bytes). No texture pack: vanilla hyu0_main0_v00 has none either.</summary>
    public static Dictionary<string, byte[]> NoCamoFiles(string prefix)
    {
        var fpk = new Formats.FoxPackage();
        fpk.Files.Add(new Formats.PackageFile(NoCamoFv2(prefix), Formats.Fv2File.Empty().Write()));
        return new(StringComparer.Ordinal) { [NoCamoFpk(prefix)] = fpk.Write(), [NoCamoFpk(prefix) + "d"] = new Formats.FoxPackage { IsFpkd = true }.Write() };
    }

    /// <summary>Cells in one Sortie Prep row, the suit's own cells included (V Framework AddListSuit.cpp).</summary>
    public const int RowCells = 15;

    /// <summary>The camo's name for a name or a camo number.</summary>
    public static string CamoName(string outfit) =>
        int.TryParse(outfit, out int n) ? GameKnowledge.Camos.FirstOrDefault(c => c.PlayerCamoType == n)?.Name ?? outfit : outfit;

    // shortcut: the game maps a camo to its parts type at run time (GetPartsTypeAtCamoType); the camo's first player parts
    // name stands in for it. Upgrade if a camo with other parts in the list proves to have its own slots.
    private static string PartsOf(string outfit) => GameKnowledge.CamoByName(CamoName(outfit))?.PlayerParts.FirstOrDefault() ?? CamoName(outfit);
}
