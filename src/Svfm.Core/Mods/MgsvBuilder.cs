using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;

namespace Svfm.Core.Mods;

/// <summary>
/// Builds a .mgsv the way MakeBite (SnakeBite's mod builder) does, so it installs with both
/// SnakeBite and DMM: metadata.xml first, then the files under Assets/ and GameDir/.
/// <list type="bullet">
/// <item>QarEntries: every /Assets file; Compressed = true for packages only.</item>
/// <item>FpkEntries: every file inside every package.</item>
/// <item>FileEntries: game-folder files.</item>
/// </list>
/// Entries are sorted by path (ordinal), MD5 in upper-case hex, CRLF line ends.
/// </summary>
public sealed class MgsvBuilder
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public string Author { get; set; } = "";
    public string Website { get; set; } = "";
    public string Description { get; set; } = "";
    public string MgsVersion { get; set; } = "1.0.15.4";
    public string SbVersion { get; set; } = "0.9.2.5";

    private readonly SortedDictionary<string, byte[]> _archive = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, byte[]> _game = new(StringComparer.Ordinal);

    /// <summary>A file for the game archives, by game path (/Assets/...).</summary>
    public void AddArchiveFile(string gamePath, byte[] data)
    {
        // An unnamed file keeps SnakeBite's hash name (/1162e2d88aa33.ftex); everything else is under /Assets/.
        bool hashed = gamePath.IndexOf('.') is > 1 and var dot && Hashing.FoxHash.HashedName(gamePath[..dot]) != null;
        if (!hashed && !gamePath.StartsWith("/Assets/", StringComparison.Ordinal))
            throw new ArgumentException($"Archive files must be under /Assets/: {gamePath}");
        _archive[gamePath] = data;
    }

    /// <summary>Takes a file out of the game archives (a stale .ftexs mip of a replaced texture).</summary>
    public bool RemoveArchiveFile(string gamePath) => _archive.Remove(gamePath);

    /// <summary>A file for the game folder, by path relative to it (/mod/modules/MyMod.lua).</summary>
    public void AddGameFile(string relativePath, byte[] data)
    {
        if (!relativePath.StartsWith('/')) relativePath = "/" + relativePath;
        _game[relativePath] = data;
    }

    public IReadOnlyDictionary<string, byte[]> ArchiveFiles => _archive;
    public IReadOnlyDictionary<string, byte[]> GameFiles => _game;

    /// <summary>
    /// Problems that would make SnakeBite or DMM leave something out: archive file types they
    /// don't install, package contents of a type the package can't hold, invalid packages.
    /// </summary>
    public List<ModIssue> Check()
    {
        var issues = new List<ModIssue>();
        foreach (var (path, data) in _archive)
        {
            string ext = PackageOrder.LastExtension(path);
            if (!PackageOrder.ArchiveTypes.Contains(ext))
                issues.Add(new ModIssue(path, $".{ext} files are not installed into the archives"));
            if (ext is "fpk" or "fpkd")
            {
                FoxPackage pkg;
                try { pkg = FoxPackage.Read(data); }
                catch (InvalidDataException) { issues.Add(new ModIssue(path, "not a valid package")); continue; }
                foreach (var f in pkg.Files.Where(f => !PackageOrder.IsAllowed(f.Path, pkg.IsFpkd)))
                    issues.Add(new ModIssue(path + " > " + f.Path, $"a .{f.Extension} file can't be inside a .{ext}; it would be dropped"));
            }
            else if (ext == "pftxs")
                try { TexturePack.Read(data); } catch (InvalidDataException) { issues.Add(new ModIssue(path, "not a valid package")); }
        }
        string[] blocked = ["/mgsvtpp.exe", "/mgsvmgo.exe", "/steam_api64.dll", "/version_info.txt", "/snakebite.xml"];
        foreach (var path in _game.Keys)
        {
            string lower = path.ToLowerInvariant();
            if (blocked.Contains(lower) || lower.StartsWith("/master/", StringComparison.Ordinal) && lower.EndsWith(".dat", StringComparison.Ordinal))
                issues.Add(new ModIssue(path, "SnakeBite and DMM refuse to overwrite this game file"));
        }
        return issues;
    }

    public string BuildMetadataXml()
    {
        // XmlSerializer (what MakeBite uses) escapes only these; non-ASCII text is written as is.
        static string Attr(string s) => Text(s).Replace("\"", "&quot;");
        static string Text(string s) => Valid(s).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        // A control character (pasted text) makes a metadata.xml that no XML reader accepts, so SnakeBite can't install the mod: it is dropped.
        static string Valid(string s) => s.Any(c => !System.Xml.XmlConvert.IsXmlChar(c) && !char.IsSurrogate(c)) ? new string(s.Where(c => System.Xml.XmlConvert.IsXmlChar(c) || char.IsSurrogate(c)).ToArray()) : s;
        static string Md5(byte[] d) => Convert.ToHexString(MD5.HashData(d));

        var x = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            $"<ModEntry xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" Name=\"{Attr(Name)}\" Version=\"{Attr(Version)}\" Author=\"{Attr(Author)}\" Website=\"{Attr(Website)}\">",
            $"  <MGSVersion Version=\"{MgsVersion}\" />",
            $"  <SBVersion Version=\"{SbVersion}\" />",
            // XmlSerializer (what MakeBite uses) writes an empty element self-closed, so a mod with no description has
            // "<Description />" - not "<Description></Description>". Matching it keeps a rebuild byte-identical.
            Description.Length == 0 ? "  <Description />"
                : $"  <Description>{Text(Description.ReplaceLineEndings("\r\n"))}</Description>", // XmlWriter writes CRLF inside text too
        };
        var qarRows = new List<string>();
        var fpkRows = new List<(string pkg, string file, string md5)>();
        // MakeBite lists files in Windows folder order: case-insensitive (upper-case ordinal), and
        // packages by their source folder name (x_fpk, x_fpkd), which puts "a_b.fpk" before "a.fpk"'s
        // neighbours differently from a plain path sort. Matched on 5 MakeBite-built mods.
        foreach (var (path, data) in _archive.OrderBy(a => a.Key.ToUpperInvariant(), StringComparer.Ordinal))
        {
            bool package = path.EndsWith(".fpk", StringComparison.Ordinal) || path.EndsWith(".fpkd", StringComparison.Ordinal);
            qarRows.Add($"    <QarEntry Hash=\"{FoxHash.PathCode64(path)}\" FilePath=\"{Attr(path)}\" Compressed=\"{(package ? "true" : "false")}\" ContentHash=\"{Md5(data)}\" />");
            if (package)
                foreach (var f in FoxPackage.Read(data).Files)
                    fpkRows.Add((path, f.Path, Md5(f.Data)));
        }
        Section(x, "QarEntries", qarRows);
        Section(x, "FpkEntries", fpkRows.OrderBy(r => FolderKey(r.pkg), StringComparer.Ordinal).ThenBy(r => r.file.ToUpperInvariant(), StringComparer.Ordinal)
            .Select(r => $"    <FpkEntry FpkFile=\"{Attr(r.pkg)}\" FilePath=\"{Attr(r.file)}\" ContentHash=\"{r.md5}\" />").ToList());
        Section(x, "FileEntries", _game.OrderBy(g => g.Key.ToUpperInvariant(), StringComparer.Ordinal).Select(g => $"    <FileEntry FilePath=\"{Attr(g.Key)}\" ContentHash=\"{Md5(g.Value)}\" />").ToList());
        x.Add("  <WmvEntries />");
        x.Add("</ModEntry>");
        return string.Join("\r\n", x);
    }

    private static string FolderKey(string package) =>
        (package.EndsWith(".fpkd", StringComparison.Ordinal) ? package[..^5] + "_fpkd" : package[..^4] + "_fpk").ToUpperInvariant();

    private static void Section(List<string> x, string name, List<string> rows)
    {
        if (rows.Count == 0) { x.Add($"  <{name} />"); return; }
        x.Add($"  <{name}>");
        x.AddRange(rows);
        x.Add($"  </{name}>");
    }

    /// <summary>The finished .mgsv (a zip).</summary>
    public byte[] Build()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "metadata.xml", Encoding.UTF8.GetBytes(BuildMetadataXml()));
            foreach (var (path, data) in _archive) Add(zip, path.TrimStart('/'), data);
            foreach (var (path, data) in _game) Add(zip, "GameDir/" + path.TrimStart('/'), data);
        }
        return ms.ToArray();
    }

    /// <summary>Writes a temp file next to <paramref name="file"/> first, so a failed write leaves the old file whole.</summary>
    public void Save(string file)
    {
        byte[] data = Build();
        string tmp = file + ".tmp";
        try { File.WriteAllBytes(tmp, data); File.Move(tmp, file, overwrite: true); }
        finally { File.Delete(tmp); }
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(data);
    }

    /// <summary>Starts a builder from an opened mod (same files and details), e.g. to edit and rebuild it.</summary>
    public static MgsvBuilder From(MgsvMod mod)
    {
        var m = mod.Metadata;
        var b = new MgsvBuilder
        {
            Name = m.Name, Version = m.Version, Author = m.Author, Website = m.Website,
            Description = m.Description, MgsVersion = m.MgsVersion, SbVersion = m.SbVersion,
        };
        foreach (var (p, d) in mod.ArchiveFiles) b.AddArchiveFile(p, d);
        foreach (var (p, d) in mod.GameFiles) b.AddGameFile(p, d);
        return b;
    }
}
