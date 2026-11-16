using System.Xml.Linq;

namespace Svfm.Core.Mods;

/// <summary>The files a mod puts in the game, in the three kinds both mod managers track.</summary>
public sealed record ModFootprint(string Name, IReadOnlyList<string> QarFiles, IReadOnlyList<(string pack, string file)> PackFiles,
    IReadOnlyList<string> GameDirFiles)
{
    /// <summary>From a mod's metadata.xml (or one ModEntry of snakebite.xml).</summary>
    public static ModFootprint From(MgsvMetadata m) => new(m.Name,
        m.QarEntries.Select(q => q.FilePath).ToList(),
        m.FpkEntries.Select(f => (f.FpkFile, f.FilePath)).ToList(),
        m.FileEntries.Select(f => f.FilePath).ToList());

    public static ModFootprint From(MgsvBuilder b) => From(MgsvMetadata.Parse(b.BuildMetadataXml()));
}

public enum ConflictKind { ArchiveFile, PackFile, GameDirFile }

/// <summary>A file two mods both put in the game: the later one in the load order wins it.</summary>
public sealed record ModConflict(string OtherMod, ConflictKind Kind, string Path, string? Pack)
{
    public override string ToString() => Kind switch
    {
        ConflictKind.PackFile => $"{OtherMod}: {Path} inside {Pack}",
        ConflictKind.GameDirFile => $"{OtherMod}: game folder file {Path}",
        _ => $"{OtherMod}: {Path}",
    };
}

/// <summary>
/// Clash check against other mods, with the rules both managers use (SnakeBite PreinstallManager.hasConflict;
/// DMM ConflictScanner): two mods clash when they ship the same non-package archive file (texture pack, loose
/// texture, Lua …), the same file inside the same .fpk/.fpkd, or the same game-folder file. Packages at the same
/// path are merged file by file, so sharing a package (e.g. lang_default_data_eng.fpk) is not a clash as long as
/// the files inside differ — which is why every tool-made mod names its .lng2 after its own project.
/// </summary>
public static class ModConflicts
{
    public static List<ModConflict> Find(ModFootprint mine, IEnumerable<ModFootprint> others)
    {
        var list = new List<ModConflict>();
        var cmp = StringComparer.OrdinalIgnoreCase;
        var qar = new HashSet<string>(mine.QarFiles.Where(p => !IsPackage(p)), cmp);
        var packs = new HashSet<(string, string)>(mine.PackFiles.Select(f => (f.pack.ToLowerInvariant(), f.file.ToLowerInvariant())));
        var game = new HashSet<string>(mine.GameDirFiles, cmp);
        foreach (var o in others)
        {
            if (string.Equals(o.Name, mine.Name, StringComparison.Ordinal)) continue; // an older install of the same mod
            list.AddRange(o.QarFiles.Where(p => !IsPackage(p) && qar.Contains(p)).Select(p => new ModConflict(o.Name, ConflictKind.ArchiveFile, p, null)));
            list.AddRange(o.PackFiles.Where(f => packs.Contains((f.pack.ToLowerInvariant(), f.file.ToLowerInvariant())))
                .Select(f => new ModConflict(o.Name, ConflictKind.PackFile, f.file, f.pack)));
            list.AddRange(o.GameDirFiles.Where(game.Contains).Select(p => new ModConflict(o.Name, ConflictKind.GameDirFile, p, null)));
        }
        return list;
    }

    private static bool IsPackage(string p) => p.EndsWith(".fpk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".fpkd", StringComparison.OrdinalIgnoreCase);

    /// <summary>The mods SnakeBite has installed (snakebite.xml in the game folder).</summary>
    public static List<ModFootprint> ReadSnakeBiteXml(string snakebiteXml)
    {
        var root = XDocument.Load(snakebiteXml).Root;
        return (root?.Element("Mods")?.Elements("ModEntry") ?? []).Select(e => ModFootprint.From(MgsvMetadata.Parse(e.ToString()))).ToList();
    }

    /// <summary>
    /// The mods in a folder (.mgsv, or .zip holding one): DMM keeps every mod it can install in its Mods folder,
    /// so checking that folder covers the load order. Unreadable files are reported, not fatal.
    /// </summary>
    public static List<ModFootprint> ReadModsFolder(string folder, List<string>? problems = null)
    {
        var list = new List<ModFootprint>();
        foreach (var f in Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".mgsv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            try { list.Add(ModFootprint.From(MgsvMod.Open(f).Metadata)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or AmbiguousModException or System.Xml.XmlException)
            { problems?.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        return list;
    }
}
