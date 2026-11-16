using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>
/// The bodies of one mod grouped by the character who wears them (issue #57). For each character, element 0 is the
/// Base look and the other elements are the Variants, in the order the bodies were found.
/// </summary>
public static class BodyPlan
{
    /// <summary>Groups the bodies by character. The characters are in <see cref="PlayerBranch"/> order.</summary>
    public static SortedDictionary<PlayerBranch, List<BodyReplacement>> Build(IReadOnlyList<(BodyReplacement body, PlayerBranch who)> bodies)
    {
        var plan = new SortedDictionary<PlayerBranch, List<BodyReplacement>>();
        foreach (var (body, who) in bodies)
        {
            if (!plan.TryGetValue(who, out var list)) plan[who] = list = [];
            list.Add(body);
        }
        return plan;
    }
}
