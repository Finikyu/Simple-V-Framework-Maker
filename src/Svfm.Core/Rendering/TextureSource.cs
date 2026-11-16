using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;

namespace Svfm.Core.Rendering;

/// <summary>
/// Finds textures for the renderer by path code. Order: the mod's own texture packs and loose files,
/// then the game's loose textures (named through the path dictionary), then the game texture packs
/// that were added. Shared flat textures (cm_flat_*) are built in, since they are single colours.
/// </summary>
public sealed class TextureSource
{
    private readonly GameData? _game;
    private readonly PathDictionary? _dict;
    private readonly Dictionary<ulong, PackedTexture> _modPacks = new();
    private readonly Dictionary<ulong, PackedTexture> _gamePacks = new();
    private readonly Dictionary<string, byte[]> _modLoose = new(StringComparer.Ordinal);
    /// <summary>Path code → .ftex path of the mod's loose textures (new paths are not in the dictionary).</summary>
    private readonly Dictionary<ulong, string> _modLooseByCode = new();
    private readonly Dictionary<ulong, RgbaImage?> _cache = new();

    public TextureSource(GameData? game, PathDictionary? dict)
    {
        _game = game;
        _dict = dict ?? PathDictionary.Bundled;
    }

    /// <summary>A texture pack of the mod (its textures win over the game's).</summary>
    public void AddModPack(TexturePack pack)
    {
        foreach (var t in pack.Textures) _modPacks[t.PathCode] = t;
    }

    /// <summary>A vanilla texture pack (used when the loose game texture isn't available).</summary>
    public void AddGamePack(TexturePack pack)
    {
        foreach (var t in pack.Textures) _gamePacks.TryAdd(t.PathCode, t);
    }

    /// <summary>A loose texture file of the mod (.ftex or .N.ftexs) by game path.</summary>
    public void AddModFile(string gamePath, byte[] data)
    {
        _modLoose[gamePath] = data;
        if (gamePath.EndsWith(".ftex", StringComparison.Ordinal)) _modLooseByCode[FoxHash.PathCode64(gamePath)] = gamePath;
    }

    private readonly HashSet<ulong> _missing = new();

    /// <summary>
    /// Textures asked for that nobody has (not the mod, not the game). The picture draws them blank, but in game such a
    /// model flickers between other textures, so callers report these (see Project.TextureCheck).
    /// </summary>
    public IReadOnlyCollection<ulong> Missing => _missing;

    public RgbaImage? Get(ulong pathCode)
    {
        if (_cache.TryGetValue(pathCode, out var hit)) return hit;
        RgbaImage? img;
        // A damaged texture (or a packed one with no .ftex part) draws blank like a missing one; before, it stopped the whole port.
        try { img = Find(pathCode); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { img = null; }
        if (img == null) _missing.Add(pathCode);
        return _cache[pathCode] = img;
    }

    private RgbaImage? Find(ulong code)
    {
        string? path = _modLooseByCode.TryGetValue(code, out var own) ? own : _dict?.TryGetPath(code);
        if (path != null && Flat(path) is { } flat) return flat;

        if (_modPacks.TryGetValue(code, out var mp)) return Decode(mp, path);
        if (path != null && _modLoose.TryGetValue(path, out var header))
        {
            var files = new Dictionary<int, byte[]>();
            for (int k = 1; k <= 6; k++)
                if (_modLoose.TryGetValue(path[..^5] + $".{k}.ftexs", out var part)) files[k] = part;
            return FtexTexture.Read(header).DecodeLargestAvailable(files);
        }
        if (path != null && _game?.TryReadTexture(path) is { } loose) return loose;
        if (_gamePacks.TryGetValue(code, out var gp)) return Decode(gp, path);
        // An unnamed game file (no dictionary name): the header and its small mips, found by path code.
        if (path == null && _game?.TryReadCode(code) is { } unnamed) return FtexTexture.Read(unnamed).DecodeLargestAvailable(new Dictionary<int, byte[]>());
        return null;
    }

    /// <summary>A packed texture, with any larger loose mips the game has under the same path.</summary>
    private RgbaImage Decode(PackedTexture t, string? path)
    {
        var files = t.FtexsFiles();
        if (path != null && _game != null)
            for (int k = 1; k <= 6; k++)
                if (!files.ContainsKey(k) && _game.TryRead(path[..^5] + $".{k}.ftexs") is { } part) files[k] = part;
        return t.Header.DecodeLargestAvailable(files);
    }

    /// <summary>The shared single-colour textures. Normal maps use the DXT5nm layout (X in alpha, Y in green).</summary>
    public static RgbaImage? Flat(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("cm_flat_", StringComparison.Ordinal)) return null;
        byte[] px = name switch
        {
            "cm_flat_black" => [0, 0, 0, 255],
            "cm_flat_white" => [255, 255, 255, 255],
            "cm_flat_nrm" => [128, 128, 128, 128],
            "cm_flat_lambert_srm" => [0, 0, 0, 255],
            _ when name.StartsWith("cm_flat_gry", StringComparison.Ordinal) && name.Length >= 14
                   && byte.TryParse(name.AsSpan(11, 3), out var g) => [g, g, g, 255],
            _ => [128, 128, 128, 255],
        };
        return new RgbaImage(1, 1, px);
    }

    /// <summary>Path code of a texture path, for callers that build swap tables.</summary>
    public static ulong Code(string ftexPath) => FoxHash.PathCode64(ftexPath);
}
