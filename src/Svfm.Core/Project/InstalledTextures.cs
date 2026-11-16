using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Mods;

namespace Svfm.Core.Project;

/// <summary>Where one piece of a texture (the .ftex header = piece 0, or mip file N = .N.ftexs) comes from.</summary>
public sealed record TexturePiece(int Number, string Source, byte[] Data);

/// <summary>
/// The loose textures a port takes from a mod. A texture is read by the game from several places: the header and
/// smallest mip from a loaded texture pack entry (.pftxs) when there is one, every piece from the archive by path
/// (.ftex, .N.ftexs) otherwise; the large mips always from the archive. A replacer overrides files at their paths, so all
/// of these agree on the mod's version. A port can't put loose files at vanilla paths (they would change the vanilla
/// game everywhere), so a texture the mod installs there is moved as one complete unit: every piece resolved as the game
/// would after SnakeBite installs the mod (mod files over game files), copied under a new name of the same length, and
/// the port's models (and .fv2 files) pointed at it. A texture that can't be completed (a piece missing from the game
/// data given) is left fully vanilla, never mixed. Files at new paths are copied as they are.
/// </summary>
public static class InstalledTextures
{
    public sealed class Result
    {
        /// <summary>Vanilla texture code → its copy's code.</summary>
        public Dictionary<ulong, ulong> Codes { get; } = [];
        /// <summary>Old file name (no extension) → new one of the same length, for models that name textures as text.</summary>
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
        /// <summary>Textures left vanilla because a piece was missing: their mod-pack entries must give way to vanilla.</summary>
        public HashSet<ulong> MadeVanilla { get; } = [];
    }

    /// <summary>
    /// Every piece of a texture the game could read after the mod is installed, with where it comes from: "mod" (a loose
    /// file the mod ships), "mod pack" (its entry in the mod's pack of the package), "game" (the vanilla archive),
    /// "vanilla pack" (the vanilla pack's entry). A pack entry holds the header (0) and mip file 1; the archive every piece.
    /// </summary>
    public static List<TexturePiece> Pieces(string ftexPath, GameData game, MgsvMod? mod, TexturePack? modPack, TexturePack? vanillaPack)
    {
        ulong code = FoxHash.PathCode64(ftexPath);
        string stem = ftexPath[..^5];
        var pieces = new List<TexturePiece>();
        void FromPack(TexturePack? pack, string source)
        {
            foreach (var part in pack?.Find(code)?.Parts ?? [])
                if (part.FileNumber is { } n) pieces.Add(new TexturePiece(n, source, part.Data));
        }
        void FromArchive(int n)
        {
            string path = n == 0 ? ftexPath : $"{stem}.{n}.ftexs";
            if (mod != null && mod.ArchiveFiles.TryGetValue(path, out var m) && mod.Ships(path)) pieces.Add(new TexturePiece(n, "mod", m));
            if (game.TryRead(path) is { } g) pieces.Add(new TexturePiece(n, "game", g));
        }
        FromPack(modPack, "mod pack");
        FromPack(vanillaPack, "vanilla pack");
        for (int n = 0; n <= 6; n++) FromArchive(n);
        return pieces;
    }

    /// <summary>
    /// One complete, self-consistent loose set (header + every mip file it names) as the game reads it while the package is
    /// loaded: the package's own pack entry first (its header and .1 are read from the loaded pack), then the mod's loose
    /// files, then the game, then the vanilla pack, taking for each mip file the first whose size fits the header's mip
    /// table (so a set never mixes pieces of two different textures). D-Dog Camos ships a loose item0 header and a
    /// different one in each gear's pack (each gear its own camo, whole in the pack): the gear shows its pack's. Null when a
    /// piece is missing or nothing fits.
    /// </summary>
    public static Dictionary<int, byte[]>? Complete(List<TexturePiece> pieces)
    {
        string[] order = ["mod pack", "mod", "game", "vanilla pack"];
        foreach (var header in order.Select(src => pieces.FirstOrDefault(p => p.Number == 0 && p.Source == src)).OfType<TexturePiece>())
        {
            FtexTexture h;
            try { h = FtexTexture.Read(header.Data); } catch (Exception) { continue; }
            var set = new Dictionary<int, byte[]> { [0] = header.Data };
            bool ok = true;
            for (int n = 1; n <= h.FtexsFileCount && ok; n++)
            {
                var fit = order.Select(src => pieces.FirstOrDefault(p => p.Number == n && p.Source == src && Fits(h, n, p.Data))).OfType<TexturePiece>().FirstOrDefault();
                if (fit == null) ok = false; else set[n] = fit.Data;
            }
            if (ok) return set;
        }
        return null;
    }

    /// <summary>True when a mip file is exactly as long as the header's mip table says (checked on every vanilla texture file in the test data).</summary>
    public static bool Fits(FtexTexture header, int fileNumber, byte[] data)
    {
        var mips = header.Mips.Where(m => m.FileNumber == fileNumber).ToList();
        if (mips.Count == 0) return false;
        // A compressed mip is a table of 8-byte chunk headers followed by its chunks. Vanilla files store StoredSize
        // without the table; some mod tools store it with the table (GZ Sneaking Suit Black: every mip is StoredSize long
        // from its offset, table included). Either is a fit. Pack parts are padded to 16 bytes (397 of 2048 vanilla pack
        // .1 parts end 8 bytes past their last mip).
        int withTable = mips.Max(m => m.Offset + m.StoredSize + 8 * m.ChunkCount);
        int tableInside = mips.Max(m => m.Offset + m.StoredSize);
        return (data.Length >= withTable && data.Length < withTable + 16) || (data.Length >= tableInside && data.Length < tableInside + 16);
    }

    /// <summary>True when the dictionary or the game says this is a vanilla path (a loose file there changes vanilla).</summary>
    public static bool IsVanillaPath(GameData game, string ftexPath) =>
        game.Contains(ftexPath) || PathDictionary.Bundled.TryGetPath(FoxHash.PathCode64(ftexPath))?.StartsWith("/Assets/tpp/", StringComparison.Ordinal) == true;

    /// <summary>
    /// Copies the mod's loose textures that <paramref name="used"/> names into <paramref name="files"/>: new paths as they
    /// are; textures the mod installs at vanilla paths as complete copies under new names (see the class). <paramref name="modPack"/>
    /// /<paramref name="vanillaPack"/> are the package's packs (either may be null). UI pictures are skipped.
    /// With <paramref name="ownCopies"/>, textures at new paths are copied under the port's own names too (a mod whose
    /// optional downloads ship different textures at the same paths, ported as several looks: each look needs its own).
    /// </summary>
    public static Result Collect(GameData game, MgsvMod mod, IReadOnlySet<ulong> used, string prefix, TexturePack? modPack, TexturePack? vanillaPack,
        IDictionary<string, byte[]> files, List<string> notes, bool ownCopies = false)
    {
        var renamed = new List<string>();
        // Names given in this call: a stem of 4 characters or fewer is replaced whole, so two such textures (LRR's lrr0,
        // lrr1 …) would get the same name without this.
        var given = new HashSet<string>(StringComparer.Ordinal);
        var r = new Result();
        int unchanged = 0;
        var moved = new List<string>();
        var vanillaKept = new List<string>();
        var inPack = new List<string>();
        foreach (var group in mod.ArchiveFiles.Keys
                     .Where(k => k.Contains(".ftex", StringComparison.Ordinal) && !k.StartsWith("/Assets/tpp/ui/", StringComparison.Ordinal))
                     .GroupBy(TextureCheck.FtexOf))
        {
            string ftex = group.Key;
            ulong code = FoxHash.PathCode64(ftex);
            var shipped = group.Where(mod.Ships).ToList();
            if (!used.Contains(code) || shipped.Count == 0) continue;
            if (!IsVanillaPath(game, ftex) && vanillaPack?.Find(code) == null)
            {
                if (ownCopies && Complete(Pieces(ftex, game, mod, modPack, vanillaPack)) is { } own)
                {
                    string d0 = ftex[..(ftex.LastIndexOf('/') + 1)], s0 = ftex[(ftex.LastIndexOf('/') + 1)..^5];
                    string n0 = SameLengthName(s0, prefix, n => IsVanillaPath(game, $"{d0}{n}.ftex") || mod.ArchiveFiles.ContainsKey($"{d0}{n}.ftex") || given.Contains($"{d0}{n}"));
                    given.Add($"{d0}{n0}");
                    foreach (var (n, data) in own) files[n == 0 ? $"{d0}{n0}.ftex" : $"{d0}{n0}.{n}.ftexs"] = data;
                    r.Codes[code] = FoxHash.PathCode64($"{d0}{n0}.ftex");
                    r.Names[s0] = n0;
                    renamed.Add(s0 + ".ftex");
                    continue;
                }
                foreach (var path in shipped) files[path] = mod.ArchiveFiles[path];
                continue;
            }
            // Re-shipped unchanged (the game data has every shipped piece, byte for byte): nothing to do.
            if (shipped.All(x => game.TryRead(x) is { } g && g.AsSpan().SequenceEqual(mod.ArchiveFiles[x]))) { unchanged++; continue; }
            string name = ftex[(ftex.LastIndexOf('/') + 1)..];
            // Every shipped loose piece is also in the package's own pack entry (header and .1 come from the pack while
            // the package is loaded), and the game has no other version of any of them by path: the installed mod reads
            // this texture from the pack, plus whatever large mips the game has by path. The port keeps that entry in its
            // own pack as it is, which is the installed state exactly; nothing global moves (Cuba's partial face maps:
            // header + .1 in the pack, the loose .1 the same). When the game has its own version of a shipped piece at
            // that path (D-Dog Camos: .2/.3 also loose in texture0), the installed mod overrides it globally and the
            // port can't, so the texture is copied whole instead.
            if (modPack?.Find(code) is { } entry
                && shipped.All(x => entry.Parts.FirstOrDefault(pp => pp.FileNumber == PieceNumber(x)) is { } pp && PortCheck.SamePiece(pp.Data, mod.ArchiveFiles[x])
                                    && (game.TryRead(x) is not { } g || PortCheck.SamePiece(g, mod.ArchiveFiles[x]))))
            { inPack.Add(name); continue; }
            var set = Complete(Pieces(ftex, game, mod, modPack, vanillaPack));
            if (set == null) { r.MadeVanilla.Add(code); vanillaKept.Add(name); continue; }
            string dir = ftex[..(ftex.LastIndexOf('/') + 1)], stem = name[..^5];
            string stemNew = SameLengthName(stem, prefix, n => IsVanillaPath(game, $"{dir}{n}.ftex") || given.Contains($"{dir}{n}"));
            given.Add($"{dir}{stemNew}");
            foreach (var (n, data) in set) files[n == 0 ? $"{dir}{stemNew}.ftex" : $"{dir}{stemNew}.{n}.ftexs"] = data;
            r.Codes[code] = FoxHash.PathCode64($"{dir}{stemNew}.ftex");
            r.Names[stem] = stemNew;
            moved.Add(name);
        }
        if (moved.Count > 0)
            notes.Add($"{moved.Count} texture(s) the mod installs at vanilla paths ({List(moved)}) are copied whole under new names of the same length, " +
                      "with every piece as the installed mod has it; the models point at the copies (the vanilla textures stay as they are).");
        if (vanillaKept.Count > 0)
            notes.Add($"{vanillaKept.Count} texture(s) the mod installs at vanilla paths ({List(vanillaKept)}) can't be completed from the game data given " +
                      "(a piece is missing): they stay fully vanilla in the port. Open the full game to bring the mod's version along.");
        if (inPack.Count > 0)
            notes.Add($"{inPack.Count} texture(s) the mod installs at vanilla paths ({List(inPack)}) are read from the package's own texture pack " +
                      "(the loose pieces it ships are the same as the pack's): kept in the port's pack as the mod has them.");
        if (unchanged > 0) notes.Add($"{unchanged} vanilla texture(s) the mod re-ships unchanged are left out.");
        if (renamed.Count > 0)
            notes.Add($"{renamed.Count} texture(s) at the mod's own paths ({List(renamed)}) are copied under this look's own names of the same length " +
                      "(other looks ship other textures at those paths); the models point at the copies.");
        return r;
    }

    /// <summary>0 for a .ftex, N for a .N.ftexs.</summary>
    private static int PieceNumber(string path) =>
        path.EndsWith(".ftex", StringComparison.Ordinal) ? 0 : int.Parse(path[(path.LastIndexOf('.', path.Length - 7) + 1)..^6]);

    /// <summary>
    /// Points the models (.fmdl: hash codes, or file names patched in place) and .fv2 files in a package at the copies, and
    /// gives textures made vanilla their vanilla pack entry back. Returns notes for anything that couldn't be pointed.
    /// </summary>
    public static void Apply(FoxPackage pkg, Result r, List<string> notes)
    {
        if (r.Codes.Count == 0) return;
        int pointed = 0, failed = 0;
        for (int i = 0; i < pkg.Files.Count; i++)
        {
            var f = pkg.Files[i];
            if (f.Extension == "fmdl")
            {
                var model = FmdlModel.Read(f.Data);
                if (!model.Materials.SelectMany(m => m.Textures).Any(t => r.Codes.ContainsKey(t.PathCode))) continue;
                var re = FmdlModel.RetargetTextures(f.Data, r.Codes, out int n) ?? FmdlModel.RenameTextureFiles(f.Data, r.Names, out n);
                if (re == null || n == 0) { failed++; continue; }
                pkg.Files[i] = new PackageFile(f.Path, re);
                // Every texture of the copies is now named the new way.
                if (FmdlModel.Read(re).Materials.SelectMany(m => m.Textures).Any(t => r.Codes.ContainsKey(t.PathCode))) failed++;
                else pointed++;
            }
            else if (f.Extension == "fv2")
            {
                var fv2 = new Fv2File(f.Data);
                int n = r.Codes.Sum(kv => fv2.Replace(kv.Key, kv.Value));
                if (n > 0) pkg.Files[i] = new PackageFile(f.Path, fv2.Write());
            }
        }
        if (failed > 0) notes.Add($"{failed} model(s) couldn't be pointed at the texture copies everywhere; they show vanilla textures there.");
    }

    /// <summary>The installed pack with the entries of textures made vanilla replaced by vanilla's (or removed).</summary>
    public static bool RestoreVanilla(TexturePack pack, TexturePack? vanilla, Result r)
    {
        bool changed = false;
        foreach (var code in r.MadeVanilla)
        {
            var mine = pack.Find(code);
            if (mine == null) continue;
            var van = vanilla?.Find(code);
            pack.Textures.Remove(mine);
            if (van != null) pack.Textures.Add(van);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// A file name of the same length for a port's copy of a vanilla texture: the first part (up to the first '_', at most
    /// 4 characters) becomes hex of the prefix's hash, salted until no vanilla file has the name.
    /// </summary>
    public static string SameLengthName(string stem, string prefix, Func<string, bool> taken)
    {
        int k = Math.Min(4, stem.IndexOf('_') is > 0 and var u ? u : stem.Length);
        for (int salt = 0; salt < 1000; salt++)
        {
            string tag = FoxHash.StrCode64($"{prefix}#{salt}").ToString("x12")[..k];
            string name = tag + stem[k..];
            if (name != stem && !taken(name)) return name;
        }
        throw new InvalidOperationException($"No free name for a copy of {stem}.");
    }

    private static string List(List<string> names) => string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", …" : "");
}
