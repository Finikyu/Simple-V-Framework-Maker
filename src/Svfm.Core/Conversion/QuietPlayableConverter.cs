using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Mods;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>
/// A Quiet buddy costume turned into a playable Quiet outfit.
/// </summary>
/// <remarks>
/// The owner, item 50 of the Windows test: "There is a new wearable playable Quiet outfit designed for V-Framework mod.
/// We need to study it to incorporate it and make it compatible so buddy Quiet outfits can be used for playable Quiet."
///
/// The reference mod (<c>Quiet Outfits (V Framework)</c>) shows how V Framework does it, and it is simpler than it looks:
/// a playable Quiet outfit is an ordinary <c>V_Player.RegisterOutfit</c> whose branch is <c>quiet</c> instead of
/// <c>snake</c>. The branch carries the same <c>partsPath</c> and <c>fpkPath</c> a Snake outfit carries, pointing at a
/// <b>player</b> package (<c>/Assets/tpp/pack/player/parts/plparts_quiet_NN</c>) and a <b>player</b> .parts
/// (<c>/Assets/tpp/parts/chara/qui/quip_main0_XXX_v00.parts</c>). The R&amp;D row is an ordinary suit row
/// (<c>EQP_SUIT</c> / <c>EQP_DEV_TYPE_Suit</c>), not a buddy row.
///
/// A buddy costume is the same model in a buddy package (<c>/Assets/tpp/pack/buddy/quiet/buddy_quiet2_NN</c>) with a
/// buddy .parts. So the conversion is: copy the costume's package under a player name, without the object file of the
/// buddy, keep its .parts under a player name, and register the outfit on the quiet branch. The model does not change
/// (the buddy model has 136 bones, the player model of the game 138; the owner's test in the game, Nov 15, shows that
/// the buddy model loads on the player).
///
/// What it deliberately does not do: it does not touch the buddy costume. The buddy keeps wearing what it wore; the
/// player gets a copy. That is the same rule every other converter here follows.
/// </remarks>
public static class QuietPlayableConverter
{
    /// <summary>Where a playable Quiet outfit's package goes.</summary>
    public const string PlayerParts = "/Assets/tpp/pack/player/parts/";
    /// <summary>Where a playable Quiet outfit's .parts goes.</summary>
    public const string PlayerPartsFile = "/Assets/tpp/parts/chara/qui/";

    /// <summary>The folder of the Quiet buddy costume packages of the game.</summary>
    public const string BuddyFolder = "/Assets/tpp/pack/buddy/quiet/";

    /// <summary>The folder of the object files (.fox2) that make Quiet a buddy. A player package must not hold one.</summary>
    public const string BuddyObjects = "/Assets/tpp/level_asset/chara/buddy/quiet/";

    /// <summary>
    /// The path of an <see cref="ExtraMod"/> that is no file: a Quiet buddy costume of the game, made a playable Quiet
    /// outfit (the owner, Nov 14). Its Inner is the package name, for example buddy_quiet2_05.
    /// </summary>
    public const string GameSource = "game:quiet";

    private static readonly Dictionary<string, string> CostumeNames = new(StringComparer.Ordinal)
    {
        ["buddy_quiet2_00"] = "Quiet", ["buddy_quiet2_01"] = "Quiet (blood)", ["buddy_quiet2_02"] = "Quiet (silver)",
        ["buddy_quiet2_03"] = "Quiet (gold)", ["buddy_quiet2_04"] = "Gray XOF", ["buddy_quiet2_05"] = "Sniper Wolf",
    };

    /// <summary>The six Quiet buddy costumes of the game: the package name and the name of the costume (from the text of
    /// the game when the game is open, else a fixed name).</summary>
    public static IReadOnlyList<(string Package, string Name)> GameCostumes(GameData? game) =>
        [.. BuddyConverter.QuietCostumes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            (kv.Key, Knowledge.GameKnowledge.EnglishName(game, Knowledge.GameKnowledge.VanillaDevelop(kv.Value.dev)?.Name) is { Length: > 0 } n ? n : CostumeNames[kv.Key]))];

    /// <summary>
    /// The R&amp;D row of the playable outfit of a costume of the game: it follows the vanilla development of that buddy
    /// costume (the owner, Nov 15). The first costume (51000) is available from the start, so its outfit is too.
    /// </summary>
    /// <param name="package">The package name of the costume, for example buddy_quiet2_05.</param>
    /// <returns>The row; null when the package is not a costume of the game.</returns>
    public static DevelopRow? GameCostumeDevelop(string package) =>
        !BuddyConverter.QuietCostumes.TryGetValue(package, out var c) ? null
        : c.dev == 51000 ? new DevelopRow { Grade = 1, GmpCost = 0, InitiallyAvailable = true }
        : new DevelopRow { Grade = 1, GmpCost = 0, InitiallyAvailable = false, FollowsVanillaDevelopId = c.dev };

    /// <summary>The Quiet buddy costume packages a mod replaces, as full paths without extension.</summary>
    /// <param name="mod">The mod.</param>
    /// <returns>The package paths, in order.</returns>
    public static List<string> Detect(MgsvMod mod) =>
        mod.ArchiveFiles.Keys
            .Where(p => p.StartsWith("/Assets/tpp/pack/buddy/quiet/", StringComparison.Ordinal)
                        && p.EndsWith(".fpk", StringComparison.Ordinal)
                        && !p.Contains("_common", StringComparison.Ordinal)
                        && !p.Contains("_add", StringComparison.Ordinal)
                        && !p.Contains("_heli", StringComparison.Ordinal)
                        && !p.Contains("bq_wp", StringComparison.Ordinal))
            .Select(p => p[..^4])
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The model a Quiet costume package shows: the one its .fox2 names through the .parts, else the largest model.
    /// </summary>
    /// <param name="fpk">The costume's .fpk.</param>
    /// <param name="fpkd">Its .fpkd, when the mod ships one.</param>
    /// <returns>The model, or null when the package holds none.</returns>
    public static FmdlModel? ShownModel(FoxPackage fpk, FoxPackage? fpkd) => BuddyConverter.ShownModel(fpk, fpkd);

    /// <summary>
    /// Makes a playable Quiet outfit from a Quiet buddy costume: its package copied under a player name, a player .parts
    /// naming the same model, and the outfit registered on the quiet branch.
    /// </summary>
    /// <param name="p">The project.</param>
    /// <param name="game">Game data (the vanilla costume, for a costume the mod only retextures).</param>
    /// <param name="mod">The mod.</param>
    /// <param name="vanillaPackage">The buddy package the mod replaces, without extension.</param>
    /// <param name="o">Names and the R&amp;D row.</param>
    /// <returns>The outfit item.</returns>
    public static OutfitItem AddTo(ModProject p, GameData game, MgsvMod mod, string vanillaPackage, QuietPlayableOptions o)
    {
        var item = ToItem(p, game, mod, vanillaPackage, o);
        p.Items.Add(item);
        return item;
    }

    /// <summary>Makes the outfit item as <see cref="AddTo"/> does. The project gives the names only; the item is not added to it.</summary>
    public static OutfitItem ToItem(ModProject p, GameData game, MgsvMod mod, string vanillaPackage, QuietPlayableOptions o)
    {
        Naming.CheckId(o.Id);
        var (key, prefix) = p.NewItemNames(o.Id);
        var notes = new List<string>();

        // 1. The costume's package: the vanilla one with the mod's files over it, as SnakeBite installs it. A mod can
        //    ship the model alone (QuietIGASuit does); the .parts names more files of the vanilla package, and the game
        //    stops on the pick in Sortie Prep when they are not there (the owner's test in the game, Nov 15).
        if (!mod.ArchiveFiles.ContainsKey(vanillaPackage + ".fpk") && game.TryRead(vanillaPackage + ".fpk") == null)
            throw new InvalidDataException($"{vanillaPackage}.fpk is neither in the mod nor in the game.");
        var fpk = BodyConverter.Merge(game, mod, vanillaPackage + ".fpk", isFpkd: false, notes);
        var fpkd = BodyConverter.Merge(game, mod, vanillaPackage + ".fpkd", isFpkd: true, notes);

        // 2. The costume must show a model, or there is nothing to wear.
        if (ShownModel(fpk, fpkd) == null) throw new InvalidDataException($"{vanillaPackage}.fpk holds no model.");

        // 3. The player package: the costume's files under a player name. The model keeps its own path inside, so the
        //    .parts below can name it exactly as the buddy one did.
        string pkg = $"{PlayerParts}plparts_{prefix}";
        var playerFpk = new FoxPackage();
        foreach (var f in fpk.Files) playerFpk.Files.Add(f);
        PackageOrder.ApplySnakeBiteOrder(playerFpk);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [pkg + ".fpk"] = playerFpk.Write(),
        };
        // 5. The player .parts, inside the .fpkd (a .parts is not installed as a loose file: the builder refuses that,
        //    and the game reads it from the package). The costume's own .parts is reused, so the outfit keeps its bone
        //    and material setup.
        string partsPath = $"{PlayerPartsFile}quip_{prefix}_v00.parts";
        byte[] partsBytes = BuildParts(fpkd, partsPath);
        var playerFpkd = new FoxPackage { IsFpkd = true };
        foreach (var f in fpkd.Files) playerFpkd.Files.Add(f);
        // The object file of the buddy stays out: with it, the game stops when the outfit is picked in Sortie Prep
        // (the owner's test in the game, Nov 15: Sniper Wolf loads without it and stops with it).
        playerFpkd.Files.RemoveAll(f => f.Extension == "parts" || f.Path.StartsWith(BuddyObjects, StringComparison.Ordinal));
        playerFpkd.Files.Add(new PackageFile(partsPath, partsBytes));
        PackageOrder.ApplySnakeBiteOrder(playerFpkd);
        files[pkg + ".fpkd"] = playerFpkd.Write();
        notes.Add($"{partsPath}: the costume's own .parts, so the outfit keeps its bone and material setup.");

        // 4. The texture pack, renamed with the package. A costume that ships none keeps the vanilla one.
        byte[]? packBytes = mod.ArchiveFiles.TryGetValue(vanillaPackage + ".pftxs", out var mp) ? mp : game.TryRead(vanillaPackage + ".pftxs");
        if (packBytes != null) files[pkg + ".pftxs"] = packBytes;
        else notes.Add($"{vanillaPackage}: no texture pack came with the costume and the vanilla one was not found; its textures must load as loose files.");

        // 6. The outfit: an ordinary suit row on the quiet branch.
        string title = o.Name ?? mod.Metadata.Name.ToUpperInvariant();
        var spec = new OutfitSpec
        {
            Key = key, NameLang = $"{prefix}_name", RealNameLang = $"{prefix}_name", InfoLang = $"{prefix}_info",
            IconFtexPath = o.IconFtexPath ?? $"/Assets/tpp/ui/texture/EquipIcon/buddy/ui_qwp_suit_qui0_alp",
            Develop = o.Develop ?? new DevelopRow { Grade = 1, GmpCost = 0, InitiallyAvailable = true },
        };
        spec.Header.Add($"{title}: playable Quiet outfit from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}. The buddy costume is unchanged.");
        spec.Branches[PlayerBranch.Quiet] = new OutfitBranch
        {
            Base = new Look { PartsPath = partsPath, FpkPath = pkg + ".fpk" },
            Abilities = new Abilities { QuietMovement = true },
        };
        var item = new OutfitItem { Id = o.Id, Key = key, Prefix = prefix, Spec = spec, Title = title };
        item.SetText(spec.NameLang, title);
        item.SetText(spec.InfoLang!, o.Description ?? $"Playable Quiet outfit from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}.");
        foreach (var (path, d) in files) item.Files[path] = d;
        item.Notes.AddRange(notes);
        return item;
    }

    /// <summary>
    /// The player .parts: the costume's own, which already names the model the package holds and carries its bone and
    /// material setup. A costume that ships none cannot be made playable, because a .parts is a FoxDataSet and this tool
    /// only reads those (writing one means rebuilding the whole entity table, which is not worth it when every Quiet
    /// costume in the game and in the reference mod ships one).
    /// </summary>
    private static byte[] BuildParts(FoxPackage? fpkd, string partsPath)
    {
        var existing = fpkd?.Files.FirstOrDefault(f => f.Extension == "parts");
        if (existing == null)
            throw new InvalidDataException("the costume ships no .parts, so it cannot be made playable (a .parts names the model the outfit shows).");
        return existing.Data;
    }
}

/// <summary>Names and the R&amp;D row for a playable Quiet outfit.</summary>
public sealed record QuietPlayableOptions
{
    /// <summary>Item ID (lower-case letters, digits, _).</summary>
    public required string Id { get; init; }
    /// <summary>English name; null = the mod's name in capitals.</summary>
    public string? Name { get; init; }
    /// <summary>English description; null = a line naming the mod.</summary>
    public string? Description { get; init; }
    /// <summary>The R&amp;D icon; null = the vanilla Quiet costume icon.</summary>
    public string? IconFtexPath { get; init; }
    /// <summary>The R&amp;D row; null = grade 1, free, available at once.</summary>
    public DevelopRow? Develop { get; init; }
}
