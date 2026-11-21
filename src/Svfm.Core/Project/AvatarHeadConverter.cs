using System.Text.RegularExpressions;
using Svfm.Core.Archives;
using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Mods;
using Svfm.Core.Rendering;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>
/// Issue #126: Avatar replacer heads (mods that replace the Avatar creator's hair packages plfova_avm_hair_X0_v00 with a
/// whole head, such as "Naked Snake from MGS Delta", Nexus 2266) as head options. One head option per replaced hair slot
/// (a0, b0, c0 = hair 1-3). The mod's hair package is copied under the port's own name, with its .fv2 at a new path; the
/// vanilla hair model the .fv2 no longer attaches is left out. The head is for Snake only: a Snake head option .fv2
/// replaces Snake's face, but V Framework 1.7 keeps the Avatar creator face under a custom Avatar head
/// (kHideAvatarCreatorFace is false), and it clips through the head (owner, Nov 20: no Avatar branch). The Avatar face
/// and horn changes of such mods are not part of a head.
/// </summary>
public static class AvatarHeadConverter
{
    private const string HairPkg = "/Assets/tpp/pack/player/avatar/hair/plfova_avm_hair_{0}_v00";
    private const string HairFv2 = "/Assets/tpp/fova/chara/avm/avm_hair_{0}_v00.fv2";
    public const string NoAvatar = "Snake only. The Avatar can't wear this head: under a custom Avatar head, V Framework 1.7 keeps the face from the Avatar creator, and that face clips through this head. Snake's face is replaced, so Snake can wear it.";

    /// <summary>The hair slots (a0, b0, c0) whose package the mod replaces with models the vanilla package does not have.</summary>
    public static List<string> ReplacedSlots(GameData game, MgsvMod mod)
    {
        var slots = new List<string>();
        foreach (var path in mod.ArchiveFiles.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var m = Regex.Match(path, @"/plfova_avm_hair_([a-z]\d)_v00\.fpk$");
            if (!m.Success) continue;
            string slot = m.Groups[1].Value;
            var pkg = FoxPackage.Read(mod.ArchiveFiles[path]);
            if (pkg.Find(string.Format(HairFv2, slot)) is not { } fv2) continue;
            var van = game.TryRead(string.Format(HairPkg, slot) + ".fpk") is { } vd ? FoxPackage.Read(vd) : new FoxPackage();
            var vanModels = van.Files.Select(f => FoxHash.PathCode64(f.Path)).ToHashSet();
            if (new Fv2File(fv2.Data).Attachments().Any(a => !vanModels.Contains(a.Model))) slots.Add(slot);
        }
        return slots;
    }

    public static List<HeadOptionItem> AddTo(ModProject p, GameData game, MgsvMod mod, string setId)
    {
        Naming.CheckId(setId);
        var slots = ReplacedSlots(game, mod);
        if (slots.Count == 0) throw new ArgumentException("The mod replaces no Avatar hair package with a head.");
        var (_, setPrefix) = p.NewItemNames(setId);
        var notes = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // Textures the head packages use: own copies (head mods reuse generic paths, see SnakeFaceConverter).
        var heads = slots.ToDictionary(s => s, s => string.Format(HairPkg, s));
        var used = TextureCheck.Referenced(mod.ArchiveFiles.Where(kv => heads.Values.Any(h => kv.Key.StartsWith(h + ".", StringComparison.Ordinal))));
        string first = heads[slots[0]];
        var relocated = InstalledTextures.Collect(game, mod, used, setPrefix,
            mod.ArchiveFiles.TryGetValue(first + ".pftxs", out var fmp) ? TexturePack.Read(fmp) : null,
            game.TryRead(first + ".pftxs") is { } fvp ? TexturePack.Read(fvp) : null, files, notes, ownCopies: true);

        string modName = mod.Metadata.Name.Length > 0 ? mod.Metadata.Name : setId;
        var items = new List<HeadOptionItem>();
        bool firstItem = true;
        foreach (var slot in slots)
        {
            string van = heads[slot], id = slots.Count == 1 ? setId : $"{setId}{slot}";
            var (key, prefix) = p.NewItemNames(id);
            string pkg = $"/Assets/tpp/pack/player/fova/plfova_{prefix}", fv2Path = $"/Assets/tpp/fova/chara/sna/{prefix}.fv2";
            var fpk = FoxPackage.Read(mod.ArchiveFiles[van + ".fpk"]);
            var fv2 = fpk.Find(string.Format(HairFv2, slot))!;
            var attach = new Fv2File(fv2.Data).Attachments().Select(a => a.Model).ToHashSet();
            var vanFiles = game.TryRead(van + ".fpk") is { } vd ? FoxPackage.Read(vd).Files.Select(f => f.Path).ToHashSet() : [];
            // The vanilla hair model (changed or not) the .fv2 no longer attaches stays out: it would change nothing.
            fpk.Files.RemoveAll(f => f == fv2 || f.Extension == "fmdl" && vanFiles.Contains(f.Path) && !attach.Contains(FoxHash.PathCode64(f.Path)));
            fpk.Files.Add(new PackageFile(fv2Path, fv2.Data));   // as the mod wrote it (texture swaps included)
            var fpkd = mod.ArchiveFiles.TryGetValue(van + ".fpkd", out var md) ? FoxPackage.Read(md)
                : game.TryRead(van + ".fpkd") is { } gd ? FoxPackage.Read(gd) : new FoxPackage { IsFpkd = true };
            var vp = game.TryRead(van + ".pftxs") is { } vt ? TexturePack.Read(vt) : null;
            var pack = mod.ArchiveFiles.TryGetValue(van + ".pftxs", out var mp) ? TexturePack.Read(mp) : vp;
            InstalledTextures.Apply(fpk, relocated, notes);
            InstalledTextures.Apply(fpkd, relocated, notes);
            if (pack != null) InstalledTextures.RestoreVanilla(pack, vp, relocated);
            foreach (var dropped in PackageOrder.ApplySnakeBiteOrder(fpk).Concat(PackageOrder.ApplySnakeBiteOrder(fpkd)))
                notes.Add($"{dropped.Path}: a file type the managers leave out of packages; left out.");
            var own = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [pkg + ".fpk"] = fpk.Write(), [pkg + ".fpkd"] = fpkd.Write() };
            if (pack != null) own[pkg + ".pftxs"] = pack.Write();

            string n = (slot[0] - 'a' + 1).ToString(), title = $"{ShortName(modName).ToUpperInvariant()} (HAIR {n})";
            string icon = $"/Assets/tpp/ui/texture/EquipIcon/head/ui_head_{prefix}_alp";
            var ft = FtexWriter.Write(Icon(fpk, fv2Path), withAlpha: true, FtexKind.Linear);
            var spec = new HeadOptionSpec
            {
                SnakeFaceOnly = true, NoAvatarReason = NoAvatar, Key = key, NameLang = $"{prefix}_name", RealNameLang = $"{prefix}_name", InfoLang = $"{prefix}_info", IconFtexPath = icon,
                Develop = new DevelopRow { InitiallyAvailable = true, GmpCost = 0, Grade = 1 },
            };
            spec.Header.Add($"{title}: the head from \"{mod.Metadata.Name}\" by {mod.Metadata.Author} (Avatar hair {n}); as Snake's whole head.");
            var branch = new HeadBranch { Fv2 = fv2Path, Fpk = pkg + ".fpk" };
            spec.Branches[PlayerBranch.Snake] = branch;
            var item = new HeadOptionItem { Id = id, Key = key, Prefix = prefix, Spec = spec, Title = title };
            item.SetText($"{prefix}_name", title);
            item.SetText($"{prefix}_info", $"The head from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}.");
            item.Files[icon + ".ftex"] = ft.Ftex;
            foreach (var (k, d) in ft.Ftexs) item.Files[$"{icon}.{k}.ftexs"] = d;
            foreach (var (path, data) in own) item.Files[path] = data;
            if (firstItem)
            {
                foreach (var (path, data) in files) item.Files[path] = data;
                notes.Add("Snake wears this head in place of his face at every Demon stage. Check it in game: the head was made for the Avatar.");
                notes.Add(NoAvatar);
                foreach (var path in mod.ArchiveFiles.Keys.Where(k => ModClassifier.KindOf(k, mod) == ModFileKind.AvatarPart && !k.Contains("/avatar/hair/", StringComparison.Ordinal)))
                    notes.Add($"{path}: the Avatar's own face or horn; not part of a head, left out.");
                item.Notes.AddRange(notes);
                firstItem = false;
            }
            p.Items.Add(item);
            items.Add(item);
        }
        SnakeHeadAddon.OfferOnVanillaSuits(p, setId, items.Select(i => i.Spec.Key).ToList(), PlayerBranch.Snake);
        return items;
    }

    /// <summary>
    /// A short default title: the mod name split into words (CamelCase too), whole words up to <paramref name="max"/>
    /// characters. A long name ("NakedSnakeMGSDeltaV3Bandana") does not fit in the game's list.
    /// </summary>
    public static string ShortName(string name, int max = 16)
    {
        var words = Regex.Replace(name, @"(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[0-9])(?=[A-Za-z])", " ")
            .Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        string s = "";
        foreach (var w in words)
        {
            string next = s.Length == 0 ? w : $"{s} {w}";
            if (next.Length > max) break;
            s = next;
        }
        return s.Length > 0 ? s : name[..Math.Min(max, name.Length)];
    }

    /// <summary>The icon of an Avatar replacer head: every model its .fv2 attaches, drawn as headgear.</summary>
    /// <param name="fpk">The head's package.</param>
    /// <param name="fv2Path">The head's .fv2 in that package.</param>
    /// <returns>The icon picture.</returns>
    public static RgbaImage Icon(FoxPackage fpk, string fv2Path)
    {
        var codes = new Fv2File(fpk.Find(fv2Path)!.Data).Attachments().Select(a => a.Model).ToHashSet();
        var meshes = fpk.Files.Where(f => f.Extension == "fmdl" && codes.Contains(FoxHash.PathCode64(f.Path)))
            .SelectMany(f => FmdlModel.Read(f.Data).Meshes).ToList();
        return HeadIcon.Render(meshes, []);
    }
}
