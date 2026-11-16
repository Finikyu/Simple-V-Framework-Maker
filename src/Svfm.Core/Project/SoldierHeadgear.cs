using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>
/// DD soldiers' headgear as head options. Vanilla gives the player five head items (V Framework aliases: bandana,
/// infinitebandana, balaclava, spheadgear, hpheadgear); DD soldiers get the last three. The soldier face table
/// (TppEnemyFaceId, 900 units) also holds 18 headgear faces: the Soviet and CFA balaclavas and DD balaclavas, helmets,
/// gas masks and night-vision goggles, in male and female versions. A V Framework head option's ddMale/ddFemale branch
/// can wear any of them (TppEnemyFaceId), so each distinct one becomes a head option here.
/// Left out: dds_balaclava1/2 (553/554) and 4/5 (556/557); IH notes they show as the green-top helmet (552/555) when
/// set as a player face. svs/pfs balaclavas exist for men only.
/// </summary>
public static class SoldierHeadgear
{
    public sealed record Headgear(string Id, string Title, string Description, int? Male, int? Female, string MaleName, string? FemaleName);

    public static readonly IReadOnlyList<Headgear> All =
    [
        new("svsclava", "BALACLAVA (SOVIET)", "The balaclava worn by Soviet soldiers in Afghanistan.", 550, null, "svs_balaclava", null),
        new("pfsclava", "BALACLAVA (CFA)", "The balaclava worn by CFA soldiers in Central Africa.", 551, null, "pfs_balaclava", null),
        new("ddhelmet", "DD HELMET", "Diamond Dogs combat helmet over a balaclava.", 552, 555, "dds_balaclava0", "dds_balaclava3"),
        new("gasopen", "GAS MASK", "Gas mask over an open balaclava.", 558, 559, "dds_balaclava6", "dds_balaclava7"),
        new("gasclava", "GAS MASK + BALACLAVA", "Gas mask over a full balaclava.", 560, 562, "dds_balaclava8", "dds_balaclava10"),
        new("gashelmet", "GAS MASK + HELMET", "Gas mask with the Diamond Dogs combat helmet.", 561, 563, "dds_balaclava9", "dds_balaclava11"),
        new("nvghelmet", "NVG + HELMET", "Night-vision goggles on the Diamond Dogs combat helmet.", 564, 566, "dds_balaclava12", "dds_balaclava14"),
        new("gasnvg", "GAS MASK + NVG + HELMET", "Gas mask and night-vision goggles with the Diamond Dogs combat helmet.", 565, 567, "dds_balaclava13", "dds_balaclava15"),
    ];

    private const string Icon = "/Assets/tpp/ui/texture/EquipIcon/head/ui_head_balaclava_alp";

    /// <summary>Adds the headgear to the project (once); returns the items.</summary>
    public static List<HeadOptionItem> AddTo(ModProject p, IEnumerable<string>? ids = null)
    {
        var want = ids?.ToHashSet() ?? All.Select(h => h.Id).ToHashSet();
        var items = new List<HeadOptionItem>();
        foreach (var h in All.Where(h => want.Contains(h.Id)))
        {
            var existing = p.Items.OfType<HeadOptionItem>().FirstOrDefault(i => i.Id == h.Id);
            if (existing != null) { items.Add(existing); continue; }
            var (key, prefix) = p.NewItemNames(h.Id);
            var spec = new HeadOptionSpec
            {
                Key = key, NameLang = $"{prefix}_name", RealNameLang = $"{prefix}_name", InfoLang = $"{prefix}_info", IconFtexPath = Icon,
                Develop = new DevelopRow { InitiallyAvailable = true, GmpCost = 0, Grade = 1 },
            };
            spec.Header.Add($"{h.Title}: soldier headgear face {h.MaleName}{(h.FemaleName != null ? " / " + h.FemaleName : "")}.");
            if (h.Male != null) spec.Branches[PlayerBranch.DdMale] = new HeadBranch { EnemyFaceId = h.Male.Value.ToString() };
            if (h.Female != null) spec.Branches[PlayerBranch.DdFemale] = new HeadBranch { EnemyFaceId = h.Female.Value.ToString() };
            var item = new HeadOptionItem { Id = h.Id, Key = key, Prefix = prefix, Spec = spec, Title = h.Title };
            item.SetText($"{prefix}_name", h.Title);
            item.SetText($"{prefix}_info", h.Description);
            p.Items.Add(item);
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Adds the headgear (once) and lists each one on the outfit's DD male/female looks that show the head, for the
    /// characters it exists for. Returns the keys listed.
    /// </summary>
    public static List<string> AddTo(ModProject p, OutfitItem outfit, IEnumerable<string>? ids = null)
    {
        var items = AddTo(p, ids);
        var listed = new List<string>();
        foreach (var (who, b) in outfit.Spec.Branches)
        {
            if (who is not (PlayerBranch.DdMale or PlayerBranch.DdFemale)) continue;
            var keys = items.Where(i => i.Spec.Branches.ContainsKey(who)).Select(i => i.Spec.Key!).ToList();
            var lists = new List<List<string>>();
            if (b.ShowsHead(b.Base)) { lists.Add(b.HeadOptions); lists.Add(b.Base.HeadOptions); }
            foreach (var v in b.Variants) if (b.ShowsHead(v)) lists.Add(v.HeadOptions);
            foreach (var list in lists.Distinct(ReferenceEqualityComparer.Instance).Cast<List<string>>())
                foreach (var k in keys) if (!list.Contains(k)) list.Add(k);
            listed.AddRange(keys.Where(k => !listed.Contains(k)));
        }
        return listed;
    }
}
