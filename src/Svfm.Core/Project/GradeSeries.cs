namespace Svfm.Core.Project;

/// <summary>
/// "Make grades…" (issue #121, GLOSSARY "Grade series"): the grades a series of copies of one item can take, and the
/// "Grade before" links between the items of a project (<see cref="Svfm.Core.VFramework.DevelopRow.GradeBefore"/>).
/// </summary>
public static class GradeSeries
{
    private static IEnumerable<int> Free(ModProject p, ProjectItem src)
    {
        if (src.RdRow?.Grade is not { } g) yield break;
        var used = CopyFamily.Others(p, src).Select(i => i.RdRow!.Grade).ToHashSet();
        for (int n = g + 1; n <= CopyFamily.MaxGrade; n++) if (!used.Contains(n)) yield return n;
    }

    /// <summary>The most copies a series of the item can have: its family's free grades above it, up to 12.</summary>
    public static int MaxCount(ModProject p, ProjectItem src) => CopyFamily.NextGrade(p, src) == null ? 0 : Free(p, src).Count();

    /// <summary>The default grade of each box: the next free grade above the one before.</summary>
    public static int[] DefaultGrades(ModProject p, ProjectItem src, int count) => CopyFamily.NextGrade(p, src) == null ? [] : Free(p, src).Take(count).ToArray();

    /// <summary>Why the grades can't be made (out of range, two boxes the same, a grade the family uses), or null.</summary>
    public static string? Problem(ModProject p, ProjectItem src, IReadOnlyList<int> grades)
    {
        if (grades.FirstOrDefault(g => g is < 1 or > CopyFamily.MaxGrade) is var bad && bad != 0) return $"grade {bad} is not 1 to {CopyFamily.MaxGrade}";
        if (grades.GroupBy(g => g).FirstOrDefault(g => g.Count() > 1) is { } two) return $"two boxes have grade {two.Key} (one grade per family)";
        var family = CopyFamily.Others(p, src).Append(src).ToList();
        foreach (var g in grades)
            if (family.FirstOrDefault(i => i.RdRow?.Grade == g) is { } user) return $"grade {g} is used by \"{user.Title}\" (one grade per family)";
        return null;
    }

    /// <summary>The loop that the link of <paramref name="item"/> to <paramref name="beforeKey"/> would make, as names, or null.</summary>
    public static string? LoopOf(ModProject p, ProjectItem item, string? beforeKey)
    {
        var chain = new List<string> { item.Title };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (string? k = beforeKey; k != null && seen.Add(k);)
        {
            var at = p.Items.FirstOrDefault(i => i.Key == k);
            if (at == null) return null;
            chain.Add(at.Title);
            if (at == item) return string.Join(" needs ", chain);
            k = at.RdRow?.GradeBefore;
        }
        return null;
    }

    /// <summary>The project items an item can link to: each other item with an R&amp;D row and a key (null: every such item).</summary>
    public static IEnumerable<ProjectItem> Choices(ModProject p, ProjectItem? item) =>
        p.Items.Where(i => i != item && i.RdRow != null && !string.IsNullOrEmpty(i.Key));
}
