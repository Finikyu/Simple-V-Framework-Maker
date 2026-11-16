using Svfm.Core.Mods;
using Svfm.Core.Project;

namespace Svfm.Core.Conversion;

/// <summary>A mod that provides textures a port is missing.</summary>
public sealed record DependencySuggestion(string Source, string? Inner, string Name, string Author, int Provides, int Missing)
{
    public bool CoversAll => Provides == Missing;
}

/// <summary>
/// Finds the "dependent" .mgsv a mod needs: authors often ship textures as a separate download (Tactical Outfit's
/// Main Textures, a "Textures" part). Given the textures a port is missing, it looks through candidate mods (the other
/// .mgsv in the same .zip and the mods in the same folder) and reports which provide them.
/// </summary>
public static class DependencyFinder
{
    /// <summary>Candidates next to a mod: the other .mgsv inside its .zip, and every .mgsv / .zip in its folder.</summary>
    public static IEnumerable<(string source, string? inner)> Neighbours(string modPath, string? innerMgsv = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (modPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // A damaged zip has no neighbours inside it; the folder around it is still searched.
            IReadOnlyList<string> own;
            try { own = MgsvMod.ListMgsvInZip(modPath); } catch (InvalidDataException) { own = []; }
            foreach (var n in own)
                if (!string.Equals(n, innerMgsv, StringComparison.OrdinalIgnoreCase)) yield return (modPath, n);
        }
        string? dir = Path.GetDirectoryName(Path.GetFullPath(modPath));
        if (dir == null) yield break;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(modPath), StringComparison.OrdinalIgnoreCase) || !seen.Add(f)) continue;
            if (f.EndsWith(".mgsv", StringComparison.OrdinalIgnoreCase)) yield return (f, null);
            else if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<string> inner;
                try { inner = MgsvMod.ListMgsvInZip(f); } catch (InvalidDataException) { continue; }
                if (inner.Count == 0) yield return (f, null);
                foreach (var n in inner) yield return (f, n);
            }
        }
    }

    /// <summary>Which candidates provide the missing textures, best first. Candidates that provide none are left out.</summary>
    public static List<DependencySuggestion> Suggest(IReadOnlyCollection<ulong> missing, IEnumerable<(string source, string? inner)> candidates)
    {
        var want = missing.ToHashSet();
        var list = new List<DependencySuggestion>();
        if (want.Count == 0) return list;
        foreach (var (source, inner) in candidates.DistinctBy(c => (Path.GetFullPath(c.source).ToLowerInvariant(), c.inner?.ToLowerInvariant())))
        {
            MgsvMod m;
            try { m = MgsvMod.Open(source, inner); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { continue; }
            int n = TextureCheck.Provided(m.ArchiveFiles).Count(want.Contains);
            if (n > 0) list.Add(new DependencySuggestion(source, inner, m.Metadata.Name, m.Metadata.Author, n, want.Count));
        }
        return list.OrderByDescending(s => s.Provides).ToList();
    }
}
