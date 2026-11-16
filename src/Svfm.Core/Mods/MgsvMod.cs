using System.IO.Compression;
using System.Security.Cryptography;
using Svfm.Core.Formats;

namespace Svfm.Core.Mods;

/// <summary>A note about a mod found while opening it.</summary>
public sealed record ModIssue(string Path, string Message);

/// <summary>
/// An opened mod: its metadata and its files, keyed by game path (/Assets/... for archive and
/// package files, /mod/... etc. for game-folder files). Opens a .mgsv, or a .zip holding one.
/// Follows the install rules of SnakeBite and DMM: a file in the zip that metadata.xml doesn't list
/// is not installed, so it is reported and left out.
/// </summary>
public sealed class MgsvMod
{
    public string SourcePath { get; }
    /// <summary>For a .zip download, the .mgsv inside it that was opened.</summary>
    public string? InnerName { get; }
    public MgsvMetadata Metadata { get; }
    public IReadOnlyDictionary<string, byte[]> ArchiveFiles { get; }
    public IReadOnlyDictionary<string, byte[]> GameFiles { get; }
    public IReadOnlyList<ModIssue> Issues { get; }
    /// <summary>
    /// Files that come from a mod the player installs separately (a required dependency, see <see cref="Combine"/>):
    /// path → that mod's name. They can be read (to render, to check textures) but a port never copies them.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExternalFiles { get; private init; } = new Dictionary<string, string>();
    /// <summary>The separately installed mods this one needs (names), from <see cref="Combine"/>.</summary>
    public IReadOnlyList<MgsvMetadata> Requires { get; private init; } = [];
    /// <summary>True when a port may copy this file (it is not from a separately installed dependency).</summary>
    public bool Ships(string path) => !ExternalFiles.ContainsKey(path);

    /// <summary>A mod with no files, standing for the vanilla game (a copy of a vanilla item is made from it).</summary>
    public static MgsvMod Empty(string name, string author = "") =>
        new("", null, new MgsvMetadata { Name = name, Author = author }, new Dictionary<string, byte[]>(StringComparer.Ordinal),
            new Dictionary<string, byte[]>(StringComparer.Ordinal), []);

    /// <summary>A mod made by the app from these archive files (a body model grafted into a game costume).</summary>
    public static MgsvMod Of(string name, string author, IReadOnlyDictionary<string, byte[]> archive) =>
        new("", null, new MgsvMetadata { Name = name, Author = author }, archive.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            new Dictionary<string, byte[]>(StringComparer.Ordinal), []);

    /// <summary>This mod with other contents for some of its archive files (a package cut down to one body of several).</summary>
    public MgsvMod WithArchiveFiles(IReadOnlyDictionary<string, byte[]> changed)
    {
        var archive = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, data) in ArchiveFiles) archive[path] = changed.GetValueOrDefault(path) ?? data;
        return new MgsvMod(SourcePath, InnerName, Metadata, archive, GameFiles.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), [.. Issues])
        { ExternalFiles = ExternalFiles, Requires = Requires };
    }

    private MgsvMod(string source, string? inner, MgsvMetadata meta, Dictionary<string, byte[]> archive,
        Dictionary<string, byte[]> game, List<ModIssue> issues)
    {
        SourcePath = source;
        InnerName = inner;
        Metadata = meta;
        ArchiveFiles = archive;
        GameFiles = game;
        Issues = issues;
    }

    /// <summary>Names of the .mgsv files inside a .zip download (a download can hold several versions).</summary>
    public static IReadOnlyList<string> ListMgsvInZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Where(e => e.FullName.EndsWith(".mgsv", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullName).ToList();
    }

    /// <summary>
    /// Several .mgsv files of one mod (e.g. a body per character plus a textures and a UI part) seen as one: their files
    /// together, a later file winning on the same path; the first one's metadata. Other mods do the same with a
    /// "dependent" download (Tactical Outfit's separate Main Textures): pass it in <paramref name="mods"/> to bundle what
    /// the port uses, or in <paramref name="required"/> to read it without copying (the player installs it too).
    /// </summary>
    /// <param name="mods">The parts, in the order the user installs them.</param>
    /// <param name="primary">The mod whose name, author and file the result stands for (default: the first of <paramref name="mods"/>).</param>
    /// <param name="required">Mods read but never shipped: their files are visible to the converters (rendering, texture
    /// checks) and listed in <see cref="ExternalFiles"/>; the parts in <paramref name="mods"/> win on the same path.</param>
    public static MgsvMod Combine(IReadOnlyList<MgsvMod> mods, IReadOnlyList<MgsvMod>? required = null, MgsvMod? primary = null)
    {
        if (mods.Count == 0) throw new ArgumentException("No mods.");
        var archive = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var game = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var external = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in required ?? [])
            foreach (var (k, v) in r.ArchiveFiles) { archive[k] = v; external[k] = r.Metadata.Name; }
        foreach (var m in mods)
        {
            foreach (var (k, v) in m.ArchiveFiles) { archive[k] = v; external.Remove(k); }
            foreach (var (k, v) in m.GameFiles) game[k] = v;
        }
        var head = primary ?? mods[0];
        return new MgsvMod(head.SourcePath, primary?.InnerName, head.Metadata, archive, game, mods.SelectMany(m => m.Issues).ToList())
        {
            ExternalFiles = external,
            Requires = (required ?? []).Select(r => r.Metadata).Concat(mods.SelectMany(m => m.Requires)).ToList(),
        };
    }

    /// <param name="path">.mgsv, or .zip containing one or more .mgsv.</param>
    /// <param name="innerMgsv">For a .zip with several .mgsv: which one (default: the only one).</param>
    public static MgsvMod Open(string path, string? innerMgsv = null)
    {
        // The zip is opened straight from the file, not from a byte array: a mod can be several gigabytes (the owner's
        // "MGSV AI Upscaled Textures" packs are 2-15 GB), and File.ReadAllBytes throws "The file is too long" on those.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string? inner = null;
        using (var outer = OpenZip(file))
        {
            bool hasMetadata = outer.GetEntry("metadata.xml") != null;
            var mgsvs = outer.Entries.Where(e => e.FullName.EndsWith(".mgsv", StringComparison.OrdinalIgnoreCase)).ToList();
            if (mgsvs.Count > 0 && (!hasMetadata || innerMgsv != null || !HasListedFiles(outer)))
            {
                var pick = innerMgsv != null
                    ? mgsvs.FirstOrDefault(e => e.FullName == innerMgsv) ?? throw new FileNotFoundException($"{innerMgsv} is not in {path}.")
                    : mgsvs.Count == 1 ? mgsvs[0] : throw new AmbiguousModException(path, mgsvs.Select(e => e.FullName).ToList());
                inner = pick.FullName;
                using var s = pick.Open();
                var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return Load(path, inner, ms);
            }
        }
        file.Position = 0;
        return Load(path, inner, file);
    }

    private static bool HasListedFiles(ZipArchive zip)
    {
        using var s = zip.GetEntry("metadata.xml")!.Open();
        var meta = MgsvMetadata.Parse(new StreamReader(s).ReadToEnd());
        return meta.QarEntries.Count + meta.FileEntries.Count > 0;
    }

    private static MgsvMod Load(string source, string? inner, Stream stream)
    {
        using var zip = OpenZip(stream);
        var metaEntry = zip.GetEntry("metadata.xml") ?? throw new InvalidDataException("No metadata.xml: not a SnakeBite/DMM mod.");
        MgsvMetadata meta;
        using (var s = metaEntry.Open()) meta = MgsvMetadata.Parse(new StreamReader(s).ReadToEnd());

        var issues = new List<ModIssue>();
        var archive = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var game = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Some mods list their files without the /Assets root (the Halo Assault Rifle in Guntest writes
        // /tpp/pack/collectible/chimera/receiver/ar02_main0_def_v00.fpk). The game and every converter use the full
        // path, so a mod written that way matched no route at all and came back as "No route known for these files"
        // (the owner: "Some gun mods seem to have difficulty having their routes known"). The root is put back here, once,
        // for every mod, rather than in each converter.
        // Only /tpp/ is under /Assets. /mod/ (the V Framework module) and /master/ are not, so they are left alone.
        static string GamePath(string p) => p.StartsWith("/tpp/", StringComparison.Ordinal) ? "/Assets" + p : p;
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')))
            // Two names that differ only in case or slash: the first is read, the next one is an issue (it threw before).
            if (!entries.TryAdd(e.FullName.Replace('\\', '/'), e))
                issues.Add(new ModIssue("/" + e.FullName.Replace('\\', '/'), "in the mod twice (the names differ only in case or slash); only the first is read"));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.xml", "readme.txt" };

        foreach (var q in meta.QarEntries)
        {
            string zipName = q.FilePath.TrimStart('/');
            if (!entries.TryGetValue(zipName, out var ze)) { issues.Add(new ModIssue(q.FilePath, "listed in metadata.xml but not in the mod; it would not install")); continue; }
            used.Add(zipName);
            byte[] d = Read(ze);
            if (q.ContentHash.Length > 0 && !string.Equals(Convert.ToHexString(MD5.HashData(d)), q.ContentHash, StringComparison.OrdinalIgnoreCase))
                issues.Add(new ModIssue(q.FilePath, "content differs from the hash in metadata.xml (the mod was edited after it was built)"));
            archive[GamePath(q.FilePath)] = d;
        }
        foreach (var f in meta.FileEntries)
        {
            string zipName = "GameDir/" + f.FilePath.TrimStart('/');
            if (!entries.TryGetValue(zipName, out var ze)) { issues.Add(new ModIssue(f.FilePath, "listed in metadata.xml but not in the mod")); continue; }
            used.Add(zipName);
            game[GamePath(f.FilePath)] = Read(ze);
        }
        // FpkEntries should describe the packages exactly; SnakeBite skips listed files that are missing.
        foreach (var group in meta.FpkEntries.GroupBy(e => e.FpkFile))
        {
            if (!archive.TryGetValue(GamePath(group.Key), out var pkgBytes)) { issues.Add(new ModIssue(group.Key, "package listed in FpkEntries but not in the mod")); continue; }
            FoxPackage pkg;
            try { pkg = FoxPackage.Read(pkgBytes); }
            catch (InvalidDataException) { issues.Add(new ModIssue(group.Key, "not a valid package")); continue; }
            var inside = pkg.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var e in group.Where(e => !inside.Contains(e.FilePath)))
                issues.Add(new ModIssue(group.Key + " > " + e.FilePath, "listed in metadata.xml but not inside the package; skipped"));
        }
        foreach (var name in entries.Keys.Where(n => !used.Contains(n) && !n.EndsWith('/')))
            issues.Add(new ModIssue("/" + name, "in the mod but not listed in metadata.xml; SnakeBite and DMM skip it"));
        // Unnamed packages (SnakeBite's hash names at the archive root, as GzsTool unpacks files it has no name for:
        // Volgin's Horse ships buddy_horse2_00.fpk as /34a43c0a13f24.fpk) are installed by that hash, so they are the
        // package the dictionary names for it: they are read under that path, as the game sees them (the converters
        // find what a mod replaces by package path). Textures keep their hash names: models name them by hash anyway,
        // and a texture's hash can land on a vanilla name of another game (Quiet IGA's /68c5bfbc5206.ftex is an MGO
        // path in the dictionary), where renaming would change how the port treats it.
        foreach (var key in archive.Keys.ToList())
        {
            if (!(key.EndsWith(".fpk", StringComparison.Ordinal) || key.EndsWith(".fpkd", StringComparison.Ordinal) || key.EndsWith(".pftxs", StringComparison.Ordinal))) continue;
            int dot = key.IndexOf('.');
            if (dot <= 1 || Hashing.FoxHash.HashedName(key[..dot]) == null) continue;
            if (Archives.PathDictionary.Bundled.TryGetPath(Hashing.FoxHash.PathCode64(key)) is not { } real || real == key || archive.ContainsKey(real)) continue;
            archive[real] = archive[key];
            archive.Remove(key);
        }

        return new MgsvMod(source, inner, meta, archive, game, issues);
    }

    private static byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Opens a zip; DMM also accepts mods with data in front of the zip, so that is handled too.</summary>
    private static ZipArchive OpenZip(Stream stream)
    {
        try { return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException e)
        {
            // Data in front of the zip: find the first local file header and start there.
            if (!stream.CanSeek) throw;
            stream.Position = 0;
            var buffer = new byte[1 << 20];
            long start = -1;
            int keep = 0;
            while (start < 0)
            {
                int n = stream.Read(buffer, keep, buffer.Length - keep);
                if (n <= 0) break;
                int total = keep + n;
                int at = buffer.AsSpan(0, total).IndexOf("PK\u0003\u0004"u8);
                if (at >= 0) { start = stream.Position - total + at; break; }
                keep = Math.Min(3, total);
                buffer.AsSpan(total - keep).CopyTo(buffer);
            }
            if (start < 0) throw new InvalidDataException("This is not a .mgsv or .zip file: it has no zip data in it.", e);
            if (start == 0) throw new InvalidDataException("The .mgsv file is damaged: " + e.Message, e);
            stream.Position = start;
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
    }
}

public sealed class AmbiguousModException(string path, IReadOnlyList<string> choices)
    : Exception($"{path} contains {choices.Count} mods. Pick one: {string.Join(", ", choices)}")
{
    public IReadOnlyList<string> Choices { get; } = choices;
}
