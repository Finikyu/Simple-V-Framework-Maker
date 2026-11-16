using System.Text;

namespace Svfm.Core.Hashing;

/// <summary>
/// Fox Engine string and path hashes.
/// <list type="bullet">
/// <item><see cref="StrCode64"/> / <see cref="StrCode32"/>: names, language IDs (lng2 keys).</item>
/// <item><see cref="PathCode64"/>: the QAR hash of a full game path, as stored in .dat archives,
/// SnakeBite metadata, .fv2 texture tables and fmdl path-code blocks.</item>
/// </list>
/// Matches GzsTool 0.5.3 (Hashing.cs), the reference used by SnakeBite and DMM.
/// </summary>
public static class FoxHash
{
    private const ulong Seed0 = 0x9ae16a3b2f90404fUL;

    /// <summary>Set on paths outside /Assets/ (e.g. /Tpp/...) and on /Assets/tpptest*.</summary>
    public const ulong MetaFlag = 0x4000000000000UL;

    /// <summary>Low 50 bits of a path code: the hash of the path without extension.</summary>
    public const ulong PathMask = 0x3FFFFFFFFFFFFUL;

    /// <summary>Bit position of the 13-bit extension ID inside a path code.</summary>
    public const int ExtensionShift = 51;

    public static ulong StrCode64(string text)
    {
        if (text.Length == 0) return 0;
        byte[] b = Encoding.UTF8.GetBytes(text);
        byte[] z = new byte[b.Length + 1];
        b.CopyTo(z, 0);
        ulong seed1 = ((ulong)b[0] << 16) + (ulong)b.Length;
        return CityHash.Hash64WithSeeds(z, Seed0, seed1) & 0xFFFFFFFFFFFFUL;
    }

    public static uint StrCode32(string text) => (uint)(StrCode64(text) & 0xFFFFFFFFUL);

    /// <summary>Hash of a path or name without its extension (GzsTool HashFileName).</summary>
    public static ulong HashName(string text, bool removeExtension = true)
    {
        if (removeExtension)
        {
            int dot = text.IndexOf('.');
            if (dot >= 0) text = text[..dot];
        }

        bool meta = false;
        const string assets = "/Assets/";
        if (text.StartsWith(assets, StringComparison.Ordinal))
        {
            text = text[assets.Length..];
            if (text.StartsWith("tpptest", StringComparison.Ordinal)) meta = true;
        }
        else
        {
            meta = true;
        }
        text = text.TrimStart('/');

        byte[] bytes = Encoding.ASCII.GetBytes(text);
        ulong seed1 = 0;
        for (int i = bytes.Length - 1, j = 0; i >= 0 && j < 8; i--, j++)
            seed1 |= (ulong)bytes[i] << (8 * j);
        ulong h = CityHash.Hash64WithSeeds(bytes, Seed0, seed1) & PathMask;
        return meta ? h | MetaFlag : h;
    }

    /// <summary>13-bit extension ID of an extension written without the leading dot (e.g. "ftex", "1.ftexs").</summary>
    public static ulong ExtensionId(string extension) => HashName(extension, removeExtension: false) & 0x1FFF;

    /// <summary>QAR path code of a full game path such as <c>/Assets/tpp/pack/player/parts/plparts_normal.fpk</c>.</summary>
    public static ulong PathCode64(string path)
    {
        path = path.Replace('\\', '/');
        int dot = path.IndexOf('.');
        string name = dot < 0 ? path : path[..dot];
        string ext = dot < 0 ? "" : path[(dot + 1)..];
        ulong typeId = KnownExtensions.TryGetId(ext, out ulong id) ? id : 0;
        return (typeId << ExtensionShift) | (HashedName(name) ?? HashName(name));
    }

    /// <summary>
    /// The name hash of an unnamed file as SnakeBite/MakeBite store it: the hex hash at the archive root
    /// (<c>/1162e2d88aa33.ftex</c> = name hash 0x1162e2d88aa33), or null for an ordinary path.
    /// </summary>
    public static ulong? HashedName(string nameWithoutExtension)
    {
        var n = nameWithoutExtension.TrimStart('/');
        if (n.Length is < 10 or > 13 || n.Contains('/') || !n.All(Uri.IsHexDigit)) return null;
        ulong v = Convert.ToUInt64(n, 16);
        return v <= PathMask + MetaFlag ? v : null;
    }

    /// <summary>Extension of a path code, or null if the ID is not a known extension.</summary>
    public static string? ExtensionOf(ulong pathCode) => KnownExtensions.TryGetName(pathCode >> ExtensionShift, out var e) ? e : null;
}

/// <summary>The file extensions GzsTool 0.5.3 knows. Only these get an extension ID in a path code.</summary>
public static class KnownExtensions
{
    public static readonly IReadOnlyList<string> All =
    [
        "1.ftexs", "1.nav2", "2.ftexs", "3.ftexs", "4.ftexs", "5.ftexs", "6.ftexs", "ag.evf", "aia", "aib", "aibc",
        "aig", "aigc", "aim", "aip", "ait", "atsh", "bnd", "bnk", "cc.evf", "clo", "csnav", "dat", "des", "dnav",
        "dnav2", "eng.lng", "ese", "evb", "evf", "fag", "fage", "fago", "fagp", "fagx", "fclo", "fcnp", "fcnpx",
        "fdes", "fdmg", "ffnt", "fmdl", "fmdlb", "fmtt", "fnt", "fova", "fox", "fox2", "fpk", "fpkd", "fpkl", "frdv",
        "fre.lng", "frig", "frt", "fsd", "fsm", "fsml", "fsop", "fstb", "ftex", "fv2", "fx.evf", "fxp", "gani",
        "geom", "ger.lng", "gpfp", "grxla", "grxoc", "gskl", "htre", "info", "ita.lng", "jpn.lng", "json", "lad",
        "ladb", "lani", "las", "lba", "lng", "lpsh", "lua", "mas", "mbl", "mog", "mtar", "mtl", "nav2", "nta", "obr",
        "obrb", "param", "parts", "path", "pftxs", "ph", "phep", "phsd", "por.lng", "qar", "rbs", "rdb", "rdf",
        "rnav", "rus.lng", "sad", "sand", "sani", "sbp", "sd.evf", "sdf", "sim", "simep", "snav", "spa.lng", "spch",
        "sub", "subp", "tgt", "tre2", "txt", "uia", "uif", "uig", "uigb", "uil", "uilb", "utxl", "veh", "vfx",
        "vfxbin", "vfxdb", "vnav", "vo.evf", "vpc", "wem", "wmv", "xml",
    ];

    private static readonly Dictionary<string, ulong> ByName;
    private static readonly Dictionary<ulong, string> ById;

    static KnownExtensions()
    {
        ByName = new Dictionary<string, ulong>(StringComparer.Ordinal);
        ById = new Dictionary<ulong, string>();
        foreach (var e in All)
        {
            ulong id = FoxHash.ExtensionId(e);
            ByName[e] = id;
            ById.TryAdd(id, e);
        }
    }

    public static bool TryGetId(string extension, out ulong id) => ByName.TryGetValue(extension, out id);
    public static bool TryGetName(ulong id, out string extension) => ById.TryGetValue(id, out extension!);
}
