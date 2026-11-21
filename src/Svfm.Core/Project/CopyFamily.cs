namespace Svfm.Core.Project;

/// <summary>
/// One grade per Copy family (#113, owner rule): the source and every copy made from it (<see cref="ModProject.CopyOf"/>)
/// must not share a grade. V Framework refuses a second R&amp;D row in a used grade cell (DevelopInjectRefusal::CellOccupied).
/// Weapons and items only; outfits are not in this rule.
/// </summary>
public static class CopyFamily
{
    /// <summary>The highest grade of an R&amp;D row.</summary>
    public const int MaxGrade = 12; // #124: the game supports grades 1 to 12.

    private static bool InRule(ProjectItem i) => i.Kind is ItemKind.Weapon or ItemKind.Item && i.RdRow?.Grade != null;

    private static string Root(ModProject p, ProjectItem i) => p.CopyOf.GetValueOrDefault(i.Id, i.Id);

    /// <summary>The other members of the item's family that are in the project and in the rule.</summary>
    public static IEnumerable<ProjectItem> Others(ModProject p, ProjectItem item) =>
        p.Items.Where(i => i != item && InRule(i) && Root(p, i) == Root(p, item));

    /// <summary>The grade a new copy of the item gets: the next grade above it that no member uses; null when none up to 15 is free, or the item is not in the rule.</summary>
    public static int? NextGrade(ModProject p, ProjectItem item)
    {
        if (!InRule(item)) return null;
        var used = Others(p, item).Select(i => i.RdRow!.Grade!.Value).Append(item.RdRow!.Grade!.Value).ToHashSet();
        for (int g = item.RdRow!.Grade!.Value + 1; g <= MaxGrade; g++) if (!used.Contains(g)) return g;
        return null;
    }

    /// <summary>Why the family refuses a copy of the item (no free grade), or null.</summary>
    public static string? LockOf(ModProject p, ProjectItem item) =>
        InRule(item) && NextGrade(p, item) == null
            ? item.RdRow!.Grade >= MaxGrade ? $"grade {item.RdRow.Grade} is the highest grade (the game supports grades 1 to {MaxGrade})"
            : $"no grade above {item.RdRow.Grade} up to {MaxGrade} is free in its copy family (one grade per family)" : null;

    /// <summary>The first other member of the family with the item's grade, or null.</summary>
    public static ProjectItem? ClashOf(ModProject p, ProjectItem item) =>
        InRule(item) ? Others(p, item).FirstOrDefault(i => i.RdRow!.Grade == item.RdRow!.Grade) : null;

    /// <summary>The build errors: one line per pair of members with the same grade.</summary>
    public static IEnumerable<string> Errors(ModProject p) =>
        p.Items.Where(InRule).GroupBy(i => (Root(p, i), i.RdRow!.Grade)).Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(i => $"\"{i.Title}\""))} are one copy family and have the same grade {g.Key.Grade}. Give each its own grade on the R&D tab.");
}
