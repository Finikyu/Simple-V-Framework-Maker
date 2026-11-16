using Svfm.Core.Archives;

namespace Svfm.Core.Conversion;

/// <summary>The kinds of R&amp;D icon, each with its own vanilla set.</summary>
public enum IconKind { Suit, QuietCostume, DDog, DHorse, Head }

/// <summary>
/// The vanilla icons of a kind (EquipIcon), for the "game icon" choice: an item may show any base-game icon of its
/// kind. The R&amp;D row points at the vanilla file, so nothing is copied or changed.
/// </summary>
public static class VanillaIcons
{
    public const string EquipIcon = "/Assets/tpp/ui/texture/EquipIcon/";

    public static string Prefix(IconKind kind) => EquipIcon + kind switch
    {
        IconKind.Suit => "suit/ui_st_",
        IconKind.QuietCostume => "buddy/ui_qwp_suit_",
        IconKind.DDog => "buddy/ui_dd_",
        IconKind.DHorse => "buddy/ui_dh_",
        IconKind.Head => "head/",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The kind's vanilla icons the game has (paths without ".ftex"), from the dictionary.</summary>
    public static List<string> List(GameData game, IconKind kind, PathDictionary? dict = null) =>
        (dict ?? PathDictionary.Bundled).PathsUnder(Prefix(kind))
            .Select(p => p.EndsWith(".ftex", StringComparison.Ordinal) ? p[..^5] : p)
            .Where(p => !p.Contains('.') && game.Contains(p + ".ftex"))
            .Distinct().OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Checks a game-icon choice: a vanilla icon of this kind that the game has.</summary>
    public static string Check(GameData game, IconKind kind, string ftexPath)
    {
        string p = ftexPath.EndsWith(".ftex", StringComparison.Ordinal) ? ftexPath[..^5] : ftexPath;
        if (!p.StartsWith(Prefix(kind), StringComparison.Ordinal))
            throw new ArgumentException($"{p} is not a vanilla {kind} icon (those are under {Prefix(kind)}).");
        if (!game.Contains(p + ".ftex")) throw new FileNotFoundException($"The game has no icon {p}.");
        return p;
    }
}
