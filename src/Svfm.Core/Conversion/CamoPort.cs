using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;

namespace Svfm.Core.Conversion;

/// <summary>A texture moved from a vanilla path to the port's own path.</summary>
public sealed record TextureMove(string VanillaPath, string NewPath);

/// <summary>Files of one ported camo look, plus the two paths V Framework needs.</summary>
public sealed class CamoPortResult
{
    /// <summary>Archive files (game path → bytes): package, .fpkd, texture pack, renamed loose texture parts.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    /// <summary>For V Framework <c>camoFpk</c>.</summary>
    public required string CamoFpk { get; init; }
    /// <summary>For V Framework <c>camoFv2</c>.</summary>
    public required string CamoFv2 { get; init; }
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Turns a camo replacer (a mod that overwrites a vanilla camo texture) into a separate camo that
/// leaves the vanilla one alone. Recipe from the research (vanilla c12/c13 against True Black Ocelot):
/// <list type="number">
/// <item>The camo .fpk holds one .fv2 whose file table points at the camo texture. Copy it as installed (the mod's
/// .fv2 when it changes it, e.g. to point at its own textures at new paths) under a new name, and point it at the
/// moved texture's new path. Textures it names at new paths are copied as they are.</item>
/// <item>The vanilla .fpkd holds a .fox2 that loads the texture pack named like the package; it
/// is copied unchanged.</item>
/// <item>The mod's texture pack is renamed with the package; textures at vanilla paths in it get
/// new paths (every part code rewritten).</item>
/// <item>Loose texture parts the mod ships at a vanilla path are renamed the same way.</item>
/// </list>
/// </summary>
public static class CamoPort
{
    /// <param name="game">Vanilla game files.</param>
    /// <param name="mod">The opened replacer mod.</param>
    /// <param name="vanillaPackage">Vanilla camo package without extension, e.g. /Assets/tpp/pack/player/fova/plfova_sna0_main1_c13.</param>
    /// <param name="newPackage">The port's package without extension, e.g. /Assets/tpp/pack/player/fova/plfova_svfm_tbo_main1.</param>
    /// <param name="moves">Vanilla textures the mod replaces and the paths they move to.</param>
    public static CamoPortResult Port(GameData game, Mods.MgsvMod mod, string vanillaPackage, string newPackage, IReadOnlyList<TextureMove> moves)
    {
        // The camo package as installed: the mod's files over vanilla (a mod may change the .fv2 itself, pointing it at
        // textures at new paths, as Darkened Tiger does).
        var fpk = BodyConverter.Merge(game, mod, vanillaPackage + ".fpk", isFpkd: false, []);
        byte[] fpkd = mod.ArchiveFiles.TryGetValue(vanillaPackage + ".fpkd", out var modFpkd) ? modFpkd : game.Read(vanillaPackage + ".fpkd");

        var fv2Entry = fpk.Files.SingleOrDefault(f => f.Extension == "fv2")
            ?? throw new InvalidDataException($"{vanillaPackage}.fpk has no single .fv2; not a camo package.");
        var vanillaFv2 = FoxPackage.Read(game.Read(vanillaPackage + ".fpk")).Files.SingleOrDefault(f => f.Extension == "fv2");
        bool modFv2 = vanillaFv2 == null || !vanillaFv2.Data.AsSpan().SequenceEqual(fv2Entry.Data);
        var fv2 = new Fv2File(fv2Entry.Data);
        int replaced = 0;
        foreach (var m in moves) replaced += fv2.Replace(m.VanillaPath, m.NewPath);

        string newName = newPackage[(newPackage.LastIndexOf('/') + 1)..].Replace("plfova_", "");
        string fv2Path = fv2Entry.Path[..(fv2Entry.Path.LastIndexOf('/') + 1)] + newName + ".fv2";
        var newFpk = new FoxPackage { IsFpkd = false };
        newFpk.Files.Add(new PackageFile(fv2Path, fv2.Write()));
        newFpk.References.AddRange(fpk.References);

        var result = new CamoPortResult { CamoFpk = newPackage + ".fpk", CamoFv2 = fv2Path };
        if (replaced == 0 && !modFv2) result.Notes.Add($"{fv2Entry.Path} does not use any of the replaced textures; the camo will look vanilla.");
        result.Files[newPackage + ".fpk"] = newFpk.Write();
        result.Files[newPackage + ".fpkd"] = fpkd;

        // Texture pack: the mod's, else vanilla (then only loose parts carry the change).
        string packPath = vanillaPackage + ".pftxs";
        byte[]? packBytes = mod.ArchiveFiles.TryGetValue(packPath, out var mp) ? mp : game.TryRead(packPath);
        if (packBytes == null) throw new FileNotFoundException($"No texture pack for {vanillaPackage}.");
        var pack = TexturePack.Read(packBytes);
        foreach (var m in moves)
        {
            var t = pack.Find(m.VanillaPath);
            if (t != null) t.Rename(m.NewPath);
        }
        result.Files[newPackage + ".pftxs"] = pack.Write();

        // Loose parts of the replaced textures (e.g. the large .2/.3.ftexs mips).
        foreach (var m in moves)
        {
            string oldStem = m.VanillaPath[..^".ftex".Length], newStem = m.NewPath[..^".ftex".Length];
            foreach (var (path, data) in mod.ArchiveFiles)
                if (path.StartsWith(oldStem + ".", StringComparison.Ordinal))
                    result.Files[newStem + path[oldStem.Length..]] = data;
        }

        // Textures the .fv2 names at new paths (not in the game): the mod's files, copied as they are.
        var named = fv2.Textures.ToHashSet();
        int copied = 0;
        foreach (var (path, data) in mod.ArchiveFiles)
            if (path.Contains(".ftex", StringComparison.Ordinal) && mod.Ships(path) && !result.Files.ContainsKey(path)
                && named.Contains(FoxHash.PathCode64(Project.TextureCheck.FtexOf(path))) && !game.Contains(Project.TextureCheck.FtexOf(path)))
            { result.Files[path] = data; copied++; }
        if (copied > 0) result.Notes.Add($"{copied} texture file(s) the mod's {fv2Entry.Path[(fv2Entry.Path.LastIndexOf('/') + 1)..]} names at new paths, copied as they are.");
        return result;
    }

    /// <summary>The vanilla textures (path codes) a camo package's .fv2 points at.</summary>
    public static IReadOnlyList<ulong> TexturesOf(GameData game, string vanillaPackage)
    {
        var fpk = FoxPackage.Read(game.Read(vanillaPackage + ".fpk"));
        return fpk.Files.Where(f => f.Extension == "fv2").SelectMany(f => new Fv2File(f.Data).Textures).ToList();
    }

    /// <summary>True when the mod's pack holds a texture that the vanilla camo uses, i.e. the mod replaces this camo.</summary>
    /// <remarks>
    /// Also true when the mod changes the camo's .fv2 (pointing it at its own textures) or ships loose files of a texture
    /// the vanilla camo uses.
    /// </remarks>
    public static bool ModReplaces(GameData game, Mods.MgsvMod mod, string vanillaPackage)
    {
        if (!game.Contains(vanillaPackage + ".fpk")) return false;
        var used = TexturesOf(game, vanillaPackage).ToHashSet();
        if (mod.ArchiveFiles.TryGetValue(vanillaPackage + ".pftxs", out var pack) && TexturePack.Read(pack).Textures.Any(t => used.Contains(t.PathCode)))
            return true;
        if (mod.ArchiveFiles.TryGetValue(vanillaPackage + ".fpk", out var modFpk))
        {
            var mine = FoxPackage.Read(modFpk).Files.Where(f => f.Extension == "fv2").ToList();
            var vanilla = FoxPackage.Read(game.Read(vanillaPackage + ".fpk")).Files.Where(f => f.Extension == "fv2").ToDictionary(f => f.Path, f => f.Data);
            if (mine.Any(f => !vanilla.TryGetValue(f.Path, out var v) || !v.AsSpan().SequenceEqual(f.Data))) return true;
        }
        return mod.ArchiveFiles.Keys.Any(p => p.Contains(".ftex", StringComparison.Ordinal) && mod.Ships(p) && used.Contains(FoxHash.PathCode64(Project.TextureCheck.FtexOf(p))));
    }

    internal static ulong Code(string path) => FoxHash.PathCode64(path);
}
