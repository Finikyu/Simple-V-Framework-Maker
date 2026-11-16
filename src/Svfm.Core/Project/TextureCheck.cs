using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;

namespace Svfm.Core.Project;

/// <summary>A texture a model uses that nothing provides: not in the build, not in the game.</summary>
/// <summary>A texture a model uses that nobody provides. <paramref name="Base"/>: it is a base-colour map (Base_Tex_*).</summary>
public sealed record MissingTexture(ulong PathCode, string Model, bool Base = true);

/// <summary>
/// Finds textures the models of a build use but nobody provides. In game a missing texture is not a blank: the slot
/// shows whatever texture last sat in that memory, so the model flickers between other textures (Snake's face, other
/// suits). Seen Oct 25 with Tactical Outfit: its models use textures from a separate "Main Textures" mod that wasn't
/// installed. A texture counts as provided when the build carries it (a loose .ftex or inside a .pftxs), the game has it
/// (any archive, looked up by path code so unnamed files count), or, when only part of the game is given (tests), the
/// community dictionary names it under /Assets/tpp/.
/// </summary>
public static class TextureCheck
{
    /// <summary>Path codes of the textures carried by these files: loose .ftex files and the contents of .pftxs packs.</summary>
    public static HashSet<ulong> Provided(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        var set = new HashSet<ulong>();
        foreach (var (path, data) in files)
        {
            if (path.EndsWith(".ftex", StringComparison.Ordinal)) set.Add(FoxHash.PathCode64(path));
            else if (path.EndsWith(".pftxs", StringComparison.Ordinal))
            {
                TexturePack pack;
                try { pack = TexturePack.Read(data); } catch (InvalidDataException) { continue; } // a damaged pack provides nothing; MgsvBuilder.Check reports it
                foreach (var t in pack.Textures) set.Add(t.PathCode);
            }
        }
        return set;
    }

    /// <summary>The models (.fmdl) inside the packages (.fpk) among these files: (package/model path, model).</summary>
    public static IEnumerable<(string where, FmdlModel model)> Models(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        foreach (var (path, data) in files)
        {
            if (path.EndsWith(".fmdl", StringComparison.Ordinal)) { yield return (path, FmdlModel.Read(data)); continue; }
            if (!path.EndsWith(".fpk", StringComparison.Ordinal)) continue;
            foreach (var f in FoxPackage.Read(data).Files.Where(f => f.Extension == "fmdl"))
                yield return ($"{path} → {f.Path}", FmdlModel.Read(f.Data));
        }
    }

    /// <summary>
    /// Every texture code the mod's own models and looks can use: materials of every model in its packages (hidden mesh
    /// groups included: a look may show them) and the texture swaps of its .fv2 files.
    /// </summary>
    public static HashSet<ulong> Referenced(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        var set = new HashSet<ulong>();
        foreach (var (path, data) in files)
        {
            if (!path.EndsWith(".fpk", StringComparison.Ordinal) && !path.EndsWith(".fpkd", StringComparison.Ordinal)) continue;
            FoxPackage pk;
            try { pk = FoxPackage.Read(data); } catch (InvalidDataException) { continue; }
            foreach (var f in pk.Files)
            {
                if (f.Extension == "fmdl") foreach (var t in FmdlModel.Read(f.Data).Materials.SelectMany(m => m.Textures)) set.Add(t.PathCode);
                else if (f.Extension == "fv2") foreach (var sw in new Fv2File(f.Data).TextureSwaps()) set.Add(sw.Texture);
            }
        }
        return set;
    }

    /// <summary>The .ftex a texture file belongs to: x.ftex for x.ftex and x.N.ftexs.</summary>
    public static string FtexOf(string path) =>
        System.Text.RegularExpressions.Regex.Match(path, @"^(.*)\.\d+\.ftexs$") is { Success: true } m ? m.Groups[1].Value + ".ftex" : path;

    /// <summary>
    /// The texture codes of the materials the model draws: materials of meshes in hidden mesh groups are skipped (a head
    /// add-on keeps the whole body model and shows only its gear; the hidden body's textures never load).
    /// </summary>
    public static IEnumerable<ulong> Uses(FmdlModel m) => m.Meshes
        .Where(x => !(x.MeshGroup >= 0 && x.MeshGroup < m.MeshGroups.Count && m.MeshGroups[x.MeshGroup].hidden))
        .Select(x => x.MaterialIndex).Distinct()
        .SelectMany(i => m.Materials[i].Textures).Select(t => t.PathCode).Distinct();

    // shortcut: by hash only fox3DFW_GlassColor_CommonRef_LNM is known (HK33SG); a model with shader names is tested by the name. Add a hash when a model with hashes needs it.
    private static bool IsGlass(FmdlMaterial mt) =>
        mt.Shader?.StartsWith("fox3DFW_Glass", StringComparison.Ordinal) == true || mt.ShaderHash == Hashing.FoxHash.StrCode64("fox3DFW_GlassColor_CommonRef_LNM");

    private static readonly HashSet<ulong> BaseRoles = [Hashing.FoxHash.StrCode64("Base_Tex_SRGB"), Hashing.FoxHash.StrCode64("Base_Tex_LIN")];

    /// <summary>
    /// The base-colour textures (Base_Tex_SRGB/LIN slots) of the materials the model draws. A missing base map shows
    /// another texture (flicker: Tactical Outfit without its Main Textures); a missing normal/specular/detail map only
    /// changes the shading (Cuba's MSF outfit points at 7 Metal Gear Survive maps TPP lacks and looks right in game).
    /// A glass material does not count (the owner, Nov 11): the scope lens of HK33SG (Zeta) names a base map that is not
    /// in the mod, on its Nexus page or in any archive of the game, and the mod works as it is.
    /// </summary>
    // INFERENCE: a glass shader draws from its colour parameters and the reflection, so a missing base map does not show. Not tested in game.
    public static HashSet<ulong> BaseUses(FmdlModel m) => m.Meshes
        .Where(x => !(x.MeshGroup >= 0 && x.MeshGroup < m.MeshGroups.Count && m.MeshGroups[x.MeshGroup].hidden))
        .Select(x => x.MaterialIndex).Distinct()
        .Where(i => !IsGlass(m.Materials[i]))
        .SelectMany(i => m.Materials[i].Textures).Where(t => BaseRoles.Contains(t.RoleHash)).Select(t => t.PathCode).ToHashSet();

    /// <summary>
    /// The textures the models in <paramref name="files"/> use that neither the files, the game nor the dictionary have:
    /// one entry per (texture, model).
    /// </summary>
    public static List<MissingTexture> Find(IReadOnlyCollection<KeyValuePair<string, byte[]>> files, GameData? game, PathDictionary? dict = null,
        IReadOnlySet<ulong>? alsoProvided = null)
    {
        dict ??= PathDictionary.Bundled;
        var provided = Provided(files);
        var missing = new List<MissingTexture>();
        var known = new Dictionary<ulong, bool>();
        foreach (var (where, model) in Models(files))
        {
            var baseCodes = BaseUses(model);
            foreach (var code in Uses(model))
            {
                if (!known.TryGetValue(code, out bool ok))
                    known[code] = ok = provided.Contains(code) || alsoProvided?.Contains(code) == true
                        || game?.ContainsCode(code, dict) == true || VanillaByName(code, game, dict);
                if (!ok) missing.Add(new MissingTexture(code, where, baseCodes.Contains(code)));
            }
        }
        return missing;
    }

    /// <summary>
    /// Stand-in for the game when only part of it is given: the dictionary names the texture under /Assets/tpp/. The
    /// community dictionary also names files of other Fox Engine games (P.T.'s /Assets/sh/…: P.T. Decoy's first version
    /// points its model at them), which TPP doesn't have, so those don't count.
    /// </summary>
    private static bool VanillaByName(ulong code, GameData? game, PathDictionary dict) =>
        game?.IsComplete != true && dict.TryGetPath(code) is string p && p.StartsWith("/Assets/tpp/", StringComparison.Ordinal);

    /// <summary>One line for the user: how many textures are missing, which models use them, and the usual cause.</summary>
    public static string Describe(IReadOnlyList<MissingTexture> missing, bool shadingOnly = false)
    {
        var models = missing.Select(m => m.Model).Distinct().ToList();
        var codes = missing.Select(m => m.PathCode).Distinct().ToList();
        // Models ported from other Fox Engine games sometimes still point at that game's textures (the community
        // dictionary names them): Metal Gear Survive /Assets/ssd/, P.T. /Assets/sh/, MGO /Assets/mgo/ ones TPP lacks.
        var foreign = codes.Select(c => PathDictionary.Bundled.TryGetPath(c)).OfType<string>()
            .Where(p => !p.StartsWith("/Assets/tpp/", StringComparison.Ordinal)).ToList();
        string Game(string p) => p.StartsWith("/Assets/ssd/", StringComparison.Ordinal) ? "Metal Gear Survive"
            : p.StartsWith("/Assets/sh/", StringComparison.Ordinal) ? "P.T." : p.StartsWith("/Assets/mgo/", StringComparison.Ordinal) ? "MGO" : "another game";
        if (foreign.Count > 0)
            return $"{codes.Count} texture{(codes.Count == 1 ? "" : "s")} used by {string.Join(", ", models)} are not in the mod or the game; " +
                $"{foreign.Count} of them are {string.Join("/", foreign.Select(Game).Distinct())} paths ({string.Join(", ", foreign.Take(3).Select(p => p[(p.LastIndexOf('/') + 1)..]))}" +
                $"{(foreign.Count > 3 ? ", …" : "")}): the model was ported from that game and still points at its textures, which TPP doesn't have. " +
                (shadingOnly ? "They are shading maps (normal, specular, detail …), not base colour: only the shading there differs, as in the original mod."
                             : "In game the model would flicker between other textures (the original mod too). The mod needs those textures shipped under TPP paths.");
        if (shadingOnly)
            return $"{codes.Count} shading map{(codes.Count == 1 ? "" : "s")} (normal, specular, detail …, or a map of a glass material; not base colour) used by {string.Join(", ", models)} " +
                   "are not in the mod or the game (" + string.Join(", ", codes.Take(4).Select(c => c.ToString("x16"))) + (codes.Count > 4 ? ", …" : "") +
                   "): only the shading there differs, as in the original mod.";
        return $"{codes.Count} texture{(codes.Count == 1 ? "" : "s")} used by {string.Join(", ", models)} " +
            "are not in the mod or the game (" + string.Join(", ", codes.Take(4).Select(c => c.ToString("x16"))) +
            (codes.Count > 4 ? ", …" : "") + "). In game the model would flicker between other textures. Such textures usually " +
            "come in a separate texture mod from the same author (often named \"Textures\" or \"Main Textures\"); add it to the conversion.";
    }
}
