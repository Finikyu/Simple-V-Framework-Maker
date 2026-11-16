using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Mods;
using Svfm.Core.Conversion;

namespace Svfm.Core.Project;

/// <summary>
/// Compares a port with the mod as installed, the way the game reads it, before anything is tested in game. For each
/// package pair (the vanilla package the mod replaces, and the port's own package): every file of the installed package
/// (vanilla merged with the mod's files, as SnakeBite/DMM install it) should be in the port at the same path with the same
/// bytes; and for every texture slot of every model, each piece the game could read in the port (header, every mip file;
/// from the port's own pack or from the archive: the port's loose files over the game's) must be a piece the installed
/// original could give too. A piece only the port could give is a mix of two versions (Cuba 0.1/0.3 crashed, its face
/// broke): an error. Files the port changes on purpose (a renamed package's .parts, a model pointed at texture copies)
/// are listed as changes.
/// </summary>
public static class PortCheck
{
    public sealed record Finding(bool Error, string Text);

    public static List<Finding> Compare(GameData game, MgsvMod mod, IReadOnlyDictionary<string, byte[]> portFiles,
        IReadOnlyList<(string vanillaPackage, string portPackage)> pairs)
    {
        var findings = new List<Finding>();
        foreach (var (van, port) in pairs)
        {
            string vanName = van[(van.LastIndexOf('/') + 1)..];
            if (!portFiles.TryGetValue(port + ".fpk", out var pf)) { findings.Add(new(true, $"{vanName}: the port has no package {port}.fpk.")); continue; }
            var noNotes = new List<string>();
            var orig = BodyConverter.Merge(game, mod, van + ".fpk", isFpkd: false, noNotes);
            var origD = BodyConverter.Merge(game, mod, van + ".fpkd", isFpkd: true, noNotes);
            var portFpk = FoxPackage.Read(pf);
            var portFpkd = portFiles.TryGetValue(port + ".fpkd", out var pd) ? FoxPackage.Read(pd) : new FoxPackage { IsFpkd = true };

            // Files: same paths, same bytes.
            var changed = new List<string>();
            foreach (var (o, p) in new[] { (orig, portFpk), (origD, portFpkd) })
            {
                foreach (var f in o.Files)
                {
                    var mine = p.Find(f.Path);
                    if (mine == null) changed.Add($"{Short(f.Path)} not at its path");
                    else if (!mine.Data.AsSpan().SequenceEqual(f.Data)) changed.Add($"{Short(f.Path)} changed");
                }
                foreach (var f in p.Files.Where(f => o.Find(f.Path) == null)) changed.Add($"{Short(f.Path)} added");
            }
            if (changed.Count > 0) findings.Add(new(false, $"{vanName}: the port differs from the installed mod in {changed.Count} file(s): {string.Join("; ", changed.Take(8))}{(changed.Count > 8 ? "; …" : "")}."));

            // Textures: every piece the port could give must be one the installed original could give.
            var origPack = mod.ArchiveFiles.TryGetValue(van + ".pftxs", out var mp) ? TexturePack.Read(mp)
                : game.TryRead(van + ".pftxs") is { } vp ? TexturePack.Read(vp) : null;
            var portPack = portFiles.TryGetValue(port + ".pftxs", out var pp) ? TexturePack.Read(pp) : null;
            var origArchive = new Dictionary<ulong, byte[]>();
            foreach (var (path, data) in mod.ArchiveFiles) if (mod.Ships(path)) origArchive[FoxHash.PathCode64(path)] = data;
            var portArchive = new Dictionary<ulong, byte[]>();
            foreach (var (path, data) in portFiles) portArchive[FoxHash.PathCode64(path)] = data;

            var mixed = new List<string>();
            var lost = new List<string>();
            var added = new List<string>();
            var inconsistent = new List<string>();
            var checkedCodes = new HashSet<(ulong, ulong)>();
            foreach (var pm in portFpk.Files.Where(f => f.Extension == "fmdl"))
            {
                // The installed model this one stands for: same path, else (a moved model) the installed one with the same meshes.
                var portModel = FmdlModel.Read(pm.Data);
                var om = orig.Find(pm.Path) ?? orig.Files.FirstOrDefault(f => f.Extension == "fmdl" && SameShape(FmdlModel.Read(f.Data), portModel));
                if (om == null) continue;
                var origModel = FmdlModel.Read(om.Data);
                for (int mi = 0; mi < Math.Min(origModel.Materials.Count, portModel.Materials.Count); mi++)
                {
                    var os = origModel.Materials[mi].Textures;
                    var ps = portModel.Materials[mi].Textures;
                    for (int ti = 0; ti < Math.Min(os.Count, ps.Count); ti++)
                    {
                        ulong oc = os[ti].PathCode, pc = ps[ti].PathCode;
                        if (!checkedCodes.Add((oc, pc))) continue;
                        var oPieces = Pieces(oc, origArchive, game, origPack);
                        var pPieces = Pieces(pc, portArchive, game, portPack);
                        string name = Name(oc);
                        // The installed mod itself pairs a header with mip files that don't fit it (Black Suits' KNIFE:
                        // the loose item2 mips are the STUN version, the KNIFE pack keeps the vanilla header): there is no
                        // consistent installed state to copy. A port that is consistent (one whole version) is the best
                        // it can be: a note, not an error.
                        if (!Consistent(oPieces) && Consistent(pPieces))
                        {
                            inconsistent.Add(name);
                            continue;
                        }
                        foreach (var (n, versions) in pPieces)
                        {
                            // A mix: the original gives this piece, and the port can give a different version of it. A piece
                            // only the port gives (a vanilla pack entry the mod's pack dropped, put back) is an addition.
                            if (!oPieces.TryGetValue(n, out var allowed)) { added.Add($"{name} {(n == 0 ? "header" : $"mip file {n}")}"); continue; }
                            if (versions.Any(v => !allowed.Any(a => SamePiece(a, v)))) mixed.Add($"{name} {(n == 0 ? "header" : $"mip file {n}")}");
                        }
                        foreach (var n in oPieces.Keys.Where(n => !pPieces.ContainsKey(n))) lost.Add($"{name} {(n == 0 ? "header" : $"mip file {n}")}");
                    }
                }
            }
            if (mixed.Count > 0)
                findings.Add(new(true, $"{vanName}: {mixed.Count} texture piece(s) the port could load are not what the installed mod gives ({string.Join(", ", mixed.Take(6))}{(mixed.Count > 6 ? ", …" : "")}): " +
                                       "the game could mix two versions of a texture (a crash or a broken texture). The port must carry the mod's version whole, or the vanilla one whole."));
            if (added.Count > 0)
                findings.Add(new(false, $"{vanName}: {added.Count} texture piece(s) only the port gives ({string.Join(", ", added.Take(6))}{(added.Count > 6 ? ", …" : "")}): vanilla pack entries the mod's pack left out, put back."));
            if (inconsistent.Count > 0)
                findings.Add(new(false, $"{vanName}: the installed mod pairs {string.Join(", ", inconsistent.Take(6))} with mip files that don't fit its header " +
                                        "(the loose mips are another texture's version): the port carries one whole version instead (vanilla, or the mod's copied whole)."));
            if (lost.Count > 0)
                findings.Add(new(false, $"{vanName}: {lost.Count} texture piece(s) of the installed mod are not in the port or the game given ({string.Join(", ", lost.Take(6))}{(lost.Count > 6 ? ", …" : "")})."));
        }
        return findings;
    }

    /// <summary>Every version of each piece (0 = header, N = .N.ftexs) the game could read for a texture: pack entry and archive.</summary>
    private static Dictionary<int, List<byte[]>> Pieces(ulong ftexCode, IReadOnlyDictionary<ulong, byte[]> archive, GameData game, TexturePack? pack)
    {
        var d = new Dictionary<int, List<byte[]>>();
        void Add(int n, byte[] data)
        {
            if (!d.TryGetValue(n, out var l)) d[n] = l = [];
            if (!l.Any(x => x.AsSpan().SequenceEqual(data))) l.Add(data);
        }
        foreach (var part in pack?.Find(ftexCode)?.Parts ?? [])
            if (part.FileNumber is { } n) Add(n, part.Data);
        ulong name = ftexCode & ((1UL << FoxHash.ExtensionShift) - 1);
        for (int n = 0; n <= 6; n++)
        {
            string ext = n == 0 ? "ftex" : $"{n}.ftexs";
            if (!KnownExtensions.TryGetId(ext, out var id)) continue;
            ulong code = (id << FoxHash.ExtensionShift) | name;
            if (archive.TryGetValue(code, out var a)) Add(n, a);
            else if (game.TryReadCode(code) is { } g) Add(n, g);
        }
        return d;
    }

    /// <summary>
    /// True when every header version the game could read has, for each mip file it names, only versions that fit it
    /// (InstalledTextures.Fits): whatever the game picks is one whole texture. No header = nothing to check.
    /// </summary>
    private static bool Consistent(Dictionary<int, List<byte[]>> pieces)
    {
        if (!pieces.TryGetValue(0, out var headers)) return true;
        foreach (var hd in headers)
        {
            FtexTexture h;
            try { h = FtexTexture.Read(hd); } catch (Exception) { return false; }
            for (int n = 1; n <= h.FtexsFileCount; n++)
                if (pieces.TryGetValue(n, out var versions) && versions.Any(v => !InstalledTextures.Fits(h, n, v))) return false;
        }
        return true;
    }

    /// <summary>The same piece: equal bytes, or one a pack copy of the other (a pack part is padded to 16 bytes; vanilla padding is not always zero).</summary>
    internal static bool SamePiece(byte[] a, byte[] b)
    {
        var (s, l) = a.Length <= b.Length ? (a, b) : (b, a);
        return l.Length - s.Length < 16 && l.AsSpan(0, s.Length).SequenceEqual(s);
    }

    private static bool SameShape(FmdlModel a, FmdlModel b) =>
        a.Meshes.Count == b.Meshes.Count && a.Meshes.Zip(b.Meshes).All(z => z.First.Positions.Length == z.Second.Positions.Length);

    private static string Name(ulong code) => PathDictionary.Bundled.TryGetPath(code) is { } p ? p[(p.LastIndexOf('/') + 1)..] : code.ToString("x16");

    private static string Short(string path) => path[(path.LastIndexOf('/') + 1)..];
}
