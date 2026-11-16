namespace Svfm.Core.Formats;

/// <summary>
/// Which file types SnakeBite (and DMM, which follows it) accept in each container, and the
/// order it writes package contents in. Anything not listed is dropped at install, so the
/// converter must not rely on it. Lists recorded from SnakeBite 0.9.x (GzsLib archiveExtensions).
/// </summary>
public static class PackageOrder
{
    /// <summary>Types SnakeBite puts into a .dat archive.</summary>
    public static readonly IReadOnlyList<string> ArchiveTypes =
        ["bnk", "dat", "ffnt", "fmtt", "fpk", "fpkd", "fsm", "fsop", "ftex", "ftexs", "json", "lua", "pftxs", "sbp", "subp", "wem"];

    /// <summary>Types allowed in a .fpk, in the order they are written.</summary>
    public static readonly IReadOnlyList<string> FpkTypes =
    [
        "caar", "fnt", "atsh", "frig", "adm", "frt", "fpkl", "fsm", "ftdp", "geobv", "ftex", "geoms", "gimr", "gpfp",
        "grxla", "grxoc", "htre", "lba", "lpsh", "mog", "mtar", "nav2", "nta", "rdf", "ends", "sand", "mbl", "tcvp",
        "spch", "trap", "uigb", "uilb", "pcsp", "tre2", "fstb", "twpf", "fv2t", "fmdl", "geom", "gskl", "fcnp", "frdv",
        "fdes", "fclo", "uif", "uia", "subp", "sani", "ladb", "frl", "fv2", "obr", "lng2", "mtard", "obrb", "dfrm",
    ];

    /// <summary>Types allowed in a .fpkd, in the order they are written.</summary>
    public static readonly IReadOnlyList<string> FpkdTypes =
        ["fox2", "evf", "parts", "vfxlb", "vfx", "vfxlf", "veh", "frld", "des", "bnd", "tgt", "phsd", "ph", "sim", "clo", "fsd", "sdf", "lua", "lng"];

    /// <summary>The last extension of a path (".1.ftexs" gives "ftexs"), as SnakeBite compares it.</summary>
    public static string LastExtension(string path)
    {
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        return dot > slash ? path[(dot + 1)..] : "";
    }

    public static bool IsAllowed(string path, bool fpkd) => (fpkd ? FpkdTypes : FpkTypes).Contains(LastExtension(path));

    /// <summary>
    /// Orders files as SnakeBite writes them: by path (ordinal; ascending for .fpk, descending for
    /// .fpkd), then grouped by type in the list order. Files of types not allowed are returned separately.
    /// </summary>
    public static (List<T> ordered, List<T> dropped) Order<T>(IEnumerable<T> files, Func<T, string> path, bool fpkd)
    {
        var list = files.ToList();
        list.Sort((a, b) => fpkd ? string.CompareOrdinal(path(b), path(a)) : string.CompareOrdinal(path(a), path(b)));
        var types = fpkd ? FpkdTypes : FpkTypes;
        var ordered = new List<T>();
        foreach (var t in types)
            ordered.AddRange(list.Where(f => LastExtension(path(f)) == t));
        var dropped = list.Where(f => !types.Contains(LastExtension(path(f)))).ToList();
        return (ordered, dropped);
    }

    /// <summary>Puts a package's files in SnakeBite order and returns the files it would drop.</summary>
    public static List<PackageFile> ApplySnakeBiteOrder(FoxPackage package)
    {
        var (ordered, dropped) = Order(package.Files, f => f.Path, package.IsFpkd);
        package.Files.Clear();
        package.Files.AddRange(ordered);
        return dropped;
    }
}
