using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;
using Svfm.Core.VFramework;

namespace Svfm.Core.Conversion;

/// <summary>Which fatigues looks the outfit gets (each is one entry on the cycle button).</summary>
[Flags]
public enum FatiguesLooks { Normal = 1, Scarf = 2, Naked = 4, All = Normal | Scarf | Naked }

/// <summary>How the suit-list icon is made.</summary>
public abstract record IconChoice
{
    /// <summary>A 1:1 crop of the mod's camo texture (the vanilla fatigues style).</summary>
    public sealed record CamoCrop(int X, int Y, float Contrast = SuitIcon.DefaultContrast, float Brightness = 1f) : IconChoice;
    /// <summary>The vanilla icon of the replaced camo, recoloured: hues in range kept, the rest greyed and darkened.</summary>
    public sealed record RecolourVanilla(float HueFrom, float HueTo, float Darken = 0.35f) : IconChoice;
    /// <summary>The vanilla icon of the replaced camo, as is (no icon file in the mod).</summary>
    public sealed record Vanilla : IconChoice;
    /// <summary>The icon the mod ships (at a vanilla icon path, which the port never overwrites): copied to the port's own path.</summary>
    public sealed record ModIcon(string FtexPath) : IconChoice;
    /// <summary>Any vanilla icon of the item's kind (VanillaIcons.List), shown as it is: the R&amp;D row points at it.</summary>
    public sealed record GameIcon(string FtexPath) : IconChoice;
    /// <summary>A picture the user gives (PNG bytes): a finished 256×128 icon is used as it is, any other picture is fitted into the vanilla frame (SuitIcon.FromPicture).</summary>
    public sealed record Picture(byte[] Png) : IconChoice
    {
        public RgbaImage Icon() => SuitIcon.FromPicture(Formats.Png.Decode(Png));
    }
    /// <summary>
    /// A render of the outfit's own model wearing the mod's camo (applied through the port's .fv2), lit and
    /// framed like the vanilla render icons. <paramref name="WithScarf"/> draws the scarf look.
    /// </summary>
    public sealed record Render(bool WithScarf = true, Rendering.IconFraming? Framing = null, Rendering.IconLighting? Lighting = null) : IconChoice;

    /// <summary>
    /// The icon made from the mod's own assets in the style of the replaced camo's vanilla icon: a render for
    /// render-style icons (BLACK), otherwise a fabric crop of the camo texture at the vanilla crop origin.
    /// </summary>
    public static IconChoice DefaultFor(Knowledge.CamoInfo camo) =>
        camo.IconStyle != null && camo.IconStyle.Contains("render", StringComparison.OrdinalIgnoreCase)
            ? new Render()
            : camo.IconTextureOrigin is [var x, var y] ? new CamoCrop(x, y)
            : new CamoCrop(SuitIcon.DefaultCropOrigin.x, SuitIcon.DefaultCropOrigin.y);
}

/// <summary>Settings for turning a Snake/Avatar camo replacer into its own V Framework outfit.</summary>
public sealed record CamoConversionOptions
{
    /// <summary>Short lower-case ID used in file names, e.g. "tbo".</summary>
    public required string Id { get; init; }
    /// <summary>V Framework registration key; keep it across rebuilds.</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? RealName { get; init; }
    public required string Description { get; init; }
    public string ModName { get; init; } = "";
    public string ModVersion { get; init; } = "1.0";
    public string ModAuthor { get; init; } = "";
    public string ModDescription { get; init; } = "";
    /// <summary>Behaviour preset ID (GameKnowledge.Presets), e.g. "sneaking_suit_g4" or "fatigues".</summary>
    public string Preset { get; init; } = "fatigues";
    public FatiguesLooks Looks { get; init; } = FatiguesLooks.All;
    /// <summary>Order on the cycle button. Vanilla fatigues go Naked, Standard, Scarf.</summary>
    public IReadOnlyList<FatiguesLooks> LookOrder { get; init; } = [FatiguesLooks.Naked, FatiguesLooks.Normal, FatiguesLooks.Scarf];
    /// <summary>The look worn when the suit is first picked.</summary>
    public FatiguesLooks DefaultLook { get; init; } = FatiguesLooks.Normal;
    /// <summary>Camo concealment copied from a vanilla camo by name (V Framework camoBonusType). Null = use <see cref="CamoBonusValues"/>.</summary>
    public string? CamoBonusType { get; init; }
    /// <summary>
    /// Camo concealment per ground material (camoBonusValues). Null = the replaced camo's own values from the game's
    /// table (GameKnowledge.CamoValues), which the user can then edit.
    /// </summary>
    public IReadOnlyDictionary<string, int>? CamoBonusValues { get; init; }
    /// <summary>How the icon is made; null = <see cref="IconChoice.DefaultFor"/> the replaced camo (from the mod's own assets).</summary>
    public IconChoice? Icon { get; init; }
    /// <summary>The user's qar_dictionary.txt; lets the icon renderer use full-size game textures.</summary>
    public PathDictionary? Dictionary { get; init; }
    /// <summary>Heads offered on every look; null = the vanilla fatigues' (Bandana, Infinity Bandana).</summary>
    public IReadOnlyList<string>? HeadOptions { get; init; }
    public string NormalLabel { get; init; } = "STANDARD";
    public string ScarfLabel { get; init; } = "SCARF";
    public string NakedLabel { get; init; } = "NAKED";
    /// <summary>Extra languages: lang ID suffix ("name", "real", "info", "normal", "scarf", "naked") → language → text.</summary>
    public Dictionary<string, Dictionary<string, string>> Translations { get; init; } = [];
}

public sealed class ConversionResult
{
    public required MgsvBuilder Mod { get; init; }
    public required OutfitSpec Outfit { get; init; }
    public required string Lua { get; init; }
    public RgbaImage? Icon { get; init; }
    /// <summary>The icon the vanilla suit had, kept as the backup the design calls for.</summary>
    public RgbaImage? OldIcon { get; init; }
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Converter for mods that replace one of Snake's fatigues camos (texture packs of
/// plfova_sna0_main1_c&lt;id&gt; and/or plfova_sna8_main0_c&lt;id&gt;), such as True Black Ocelot.
/// The result is a new suit for Snake and Avatar (they share data) with the chosen looks; the
/// vanilla camo stays as it was. Body models are the vanilla fatigues packages.
/// </summary>
public static class CamoConverter
{
    private const string Fova = "/Assets/tpp/pack/player/fova/";

    /// <summary>The vanilla camos this mod replaces (fatigues and/or naked), by camo table entry.</summary>
    public static List<(CamoInfo camo, bool fatigues, bool naked)> Detect(GameData game, MgsvMod mod)
    {
        var found = new List<(CamoInfo, bool, bool)>();
        foreach (var c in GameKnowledge.Camos.Where(c => c.Snake != null))
        {
            string main1 = c.Snake!.NormalScarf[..^4], naked = c.Snake.Naked[..^4];
            bool f = CamoPort.ModReplaces(game, mod, main1);
            bool n = CamoPort.ModReplaces(game, mod, naked);
            if (f || n) found.Add((c, f, n));
        }
        return found;
    }

    private const string CamoufTablePath = "/Assets/tpp/level_asset/chara/player/game_object/player2_camouf_param.lua";

    /// <summary>
    /// The camo values a port of <paramref name="camo"/> starts from (issue #89): the row of the mod's own
    /// player2_camouf_param.lua when it ships one, else the game's row.
    /// </summary>
    public static Dictionary<string, int> StartCamoValues(MgsvMod mod, CamoInfo camo)
    {
        foreach (var files in new[] { mod.ArchiveFiles, mod.GameFiles })
            foreach (var path in new[] { CamoufTablePath, CamoufTablePath[1..] })
                if (files.TryGetValue(path, out var data)
                    && GameKnowledge.CamoRowFromTable(System.Text.Encoding.UTF8.GetString(data), camo.PlayerCamoType) is { } row)
                    return row;
        return new Dictionary<string, int>(GameKnowledge.CamoValues(camo.Name) ?? new Dictionary<string, int>());
    }

    /// <summary>The mod's own English text for the first of these lang IDs that it has (issue #89); null when none.</summary>
    public static string? ModInfoText(MgsvMod mod, IEnumerable<string?> langIds) =>
        BuddyConverter.ModLang(mod).GetValueOrDefault("eng") is { } eng
            ? langIds.OfType<string>().Select(id => eng.Get(Hashing.FoxHash.StrCode32(id))).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            : null;

    /// <summary>The port as a one-item project (module SVFM_&lt;Id&gt;.lua, text svfm_&lt;id&gt;).</summary>
    public static ConversionResult Convert(GameData game, MgsvMod mod, CamoInfo camo, CamoConversionOptions o)
    {
        string id = o.Id.ToLowerInvariant();
        var project = new Project.ModProject
        {
            Id = id,
            Name = o.ModName.Length > 0 ? o.ModName : $"{o.Name} (V Framework)",
            Version = o.ModVersion,
            Author = o.ModAuthor.Length > 0 ? o.ModAuthor : mod.Metadata.Author,
            Description = o.ModDescription.Length > 0 ? o.ModDescription
                : $"{o.Name}: a new outfit for Snake and Avatar, made from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}. " +
                  $"The vanilla {camo.Description} camo is unchanged.\r\n\r\nRequires Infinite Heaven, IHHook and V Framework 1.7.",
        };
        var (item, icon, oldIcon) = ToItem(game, mod, camo, o, $"svfm_{id}");
        project.Items.Add(item);
        var built = Project.ProjectBuilder.Build(project);
        var result = new ConversionResult { Mod = built.Mod, Outfit = item.Spec, Lua = built.Lua, Icon = icon, OldIcon = oldIcon };
        result.Notes.AddRange(item.Notes);
        result.Notes.AddRange(built.Errors);
        return result;
    }

    /// <summary>
    /// The port as one project item: its files, text and outfit, with every name built from
    /// <paramref name="prefix"/> (e.g. "svfm_tbo") so several ports can share a project or be installed together.
    /// </summary>
    public static (Project.OutfitItem item, RgbaImage? icon, RgbaImage? oldIcon) ToItem(GameData game, MgsvMod mod, CamoInfo camo,
        CamoConversionOptions o, string prefix)
    {
        string id = o.Id.ToLowerInvariant();
        Project.Naming.CheckId(id);
        var notes = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var snakeFiles = camo.Snake ?? throw new ArgumentException($"{camo.Name} has no Snake fatigues camo.");

        // 1. Camo packages, at the port's own paths.
        CamoPortResult? main1 = null, naked = null;
        string main1Vanilla = snakeFiles.NormalScarf[..^4], nakedVanilla = snakeFiles.Naked[..^4];
        if ((o.Looks & (FatiguesLooks.Normal | FatiguesLooks.Scarf)) != 0)
        {
            main1 = CamoPort.Port(game, mod, main1Vanilla, $"{Fova}plfova_{prefix}_main1",
                [new TextureMove(snakeFiles.Texture, $"/Assets/tpp/chara/sna/Pictures/{prefix}_main1_def_bsm.ftex")]);
            if (!CamoPort.ModReplaces(game, mod, main1Vanilla)) notes.Add("The mod does not change the fatigues camo; Normal and Scarf look vanilla.");
        }
        if ((o.Looks & FatiguesLooks.Naked) != 0)
        {
            naked = CamoPort.Port(game, mod, nakedVanilla, $"{Fova}plfova_{prefix}_naked",
                [new TextureMove(snakeFiles.NakedTexture, $"/Assets/tpp/chara/sna/Pictures/{prefix}_naked_def_bsm.ftex")]);
            if (!CamoPort.ModReplaces(game, mod, nakedVanilla)) notes.Add("The mod does not change the naked camo; the Naked look is vanilla.");
        }
        foreach (var r in new[] { main1, naked })
            if (r != null) { foreach (var (p, d) in r.Files) files[p] = d; notes.AddRange(r.Notes); }

        // 2. Text.
        string L(string what) => $"{prefix}_{what}";
        var text = new ModText { FileBase = prefix };
        text.Set(L("name"), o.Name);
        text.Set(L("real"), o.RealName ?? o.Name);
        text.Set(L("info"), o.Description);
        text.Set(L("normal"), o.NormalLabel);
        text.Set(L("scarf"), o.ScarfLabel);
        text.Set(L("naked"), o.NakedLabel);
        foreach (var (what, per) in o.Translations)
            foreach (var (lang, t) in per) text.Set(L(what), lang, t);

        // 3. Icon.
        string iconPath = $"/Assets/tpp/ui/texture/EquipIcon/suit/ui_st_{prefix}_alp";
        RgbaImage? oldIcon = camo.Icon != null ? game.TryReadTexture(camo.Icon + ".ftex") : null;
        RgbaImage? icon = null;
        var iconChoice = o.Icon ?? IconChoice.DefaultFor(camo);
        switch (iconChoice)
        {
            case IconChoice.Vanilla:
                iconPath = camo.Icon ?? iconPath;
                icon = oldIcon;
                break;
            case IconChoice.RecolourVanilla rc:
                if (oldIcon == null) throw new FileNotFoundException($"vanilla icon {camo.Icon} not found");
                icon = SuitIcon.RecolourKeepHue(oldIcon, rc.HueFrom, rc.HueTo, darken: rc.Darken);
                break;
            case IconChoice.CamoCrop cc:
                if (oldIcon == null) throw new FileNotFoundException($"vanilla icon {camo.Icon} not found (needed for the frame mask)");
                var tex = CamoTexture(game, files, main1, mod, main1Vanilla, $"/Assets/tpp/chara/sna/Pictures/{prefix}_main1_def_bsm.ftex");
                icon = SuitIcon.FromCamoTexture(tex, SuitIcon.MaskFrom(oldIcon), (cc.X, cc.Y), cc.Contrast, cc.Brightness);
                break;
            case IconChoice.Picture pic:
                icon = pic.Icon();
                break;
            case IconChoice.GameIcon gi:
                iconPath = VanillaIcons.Check(game, IconKind.Suit, gi.FtexPath);
                icon = game.TryReadTexture(iconPath + ".ftex");
                break;
            case IconChoice.Render rr:
            {
                if (main1 == null) throw new ArgumentException("A rendered icon needs the Normal or Scarf look (the fatigues camo).");
                var camoFpk = FoxPackage.Read(files[main1.CamoFpk]);
                var camoPack = TexturePack.Read(files[main1.CamoFpk[..^4] + ".pftxs"]);
                bool scarf = rr.WithScarf && o.Looks.HasFlag(FatiguesLooks.Scarf);
                icon = SuitRender.Icon(game, o.Dictionary, new SuitRenderInput
                {
                    BodyPackage = scarf ? "/Assets/tpp/pack/player/parts/plparts_normal_scarf" : "/Assets/tpp/pack/player/parts/plparts_normal",
                    Fv2s = SuitRender.Fv2sIn(camoFpk).ToList(),
                    ModPacks = [camoPack],
                    ModFiles = main1.Files.Where(f => f.Key.Contains(".ftex", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal),
                    Framing = rr.Framing,
                    Lighting = rr.Lighting,
                });
                break;
            }
        }
        if (iconChoice is not (IconChoice.Vanilla or IconChoice.GameIcon) && icon != null)
        {
            var ft = SuitIcon.ToGameFiles(icon);
            files[iconPath + ".ftex"] = ft.Ftex;
            foreach (var (n, d) in ft.Ftexs) files[$"{iconPath}.{n}.ftexs"] = d;
        }

        // 4. Outfit.
        var preset = GameKnowledge.Preset(o.Preset);
        var looks = new List<Look>();
        Look Fatigue(string package, string label, CamoPortResult camoFiles) => new()
        {
            PartsPath = PartsPathOf(game, package),
            FpkPath = package + ".fpk",
            CamoFpk = camoFiles.CamoFpk,
            CamoFv2 = camoFiles.CamoFv2,
            DisplayName = L(label),
        };
        foreach (var look in o.LookOrder.Concat([FatiguesLooks.Naked, FatiguesLooks.Normal, FatiguesLooks.Scarf]).Distinct())
        {
            if (!o.Looks.HasFlag(look)) continue;
            var l = look switch
            {
                FatiguesLooks.Normal => Fatigue("/Assets/tpp/pack/player/parts/plparts_normal", "normal", main1!),
                FatiguesLooks.Scarf => Fatigue("/Assets/tpp/pack/player/parts/plparts_normal_scarf", "scarf", main1!),
                _ => Fatigue("/Assets/tpp/pack/player/parts/plparts_naked", "naked", naked!),
            };
            looks.Add(l with { Default = look == o.DefaultLook });
        }
        if (looks.Count == 0) throw new ArgumentException("No look chosen.");
        // V Framework starts on the base look unless a variant says default; a default base needs no flag.
        if (looks[0].Default) looks[0] = looks[0] with { Default = false };

        var spec = new OutfitSpec
        {
            Key = o.Key,
            NameLang = L("name"),
            RealNameLang = L("real"),
            InfoLang = L("info"),
            IconFtexPath = iconPath,
            Develop = new DevelopRow { Grade = 1, GmpCost = 0, InitiallyAvailable = true },
        };
        spec.Header.Add($"{o.Name}: V Framework outfit converted from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}.");
        spec.Header.Add($"Behaves like: {preset.Label}. Looks: {string.Join(", ", looks.Select(l => text.Get(l.DisplayName!, "eng")))}.");
        var heads = o.HeadOptions?.ToList() ?? VanillaHeads.For(PlayerBranch.Snake);
        spec.Branches[PlayerBranch.Snake] = new OutfitBranch
        {
            Base = looks[0],
            HeadOptions = heads,
            Variants = looks.Skip(1).Select(v => v with { HeadOptions = heads }).ToList(),
            // Issue #69: sparks come from the ported packages, not from the preset.
            Abilities = preset.Abilities with { SprintSpark = Svfm.Core.VFramework.SprintSpark.In(files) },
            CamoBonusType = o.CamoBonusType,
            CamoBonusValues = o.CamoBonusType != null ? null
                : new Dictionary<string, int>(o.CamoBonusValues ?? StartCamoValues(mod, camo)),
        };
        var item = new Project.OutfitItem
        {
            Id = id, Key = o.Key, Prefix = prefix, Spec = spec, Title = o.Name,
        };
        if (o.CamoBonusType == null && o.CamoBonusValues == null)
            item.CamoFromDescription.AddRange(Knowledge.GameKnowledge.FillCamoFromDescription(spec.Branches[PlayerBranch.Snake].CamoBonusValues!, ModInfoText(mod, [camo.LangInfo])));
        foreach (var (p, d) in files) item.Files[p] = d;
        foreach (var (lid, per) in text.Texts) foreach (var (lang, t) in per) item.SetText(lid, lang, t);
        item.Notes.AddRange(notes);
        return (item, icon, oldIcon);
    }

    /// <summary>
    /// The camo texture of the port's fatigues look, for a cropped icon: the moved texture when the .fv2 names it, else the
    /// first texture the .fv2 names (a mod that points the .fv2 at its own textures), from the port's pack or loose files.
    /// </summary>
    /// <summary>The mod's camo texture as the port carries it (for the camo-crop icon), read back from a ported item's files.</summary>
    /// <param name="game">The game.</param>
    /// <param name="files">The ported item's files.</param>
    /// <param name="mod">The source mod.</param>
    /// <param name="camo">The camo the mod replaces.</param>
    /// <param name="prefix">The item's file prefix.</param>
    /// <returns>The decoded texture.</returns>
    public static RgbaImage CamoTextureOf(GameData game, IReadOnlyDictionary<string, byte[]> files, MgsvMod mod, CamoInfo camo, string prefix)
    {
        var f = files.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var snake = camo.Snake ?? throw new ArgumentException($"{camo.Name} has no Snake fatigues camo.");
        string fpk = $"{Fova}plfova_{prefix}_main1.fpk";
        // No fatigues package of its own: the mod's pack, else the game's (a mod may not ship one).
        if (!f.ContainsKey(fpk)) return CamoTexture(game, f, null, mod, snake.NormalScarf[..^4], "");
        return CamoTexture(game, f, new CamoPortResult { CamoFpk = fpk, CamoFv2 = "" }, mod, snake.NormalScarf[..^4], $"/Assets/tpp/chara/sna/Pictures/{prefix}_main1_def_bsm.ftex");
    }

    /// <summary>The camo-crop icon for a texture: the vanilla icon's frame filled with a piece of the fabric.</summary>
    /// <param name="game">The game (for the vanilla icon's frame).</param>
    /// <param name="camo">The camo the mod replaces.</param>
    /// <param name="texture">The camo texture.</param>
    /// <param name="crop">Where to cut, and the tone.</param>
    /// <param name="zoom">SIZE: 1 cuts the icon window as it is; more draws the fabric bigger.</param>
    /// <returns>The 256x128 icon.</returns>
    public static RgbaImage CropIcon(GameData game, CamoInfo camo, RgbaImage texture, IconChoice.CamoCrop crop, float zoom = 1f)
    {
        var old = camo.Icon != null ? game.TryReadTexture(camo.Icon + ".ftex") : null;
        if (old == null) throw new FileNotFoundException($"vanilla icon {camo.Icon} not found (needed for the frame mask)");
        return SuitIcon.FromCamoTexture(texture, SuitIcon.MaskFrom(old), (crop.X, crop.Y), crop.Contrast, crop.Brightness, zoom: zoom);
    }

    private static RgbaImage CamoTexture(GameData game, Dictionary<string, byte[]> files, CamoPortResult? main1, MgsvMod mod, string main1Vanilla, string moved)
    {
        // Normal and Scarf not ported: crop the mod's fatigues pack, else the vanilla one (as CamoPort does).
        if (main1 == null)
            return TexturePack.Read(mod.ArchiveFiles.TryGetValue(main1Vanilla + ".pftxs", out var mp) ? mp
                : game.TryRead(main1Vanilla + ".pftxs") ?? throw new FileNotFoundException($"No texture pack for {main1Vanilla}.")).Textures.FirstOrDefault()?.Decode() ?? throw new InvalidDataException($"The texture pack for {main1Vanilla} holds no texture.");
        var pack = TexturePack.Read(files[main1.CamoFpk[..^4] + ".pftxs"]);
        var fv2 = new Fv2File(FoxPackage.Read(files[main1.CamoFpk]).Files.Single(f => f.Extension == "fv2").Data);
        var names = fv2.Textures.ToList();
        ulong movedCode = Hashing.FoxHash.PathCode64(moved);
        foreach (var code in names.Contains(movedCode) ? [movedCode] : names)
        {
            if (pack.Find(code) is { } t) return t.Decode();
            var ftex = files.Keys.FirstOrDefault(k => k.EndsWith(".ftex", StringComparison.Ordinal) && Hashing.FoxHash.PathCode64(k) == code);
            if (ftex != null)
            {
                var mips = Enumerable.Range(1, 6).Where(k => files.ContainsKey($"{ftex[..^5]}.{k}.ftexs")).ToDictionary(k => k, k => files[$"{ftex[..^5]}.{k}.ftexs"]);
                return FtexTexture.Read(files[ftex]).DecodeLargestAvailable(mips);
            }
        }
        return pack.Textures.FirstOrDefault()?.Decode() ?? throw new InvalidDataException("The camo texture pack holds no texture.");
    }

    /// <summary>The .parts file a vanilla body package loads (the single .parts in its .fpkd).</summary>
    public static string PartsPathOf(GameData game, string package)
    {
        var fpkd = FoxPackage.Read(game.Read(package + ".fpkd"));
        return fpkd.Files.Single(f => f.Extension == "parts").Path;
    }

    private static string ToPascal(string id) =>
        string.Concat(id.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
}
