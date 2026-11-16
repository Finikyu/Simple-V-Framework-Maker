namespace Svfm.Core.Archives;

/// <summary>
/// The vanilla game files, looked up by game path across every source in priority order.
/// Sources are QAR archives or unpacked folders (GzsTool dumps). Retail copies made by the mod
/// managers (*.original) are used in place of the live, modded archives.
/// </summary>
public sealed class GameData : IDisposable
{
    private readonly List<(string name, QarArchive? qar, string? folder)> _sources = [];

    public IReadOnlyList<string> Sources => _sources.Select(s => s.name).ToList();

    /// <summary>
    /// True when this is a whole installed game (<see cref="OpenGameFolder"/>): a file it doesn't have is not in the game.
    /// Partial dumps (tests) are not complete; checks then fall back on the path dictionary.
    /// </summary>
    public bool IsComplete { get; private set; }

    /// <summary>
    /// Opens the archives of an installed game, patch archives first:
    /// master\0\00.dat, 01.dat, master\1\MGSVTUPDATEV0110\0\00.dat, 01.dat, then chunk0–4, texture0–4.
    /// For each, the manager's *.original copy is preferred when present, so mods never leak in.
    /// </summary>
    public static GameData OpenGameFolder(string gameFolder)
    {
        var g = new GameData();
        string master = Path.Combine(gameFolder, "master");
        string[] order =
        [
            Path.Combine("0", "00.dat"), Path.Combine("0", "01.dat"),
            Path.Combine("1", "MGSVTUPDATEV0110", "0", "00.dat"), Path.Combine("1", "MGSVTUPDATEV0110", "0", "01.dat"),
            "chunk0.dat", "chunk1.dat", "chunk2.dat", "chunk3.dat", "chunk4.dat",
            "texture0.dat", "texture1.dat", "texture2.dat", "texture3.dat", "texture4.dat",
        ];
        try
        {
            foreach (var rel in order)
            {
                string p = Path.Combine(master, rel);
                string? chosen = Chosen(p);
                if (chosen != null) g.AddArchive(chosen);
                g._opened.Add((p, chosen, Stamp(chosen)));
            }
        }
        catch { g.Dispose(); throw; } // one damaged archive must not keep the others open
        // An unpacked dump (GzsTool: 00_dat/, chunk0_dat/, texture0_dat/, EquipIcon/) is accepted too, so the tool can
        // be pointed at a folder of extracted game files instead of an installed game. Without this, every lookup
        // missed and every mod came back as "No route known for these files" - which is what the owner saw when the
        // game files were moved to "Original Game Data".
        if (g._sources.Count == 0) g.AddDump(gameFolder);
        g.IsComplete = g._sources.Count >= 10;
        return g;
    }

    // The archives OpenGameFolder chose: the live path, the file read (the live one or its *.original, null when neither
    // exists) and that file's size and write time when it was opened.
    private readonly List<(string live, string? chosen, (long, DateTime) stamp)> _opened = [];

    private static string? Chosen(string p) => File.Exists(p + ".original") ? p + ".original" : File.Exists(p) ? p : null;

    private static (long, DateTime) Stamp(string? file)
    {
        if (file == null) return default;
        var fi = new FileInfo(file);
        return (fi.Length, fi.LastWriteTimeUtc);
    }

    /// <summary>
    /// True when a mod manager (SnakeBite, DMM) or a Steam verify has changed the game's archives since
    /// <see cref="OpenGameFolder"/>: a retail copy (*.original) made or removed, or an archive written. The file tables
    /// read at open then may not fit the files, so the caller opens the game again. Always false for other sources.
    /// </summary>
    public bool Changed()
    {
        try
        {
            foreach (var (live, chosen, stamp) in _opened)
                if (Chosen(live) != chosen || Stamp(chosen) != stamp) return true;
            return false;
        }
        catch (IOException) { return true; } // a file in the middle of a move
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Adds an unpacked game dump: every "&lt;name&gt;_dat" folder is a game folder's contents, and an "EquipIcon"
    /// folder is the icon dump. Used when the folder is not an installed game.
    /// </summary>
    public void AddDump(string root)
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(dir);
            if (name.EndsWith("_dat", StringComparison.OrdinalIgnoreCase)) AddFolder(dir);
            else if (name.Equals("EquipIcon", StringComparison.OrdinalIgnoreCase)) AddFolder(dir, "/Assets/tpp/ui/texture/EquipIcon");
        }
    }

    public void AddArchive(string path) => _sources.Add((path, QarArchive.Open(path), null));

    /// <summary>An unpacked folder whose layout matches game paths (Assets/tpp/...).</summary>
    public void AddFolder(string path) => _sources.Add((path, null, path));

    /// <summary>
    /// An unpacked folder holding one game folder's contents, e.g. an EquipIcon dump for
    /// /Assets/tpp/ui/texture/EquipIcon. Handy for tests and partial dumps.
    /// </summary>
    public void AddFolder(string path, string gameFolder)
    {
        string prefix = gameFolder.TrimEnd('/') + "/";
        _sources.Add((path, null, null));
        _mapped.Add((prefix, path));
    }

    private readonly List<(string prefix, string folder)> _mapped = [];

    private string? MappedFile(string gamePath)
    {
        foreach (var (prefix, folder) in _mapped)
            if (gamePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                string f = Path.Combine(folder, gamePath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(f)) return f;
            }
        return null;
    }

    /// <summary>
    /// A texture by its .ftex path: the header and every .N.ftexs found for it in the sources
    /// (large mips are loose files, usually in a different archive from the header).
    /// </summary>
    public Formats.RgbaImage? TryReadTexture(string ftexPath)
    {
        var header = TryRead(ftexPath);
        if (header == null) return null;
        var files = new Dictionary<int, byte[]>();
        for (int k = 1; k <= 6; k++)
        {
            var part = TryRead(ftexPath[..^5] + $".{k}.ftexs");
            if (part != null) files[k] = part;
        }
        return Formats.FtexTexture.Read(header).DecodeLargestAvailable(files);
    }

    /// <summary>The file from the first source that has it, or null.</summary>
    public byte[]? TryRead(string gamePath) => TryRead(gamePath, out _);

    /// <summary>When the SVFM_TRACE_READS environment variable names a file, every game path asked for is appended to it
    /// (used to find the vanilla files the tests need).</summary>
    private static readonly string? TraceFile = Environment.GetEnvironmentVariable("SVFM_TRACE_READS");
    private static readonly object TraceLock = new();
    private static void Trace(string gamePath)
    {
        if (TraceFile == null) return;
        lock (TraceLock) File.AppendAllText(TraceFile, gamePath + "\n");
    }

    public byte[]? TryRead(string gamePath, out string? source)
    {
        Trace(gamePath);
        string? mapped = MappedFile(gamePath);
        if (mapped != null) { source = mapped; return File.ReadAllBytes(mapped); }
        foreach (var (name, qar, folder) in _sources)
        {
            if (qar != null && qar.TryGetEntry(gamePath, out var e)) { source = name; return qar.Read(e); }
            if (folder != null)
            {
                string f = Path.Combine(folder, gamePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(f)) { source = name; return File.ReadAllBytes(f); }
            }
        }
        if (StoredCode(gamePath) is { } code)
            foreach (var (name, qar, _) in _sources)
                if (qar != null && qar.TryGetEntry(code, out var e)) { source = name; return qar.Read(e); }
        source = null;
        return null;
    }

    /// <summary>
    /// The vanilla soldier face packages (<c>cm_head/face/</c>) that chunk0.dat stores under a name code that is not the
    /// hash of their path, so a lookup by path misses them in an installed game. An unpacked dump has the files under these
    /// names, which is why the preview drew the face from the dump but only the body's eyes from the owner's game (issue
    /// #10). Each value is the stored code of the package's .fpk; its .fpkd and .pftxs share the low 50 bits. Found by
    /// matching the dump's files byte for byte to chunk0.dat entries (Nov 4).
    /// </summary>
    private static readonly Dictionary<string, ulong> StoredFaceCodes = new(StringComparer.Ordinal)
    {
        ["/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_m0_h0_v000_eye0"] = 0x5228f1228cc1b015,
        ["/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_f0_h0_v000_eye0"] = 0x52292e655364ff14,
        ["/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_dds5_head_z_eye0"] = 0x522a41dfc81734bf,
        ["/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_dds6_head_z_eye0"] = 0x5228b9136f01a09d,
        ["/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_dds3_eqhd1_eye0"] = 0x52289061f94e0534,
        ["/Assets/tpp/pack/fova/common_source/chara/cm_head/face/cm_dds8_eqhd1_eye0"] = 0x52289534d8eafea0,
    };

    /// <summary>The code chunk0.dat stores this soldier face file under, or null for any other path.</summary>
    private static ulong? StoredCode(string gamePath)
    {
        int dot = gamePath.IndexOf('.');
        if (dot < 0 || !StoredFaceCodes.TryGetValue(gamePath[..dot], out var fpk)) return null;
        return (Hashing.FoxHash.PathCode64(gamePath) & ~Hashing.FoxHash.PathMask) | (fpk & Hashing.FoxHash.PathMask);
    }

    public byte[] Read(string gamePath) =>
        TryRead(gamePath) ?? throw new FileNotFoundException($"{gamePath} is not in any of the game sources.");

    public bool Contains(string gamePath) => TraceThen(gamePath) && (MappedFile(gamePath) != null || _sources.Any(s =>
        s.qar != null ? s.qar.Contains(gamePath)
        : s.folder != null && File.Exists(Path.Combine(s.folder, gamePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)))));

    /// <summary>
    /// True when an archive source holds the file with this path code (QAR entries are keyed by it, so unnamed files are
    /// found too). Unpacked folders can only be searched by name: the dictionary names the code, or the answer is false.
    /// </summary>
    public bool ContainsCode(ulong pathCode, PathDictionary? dict = null)
    {
        if (_sources.Any(s => s.qar != null && s.qar.TryGetEntry(pathCode, out _))) return true;
        return (dict ?? PathDictionary.Bundled).TryGetPath(pathCode) is string p && Contains(p);
    }

    /// <summary>
    /// The file with this path code from the first archive source that has it (unnamed files included), or null.
    /// Unpacked folders (tests) are searched by the name the dictionary gives the code.
    /// </summary>
    public byte[]? TryReadCode(ulong pathCode, PathDictionary? dict = null)
    {
        foreach (var (_, qar, _) in _sources)
            if (qar != null && qar.TryGetEntry(pathCode, out var e)) return qar.Read(e);
        return (dict ?? PathDictionary.Bundled).TryGetPath(pathCode) is string p ? TryRead(p) : null;
    }

    private static bool TraceThen(string gamePath) { Trace(gamePath); return true; }

    public void Dispose()
    {
        foreach (var s in _sources) s.qar?.Dispose();
    }
}
