using System.Text;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Mods;

namespace Svfm.Core.VFramework;

/// <summary>
/// The sprint sparks effect (abilities.sprintSpark). V Framework plays the .vfx only when it is packed with the outfit, so
/// the build puts the file into the look's package (issue #26).
/// </summary>
public static class SprintSpark
{
    /// <summary>Raiden's effect, from plparts_raiden.fpkd (tested in game Oct 24): the default of the Sprint sparks switch.</summary>
    public const string DefaultPath = "/Assets/tpp/effect/vfx_data/chara/fx_tpp_chrfotspk01_s5.vfx";
    /// <summary>The default before issue #26. It is not in the game, so the build fails with it.</summary>
    public const string OldDefaultPath = "/Assets/tpp/effect/vfx/player/pl_vfx_sprint_spark.vfx";

    /// <summary>
    /// Puts each branch's sprint sparks .vfx into every package of its looks that the mod has, when it is not there yet.
    /// The file comes from another package of the mod, or from the game (a loose file, or a player package).
    /// </summary>
    /// <returns>An error for each effect that the build can not put in the mod.</returns>
    public static List<string> Pack(MgsvBuilder b, string who, IEnumerable<OutfitBranch> branches, GameData? game)
    {
        var errors = new List<string>();
        foreach (var br in branches)
        {
            if (br.Abilities.SprintSpark is not { } vfx) continue;
            var looks = new[] { br.Base }.Concat(br.Variants).Select(l => l.FpkPath + "d").Distinct().ToList();
            var own = looks.Where(b.ArchiveFiles.ContainsKey).ToList();
            if (own.Count == 0)
            {
                // A vanilla package carries the effect only when the game has it there.
                if (looks.Any(p => game?.TryRead(p) is { } van && Find(van, vfx) != null)) continue;
                errors.Add($"{who}: the sprint sparks effect {vfx} must be in the outfit's package. The mod has no package for this outfit.");
                continue;
            }
            var missing = own.Where(p => Find(b.ArchiveFiles[p], vfx) == null).ToList();
            if (missing.Count == 0) continue;
            var data = b.ArchiveFiles.Where(f => f.Key.EndsWith(".fpkd", StringComparison.Ordinal) || f.Key.EndsWith(".fpk", StringComparison.Ordinal))
                .Select(f => Find(f.Value, vfx)).FirstOrDefault(d => d != null) ?? FromGame(game, vfx);
            if (data == null)
            {
                errors.Add($"{who}: the sprint sparks effect {vfx} is not in the mod or in the game files" +
                    (game == null ? " (no game files are open)." : ". Change the path, or put the .vfx file in the outfit's package."));
                continue;
            }
            foreach (var p in missing)
            {
                var pkg = FoxPackage.Read(b.ArchiveFiles[p]);
                pkg.Files.Add(new PackageFile(vfx, data));
                PackageOrder.ApplySnakeBiteOrder(pkg);
                b.AddArchiveFile(p, pkg.Write());
            }
        }
        return errors;
    }

    /// <summary>
    /// Issue #69: the porter's sparks. An outfit has sparks when one of its packages (.fpk or .fpkd) holds the effect.
    /// </summary>
    /// <param name="files">The outfit's archive files (game path → bytes); files that are not packages are skipped.</param>
    /// <returns><see cref="DefaultPath"/>, or null when no package holds it.</returns>
    // shortcut: finds only Raiden's effect (the one effect in the game files), add paths when a mod ships another sparks .vfx
    public static string? In(IEnumerable<KeyValuePair<string, byte[]>> files) =>
        files.Any(f => (f.Key.EndsWith(".fpk", StringComparison.Ordinal) || f.Key.EndsWith(".fpkd", StringComparison.Ordinal)) && Find(f.Value, DefaultPath) != null) ? DefaultPath : null;

    /// <summary>The file from a Fox package, or null. Names are stored as plain text, so most packages are skipped unread.</summary>
    private static byte[]? Find(byte[] package, string path) =>
        package.AsSpan().IndexOf(Encoding.Latin1.GetBytes(path)) < 0 ? null : FoxPackage.Read(package).Find(path)?.Data;

    /// <summary>The .vfx as a loose game file, or from the first player package that has it (Raiden's is in plparts_raiden.fpkd).</summary>
    private static byte[]? FromGame(GameData? game, string vfx)
    {
        if (game == null) return null;
        if (game.TryRead(vfx) is { } loose) return loose;
        const string Player = "/Assets/tpp/pack/player/";
        foreach (var pack in PathDictionary.Bundled.PathsUnder(Player + "parts/").Concat(PathDictionary.Bundled.PathsUnder(Player)).Distinct())
            foreach (var ext in new[] { ".fpkd", ".fpk" })
                if (game.TryRead(pack + ext) is { } bytes && Find(bytes, vfx) is { } d) return d;
        return null;
    }
}
