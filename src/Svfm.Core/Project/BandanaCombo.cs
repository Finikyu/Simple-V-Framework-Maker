using Svfm.Core.Hashing;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>
/// Issue #126, option B: a "Bandana combo" of a head option is a second head option, the same head with the game's
/// bandana on top, for Snake. V Framework 1.7 loads only the head's own face .fv2/.fpk for a custom head (by Demon
/// stage), so each stage gets one package: a copy of the head's package, a bandana-only model cut from the vanilla
/// bandana face (<see cref="SnakeHeadAddon.AddSnakeBandana"/>) with its cloth .sim, and an .fv2 that attaches the head's
/// models and the bandana. The Avatar branch gets the same: the head's Avatar models plus the Avatar bandana
/// (<see cref="SnakeHeadAddon.AddAvatarBandana"/>), because the Avatar's own band does not show under a custom head
/// (owner, in game, Nov 20). The combo follows the development state of the vanilla Bandana (37001).
/// </summary>
public static class BandanaCombo
{
    public const int BandanaDevelopId = 37001;

    // Owner, Nov 22: a plain copy and an infinite copy can each be added and removed on their own.
    private const string Suffix = "_bandana", InfiniteSuffix = "_bandana_inf";

    /// <summary>The combo item of <paramref name="h"/> (the infinite copy when <paramref name="infinite"/>) in the project, or null.</summary>
    public static HeadOptionItem? Of(ModProject p, HeadOptionItem h, bool infinite = false) =>
        p.Items.OfType<HeadOptionItem>().FirstOrDefault(i => i.Id == IdOf(h, infinite));

    private static string IdOf(HeadOptionItem h, bool infinite) => h.Id + (infinite ? InfiniteSuffix : Suffix);

    /// <summary>The id of the head that the combo item <paramref name="id"/> copies, and if it is the infinite copy; null for an item that is not a combo.</summary>
    public static (string head, bool infinite)? HeadOf(string id) =>
        id.EndsWith(InfiniteSuffix, StringComparison.Ordinal) ? (id[..^InfiniteSuffix.Length], true)
        : id.EndsWith(Suffix, StringComparison.Ordinal) ? (id[..^Suffix.Length], false) : null;

    /// <summary>
    /// Adds the combo of <paramref name="h"/> (once) right after it and lists it on every Snake look that lists
    /// <paramref name="h"/>. With <paramref name="infinite"/>, the combo is the infinite copy: it gives infinite ammo.
    /// Returns the combo item.
    /// </summary>
    public static HeadOptionItem Add(GameData game, ModProject p, HeadOptionItem h, bool infinite = false)
    {
        if (Of(p, h, infinite) is { } had) return had;
        if (!h.Spec.Branches.TryGetValue(PlayerBranch.Snake, out var snake) || snake.Fv2 == null || snake.Fpk == null)
            throw new InvalidOperationException($"\"{h.Title}\" has no Snake head, so it has no bandana combo.");
        var (key, prefix) = p.NewItemNames(IdOf(h, infinite));
        var stages = snake.FaceStages.Count > 0 ? snake.FaceStages : new Dictionary<int, (string fv2, string fpk)> { [1] = (snake.Fv2, snake.Fpk) };
        var notes = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var newStages = new Dictionary<int, (string fv2, string fpk)>();
        // A head set (SnakeHeadAddon) keeps its shared stage package only in the files of its first option.
        byte[]? Load(string path) => h.Files.TryGetValue(path, out var d) ? d
            : p.Items.FirstOrDefault(i => i.Files.ContainsKey(path)) is { } holder ? holder.Files[path] : game.TryRead(path);
        foreach (var (stage, (fv2, fpk)) in stages.OrderBy(kv => kv.Key))
        {
            // A branch may name its package with or without ".fpk"; the combo keeps the same form.
            bool ext = fpk.EndsWith(".fpk", StringComparison.Ordinal);
            string src = ext ? fpk[..^".fpk".Length] : fpk, pkg = $"/Assets/tpp/pack/player/fova/plfova_{prefix}_s{stage}";
            var head = FoxPackage.Read(Load(src + ".fpk") ?? throw new InvalidDataException($"\"{h.Title}\": {src}.fpk is not in the item or the game."));
            var headFv2 = head.Find(fv2) ?? throw new InvalidDataException($"\"{h.Title}\": {fpk} has no {fv2}.");
            var fpkOut = new FoxPackage();
            foreach (var f in head.Files) if (f.Path != fv2) fpkOut.Files.Add(f);
            var fpkd = Load(src + ".fpkd") is { } dd ? FoxPackage.Read(dd) : new FoxPackage { IsFpkd = true };
            var pack = Load(src + ".pftxs") is { } td ? TexturePack.Read(td) : new TexturePack();
            var band = SnakeHeadAddon.AddSnakeBandana(game, Math.Clamp(stage, 1, 3), Naming.SnaModel(prefix, $"s{stage}"), fpkOut, fpkd, pack, notes);
            string fv2Out = $"/Assets/tpp/fova/chara/sna/{prefix}_s{stage}.fv2";
            fpkOut.Files.Add(new PackageFile(fv2Out, Fv2File.Attach([.. new Fv2File(headFv2.Data).Attachments(), band]).Write()));
            PackageOrder.ApplySnakeBiteOrder(fpkOut);
            PackageOrder.ApplySnakeBiteOrder(fpkd);
            files[pkg + ".fpk"] = fpkOut.Write();
            files[pkg + ".fpkd"] = fpkd.Write();
            files[pkg + ".pftxs"] = pack.Write();
            newStages[stage] = (fv2Out, ext ? pkg + ".fpk" : pkg);
        }
        // The combo gets a copy of the head's icon at a path of its own (issue #133): a shared icon file gets no infinity
        // mark, and a Generate of one item would change the icon of both.
        string? icon = h.Spec.IconFtexPath;
        if (icon != null && Load(icon + ".ftex") is { } ftex)
        {
            string own = $"/Assets/tpp/ui/texture/EquipIcon/svfm/ui_{prefix}_alp";
            files[own + ".ftex"] = ftex;
            for (int k = 1; k <= 6; k++) if (Load($"{icon}.{k}.ftexs") is { } mip) files[$"{own}.{k}.ftexs"] = mip;
            icon = own;
        }
        string title = (h.Title.Length > 0 ? h.Title : h.Id) + (infinite ? " + Infinite Bandana" : " + Bandana");
        var spec = new HeadOptionSpec
        {
            SnakeFaceOnly = h.Spec.SnakeFaceOnly, NoAvatarReason = h.Spec.NoAvatarReason, Key = key, NameLang = $"{prefix}_name", RealNameLang = $"{prefix}_name", InfoLang = $"{prefix}_info",
            IconFtexPath = icon, InfiniteAmmo = infinite || h.Spec.InfiniteAmmo,
            Develop = new DevelopRow { InitiallyAvailable = false, GmpCost = 0, Grade = 1, FollowsVanillaDevelopId = BandanaDevelopId,
                UsageGmpCost = Knowledge.GameKnowledge.VanillaDevelop(BandanaDevelopId)?.UsageGmp ?? 0 },
        };
        spec.Header.Add($"{title}: \"{h.Title}\" with the game's bandana; available once the vanilla Bandana ({BandanaDevelopId}) is developed.");
        var first = newStages.OrderBy(kv => kv.Key).First().Value;
        spec.Branches[PlayerBranch.Snake] = snake with { Fv2 = first.fv2, Fpk = first.fpk, FaceStages = snake.FaceStages.Count > 0 ? newStages : [] };
        // The Avatar's own band does not show under a custom head (owner, Nov 20: not under FACE SCARF), so the combo's
        // Avatar branch attaches the head's Avatar models plus the Avatar bandana, as the SVFM bandana heads do.
        if (h.Spec.Branches.TryGetValue(PlayerBranch.Avatar, out var av) && av is { Fv2: { } avFv2, Fpk: { } avFpk })
        {
            bool ext = avFpk.EndsWith(".fpk", StringComparison.Ordinal);
            string src = ext ? avFpk[..^".fpk".Length] : avFpk, pkg = $"/Assets/tpp/pack/player/fova/plfova_{prefix}_av";
            var head = FoxPackage.Read(Load(src + ".fpk") ?? throw new InvalidDataException($"\"{h.Title}\": {src}.fpk is not in the item or the game."));
            var headFv2 = head.Find(avFv2) ?? throw new InvalidDataException($"\"{h.Title}\": {avFpk} has no {avFv2}.");
            var fpkOut = new FoxPackage();
            foreach (var f in head.Files) if (f.Path != avFv2) fpkOut.Files.Add(f);
            var fpkd = Load(src + ".fpkd") is { } dd ? FoxPackage.Read(dd) : new FoxPackage { IsFpkd = true };
            var pack = Load(src + ".pftxs") is { } td ? TexturePack.Read(td) : new TexturePack();
            string model = Naming.SnaModel(prefix, "avbandana");
            var sim = SnakeHeadAddon.AddAvatarBandana(game, model, fpkOut, fpkd, pack, notes);
            string fv2Out = $"/Assets/tpp/fova/chara/sna/{prefix}_av.fv2";
            fpkOut.Files.Add(new PackageFile(fv2Out, Fv2File.Attach([new(FoxHash.PathCode64(model), sim), .. new Fv2File(headFv2.Data).Attachments()]).Write()));
            PackageOrder.ApplySnakeBiteOrder(fpkOut);
            PackageOrder.ApplySnakeBiteOrder(fpkd);
            files[pkg + ".fpk"] = fpkOut.Write();
            files[pkg + ".fpkd"] = fpkd.Write();
            files[pkg + ".pftxs"] = pack.Write();
            spec.Branches[PlayerBranch.Avatar] = av with { Fv2 = fv2Out, Fpk = ext ? pkg + ".fpk" : pkg };
        }
        var item = new HeadOptionItem { Id = IdOf(h, infinite), Key = key, Prefix = prefix, Spec = spec, Title = title };
        foreach (var (path, data) in files) item.Files[path] = data;
        item.Notes.AddRange(notes);
        item.SetText(spec.NameLang, title);
        item.SetText(spec.InfoLang, infinite ? $"{h.Title} with the bandana. Infinite ammo." : $"{h.Title} with the bandana.");
        int at = p.Items.IndexOf(h);
        p.Items.Insert(at < 0 ? p.Items.Count : at + 1, item);
        foreach (var o in p.Items.Select(ProjectEdits.OutfitOf).OfType<OutfitItem>())
            foreach (var who in new[] { PlayerBranch.Snake, PlayerBranch.Avatar })
                if (spec.Branches.ContainsKey(who) && o.Spec.Branches.TryGetValue(who, out var b) && b.HeadOptions.Concat(b.Base.HeadOptions).Contains(h.Spec.Key))
                    o.Spec.Branches[who] = ModHeads.With(b, [key], true);
        // Vanilla suits that offer the head (ExtendVanillaOutfit) offer the combo too, right after it.
        foreach (var x in p.Items.OfType<ExtraLooksItem>())
            foreach (var (who, b) in x.Spec.Branches.ToList())
                if (spec.Branches.ContainsKey(who) && b.HeadOptions.IndexOf(h.Spec.Key!) is var i and >= 0 && !b.HeadOptions.Contains(key))
                    x.Spec.Branches[who] = b with { HeadOptions = [.. b.HeadOptions[..(i + 1)], key, .. b.HeadOptions[(i + 1)..]] };
        return item;
    }

    /// <summary>Takes the combo of <paramref name="h"/> (the infinite copy when <paramref name="infinite"/>) out of the project and off every look.</summary>
    public static void Remove(ModProject p, HeadOptionItem h, bool infinite = false)
    {
        if (Of(p, h, infinite) is not { } c) return;
        foreach (var o in p.Items.Select(ProjectEdits.OutfitOf).OfType<OutfitItem>())
            foreach (var who in o.Spec.Branches.Keys.ToList())
                o.Spec.Branches[who] = ModHeads.With(o.Spec.Branches[who], [c.Spec.Key], false);
        foreach (var x in p.Items.OfType<ExtraLooksItem>())
            foreach (var (who, b) in x.Spec.Branches.ToList())
                if (b.HeadOptions.Contains(c.Spec.Key!))
                    x.Spec.Branches[who] = b with { HeadOptions = [.. b.HeadOptions.Where(k => k != c.Spec.Key)] };
        p.Items.Remove(c);
    }
}
