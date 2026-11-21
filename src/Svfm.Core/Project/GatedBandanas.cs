using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>
/// Bandana and Infinity Bandana as custom head options that respect development. V Framework 1.7 lists vanilla heads
/// ("bandana", "infinitebandana") on custom outfits without checking development, but hides a custom head whose R&amp;D
/// row is undeveloped (ItemSelectorCallbackImpl_AddListSuit, IsExplicitlyUndevelopedByDevelopId). So each bandana is a
/// custom head using the vanilla bandana face packages (face4/5/6 = Demon stages 1–3), with an R&amp;D row that never
/// shows and copies the vanilla bandana's development state (DevelopRow.FollowsVanillaDevelopId), so the player never
/// develops it twice. The Infinity version adds infiniteAmmo. Names and icons are the vanilla ones. The Avatar branch
/// keeps the Avatar's own face and attaches the Avatar's vanilla bandana, so the Avatar's bandana waits for development too
/// (the check in AddListSuit has no player-type condition).
/// </summary>
public static class GatedBandanas
{
    private const string Fv2 = "/Assets/tpp/fova/chara/sna/sna0_face{0}_v00.fv2";
    private const string Fpk = "/Assets/tpp/pack/player/fova/plfova_sna0_face{0}_v00.fpk";

    /// <summary>
    /// Adds the bandanas to the project (once) and lists them on every Snake and Avatar look of the outfit that shows the
    /// character's head. Looks with the head hidden (built-in head, enableHead = false) get none; an outfit made from
    /// a suit without head options shouldn't be passed (BodyConverter leaves its heads empty). Returns the keys added.
    /// </summary>
    public static List<string> AddTo(ModProject p, OutfitItem outfit) => AddTo(p, outfit, [PlayerBranch.Snake, PlayerBranch.Avatar]);

    private static List<string> AddTo(ModProject p, OutfitItem outfit, IReadOnlyCollection<PlayerBranch> wearers)
    {
        var keys = KeysIn(p);
        if (keys.Count == 0) keys = AddTo(p);
        // A head of a reopened older build has no Avatar branch yet.
        foreach (var h in p.Items.OfType<HeadOptionItem>().Where(IsGated)) AddAvatarBranch(h);
        foreach (var who in outfit.Spec.Branches.Keys.Where(w => Wearer(w) && wearers.Contains(w)).ToList())
            outfit.Spec.Branches[who] = ModHeads.With(outfit.Spec.Branches[who], keys, true);
        return keys;
    }

    /// <summary>
    /// The Avatar keeps its own face and attaches the Avatar's vanilla bandana (as a SnakeHeadAddon bandana option). The
    /// files are made at build time from the game files (<see cref="AvatarFiles"/>), so the project file stays small.
    /// </summary>
    private static void AddAvatarBranch(HeadOptionItem h)
    {
        if (h.Spec.Branches.ContainsKey(PlayerBranch.Avatar) || h.Prefix.Length == 0) return;
        h.Spec.Branches[PlayerBranch.Avatar] = new HeadBranch
        {
            Fv2 = $"/Assets/tpp/fova/chara/sna/{h.Prefix}_av.fv2", Fpk = $"/Assets/tpp/pack/player/fova/plfova_{h.Prefix}_av.fpk",
        };
    }

    /// <summary>
    /// The files of a bandana head's Avatar branch: one package with the Avatar bandana model, its cloth .sim and an .fv2
    /// that attaches them. Empty when the head has no Avatar branch or already holds the package (a reopened build).
    /// </summary>
    public static Dictionary<string, byte[]> AvatarFiles(Archives.GameData game, HeadOptionItem h, List<string> notes)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!IsGated(h) || !h.Spec.Branches.TryGetValue(PlayerBranch.Avatar, out var av) || av is not { Fpk: { } avFpk, Fv2: { } avFv2 }
            || h.Files.ContainsKey(avFpk)) return files;
        string pkg = avFpk[..^".fpk".Length];
        string model = Naming.SnaModel(h.Prefix, "avbandana");
        var fpk = new Formats.FoxPackage();
        var fpkd = new Formats.FoxPackage { IsFpkd = true };
        var pack = new Formats.TexturePack();
        var sim = SnakeHeadAddon.AddAvatarBandana(game, model, fpk, fpkd, pack, notes);
        fpk.Files.Add(new Formats.PackageFile(avFv2, Formats.Fv2File.Attach([new(Hashing.FoxHash.PathCode64(model), sim)]).Write()));
        Formats.PackageOrder.ApplySnakeBiteOrder(fpk);
        Formats.PackageOrder.ApplySnakeBiteOrder(fpkd);
        files[pkg + ".fpk"] = fpk.Write();
        files[pkg + ".fpkd"] = fpkd.Write();
        files[pkg + ".pftxs"] = pack.Write();
        return files;
    }

    private static bool Wearer(PlayerBranch who) => who is PlayerBranch.Snake or PlayerBranch.Avatar;
    private static List<string> AllKeys(ModProject p) => [.. VanillaHeads.DevelopIds.Keys, .. KeysIn(p)];

    /// <summary>True when the outfit lists the bandana on Snake or Avatar, as the game's head or as the head that respects development.</summary>
    public static bool Listed(ModProject p, OutfitItem outfit, PlayerBranch? only = null)
    {
        var keys = AllKeys(p);
        return outfit.Spec.Branches.Any(kv => Wearer(kv.Key) && (only == null || kv.Key == only) && kv.Value.HeadOptions.Concat(kv.Value.Base.HeadOptions).Any(keys.Contains));
    }

    /// <summary>
    /// Lists the Bandana and the Infinity Bandana on the outfit's Snake and Avatar looks, or takes them off (issue #21).
    /// A bandana listed as the game's head can be worn before it is developed (V Framework 1.7 doesn't check vanilla
    /// heads); with <paramref name="develop"/> it is listed as the heads of this class instead. Bandana heads that no
    /// outfit lists any more are taken out of the project.
    /// </summary>
    public static void List(ModProject p, OutfitItem outfit, bool on, bool develop, PlayerBranch? only = null)
    {
        var keys = AllKeys(p);
        var wearers = outfit.Spec.Branches.Keys.Where(w => Wearer(w) && (only == null || w == only)).ToList();
        foreach (var who in wearers)
            outfit.Spec.Branches[who] = ModHeads.With(outfit.Spec.Branches[who], keys, false);
        if (on && develop) AddTo(p, outfit, wearers);
        else if (on)
            foreach (var who in wearers)
                outfit.Spec.Branches[who] = ModHeads.With(outfit.Spec.Branches[who], [.. VanillaHeads.DevelopIds.Keys], true);
        var listed = p.Items.Select(ProjectEdits.OutfitOf).OfType<OutfitItem>().SelectMany(o => o.Spec.Branches.Values)
            .SelectMany(b => b.HeadOptions.Concat(b.Base.HeadOptions).Concat(b.Variants.SelectMany(v => v.HeadOptions))).ToHashSet();
        p.Items.RemoveAll(i => i is HeadOptionItem h && IsGated(h) && !listed.Contains(h.Spec.Key));
    }

    /// <summary>
    /// The keys of the bandana heads in the project (none until <see cref="AddTo(ModProject)"/>): the tool's own, or those
    /// of a reopened SVFM build (issue #82).
    /// </summary>
    public static List<string> KeysIn(ModProject p) =>
        p.Items.Select(ProjectEdits.HeadOf).OfType<HeadOptionItem>().Where(IsGated).Select(h => h.Spec.Key).ToList();

    /// <summary>
    /// True for one of the two bandana heads this class adds. The Snake face tells: the vanilla bandana face at all three
    /// Demon stages. Not the item ID (a reopened build gets its IDs from the keys), and not the R&amp;D row: the user can
    /// turn the follow off on a bandana and make any head follow a bandana row (issue #98).
    /// </summary>
    public static bool IsGated(HeadOptionItem h) => h.Spec.Branches.TryGetValue(PlayerBranch.Snake, out var b)
        && b.Fv2 == string.Format(Fv2, 4) && b.FaceStages.Count == 3
        && Enumerable.Range(1, 3).All(s => b.FaceStages.TryGetValue(s, out var f) && f.fv2 == string.Format(Fv2, s + 3));

    /// <summary>Adds the two head items to the project; returns their keys, to list in outfits' headOptions.</summary>
    public static List<string> AddTo(ModProject p)
    {
        var keys = new List<string>();
        foreach (var (id, name, icon, developId, infinite) in new[]
        {
            ("bandana", "name_st_37001", "ui_st_bandana_alp", 37001, false),
            ("infbandana", "name_st_37002", "ui_st_infinity_bandana_alp", 37002, true),
        })
        {
            var (key, prefix) = p.NewItemNames(id);
            var spec = new HeadOptionSpec
            {
                SnakeFaceOnly = true, Key = key, NameLang = name, RealNameLang = name, InfoLang = name.Replace("name_", "info_"), IconFtexPath = "/Assets/tpp/ui/texture/EquipIcon/suit/" + icon, InfiniteAmmo = infinite,
                Develop = new DevelopRow { InitiallyAvailable = false, GmpCost = 0, Grade = 1, FollowsVanillaDevelopId = developId,
                    UsageGmpCost = Knowledge.GameKnowledge.VanillaDevelop(developId)?.UsageGmp ?? 0 },
            };
            spec.Header.Add($"{(infinite ? "Infinity Bandana" : "Bandana")} for this mod's outfits: available once the vanilla one ({developId}) is developed.");
            var face = new HeadBranch
            {
                Fv2 = string.Format(Fv2, 4), Fpk = string.Format(Fpk, 4),
                FaceStages = new() { [1] = (string.Format(Fv2, 4), string.Format(Fpk, 4)), [2] = (string.Format(Fv2, 5), string.Format(Fpk, 5)), [3] = (string.Format(Fv2, 6), string.Format(Fpk, 6)) },
            };
            spec.Branches[PlayerBranch.Snake] = face;
            var item = new HeadOptionItem { Id = id, Key = key, Prefix = prefix, Spec = spec, Title = infinite ? "INFINITY BANDANA" : "BANDANA" };
            AddAvatarBranch(item);
            p.Items.Add(item);
            keys.Add(key);
        }
        return keys;
    }
}
