using System.IO.Compression;
using Svfm.Core.Hashing;

namespace Svfm.Core.Archives;

/// <summary>
/// Turns path codes back into game paths. The tool ships the community QAR dictionary (<see cref="Bundled"/>);
/// more GzsTool-style dictionaries (one path per line, without extension) can be loaded, and full paths seen
/// elsewhere (mod metadata, Lua, fmdl strings) added.
/// </summary>
public sealed class PathDictionary
{
    private readonly Dictionary<ulong, string> _byPathHash = new();

    public int Count => _byPathHash.Count;

    private static readonly Lazy<PathDictionary> _bundled = new(LoadBundled);

    /// <summary>
    /// The QAR dictionary shipped with the tool: kapuragu/mgsv-lookup-strings GzsTool/qar_dictionary.txt, unchanged
    /// (commit 80fe7d3, July 2026, validated with HashWrangler; 62,102 paths).
    /// Shared and read-only in spirit: add paths to a <see cref="Copy"/>.
    /// </summary>
    public static PathDictionary Bundled => _bundled.Value;

    /// <summary>A separate dictionary with the same paths, for adding more.</summary>
    public PathDictionary Copy()
    {
        var d = new PathDictionary();
        foreach (var (k, v) in _byPathHash) d._byPathHash[k] = v;
        return d;
    }

    private static PathDictionary LoadBundled()
    {
        var d = new PathDictionary();
        using var s = typeof(PathDictionary).Assembly.GetManifestResourceStream("Svfm.Archives.qar_dictionary.txt.gz")
            ?? throw new InvalidOperationException("The bundled QAR dictionary is missing from the build.");
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        while (r.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#')) d.Add(line);
        }
        return d;
    }

    public static PathDictionary FromFiles(params string[] files)
    {
        var d = new PathDictionary();
        foreach (var f in files) d.LoadFile(f);
        return d;
    }

    public void LoadFile(string file)
    {
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            Add(line);
        }
    }

    /// <summary>Adds a path, with or without extension. The first path seen for a hash wins.</summary>
    public void Add(string path)
    {
        path = path.Replace('\\', '/');
        int dot = path.IndexOf('.');
        string name = dot < 0 ? path : path[..dot];
        _byPathHash.TryAdd(FoxHash.HashName(name) & FoxHash.PathMask, name);
    }

    /// <summary>Full path with extension, or null when the name or the extension is unknown.</summary>
    /// <summary>Every known path starting with <paramref name="prefix"/> (ordinal), sorted.</summary>
    public IEnumerable<string> PathsUnder(string prefix) =>
        _byPathHash.Values.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Distinct().OrderBy(p => p, StringComparer.Ordinal);

    public string? TryGetPath(ulong pathCode)
    {
        if (!_byPathHash.TryGetValue(pathCode & FoxHash.PathMask, out var name)) return null;
        var ext = FoxHash.ExtensionOf(pathCode);
        return ext == null ? null : name + "." + ext;
    }

    /// <summary>
    /// The name GzsTool 0.5.3 gives the file when unpacking: the game path, or the hex path hash
    /// plus extension (or "_unknown") when the dictionary has no name.
    /// </summary>
    public string DisplayName(ulong pathCode)
    {
        _byPathHash.TryGetValue(pathCode & FoxHash.PathMask, out var name);
        name ??= (pathCode & FoxHash.PathMask).ToString("x");
        var ext = FoxHash.ExtensionOf(pathCode);
        return ext == null ? name + "_unknown" : name + "." + ext;
    }
}
