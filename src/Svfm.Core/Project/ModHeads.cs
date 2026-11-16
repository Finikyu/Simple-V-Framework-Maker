using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>The head options of a mod on its outfits (the Heads worn tab, issue #21).</summary>
public static class ModHeads
{
    /// <summary>
    /// True when the head was made for this character: it needs a branch for it (Avatar may wear a Snake head unless it is
    /// built on Snake's own face). Tested Oct 25: male DD heads don't sit on DD women.
    /// </summary>
    public static bool Fits(HeadOptionSpec h, PlayerBranch who) => h.Branches.ContainsKey(who)
        || (who == PlayerBranch.Avatar && h.Branches.ContainsKey(PlayerBranch.Snake) && !h.SnakeFaceOnly);

    /// <summary>
    /// The branch with the keys added to every look that shows the head (on), or removed from every look (off). The
    /// lists are replaced, not changed: undo snapshots share the old ones.
    /// </summary>
    public static OutfitBranch With(OutfitBranch b, IReadOnlyCollection<string> keys, bool on)
    {
        bool branchHead = b.ShowsHead(b.Base);
        List<string> Edit(List<string> l, bool shows) =>
            !on ? l.Where(k => !keys.Contains(k)).ToList() : shows ? [.. l, .. keys.Where(k => !l.Contains(k)).Distinct()] : [.. l];
        return b with
        {
            HeadOptions = Edit(b.HeadOptions, branchHead),
            Base = b.Base with { HeadOptions = Edit(b.Base.HeadOptions, branchHead) },
            Variants = b.Variants.Select(v => v with { HeadOptions = Edit(v.HeadOptions, b.ShowsHead(v)) }).ToList(),
        };
    }

    /// <summary>
    /// The default of a ported mod: each head option of the mod is listed on every outfit of the mod, for each wearer the
    /// head has a model for. Not for an existing V Framework mod, which keeps the head lists its script has.
    /// </summary>
    public static void ListOnOutfits(ModProject p)
    {
        var heads = p.Items.OfType<HeadOptionItem>().ToList();
        if (heads.Count == 0) return;
        foreach (var o in p.Items.OfType<OutfitItem>())
            foreach (var who in o.Spec.Branches.Keys.ToList())
            {
                var keys = heads.Where(h => Fits(h.Spec, who)).Select(h => h.Spec.Key).ToList();
                if (keys.Count > 0) o.Spec.Branches[who] = With(o.Spec.Branches[who], keys, true);
            }
    }
}
