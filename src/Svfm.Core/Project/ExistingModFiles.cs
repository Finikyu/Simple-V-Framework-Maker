using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Mods;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

// An existing mod's files per item, and the item as the tool's own kind for the 3D preview (plan Phase 3).
public static partial class ExistingMod
{
    /// <summary>The archive's files by stem: the path up to the first dot of the file name (x.fpk, x.fpkd, x.pftxs, x.1.ftexs → x). Any case.</summary>
    private static Dictionary<string, List<string>> Stems(MgsvMod mod) =>
        mod.ArchiveFiles.Keys.GroupBy(StemOf, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    private static string StemOf(string path)
    {
        int dot = path.IndexOf('.', path.LastIndexOf('/') + 1);
        return dot < 0 ? path : path[..dot];
    }

    /// <summary>
    /// Weapons: equip constant → (parts, package), from every AddToEquipIdTable entry in the mod's scripts
    /// ({ TppEquip.EQP_WP_x, type, TppEquip.WP_x, block, parts path, package path }). An entry without a package (ammo) is left out.
    /// </summary>
    private static Dictionary<string, (string parts, string fpk)> EquipPackages(IEnumerable<SandboxResult> runs)
    {
        var all = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var c in runs.SelectMany(r => r.Calls).Where(c => c.Path.EndsWith(".AddToEquipIdTable", StringComparison.Ordinal)))
            if (c.Arguments.FirstOrDefault() is LuaValue.Table t)
                foreach (var e in t.Items.OfType<LuaValue.Table>())
                    if (e.Items.Count >= 6 && e.Items[0] is LuaValue.Name n && e.Items[5] is LuaValue.Text { Value.Length: > 0 } f)
                        all.TryAdd(n.Value, (StringOf(e.Items[4]), f.Value));
        return all;
    }

    /// <summary>
    /// Fills the item's files with the archive files its script names: every file whose stem is a path the script writes
    /// (the package's .fpk, .fpkd and .pftxs, the icon's .ftex and .ftexs). The arrays are the opened mod's own, never
    /// copies: a 2 GB pack stays one copy in memory. The build does not read them (it rebuilds from the opened mod).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="mod">The opened mod.</param>
    /// <param name="stems">The archive's files by stem.</param>
    /// <param name="paths">The paths the script names for the item.</param>
    private static void CollectFiles(ExistingItem item, MgsvMod mod, Dictionary<string, List<string>> stems, IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!p.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
            string stem = StemOf(p.StartsWith('/') ? p : "/" + p);
            if (!stems.TryGetValue(stem, out var keys)) continue;
            // Kept under the script's spelling, which is how the preview looks them up.
            foreach (var k in keys) item.Files.TryAdd(stem + k[stem.Length..], mod.ArchiveFiles[k]);
        }
    }

    private static readonly Dictionary<string, PlayerBranch> Branches =
        Enum.GetValues<PlayerBranch>().ToDictionary(LuaWriter.BranchName, b => b, StringComparer.Ordinal);

    private static bool? BoolOf(LuaValue? v) => v switch
    {
        LuaValue.Name { Value: "true" } => true, LuaValue.Name { Value: "false" } => false, LuaValue.Number n => n.Value != 0, _ => null,
    };

    private static string? TextOf(LuaValue? v) => v is LuaValue.Text { Value.Length: > 0 } t ? t.Value : null;

    /// <summary>A look's abilities table (#61), so the write-back gives it back; null when the look has none.</summary>
    private static Abilities? AbilitiesOf(LuaValue? v)
    {
        if (v is not LuaValue.Table t) return null;
        return Abilities.Read(k => BoolOf(t[k]) == true, k => t[k] is LuaValue.Number n ? n.Value : null, k => TextOf(t[k]));
    }

    private static List<string> Texts(LuaValue? v) => v is LuaValue.Table t ? t.Items.Select(TextOf).OfType<string>().ToList() : [];

    /// <summary>
    /// The item as the tool's own kind (outfit, head option, weapon, buddy costume), read from what its script registers,
    /// with the item's files. Only the 3D preview uses it: it is never built, saved or edited, so a key it can't read is
    /// simply not drawn. Null when the script names no model.
    /// </summary>
    private static ProjectItem? ModelOf(ExistingItem item, LuaValue.Table? args, LuaValue.Table? constTable, Dictionary<string, (string parts, string fpk)> equip)
    {
        string key = item.Key ?? item.Id;
        string name = item.NameId ?? "";
        string icon = item.IconPath ?? "";
        // The display name stays the lang ID (the app shows the item's text for it), so an edit writes back an ID.
        Look? LookOf(LuaValue? v) => v is LuaValue.Table t && TextOf(t["fpkPath"]) is { } fpk
            ? new Look
            {
                PartsPath = StringOf(t["partsPath"]), FpkPath = fpk, CamoFpk = TextOf(t["camoFpk"]), CamoFv2 = TextOf(t["camoFv2"]),
                DisplayName = TextOf(t["displayName"]), IconFtexPath = TextOf(t["iconFtexPath"]),
                EnableArm = BoolOf(t["enableArm"]), EnableHead = BoolOf(t["enableHead"]), Default = BoolOf(t["default"]) == true, HeadOptions = Texts(t["headOptions"]),
                Abilities = AbilitiesOf(t["abilities"]),
            }
            : null;
        List<OutfitColor> ColorsOf(LuaValue? v) => v is LuaValue.Table t
            ? t.Items.OfType<LuaValue.Table>().Where(c => TextOf(c["fv2"]) != null).Select(c => new OutfitColor(TextOf(c["fv2"])!, TextOf(c["fpk"]), TextOf(c["icon"]))).ToList()
            : [];
        OutfitVoice? VoiceOf(LuaValue.Table b)
        {
            var fpks = new Dictionary<string, string>(StringComparer.Ordinal);
            if (b["voiceFpk"] is LuaValue.Table vf) foreach (var (k, v) in vf.Fields) if (TextOf(v) is { } f) fpks[k] = f;
            string? type = TextOf(b["voiceType"]);
            return fpks.Count == 0 && type == null ? null : new OutfitVoice { Fpks = fpks, Type = type };
        }

        ProjectItem? m = null;
        switch (item.ItemType)
        {
            case ItemKind.Outfit or ItemKind.ExtraLooks when args != null:
            {
                var spec = new OutfitSpec { Key = key, NameLang = name, IconFtexPath = icon };
                foreach (var (field, who) in Branches)
                {
                    if (args[field] is not LuaValue.Table b) continue;
                    var looks = new List<Look>();
                    if (LookOf(b) is { } main) looks.Add(main);
                    if (b["variants"] is LuaValue.Table vs) looks.AddRange(vs.Items.Select(LookOf).OfType<Look>());
                    // Looks on a vanilla suit have no main look of their own: the first is shown first.
                    if (looks.Count == 0) continue;
                    if (LookOf(b) == null) item.LooksOnly.Add(who);
                    spec.Branches[who] = new OutfitBranch
                    {
                        Base = looks[0], Variants = looks.Skip(1).ToList(), HeadOptions = Texts(b["headOptions"]), Voice = VoiceOf(b),
                        PrimaryColors = ColorsOf((b["colors"] as LuaValue.Table)?["primary"]), SecondaryColors = ColorsOf((b["colors"] as LuaValue.Table)?["secondary"]),
                    };
                }
                if (spec.Branches.Count > 0) m = new OutfitItem { Id = item.Id, Key = item.Key, Prefix = item.Prefix, Title = item.Title, Spec = spec };
                break;
            }
            case ItemKind.HeadOption when args != null:
            {
                var heads = new Dictionary<PlayerBranch, HeadBranch>();
                HeadBranch? HeadOf(LuaValue.Table t)
                {
                    // Snake's own face per Demon Point stage: the faceStages table, or stages written in the table itself.
                    var stages = new Dictionary<int, (string, string)>();
                    var st = t["faceStages"] as LuaValue.Table ?? t;
                    for (int i = 0; i < st.Items.Count; i++)
                        if (st.Items[i] is LuaValue.Table s && TextOf(s["fpk"]) is { } sf) stages[i + 1] = (StringOf(s["fv2"]), sf);
                    string? fpk = TextOf(t["fpk"]) ?? stages.Values.Select(s => s.Item2).FirstOrDefault();
                    return fpk == null ? null : new HeadBranch { Fv2 = TextOf(t["fv2"]), Fpk = fpk, FaceStages = stages };
                }
                foreach (var (field, who) in Branches)
                    if (args[field] is LuaValue.Table b && HeadOf(b) is { } hb) heads[who] = hb;
                // Face stages are Snake's own face (SVFM writes them for Snake-face heads and the gated bandanas), which
                // the Avatar can't wear. The follow-vanilla row tells a gated bandana (issue #82).
                var spec = new HeadOptionSpec
                {
                    Key = key, NameLang = item.NameId, IconFtexPath = item.IconPath, SnakeFaceOnly = heads.Values.Any(h => h.FaceStages.Count > 0),
                    Develop = new DevelopRow { FollowsVanillaDevelopId = item.Develop?.FollowsVanillaDevelopId },
                };
                foreach (var (who, hb) in heads) spec.Branches[who] = hb;
                // Only the character tables carry a head: V Framework reads key, showInDevelopMenu and abilities at the
                // top level, and nothing else (wiki, Custom Outfits, "Head fields"). An fv2 or fpk there is not a wearer.
                item.HeadOriginal = new Dictionary<PlayerBranch, HeadBranch>(spec.Branches);
                if (spec.Branches.Count > 0) m = new HeadOptionItem { Id = item.Id, Key = item.Key, Prefix = item.Prefix, Title = item.Title, Spec = spec };
                break;
            }
            case ItemKind.BuddyOutfit when args != null:
                foreach (var buddy in new[] { "quiet", "horse", "dog" })
                    if (args[buddy] is LuaValue.Table b && TextOf(b["fpkPath"]) is { } fpk)
                    {
                        m = new BuddyOutfitItem { Id = item.Id, Key = item.Key, Prefix = item.Prefix, Title = item.Title,
                            Spec = new BuddyOutfitSpec { Key = key, Buddy = buddy, FpkPath = fpk, NameLang = name, IconFtexPath = icon } };
                        break;
                    }
                break;
            case ItemKind.Weapon:
                // The row's equip constant names the AddToEquipIdTable entry that carries the package.
                if (constTable?.Field(ParamSchema.Const, "equipID")?.Value is LuaValue.Name eq && equip.TryGetValue(eq.Value, out var e))
                {
                    string type = constTable.Field(ParamSchema.Const, "equipDevelopTypeID")?.Value is LuaValue.Name tn ? tn.Value[(tn.Value.LastIndexOf('_') + 1)..] : "";
                    m = new WeaponItem
                    {
                        Id = item.Id, Key = item.Key, Prefix = item.Prefix, Title = item.Title,
                        Spec = new WeaponSpec
                        {
                            Key = key, EquipConstant = eq.Value, WeaponConstant = "", EquipType = type, DevelopType = type,
                            PartsPath = e.parts, FpkPath = e.fpk, NameLang = name, IconFtexPath = icon,
                        },
                    };
                }
                break;
        }
        if (m != null) foreach (var (k, v) in item.Files) m.Files[k] = v;
        return m;
    }

    /// <summary>
    /// Adds the loose textures the item's models use to its preview model, once, by reference. Done when the preview
    /// first draws the item rather than when the mod opens: reading every model of a 2 GB pack up front is slow.
    /// </summary>
    /// <param name="data">The opened mod.</param>
    /// <param name="item">The item.</param>
    public static void LoadPreviewTextures(ExistingModData data, ExistingItem item)
    {
        if (item.Model is not { } m) return;
        lock (item)
        {
            if (item.TexturesLoaded) return;
            var index = data.LooseTextures();
            foreach (var code in TextureCheck.Referenced(m.Files.ToList()))
            {
                if (!index.TryGetValue(code, out var ftex)) continue;
                m.Files.TryAdd(ftex, data.Source.ArchiveFiles[ftex]);
                string stem = ftex[..^5];
                for (int k = 1; k <= 6; k++)
                    if (data.Source.ArchiveFiles.TryGetValue($"{stem}.{k}.ftexs", out var part)) m.Files.TryAdd($"{stem}.{k}.ftexs", part);
            }
            item.TexturesLoaded = true;
        }
    }
}

public sealed partial class ExistingModData
{
    private Dictionary<ulong, string>? _looseTextures;

    /// <summary>The mod's loose textures: path code → .ftex path. Made on first use.</summary>
    internal Dictionary<ulong, string> LooseTextures()
    {
        lock (this)
            return _looseTextures ??= Source.ArchiveFiles.Keys.Where(k => k.EndsWith(".ftex", StringComparison.Ordinal))
                .GroupBy(FoxHash.PathCode64).ToDictionary(g => g.Key, g => g.First());
    }
}
