using Svfm.Core.VFramework;
using Svfm.Core.Archives;
using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;

namespace Svfm.Core.Project;

/// <summary>What opening a mod in the app produced: a project to edit, or the reason there is none yet.</summary>
public sealed class IntakeResult
{
    /// <summary>The project, or null when nothing in the mod can be ported yet.</summary>
    public ModProject? Project { get; set; }
    /// <summary>The mod's name, author and file count, for the "Found" screen.</summary>
    public required string ModName { get; init; }
    public required string ModAuthor { get; init; }
    public required ModClassification Classification { get; init; }
    /// <summary>One line per thing found: what it is and whether the app can port it.</summary>
    public List<IntakeFinding> Findings { get; } = [];
    /// <summary>True for a mod of only new textures: they belong to another mod's model, which the user can choose to port them together.</summary>
    public bool NeedsBaseMod { get; set; }
    /// <summary>The read-only summary of the mod for the Found screen.</summary>
    public List<FoundFact> Facts { get; } = [];
    /// <summary>The models the Found screen lists, each with the item it becomes.</summary>
    public List<FoundModel> Models { get; } = [];
    /// <summary>Suit-list icon of each outfit item (by item ID), for previews.</summary>
    public Dictionary<string, RgbaImage> Icons { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, Func<string, string, string, IconChoice?, (ProjectItem item, RgbaImage? icon)>> Makers { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The items whose maker only draws the icon again and gives back the item itself (the head options of a Snake face
    /// replacer, of a DD head replacer and of a body with heads inside). Such an item cannot be duplicated.
    /// </summary>
    internal HashSet<string> IconOnlyMakers { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, CamoRecipe> Recipes { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, IconSettings> IconStates { get; } = new(StringComparer.Ordinal);
    /// <summary>The items whose rendered icon uses the DD women's chest framing (see BodyConverter.ToItem).</summary>
    internal HashSet<string> WomenFramed { get; } = new(StringComparer.Ordinal);
    internal GameData? Game { get; set; }
    /// <summary>The mod and the vanilla bodies of each body port (<see cref="OutfitCopies"/> makes its copies from them).</summary>
    public Dictionary<string, BodySource> BodySources { get; } = new(StringComparer.Ordinal);

    /// <summary>The item's icon settings (style, slot drawn, crop position and tone).</summary>
    /// <param name="item">The item.</param>
    /// <returns>The current settings, or null when the item has no editable icon.</returns>
    public IconSettings? IconOf(ProjectItem item) => IconStates.GetValueOrDefault(item.Id);

    /// <summary>The settings the item starts with and the reset button goes back to.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The default settings, or null.</returns>
    public IconSettings? DefaultIconOf(ProjectItem item) => Recipes.TryGetValue(item.Id, out var r) ? IconSettings.DefaultFor(r.Camo) : IconStates.ContainsKey(item.Id) ? IconSettings.ChestDefault : null;

    /// <summary>Records icon settings without making the icon (the app applies them after a pause while a slider moves).</summary>
    /// <param name="item">The item.</param>
    /// <param name="s">The settings.</param>
    public void SetIconState(ProjectItem item, IconSettings s) => IconStates[item.Id] = s;

    /// <summary>The framing a rendered chest icon of an item is drawn with: the one the port uses, moved by the settings.</summary>
    /// <param name="item">The item.</param>
    /// <param name="s">The icon settings.</param>
    /// <returns>The framing.</returns>
    public Rendering.IconFraming ChestFraming(ProjectItem item, IconSettings s) =>
        ((item as ExistingItem)?.Model ?? item) switch
        {
            BuddyOutfitItem b => Rendering.IconFraming.ForBuddy(b.Spec.Buddy),
            _ => WomenFramed.Contains(item.Id) ? Rendering.IconFraming.DdFemale : new Rendering.IconFraming(),
        } with { OffsetX = s.OffsetX, OffsetY = s.OffsetY, Zoom = s.Zoom };

    /// <summary>True when the item's icon can be a crop of a texture: every outfit (issue #32).</summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether the crop is available.</returns>
    public bool CanCrop(ProjectItem item) => Recipes.ContainsKey(item.Id) || item is OutfitItem;

    /// <summary>True for a camo replacer: its crop uses the mod's camo texture, and its render can draw either slot.</summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether the item replaces a camo.</returns>
    public bool IsCamoReplacer(ProjectItem item) => Recipes.ContainsKey(item.Id);

    /// <summary>The main texture of an outfit that is not a camo replacer, scaled to the 2048 crop space (kept per item).</summary>
    private readonly Dictionary<string, RgbaImage> _mainTextures = new(StringComparer.Ordinal);

    /// <summary>
    /// The texture a crop cuts from: the mod's camo texture for a camo replacer, else the main texture that the outfit's
    /// model points to (a game texture when the mod has none of its own).
    /// </summary>
    private RgbaImage CropTextureOf(ProjectItem item)
    {
        if (Recipes.TryGetValue(item.Id, out var r)) return r.Texture ??= CamoConverter.CamoTextureOf(Game!, item.Files, r.Mod, r.Camo, item.Prefix);
        if (_mainTextures.TryGetValue(item.Id, out var t)) return t;
        var o = item as OutfitItem ?? throw new InvalidOperationException("Only an outfit can have a camo crop icon.");
        var game = Game ?? throw new InvalidOperationException("The game files are needed to crop a texture.");
        var (who, look) = Rendering.OutfitPreview.DefaultLook(o);
        var tex = SuitRender.MainTexture(SuitRender.Scene(game, PathDictionary.Bundled, Rendering.OutfitPreview.InputFor(game, o, who, look)))
            ?? throw new InvalidOperationException("The outfit's model names no texture that the tool can find.");
        return _mainTextures[item.Id] = tex.Width == 2048 && tex.Height == 2048 ? tex : Resize.Bilinear(tex, 2048, 2048);
    }

    /// <summary>The camo-crop icon for the given position and tone, without changing the item (fast: for dragging a slider).</summary>
    /// <param name="item">The item.</param>
    /// <param name="s">The crop settings.</param>
    /// <returns>The icon image.</returns>
    public RgbaImage PreviewCrop(ProjectItem item, IconSettings s)
    {
        var texture = CropTextureOf(item);
        return Recipes.TryGetValue(item.Id, out var r)
            ? CamoConverter.CropIcon(Game!, r.Camo, texture, new IconChoice.CamoCrop(s.CropX, s.CropY, s.Contrast, s.Brightness), s.Zoom)
            : SuitIcon.FromCamoTexture(texture, SuitIcon.DrawnMask(), (s.CropX, s.CropY), s.Contrast, s.Brightness, zoom: s.Zoom);
    }

    /// <summary>How far the crop window can move (the cropped texture's size less the 256x128 window).</summary>
    /// <param name="item">The item.</param>
    /// <returns>The largest X and Y.</returns>
    public (int x, int y) CropBounds(ProjectItem item)
    {
        var t = CropTextureOf(item);
        return (Math.Max(0, t.Width - 256), Math.Max(0, t.Height - 128));
    }

    /// <summary>Makes the item's icon from the settings and puts it in the item's files (replacing the old icon).</summary>
    /// <param name="item">The item.</param>
    /// <param name="s">The settings.</param>
    /// <returns>The new icon image.</returns>
    public RgbaImage ApplyIcon(ProjectItem item, IconSettings s)
    {
        RgbaImage icon;
        if (s.Style == IconStyle.Game) return SetGameIcon(item, s.GamePath ?? throw new InvalidOperationException("No game icon chosen."), s);
        if (IconLockOf(item) is { } why) throw new InvalidOperationException(why);
        EnsureOwnIconPath(item);
        if (s.Style == IconStyle.Custom) icon = Scaled(CustomIcons[$"{item.Id}#{s.Custom}"], s.Scale);
        else if (s.Style == IconStyle.CamoCrop) icon = PreviewCrop(item, s);
        else if (s.Style == IconStyle.Outline) icon = RenderOutline(item, s);
        else if (item is ExistingItem x) icon = RenderExisting(x, s);
        else
        {
            // A chest render needs the whole port's models: run the converter again with the render choice, and take its icon.
            var (_, img) = Makers[item.Id](item.Id, item.Key!, item.Prefix, new IconChoice.Render(s.WithScarf, s.OffsetX == 0 && s.OffsetY == 0 && s.Zoom == 1f ? null : new Rendering.IconFraming { OffsetX = s.OffsetX, OffsetY = s.OffsetY, Zoom = s.Zoom }));
            icon = img ?? throw new InvalidOperationException("The render made no icon.");
            // Only the icon is wanted. A maker does not add the item that it makes to the project.
        }
        WriteIconFiles(item, icon);
        Icons[item.Id] = icon;
        IconStates[item.Id] = s;
        // One icon file is one icon in game: the other items of an existing mod that use it get the generated icon too
        // (an upload does the same in SetCustomIcon).
        if (item is ExistingItem && s.Style != IconStyle.Custom)
            foreach (var o in IconSharersOf(item)) { WriteIconFiles(o, icon); Icons[o.Id] = icon; IconStates[o.Id] = s; }
        return icon;
    }

    /// <summary>
    /// The generated icon of an item of a mod that is already a V Framework mod: drawn from the model the 3D preview reads
    /// from its script (an outfit's default look with its camo, a costume's package, a head option's head model), and
    /// fitted to the size of the icon the item has now (a staff face picture is not 256x128).
    /// </summary>
    private RgbaImage RenderExisting(ExistingItem x, IconSettings s)
    {
        var game = Game ?? throw new InvalidOperationException("The game files are needed to draw an icon.");
        if (Project?.Existing is { } data) ExistingMod.LoadPreviewTextures(data, x);
        var framing = s.OffsetX == 0 && s.OffsetY == 0 && s.Zoom == 1f ? null : new Rendering.IconFraming { OffsetX = s.OffsetX, OffsetY = s.OffsetY, Zoom = s.Zoom };
        RgbaImage icon;
        switch (x.Model)
        {
            case OutfitItem o:
                var (who, look) = Rendering.OutfitPreview.DefaultLook(o);
                icon = SuitRender.Icon(game, PathDictionary.Bundled, Rendering.OutfitPreview.InputFor(game, o, who, look) with { Framing = framing });
                break;
            case BuddyOutfitItem b:
                icon = BuddyConverter.RenderFromFiles(game, PathDictionary.Bundled, b, new IconChoice.Render(s.WithScarf, framing));
                break;
            case HeadOptionItem h:
                var fpk = h.Spec.Branches.OrderBy(k => k.Key).Select(k => k.Value.Fpk).FirstOrDefault(f => f is { Length: > 0 });
                string stem = fpk == null ? "" : fpk.EndsWith(".fpk", StringComparison.Ordinal) ? fpk[..^4] : fpk;
                var head = h.Files.TryGetValue(stem + ".fpk", out var hb) ? SuitRender.HeadModel(FoxPackage.Read(hb)) : null;
                icon = DdHeadConverter.Icon(game, head ?? throw new InvalidOperationException("The head option has no model of its own to draw an icon from."));
                break;
            case WeaponItem:
                icon = RenderOutline(x, s);
                break;
            default:
                throw new InvalidOperationException("The script names no model the tool can draw an icon from.");
        }
        return Icons.GetValueOrDefault(x.Id) is { } now && (now.Width != icon.Width || now.Height != icon.Height) ? Cover(icon, now.Width, now.Height) : icon;
    }

    /// <summary>The picture scaled to cover a w x h window, centred, and cut to it.</summary>
    private static RgbaImage Cover(RgbaImage picture, int w, int h)
    {
        float scale = MathF.Max((float)w / picture.Width, (float)h / picture.Height);
        int sw = Math.Max(w, (int)MathF.Ceiling(picture.Width * scale)), sh = Math.Max(h, (int)MathF.Ceiling(picture.Height * scale));
        return (sw == picture.Width && sh == picture.Height ? picture : Resize.Bilinear(picture, sw, sh)).Crop((sw - w) / 2, (sh - h) / 2, w, h);
    }

    /// <summary>An outline picture of a weapon's model (side view), with the chosen attachments drawn and the chosen size.</summary>
    /// <param name="item">The weapon (or an existing weapon).</param>
    /// <param name="s">The settings: the angle, the size and the attachments hidden.</param>
    /// <returns>The 256x128 icon.</returns>
    public static RgbaImage RenderOutline(ProjectItem item, IconSettings s)
    {
        // A ported thrown item has no package of its own; its model is a new entry of the common package (SupportWeaponConverter.AddTo).
        string? pkg = (item is ExistingItem x ? x.Model : item) switch
        {
            WeaponItem w => w.Spec.FpkPath,
            SupportWeaponItem t => t.Spec.FpkPath is { Length: > 0 } f ? f : SupportWeaponConverter.CommonPackage + ".fpk",
            _ => null,
        };
        if (pkg == null || !item.Files.TryGetValue(pkg, out var bytes)) throw new InvalidOperationException("The item has no model to draw an icon from.");
        var off = Rendering.WeaponAttachments.HiddenKeys(s.Parts, s.HiddenModels);
        // Only the receiver model, as in game and in the preview (WeaponAttachments.ReceiverModel).
        string? receiver = item is WeaponItem rw ? Rendering.WeaponAttachments.ReceiverModel(rw.Spec, item.Files) : null;
        var models = Formats.FoxPackage.Read(bytes).Files.Where(f => f.Extension == "fmdl" && (receiver == null || f.Path == receiver) && !Rendering.WeaponAttachments.IsHidden(f.Path, off)).Select(f => Formats.FmdlModel.Read(f.Data)).ToList();
        if (models.Count == 0) throw new InvalidOperationException("No model found to draw an icon from.");
        var hidden = Rendering.WeaponAttachments.HiddenGroups(off);
        var o = new Rendering.OutlineIconOptions { Zoom = s.Zoom, OffsetX = s.OffsetX * 256f, OffsetY = s.OffsetY * 128f };
        // The side view is a turn of 270 degrees about the model's up axis; the default look keeps the side view as it was.
        if (s.Yaw != 0 || s.Pitch != 0) o = o with { View = Rendering.OutlineView.Custom, Yaw = 270f + s.Yaw, Pitch = s.Pitch };
        return Rendering.OutlineIconRenderer.Render(models, o, hidden);
    }

    /// <summary>The icon path (without extension) the item's R&amp;D row points at, or null when it has none.</summary>
    public static string? IconPathOf(ProjectItem item) => item.OwnIconPath;

    /// <summary>
    /// The icon path of an existing item, spelled as the item's files are keyed, when the mod ships the icon file;
    /// null when the icon is a game icon or a file the mod does not carry (an icon of its own then goes to a new path, see EnsureOwnIconPath).
    /// </summary>
    public static string? ShippedIconOf(ExistingItem x)
    {
        if (x.IconPath is not { Length: > 0 } p) return null;
        p = ExistingMod.IconStem(p);
        return x.Files.ContainsKey(p + ".ftex") ? p : null;
    }

    /// <summary>Why the icon of an item from an existing V Framework mod can't be changed, or null when an uploaded picture can replace it.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The reason, or null.</returns>
    public string? IconLockOf(ProjectItem item)
    {
        if (item is not ExistingItem x) return null;
        if (x.IconPath is not { Length: > 0 }) return "This item comes from a mod that is already a V Framework mod, and its script names no icon.";
        // A shipped icon file is written over; any other icon (a game icon, a file of another mod) is changed by a new
        // path in the script, which needs every place the script names the icon to be plain text.
        if (ShippedIconOf(x) == null && (x.IconFields.Count == 0 || x.IconFields.Any(f => f.Locked != null)))
            return "This item uses an icon that its mod does not carry, and its script names that icon in a way the tool can't change. The build keeps that icon.";
        return null;
    }

    /// <summary>Why a game icon can't be picked for the item: a game icon is a new path in the script, so every place the
    /// script names the icon must be plain text (a shipped file can still be written over by an upload).</summary>
    /// <param name="item">The item.</param>
    /// <returns>The reason, or null when a game icon can be picked.</returns>
    public string? GameIconLockOf(ProjectItem item) => IconLockOf(item)
        ?? (item is ExistingItem x && (x.IconFields.Count == 0 || x.IconFields.Any(f => f.Locked != null))
            ? "Its script names the icon in a way the tool can't change, so the icon can only be replaced by an upload." : null);

    /// <summary>The other items of an existing V Framework mod that use the same icon file (the grades of one weapon, the heads that share a staff picture).</summary>
    /// <param name="item">The item.</param>
    /// <returns>The other items; an uploaded picture becomes their icon too.</returns>
    public List<ExistingItem> IconSharersOf(ProjectItem item) =>
        item is ExistingItem x && IconPathOf(x) is { } stem
            ? Project?.Items.OfType<ExistingItem>().Where(o => o != x && IconPathOf(o) is { } s && s.Equals(stem, StringComparison.OrdinalIgnoreCase)).ToList() ?? []
            : [];

    /// <summary>Whether an icon path is one SetSlotIcon makes: it ends in "_slot" and a number. A mod's own sheet can have "_slot" inside its name.</summary>
    /// <param name="path">The icon path, without extension.</param>
    /// <returns>True for a slot icon path.</returns>
    public static bool IsSlotIconPath(string path)
    {
        int at = path.LastIndexOf("_slot", StringComparison.Ordinal);
        return at >= 0 && int.TryParse(path[(at + 5)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private readonly Dictionary<string, string> _ownIconPath = new(StringComparer.Ordinal);

    internal static void SetIconPath(ProjectItem item, string path) => item.OwnIconPath = path;

    /// <summary>After a game icon, the item's own icon path is put back before an icon of its own is written.</summary>
    private void EnsureOwnIconPath(ProjectItem item)
    {
        if (item is ExistingItem x)
        {
            // An existing item's own icon is the file its mod ships; when the mod ships none, a new file at a path of the item's own.
            string mine = ShippedIconOf(x) ?? $"/Assets/tpp/ui/texture/EquipIcon/svfm/ui_{x.Prefix}_alp";
            if (IconPathOf(x) != mine) SetIconPath(x, mine);
            return;
        }
        if (_ownIconPath.TryGetValue(item.Id, out var own)) { if (IconPathOf(item) != own) SetIconPath(item, own); return; }
        // An item that starts on a game icon (the playable Quiet outfit) carries no icon file: its own icon goes to a new path, never over the game's.
        if (IconPathOf(item) is { } now && !item.Files.ContainsKey(now + ".ftex"))
            SetIconPath(item, _ownIconPath[item.Id] = $"/Assets/tpp/ui/texture/EquipIcon/svfm/ui_{item.Prefix}_alp");
    }

    private Dictionary<string, string>? _gameIconNames;
    /// <summary>The in-game name and category of a game icon ("Name – Category", issues #16, #24), or null when no game item uses it.</summary>
    /// <param name="path">The icon path, without extension.</param>
    public string? GameIconName(string path) =>
        (_gameIconNames ??= Knowledge.GameKnowledge.GameIconNames(Game)).GetValueOrDefault(path);

    /// <summary>Whether a game icon matches the search text: each word is in its path or in its in-game name.</summary>
    /// <param name="path">The icon path.</param>
    /// <param name="name">The in-game name, or null.</param>
    /// <param name="text">The search text (empty: all match).</param>
    public static bool GameIconMatches(string path, string? name, string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => path.Contains(w, StringComparison.OrdinalIgnoreCase) || (name?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>Game icons a mod can point its R&amp;D row at: the EquipIcon pictures whose path or in-game name has <paramref name="text"/>.</summary>
    /// <param name="text">Part of the path or the in-game name (empty: the first ones).</param>
    /// <param name="max">How many to return (default: all).</param>
    /// <returns>Paths without extension, grouped by kind (named kinds first, then the icons with no in-game name), each group by title.</returns>
    public List<string> SearchGameIcons(string text, int max = int.MaxValue)
    {
        var game = Game;
        if (game == null) return [];
        var found = new List<string>();
        foreach (var p in Archives.PathDictionary.Bundled.PathsUnder("/Assets/tpp/ui/texture/EquipIcon/"))
        {
            if (IsPlaceholderIcon(p)) continue;
            if (!GameIconMatches(p, GameIconName(p), text)) continue;
            if (!game.Contains(p + ".ftex")) continue;
            found.Add(p);
        }
        return found.OrderBy(p => GameIconKind(p) == "Other").ThenBy(GameIconKind, StringComparer.Ordinal)
            .ThenBy(p => GameIconName(p) ?? p[(p.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>The kind of a game icon (the category after the dash of its in-game name, e.g. "Outfit", "Weapon"), or "Other" when it has no name.</summary>
    public string GameIconKind(string path) => GameIconName(path) is { } n && n.LastIndexOf(" – ", StringComparison.Ordinal) is > 0 and var i ? n[(i + 3)..] : "Other";

    /// <summary>Whether a game icon is a placeholder that no item may use (issue #53): dev_*, itm_icon_alp, equip_blank_icon_alp, wp_icon_prt_*.</summary>
    private static bool IsPlaceholderIcon(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.StartsWith("dev_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("wp_icon_prt_", StringComparison.OrdinalIgnoreCase)
            || file.Equals("itm_icon_alp", StringComparison.OrdinalIgnoreCase) || file.Equals("equip_blank_icon_alp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The picture of a game icon (for the picker's preview).</summary>
    public RgbaImage? GameIconImage(string path) => Game?.TryReadTexture(path + ".ftex");

    /// <summary>The game's icon of a vanilla suit by camo name (e.g. "TIGERSTRIPE"), or null when the suit or its icon is not found (issue #28).</summary>
    public RgbaImage? VanillaSuitIcon(string suit) => GameKnowledge.CamoByName(suit)?.Icon is { Length: > 0 } p ? GameIconImage(p) : null;

    /// <summary>Points the item's R&amp;D row at an icon already in the game: the mod carries no icon of its own for it.</summary>
    /// <param name="item">The item.</param>
    /// <param name="path">The game icon's path, without extension.</param>
    /// <param name="state">The settings to record (null: the current ones with the Game style).</param>
    /// <returns>The picture.</returns>
    public RgbaImage SetGameIcon(ProjectItem item, string path, IconSettings? state = null)
    {
        if (GameIconLockOf(item) is { } why) throw new InvalidOperationException(why);
        var img = GameIconImage(path) ?? throw new InvalidDataException($"The game has no icon {path}.");
        // An existing item may have no path of its own yet (its script names a game icon): the script is pointed at this one.
        if (IconPathOf(item) is { } now)
        {
            if (item is not ExistingItem && item.Files.ContainsKey(now + ".ftex")) _ownIconPath.TryAdd(item.Id, now);
            foreach (var k in item.Files.Keys.Where(k => k == now + ".ftex" || k.StartsWith(now + ".", StringComparison.Ordinal)).ToList()) item.Files.Remove(k);
        }
        else if (item is not ExistingItem) throw new InvalidOperationException("This item has no icon.");
        SetIconPath(item, path);
        Icons[item.Id] = img;
        IconStates[item.Id] = state ?? (IconStates.GetValueOrDefault(item.Id) ?? IconSettings.ChestDefault) with { Style = IconStyle.Game, GamePath = path, Scale = 1f };
        return img;
    }

    private static void WriteIconFiles(ProjectItem item, RgbaImage icon)
    {
        if (IconPathOf(item) is not { } path) return;
        foreach (var k in item.Files.Keys.Where(k => k == path + ".ftex" || k.StartsWith(path + ".", StringComparison.Ordinal)).ToList()) item.Files.Remove(k);
        var ft = Conversion.SuitIcon.ToGameFiles(icon);
        item.Files[path + ".ftex"] = ft.Ftex;
        foreach (var (n, d) in ft.Ftexs) item.Files[$"{path}.{n}.ftexs"] = d;
    }

    internal Dictionary<string, RgbaImage> CustomIcons { get; } = new(StringComparer.Ordinal);

    /// <summary>Uses a picture the user gives as the item's icon: scaled to cover the 2:1 icon window, centred, and (for outfits) put in the suit-icon frame.</summary>
    /// <param name="item">The item.</param>
    /// <param name="picture">Any picture.</param>
    /// <param name="fromView">The picture is the view of the 3D preview (Generate mode of a head option), not an upload.</param>
    /// <returns>The settings that now describe the icon.</returns>
    public IconSettings SetCustomIcon(ProjectItem item, RgbaImage picture, bool fromView = false)
    {
        RgbaImage icon;
        // An existing item's icon keeps the size the mod ships (a staff face picture is not 256x128).
        int w = 256, h = 128;
        if (item is ExistingItem x)
        {
            if (IconLockOf(x) is { } why) throw new InvalidOperationException(why);
            if (Icons.GetValueOrDefault(x.Id) is { } shipped) { w = shipped.Width; h = shipped.Height; }
        }
        if ((item is ExistingItem e ? e.Model : item) is OutfitItem && w == 256 && h == 128) icon = Conversion.SuitIcon.FromPicture(picture);
        else icon = Cover(picture, w, h);
        EnsureOwnIconPath(item);
        int n = CustomIcons.Keys.Count(k => k.StartsWith(item.Id + "#", StringComparison.Ordinal)) + 1;
        CustomIcons[$"{item.Id}#{n}"] = icon;
        var s = (IconStates.GetValueOrDefault(item.Id) ?? IconSettings.ChestDefault) with { Style = IconStyle.Custom, Custom = n, FromView = fromView, Scale = 1f };
        ApplyIcon(item, s);
        // One icon file is one icon in game: every item that uses it gets the picture, so the editor shows what the build writes.
        foreach (var o in IconSharersOf(item))
        {
            int m = CustomIcons.Keys.Count(k => k.StartsWith(o.Id + "#", StringComparison.Ordinal)) + 1;
            CustomIcons[$"{o.Id}#{m}"] = icon;
            ApplyIcon(o, (IconStates.GetValueOrDefault(o.Id) ?? IconSettings.ChestDefault) with { Style = IconStyle.Custom, Custom = m, FromView = fromView, Scale = 1f });
        }
        return s;
    }

    /// <summary>
    /// The SIZE of a picture icon (issue #131; owner, Nov 22: "the icon that's already generated being adjusted"): an upload,
    /// a head picture from the view, a game icon or the icon the mod packaged grows or shrinks about its centre. A game or
    /// packaged icon first becomes a picture of the item's own, so the mod then carries the resized icon.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="scale">The size: 1 is the picture as made.</param>
    /// <returns>The settings that now describe the icon.</returns>
    public IconSettings ScalePicture(ProjectItem item, float scale)
    {
        IconSettings? first = null;
        // One icon file is one icon in game: the items of an existing mod that share it get the same size.
        foreach (var it in IconSharersOf(item).Cast<ProjectItem>().Prepend(item))
        {
            var s = IconStates.GetValueOrDefault(it.Id);
            if (s is not { Style: IconStyle.Custom })
            {
                if (Icons.GetValueOrDefault(it.Id) is not { } shown) continue;
                int n = CustomIcons.Keys.Count(k => k.StartsWith(it.Id + "#", StringComparison.Ordinal)) + 1;
                CustomIcons[$"{it.Id}#{n}"] = shown;
                s = (s ?? IconSettings.ChestDefault) with { Style = IconStyle.Custom, Custom = n, FromView = false, GamePath = null };
            }
            s = s with { Scale = scale };
            ApplyIcon(it, s);
            first ??= s;
        }
        return first ?? throw new InvalidOperationException("This item has no icon.");
    }

    /// <summary>The picture grown or shrunk about its centre, at its own size. A smaller picture leaves a clear border.</summary>
    public static RgbaImage Scaled(RgbaImage p, float scale)
    {
        if (scale == 1f) return p;
        int w = Math.Max(1, (int)MathF.Round(p.Width * scale)), h = Math.Max(1, (int)MathF.Round(p.Height * scale));
        var big = Resize.Bilinear(p, w, h);
        var o = new RgbaImage(p.Width, p.Height);
        int dx = (p.Width - w) / 2, dy = (p.Height - h) / 2, x0 = Math.Max(0, dx), x1 = Math.Min(p.Width, dx + w);
        for (int y = Math.Max(0, dy); y < Math.Min(p.Height, dy + h); y++)
            big.Pixels.AsSpan(((y - dy) * w + x0 - dx) * 4, (x1 - x0) * 4).CopyTo(o.Pixels.AsSpan((y * p.Width + x0) * 4));
        return o;
    }

    /// <summary>The finished icon picture of a custom icon (for saving it in a project file).</summary>
    public RgbaImage? CustomIconImage(ProjectItem item, int n) => CustomIcons.GetValueOrDefault($"{item.Id}#{n}");

    /// <summary>Loads a custom icon saved in a project file.</summary>
    public void LoadCustomIcon(ProjectItem item, int n, RgbaImage icon) => CustomIcons[$"{item.Id}#{n}"] = icon;

    /// <summary>
    /// The slot icon pictures (issue #88), by icon path. A look whose <c>IconFtexPath</c> is one of these paths shows
    /// that slot icon; a look with no path shows the outfit icon. A picture stays here after an undo, so a later
    /// state that points at it again gets its files back (<see cref="SyncSlotIcons"/>).
    /// </summary>
    internal Dictionary<string, RgbaImage> SlotIcons { get; } = new(StringComparer.Ordinal);

    /// <summary>The slots (looks) of an outfit in the order the editor lists them: the base look, then the variants of the first wearer.</summary>
    public static IReadOnlyList<Look> SlotLooks(OutfitItem o)
    {
        var b = o.Spec.Branches.OrderBy(k => k.Key).First().Value;
        return [b.Base, .. b.Variants];
    }

    /// <summary>Draws the icon of one slot from its look (or the camo crop of the settings) and makes it that slot's icon.</summary>
    /// <param name="o">The outfit.</param>
    /// <param name="slot">The slot: 0 is the base look, 1 the first variant.</param>
    /// <param name="s">The framing (or the crop) of the draft.</param>
    public RgbaImage RenderSlotIcon(OutfitItem o, int slot, IconSettings s)
    {
        var game = Game ?? throw new InvalidOperationException("The game files are needed to draw an icon.");
        var who = o.Spec.Branches.Keys.Order().First();
        var icon = s.Style == IconStyle.CamoCrop ? PreviewCrop(o, s)
            : SuitRender.Icon(game, PathDictionary.Bundled, Rendering.OutfitPreview.InputFor(game, o, who, SlotLooks(o)[slot]) with { Framing = ChestFraming(o, s) });
        SetSlotIcon(o, slot, icon);
        return icon;
    }

    /// <summary>Makes the picture the icon of one slot of the outfit, for every wearer. The other slots keep their icons.</summary>
    /// <param name="o">The outfit.</param>
    /// <param name="slot">The slot: 0 is the base look, 1 the first variant.</param>
    /// <param name="icon">The finished 256x128 icon.</param>
    public void SetSlotIcon(OutfitItem o, int slot, RgbaImage icon)
    {
        // A new path for each icon: an undo puts the old path back, and the old picture stays with it.
        string stem = o.Spec.IconFtexPath + "_slot";
        int n = SlotIcons.Keys.Count(k => k.StartsWith(stem, StringComparison.Ordinal)) + 1;
        string path = stem + n;
        SlotIcons[path] = icon;
        foreach (var (who, b) in o.Spec.Branches.ToList())
        {
            if (slot == 0) o.Spec.Branches[who] = b with { Base = b.Base with { IconFtexPath = path } };
            else if (slot - 1 < b.Variants.Count) { var vs = b.Variants.ToList(); vs[slot - 1] = vs[slot - 1] with { IconFtexPath = path }; o.Spec.Branches[who] = b with { Variants = vs }; }
        }
        SyncSlotIcons(o);
    }

    /// <summary>The slot icon picture at this path, or null when the path is not a slot icon (a game icon chosen as a look's logo).</summary>
    public RgbaImage? SlotIconImage(string path) => SlotIcons.GetValueOrDefault(path);

    /// <summary>Loads a slot icon saved in a project file. Only a path of the form <see cref="SetSlotIcon"/> makes is taken.</summary>
    /// <returns>False when the path is not a slot icon path.</returns>
    public bool LoadSlotIcon(string path, RgbaImage icon)
    {
        // The project file can be edited by hand, and the path becomes a file name in the build.
        if (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') || !IsSlotIconPath(path)) return false;
        SlotIcons[path] = icon;
        return true;
    }

    /// <summary>Makes the outfit's files hold the slot icons its looks point at, and no others (after an undo, a reset or an open).</summary>
    public void SyncSlotIcons(ProjectItem item)
    {
        if (item is not OutfitItem o || SlotIcons.Count == 0) return;
        var used = o.Spec.Branches.Values.SelectMany(b => b.Variants.Prepend(b.Base)).Select(l => l.IconFtexPath).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (path, icon) in SlotIcons)
        {
            foreach (var k in o.Files.Keys.Where(k => k.StartsWith(path + ".", StringComparison.Ordinal)).ToList()) o.Files.Remove(k);
            if (!used.Contains(path)) continue;
            var ft = SuitIcon.ToGameFiles(icon);
            o.Files[path + ".ftex"] = ft.Ftex;
            foreach (var (n, d) in ft.Ftexs) o.Files[$"{path}.{n}.ftexs"] = d;
        }
    }

    /// <summary>
    /// Sync of twins: gives <paramref name="to"/> the name, the description, the R&amp;D row and the icon of <paramref name="from"/>.
    /// The icon is copied as a picture of its own, at a path of its own: the playable Quiet outfit starts on the
    /// game's costume icon, which the mod must not write over.
    /// </summary>
    public void CopyTwin(ProjectItem from, ProjectItem to)
    {
        to.Title = from.Title;
        foreach (var (a, b) in new[] { (from.NameLangId, to.NameLangId), (from.InfoLangId, to.InfoLangId) })
            if (a != null && b != null)
            {
                if (from.Text.TryGetValue(a, out var per)) to.Text[b] = new Dictionary<string, string>(per); else to.Text.Remove(b);
            }
        // Issue #123: the texts of the own upgrade lines go with the row.
        // shortcut: the twins then share the line IDs; with Sync off later, an edit of one twin's line text clashes in the lang file. Give each twin its own IDs if that comes up.
        if (from.RdRow is { } d && to.RdRow != null) { to.RdRow = d; foreach (var id in d.UpgradeLines) if (from.Text.TryGetValue(id, out var per)) to.Text[id] = new Dictionary<string, string>(per); }
        if (Icons.GetValueOrDefault(from.Id) is not { } img || (Icons.GetValueOrDefault(to.Id) is { } now && SameImage(now, img))) return;
        int n = CustomIcons.Keys.Count(k => k.StartsWith(to.Id + "#", StringComparison.Ordinal)) + 1;
        CustomIcons[$"{to.Id}#{n}"] = img;
        ApplyIcon(to, (IconStates.GetValueOrDefault(to.Id) ?? IconSettings.ChestDefault) with { Style = IconStyle.Custom, Custom = n, FromView = false, Scale = 1f });
    }

    /// <summary>The same picture: same size and same pixels.</summary>
    public static bool SameImage(RgbaImage? a, RgbaImage? b) =>
        ReferenceEquals(a, b) || (a != null && b != null && a.Width == b.Width && a.Height == b.Height && a.Pixels.AsSpan().SequenceEqual(b.Pixels));

    /// <summary>Everything that makes up an item's icon now, so it can be put back (undo, reset).</summary>
    public sealed record IconSnap(IconSettings? State, RgbaImage? Image, Dictionary<string, byte[]> Files, string? Path = null);

    /// <summary>Takes the item's icon as it is.</summary>
    public IconSnap SnapIcon(ProjectItem item)
    {
        var files = new Dictionary<string, byte[]>();
        if (IconPathOf(item) is { } path)
            foreach (var kv in item.Files.Where(k => k.Key == path + ".ftex" || k.Key.StartsWith(path + ".", StringComparison.Ordinal))) files[kv.Key] = kv.Value;
        return new IconSnap(IconStates.GetValueOrDefault(item.Id), Icons.GetValueOrDefault(item.Id), files, IconPathOf(item));
    }

    /// <summary>Puts the icon back as a snapshot had it.</summary>
    public void RestoreIcon(ProjectItem item, IconSnap snap) => RestoreIcon(item, snap, true);

    private void RestoreIcon(ProjectItem item, IconSnap snap, bool sharers)
    {
        if (snap.Path != null && IconPathOf(item) is { } was && was != snap.Path)
        {
            foreach (var k in item.Files.Keys.Where(k => k == was + ".ftex" || k.StartsWith(was + ".", StringComparison.Ordinal)).ToList()) item.Files.Remove(k);
            SetIconPath(item, snap.Path);
        }
        // An existing item that had no icon file of its own (its script names a game icon) goes back to the script's path.
        else if (sharers && snap.Path == null && item is ExistingItem { IconPathWritten: not null } ex)
        {
            string was2 = ex.IconPathWritten;
            foreach (var k in item.Files.Keys.Where(k => k == was2 + ".ftex" || k.StartsWith(was2 + ".", StringComparison.Ordinal)).ToList()) item.Files.Remove(k);
            ex.IconPathWritten = null;
        }
        // A shipped icon whose file a game icon took away has no path until its files are back, so the snapshot's path counts.
        if ((IconPathOf(item) ?? snap.Path) is { } path)
        {
            foreach (var k in item.Files.Keys.Where(k => k == path + ".ftex" || k.StartsWith(path + ".", StringComparison.Ordinal)).ToList()) item.Files.Remove(k);
            foreach (var (k, v) in snap.Files) item.Files[k] = v;
        }
        if (snap.Image != null) Icons[item.Id] = snap.Image; else Icons.Remove(item.Id);
        if (snap.State != null) IconStates[item.Id] = snap.State; else IconStates.Remove(item.Id);
        // Items that share the icon file follow (Reset of one item gives the shipped icon back to all of them).
        if (!sharers || item is not ExistingItem || IconPathOf(item) is not { } stem) return;
        foreach (var o in IconSharersOf(item))
        {
            string own = IconPathOf(o)!;
            var files = snap.Files.ToDictionary(kv => own + kv.Key[stem.Length..], kv => kv.Value);
            RestoreIcon(o, snap with { Files = files, Path = null }, false);
        }
    }

    /// <summary>Adds another opened mod's items to this project (its converters, icons and requirements come with them).</summary>
    /// <param name="other">What opening the other mod found.</param>
    /// <exception cref="InvalidOperationException">The other mod has nothing to port, or an item would clash with one already here.</exception>
    public void Merge(IntakeResult other)
    {
        var p = Project ?? throw new InvalidOperationException("No project.");
        if (other.Project == null || other.Project.Items.Count == 0) throw new InvalidOperationException($"Nothing in \"{other.ModName}\" can be ported yet" + (other.NeedsBaseMod ? " (it only holds textures; add it as a part of the mod they are for instead)." : "."));
        // Items of a plain mod can go into an existing V Framework mod (the build adds them as a module of their own, see
        // ExistingMod.Build). An existing mod's items can't go into another project: its build edits its own scripts.
        if (other.Project.Existing != null) throw new InvalidOperationException($"\"{other.ModName}\" is already a V Framework mod: its items can't be added to another mod. Open it on its own, then add the other mod's items to it.");
        foreach (var i in other.Project.Items)
            if (p.Items.Any(x => x.Id == i.Id || x.Key == i.Key)) throw new InvalidOperationException($"\"{i.Title}\" has the same ID or key as an item already in this project ({i.Key}). Open the mods one at a time instead.");
        // A slot icon is keyed by its path, so a guest picture at a host path would take the host's picture away.
        foreach (var (k, v) in other.SlotIcons)
            if (SlotIcons.TryGetValue(k, out var mine) && !mine.Pixels.AsSpan().SequenceEqual(v.Pixels)) throw new InvalidOperationException($"\"{other.ModName}\" has a different slot icon at {k}, a path this project already uses. Remove one of the two slot icons first.");
        foreach (var i in other.Project.Items) p.Items.Add(i);
        foreach (var (k, v) in other.Makers) Makers[k] = v;
        IconOnlyMakers.UnionWith(other.IconOnlyMakers);
        foreach (var (k, v) in other.Recipes) Recipes[k] = v;
        foreach (var (k, v) in other.IconStates) IconStates[k] = v;
        WomenFramed.UnionWith(other.WomenFramed);
        foreach (var (k, v) in other.CustomIcons) CustomIcons[k] = v;
        foreach (var (k, v) in other.SlotIcons) SlotIcons[k] = v;
        foreach (var (k, v) in other.Icons) Icons[k] = v;
        foreach (var (k, v) in other._ownIconPath) _ownIconPath[k] = v;
        foreach (var (k, v) in other.BodySources) BodySources[k] = v;
        p.Requires.AddRange(other.Project.Requires.Where(r => !p.Requires.Contains(r)));
        Findings.AddRange(other.Findings);
        Facts.AddRange(other.Facts);
        Models.AddRange(other.Models);
    }

    /// <summary>Reads the icon each new item carries in its files (weapons, thrown items, heads, buddy costumes) so it can be shown.</summary>
    internal void ReadItemIcons()
    {
        if (Project == null) return;
        foreach (var item in Project.Items)
        {
            if (Icons.ContainsKey(item.Id) || item is ExistingItem || IconPathOf(item) is not { } path || !item.Files.TryGetValue(path + ".ftex", out var ftex)) continue;
            try
            {
                var parts = new Dictionary<int, byte[]>();
                for (int k = 1; k <= 6; k++) if (item.Files.TryGetValue($"{path}.{k}.ftexs", out var part)) parts[k] = part;
                Icons[item.Id] = FtexTexture.Read(ftex).DecodeLargestAvailable(parts);
                IconStates[item.Id] = IconSettings.ChestDefault;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or NotSupportedException) { }
        }
    }

    /// <summary>True when <see cref="Duplicate"/> can copy this item.</summary>
    public bool CanDuplicate(ProjectItem item) => (Project == null || CopyFamily.LockOf(Project, item) == null)
        && (item is ExistingItem x ? Project?.Existing != null && ExistingMod.DuplicateLockOf(x) == null : Makers.ContainsKey(item.Id) && !IconOnlyMakers.Contains(item.Id));

    /// <summary>
    /// Makes a copy of an item with its own ID, key, file prefix and files (the converter runs again with the new names),
    /// and adds it to the project after the original. The copy starts with the original's current name and text.
    /// </summary>
    /// <param name="item">The item to copy.</param>
    /// <param name="wantedId">The ID the copy must get (a saved project asks for the IDs it saved), or null for the next free "copyN".</param>
    /// <returns>The new item.</returns>
    public ProjectItem Duplicate(ProjectItem item, string? wantedId = null)
    {
        var p = Project ?? throw new InvalidOperationException("No project.");
        int n = 2;
        while (p.Items.Any(i => i.Id == $"copy{n}")) n++;
        string newId = wantedId ?? $"copy{n}";
        if (p.Items.Any(i => i.Id == newId)) throw new InvalidOperationException($"An item with the ID {newId} already exists.");
        // An item of an existing mod: the copy is a new script of the mod (ExistingMod.Duplicate), with the icon the mod ships.
        if (item is ExistingItem existing)
        {
            var copied = ExistingMod.Duplicate(p, existing, newId);
            p.Items.Insert(p.Items.IndexOf(item) + 1, copied);
            if (ExistingMod.IconOf(p.Existing!.Source, copied) is { } pic) Icons[copied.Id] = pic;
            return copied;
        }
        if (!Makers.TryGetValue(item.Id, out var make)) throw new InvalidOperationException($"\"{item.Title}\" can't be duplicated: it has no maker in this project.");
        // Such a maker gives back the item itself: the "copy" was the same item a second time in the list, with the copy's name.
        if (IconOnlyMakers.Contains(item.Id)) throw new InvalidOperationException($"\"{item.Title}\" can't be duplicated: the app can draw its icon again, but it cannot make this head option a second time.");
        var (key, prefix) = p.NewItemNames(newId);
        var (copy, icon) = make(newId, key, prefix, null);
        // Carry over the user's edits: the original's text under the copy's text IDs.
        copy.Title = item.Title + " (copy)";
        if (item is OutfitItem src && copy is OutfitItem dst)
        {
            void Carry(string? from, string? to, string suffix)
            {
                if (from == null || to == null || !item.Text.TryGetValue(from, out var per)) return;
                foreach (var (lang, t) in per) copy.SetText(to, lang, suffix.Length > 0 && lang == "eng" && from == src.Spec.NameLang ? t + suffix : t);
            }
            Carry(src.Spec.NameLang, dst.Spec.NameLang, " (copy)");
            Carry(src.Spec.RealNameLang, dst.Spec.RealNameLang, "");
            Carry(src.Spec.InfoLang, dst.Spec.InfoLang, "");
        }
        int at = p.Items.IndexOf(item);
        p.Items.Insert(at + 1, copy);
        if (icon != null) Icons[copy.Id] = icon; else ReadItemIcons();
        Makers[copy.Id] = make;
        if (WomenFramed.Contains(item.Id)) WomenFramed.Add(copy.Id);
        if (Recipes.TryGetValue(item.Id, out var recipe)) Recipes[copy.Id] = recipe;
        if (IconStates.TryGetValue(item.Id, out var iconState) && iconState.Style != IconStyle.Game) IconStates[copy.Id] = iconState;
        return copy;
    }
}

public sealed record IntakeFinding(string What, bool Supported, string? Note = null);

/// <summary>What a body port was made from: the mod, its vanilla bodies with their wearers, and whether the port hid the head (head in the body model).</summary>
public sealed record BodySource(MgsvMod Mod, IReadOnlyList<(BodyReplacement Body, PlayerBranch Who)> Bodies, bool HeadInBody);

/// <summary>Turns a mod the user opens into a <see cref="ModProject"/>. One place the app calls, whatever the mod holds.</summary>
public static class Intake
{
    /// <summary>
    /// The Developer tab's "Unique mod IDs" switch (off by default, the owner, Nov 15). When it is on, a new port whose
    /// name is cut to 12 letters gets "_" and a 2-letter code from the full name, so two long names with the same start
    /// get different IDs. The app sets it from its settings.
    /// </summary>
    public static bool UniqueIds { get; set; }

    /// <summary>The short lower-case ID for a mod name: letters and digits, at most 12, never empty.</summary>
    /// <param name="name">The mod's name.</param>
    /// <param name="unique">True: a cut ID ends in "_" and a 2-letter code from the full name (still 12 letters).</param>
    /// <returns>An ID usable in file names.</returns>
    public static string IdFrom(string name, bool unique = false)
    {
        var s = new string(name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        if (s.Length == 0) s = "mod";
        if (char.IsAsciiDigit(s[0])) s = "m" + s;
        if (s.Length <= 12) return s;
        return unique ? s[..9] + "_" + (Hashing.FoxHash.StrCode32(s) & 0xFF).ToString("x2") : s[..12];
    }

    /// <summary>
    /// Opens a mod: sorts its files, and ports what the app can port. This is the failsafe wrapper: the owner asked for one ("We need to ensure
    /// there is a failsafe if any unrecognised file types are in the mod"). Each converter already catches the exceptions
    /// it expects, but a file type nobody anticipated can throw something else entirely (an index or key error while
    /// parsing a header, say), and that used to take the whole intake down with it. Here anything unexpected becomes a
    /// finding the user can read, and the mod still opens with whatever was understood.
    /// </summary>
    /// <param name="game">The game's files.</param>
    /// <param name="mod">The mod.</param>
    /// <returns>The result; never throws for a mod it could not fully read.</returns>
    /// <param name="uniqueIds">The "Unique mod IDs" switch for this mod; null takes <see cref="UniqueIds"/>.</param>
    public static IntakeResult Open(GameData game, MgsvMod mod, bool? uniqueIds = null)
    {
        try { return OpenCore(game, mod, uniqueIds ?? UniqueIds); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Nothing was understood: hand back an empty result that says so, rather than letting the app crash.
            // Classify is guarded too: it reads packages to find what a mod replaces, so a malformed one (the owner's
            // "GROUND ZEROES b109" has a .fv2 with a bad property size) would throw again here and take the app down.
            ModClassification cls;
            try { cls = ModClassifier.Classify(game, mod); }
            catch (Exception inner) when (inner is not OutOfMemoryException) { cls = new ModClassification([], ["The tool could not read this mod's files."]); }
            var res = new IntakeResult { Project = null, ModName = mod.Metadata.Name, ModAuthor = mod.Metadata.Author, Classification = cls, Game = game };
            res.Findings.Add(new IntakeFinding("This mod", false, $"The tool could not read it: {e.Message}"));
            return res;
        }
    }

    private static IntakeResult OpenCore(GameData game, MgsvMod mod, bool uniqueIds)
    {
        var cls = ModClassifier.Classify(game, mod);
        // A mod that is already a V Framework mod (its script calls V Framework) is edited in place, not ported.
        var existingFindings = new List<IntakeFinding>();
        if (ExistingMod.TryOpen(mod, existingFindings) is { } existing)
        {
            var er = new IntakeResult { Project = existing, ModName = mod.Metadata.Name, ModAuthor = mod.Metadata.Author, Classification = cls, Game = game };
            er.Findings.AddRange(existingFindings);
            foreach (var xi in existing.Items.OfType<ExistingItem>())
                if (ExistingMod.IconOf(mod, xi) is { } pic) er.Icons[xi.Id] = pic;
            // The Found screen reads these: without models its Continue button stays off.
            er.Facts.AddRange(FoundFacts.Build(game, mod, er));
            er.Models.AddRange(FoundFacts.Models(game, mod, er));
            return er;
        }
        var projectId = IdFrom(mod.Metadata.Name, uniqueIds);
        var project = new ModProject
        {
            Id = projectId,
            Name = mod.Metadata.Name + " (V Framework)",
            Author = mod.Metadata.Author,
            Version = "1.0",
            Description = $"{mod.Metadata.Name} by {mod.Metadata.Author}, ported to V Framework 1.7.\r\n\r\nRequires Infinite Heaven, IHHook and V Framework 1.7.",
        };
        var res = new IntakeResult { Project = project, ModName = mod.Metadata.Name, ModAuthor = mod.Metadata.Author, Classification = cls, Game = game };

        // The steps of the port, in this order. A later step reads what an earlier step found (PortContext): a Snake
        // face replacer turns the body and DD head steps off, and the DD head step puts its heads on the body step's outfit.
        // The id of an item depends on the number of items that the earlier steps made.
        var c = new PortContext(project, res, game, mod, projectId);
        PortSnakeCamos(c);
        PortSnakeFaces(c);
        PortAvatarHeads(c);
        PortChickenHat(c);
        PortBodies(c);
        PortDdHeads(c);
        PortWeaponModels(c);
        PortWeaponStatMods(c);
        PortZetaWeaponScripts(c);
        PortZetaOutfits(c);
        PortPlayableQuiet(c);
        PortBuddiesAndThrownItems(c);

        foreach (var route in RoutesNotPorted(cls.Routes, c.Covered))
            res.Findings.Add(new IntakeFinding(route, false, route.StartsWith("Texture download", StringComparison.Ordinal) ? "Choose the mod these textures are for." : "The app can't port this kind yet."));

        // The owner: "it's assumed the head options should work for the costumes shipped with it" (issue #21).
        ModHeads.ListOnOutfits(project);
        if (!res.Findings.Any(f => f.Supported)) res.Project = null;
        res.Facts.AddRange(FoundFacts.Build(game, mod, res));
        res.Models.AddRange(FoundFacts.Models(game, mod, res));
        res.NeedsBaseMod = res.Project == null && cls.Routes.Any(r => r.StartsWith("Texture download", StringComparison.Ordinal));
        res.ReadItemIcons();
        return res;
    }

    /// <summary>
    /// True for an error that stops one step of the port and not the mod: the step reports "Could not be ported" and the
    /// next steps run. Each step uses this one list. Another error stops the mod (<see cref="Open"/> reports it).
    /// </summary>
    internal static bool IsPortError(Exception e) =>
        e is FileNotFoundException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException;

    /// <summary>
    /// The routes of the classifier that no step covered: the kinds of mod that the app found and cannot port. A camo
    /// route is not reported: the camo steps report the camos themselves.
    /// </summary>
    /// <param name="routes">The routes of the classifier (ModClassification.Routes).</param>
    /// <param name="covered">The starts of the routes that the steps covered (PortContext.Covered).</param>
    internal static IEnumerable<string> RoutesNotPorted(IEnumerable<string> routes, IReadOnlyCollection<string> covered) =>
        routes.Where(r => !r.Contains("camo", StringComparison.OrdinalIgnoreCase) && !covered.Any(start => r.StartsWith(start)));

    /// <summary>What the steps of <see cref="OpenCore"/> share: the project they fill, and what a step found that a later step or the route report needs.</summary>
    private sealed class PortContext(ModProject project, IntakeResult res, GameData game, MgsvMod mod, string projectId)
    {
        public ModProject Project => project;
        public IntakeResult Res => res;
        public GameData Game => game;
        public MgsvMod Mod => mod;
        public string ProjectId => projectId;
        /// <summary>The mod is a Snake face replacer: its body packages only carry hair physics, so no outfit and no DD head is made.</summary>
        public bool FaceMod;
        /// <summary>The outfit of the body step; the DD head step lists its heads on it.</summary>
        public OutfitItem? BodyOutfit;
        /// <summary>The starts of the classifier routes that a step ported or reported itself (<see cref="RoutesNotPorted"/>).</summary>
        public HashSet<string> Covered { get; } = new(StringComparer.Ordinal);
        /// <summary>A step calls this when it ported the kind of mod that the route with this start names.</summary>
        public void Covers(string routeStart) => Covered.Add(routeStart);
        /// <summary>The playable Quiet outfit made from each costume package; the buddy costume of the same package is its twin.</summary>
        public Dictionary<string, ProjectItem> QuietByPackage { get; } = new(StringComparer.Ordinal);

        /// <summary>The id of a new item: the project's id for the first item, else the id with the kind and the number of items.</summary>
        public string NewId(string kind) => project.Items.Count == 0 ? projectId : $"{projectId}_{kind}{project.Items.Count}";

        /// <summary>Makes an item, adds it to the project and keeps its maker, so that the item can be made again (a copy, a new icon).
        /// A maker makes the item only. It does not add the item to the project: this function, Duplicate and ApplyIcon rely on it.</summary>
        public ProjectItem Add(string id, string key, string prefix, Func<string, string, string, IconChoice?, (ProjectItem item, RgbaImage? icon)> make)
        {
            var (item, icon) = make(id, key, prefix, null);
            project.Items.Add(item);
            if (icon != null) res.Icons[item.Id] = icon;
            res.Makers[item.Id] = make;
            return item;
        }
    }

    private static void PortSnakeCamos(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Snake camo replacers (True Black Ocelot and the like).
        List<(CamoInfo camo, bool fatigues, bool naked)> camos = [];
        try { camos = CamoConverter.Detect(game, mod).ToList(); }
        catch (Exception e) when (IsPortError(e)) { res.Findings.Add(new IntakeFinding("Snake camo replacer", false, "Could not be ported: " + e.Message)); }
        foreach (var (camo, fatigues, naked) in camos)
        {
            string id = project.Items.Count == 0 ? projectId : $"{projectId}_{camo.Name.ToLowerInvariant().Replace(' ', '_')}";
            id = new string(id.Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_').ToArray());
            var (key, prefix) = project.NewItemNames(id);
            (ProjectItem item, RgbaImage? icon) Make(string itemId, string itemKey, string itemPrefix, IconChoice? choice)
            {
                var o = new CamoConversionOptions
                {
                    Id = itemId, Key = itemKey, Name = mod.Metadata.Name,
                    Description = $"{mod.Metadata.Name} by {mod.Metadata.Author}.",
                    Looks = FatiguesLooks.All, Preset = "fatigues", Dictionary = PathDictionary.Bundled, Icon = choice,
                };
                var (item, icon, _) = CamoConverter.ToItem(game, mod, camo, o, itemPrefix);
                return (item, icon);
            }
            var it = c.Add(id, key, prefix, Make);
            res.Recipes[it.Id] = new CamoRecipe(camo, mod);
            res.IconStates[it.Id] = IconSettings.DefaultFor(camo);
            res.Findings.Add(new IntakeFinding($"Snake camo replacer: {camo.Description}" + (fatigues && naked ? " (fatigues and naked)" : naked ? " (naked)" : ""), true));
        }
    }

    private static void PortSnakeFaces(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Snake face replacers: head options (plain and bandana). Their body packages only carry hair physics, so no outfit.
        try
        {
            var faces = SnakeFaceConverter.ReplacedFaces(mod);
            if (faces.Count > 0)
            {
                var heads = SnakeFaceConverter.AddTo(project, game, mod, projectId, new SnakeFaceOptions { Name = mod.Metadata.Name.ToUpperInvariant(), BandanaName = mod.Metadata.Name.ToUpperInvariant() + " + BANDANA" });
                c.FaceMod = heads.Count > 0;
                if (c.FaceMod) c.Covers("Snake face replacer");
                // A maker each, so their icons can be regenerated like any other head option's. Without one, Makers[id]
                // throws when an icon is applied, so "Generate from the model" and the icon sliders did nothing on a
                // Snake face head (the owner: "In Head options, when I upload an image for an icon, I cannot generate an
                // icon from the model again"). The converter draws the icon from the face model, so the maker redraws it.
                foreach (var h in heads)
                {
                    var made = h;
                    bool bandana = made.Id.EndsWith("bandana", StringComparison.Ordinal);
                    res.IconOnlyMakers.Add(made.Id);
                    res.Makers[made.Id] = (string itemId, string itemKey, string itemPrefix, IconChoice? _) =>
                        (made, SnakeFaceConverter.Icon(SnakeFaceConverter.IconFace(game, mod, bandana)));
                    res.IconStates[made.Id] = IconSettings.ChestDefault;
                }
                res.Findings.Add(new IntakeFinding($"Snake face replacer: faces {string.Join(", ", faces)} as {heads.Count} head option(s)", c.FaceMod));
            }
        }
        catch (Exception e) when (IsPortError(e))
        {
            res.Findings.Add(new IntakeFinding("Snake face replacer", false, "Could not be ported: " + e.Message));
        }
    }

    private static void PortAvatarHeads(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Avatar replacer heads (issue #126): the Avatar hair packages replaced with a whole head, as head options.
        try
        {
            var slots = AvatarHeadConverter.ReplacedSlots(game, mod);
            if (slots.Count == 0) return;
            var heads = AvatarHeadConverter.AddTo(project, game, mod, projectId);
            c.Covers("Avatar head replacer");
            // A maker each, so "Generate from the model" can redraw the icon (see PortSnakeFaces).
            foreach (var h in heads)
            {
                var made = h;
                res.IconOnlyMakers.Add(made.Id);
                res.Makers[made.Id] = (string itemId, string itemKey, string itemPrefix, IconChoice? _) =>
                    (made, AvatarHeadConverter.Icon(FoxPackage.Read(made.Files[made.Spec.Branches[PlayerBranch.Snake].Fpk!]), made.Spec.Branches[PlayerBranch.Snake].Fv2!));
                res.IconStates[made.Id] = IconSettings.ChestDefault;
            }
            res.Findings.Add(new IntakeFinding($"Avatar head replacer: hair slots {string.Join(", ", slots)} as {heads.Count} head option(s)", true));
        }
        catch (Exception e) when (IsPortError(e))
        {
            res.Findings.Add(new IntakeFinding("Avatar head replacer", false, "Could not be ported: " + e.Message));
        }
    }

    private const string ChickenHatPkg = "/Assets/tpp/pack/player/fova/plfova_sna0_msk0_v00.fpk";

    private static void PortChickenHat(PortContext c)
    {
        var (project, res, game, mod) = (c.Project, c.Res, c.Game, c.Mod);
        // A chicken hat replacer (issue #126, the croc cap of "MGS3 Delta Camo Pack"): the hat's model as a Snake head
        // option, so it goes on any suit and takes a bandana combo, in place of the vanilla chicken hat.
        if (!mod.ArchiveFiles.TryGetValue(ChickenHatPkg, out var data)) return;
        try
        {
            var hat = FoxPackage.Read(data);
            if (hat.Files.FirstOrDefault(f => f.Extension == "fmdl") is not { } model) return;
            var van = game.TryRead(ChickenHatPkg) is { } vd ? FoxPackage.Read(vd).Find(model.Path)?.Data : null;
            if (van != null && van.AsSpan().SequenceEqual(model.Data)) return;
            // The loose textures of the mod that the hat uses (with their .ftexs mips); the others come from the game.
            var used = TextureCheck.Referenced([new(ChickenHatPkg, data)]);
            var stems = mod.ArchiveFiles.Keys.Where(k => k.EndsWith(".ftex", StringComparison.Ordinal) && used.Contains(Svfm.Core.Hashing.FoxHash.PathCode64(k))).Select(k => k[..^5]).ToList();
            var loose = mod.ArchiveFiles.Where(kv => stems.Any(s => kv.Key.StartsWith(s + ".", StringComparison.Ordinal)))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            string modName = mod.Metadata.Name.Length > 0 ? mod.Metadata.Name : c.ProjectId;
            string title = AvatarHeadConverter.ShortName(modName).ToUpperInvariant() + " HAT";
            HeadAddonModel[] models = [new("hat", model.Data, FmdlModel.Read(model.Data).Meshes.Select(m => m.MeshGroup).ToHashSet())];
            var option = new HeadAddonOption("hat", title, $"The hat from \"{mod.Metadata.Name}\" by {mod.Metadata.Author}.", ["hat"]);
            string id = c.NewId("hat");
            var heads = SnakeHeadAddon.AddTo(project, game, id, models, [option], [], loose);
            var keys = heads.Select(h => h.Spec.Key!).ToList();
            SnakeHeadAddon.OfferOnVanillaSuits(project, id, keys);
            SnakeHeadAddon.OfferOnVanillaSuits(project, id, keys, PlayerBranch.Avatar);
            // A maker each, so "Generate from the model" can redraw the icon (see PortSnakeFaces).
            foreach (var h in heads)
            {
                var made = h;
                res.IconOnlyMakers.Add(made.Id);
                res.Makers[made.Id] = (string itemId, string itemKey, string itemPrefix, IconChoice? _) =>
                {
                    var face = FoxPackage.Read(game.Read("/Assets/tpp/pack/player/fova/plfova_sna0_face0_v00.fpk")).Files.First(f => f.Path.EndsWith("_cov.fmdl", StringComparison.Ordinal));
                    return (made, SnakeHeadAddon.Icon(option, models, FmdlModel.Read(face.Data)));
                };
                res.IconStates[made.Id] = IconSettings.ChestDefault;
            }
            res.Findings.Add(new IntakeFinding($"Chicken hat replacer: {Path.GetFileName(model.Path)} as the head option {title}", true));
        }
        catch (Exception e) when (IsPortError(e))
        {
            res.Findings.Add(new IntakeFinding("Chicken hat replacer", false, "Could not be ported: " + e.Message));
        }
    }

    private static void PortBodies(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Body replacers (a model and textures that replace a vanilla suit).
        var bodies = new List<(BodyReplacement body, PlayerBranch who)>();
        foreach (var b in c.FaceMod ? [] : BodyConverter.Detect(game, mod))
        {
            // The role table says who wears the package. A package it does not list is left out, not given to Snake.
            if (GameKnowledge.BodyRoleOf(b.VanillaPackage) is { } role) bodies.Add((b, role.Who));
            else res.Findings.Add(new IntakeFinding("Body replacer: " + b.VanillaPackage.Split('/').Last(), false, "The tool does not know which character wears this body package."));
        }
        bodies = [.. bodies.OrderBy(x => x.who)];
        if (bodies.Count > 0)
        {
          try
          {
            string id = project.Items.Count == 0 ? projectId : projectId + "_body";
            var (key, prefix) = project.NewItemNames(id);
            (ProjectItem item, RgbaImage? icon) MakeBody(string itemId, string itemKey, string itemPrefix, IconChoice? choice)
            {
                var o = new BodyConversionOptions
                {
                    Id = itemId, Key = itemKey, Name = mod.Metadata.Name,
                    Description = $"{mod.Metadata.Name} by {mod.Metadata.Author}.",
                    Preset = "fatigues", Dictionary = PathDictionary.Bundled, Icon = choice ?? new IconChoice.Render(),
                };
                var (item, icon) = BodyConverter.ToItemFromPlan(game, mod, Svfm.Core.Conversion.BodyPlan.Build(bodies), o, itemPrefix);
                return (item, icon);
            }
            var it = c.Add(id, key, prefix, MakeBody);
            res.IconStates[it.Id] = IconSettings.ChestDefault;
            var iconBody = BodyConverter.IconBody(bodies); // the body ToItemFromPlan draws the icon from
            if (BodyConverter.FemaleOnly(iconBody.body, [iconBody.who])) res.WomenFramed.Add(it.Id);
            var heads = bodies.SelectMany(b => BodyConverter.HeadsInside(game, mod, b.body)).ToList();
            if (it is OutfitItem bo)
            {
                c.BodyOutfit = bo;
                res.BodySources[bo.Id] = new BodySource(mod, [.. bodies], bo.Spec.Branches.Values.First().Base.EnableHead == false);
            }
            if (heads.Count > 0 && it is OutfitItem outfit)
            {
                c.Covers("Head gear inside the body");
                var helmet = DdHeadConverter.AddTo(project, game, "head" + id, mod.Metadata.Name.ToUpperInvariant() + " HEAD", $"Head gear of {mod.Metadata.Name}.", heads);
                // A maker, so its icon can be regenerated like any other head option's (see the DD head replacer below).
                var helmetHeads = heads;
                res.IconOnlyMakers.Add(helmet.Id);
                res.Makers[helmet.Id] = (string itemId, string itemKey, string itemPrefix, IconChoice? _) =>
                    (helmet, DdHeadConverter.Icon(game, helmetHeads));
                res.IconStates[helmet.Id] = IconSettings.ChestDefault;
                foreach (var branch in new[] { PlayerBranch.DdMale, PlayerBranch.DdFemale }.Where(outfit.Spec.Branches.ContainsKey))
                    outfit.Spec.Branches[branch] = outfit.Spec.Branches[branch] with { HeadOptions = [.. outfit.Spec.Branches[branch].HeadOptions, helmet.Spec.Key] };
                res.Findings.Add(new IntakeFinding($"{heads.Count} head model(s) inside the body, added as a head option", true));
            }
            c.Covers("Body replacer");
            res.Findings.Add(new IntakeFinding($"Body replacer: {string.Join(", ", bodies.Select(b => b.body.VanillaPackage.Split('/').Last()))}", true,
                bodies.Count > 1 ? $"{bodies.Count} looks: {string.Join(" · ", bodies.Select(b => BodyConverter.SuitName(game, b.body)))}. Base: {BodyConverter.SuitName(game, iconBody.body)}." : null));
            // A body package the mod replaces should carry every part of the vanilla one. When it does not, the suit
            // builds with holes in it (the owner, on "MSF emblem undersuit 2 versions": "it would let me build it, but in
            // reality it shouldn't because it's missing so many parts"). Reported, not refused: the owner may still want
            // the port, and the missing parts are named so they can be checked.
            foreach (var b in bodies.Select(x => x.body).Distinct())
                foreach (var gap in BodyConverter.MissingParts(game, mod, b.VanillaPackage))
                    res.Findings.Add(new IntakeFinding($"Body replacer: {b.VanillaPackage.Split('/').Last()} is incomplete", true, gap));
          }
          catch (Exception e) when (IsPortError(e))
          {
            res.Findings.Add(new IntakeFinding("Body replacer", false, "Could not be ported: " + e.Message));
          }
        }
    }

    private static void PortDdHeads(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // DD head replacers (balaclavas, headgear): head options with their own model. With a body of the same mod they go
        // on that outfit; alone they are offered on every vanilla DD suit that shows a head (the vanilla heads stay).
        if (!c.FaceMod)
        {
            try
            {
                var models = DdHeadConverter.FromHeadPackages(mod);
                if (models.Count > 0)
                {
                    var perBody = models.GroupBy(m => m.Body).ToDictionary(g => g.Key, g => g.ToList());
                    int sets = perBody.Values.Max(g => g.Count);
                    var made = new List<(HeadOptionItem head, IReadOnlyList<PlayerBranch> bodies)>();
                    for (int k = 0; k < sets; k++)
                    {
                        var set = perBody.Values.Where(g => g.Count > k).Select(g => g[k]).ToList();
                        string baseName = mod.Metadata.Name.ToUpperInvariant();
                        string title = (sets > 1 ? $"{baseName} {k + 1}" : baseName) + (c.BodyOutfit != null ? " HEAD" : "");
                        string id = c.NewId("head");
                        var head = DdHeadConverter.AddTo(project, game, id, title, $"{mod.Metadata.Name} by {mod.Metadata.Author}.", set);
                        // A maker, so the icon can be regenerated. Without one, IconOf is null and the icon cards never
                        // appear (the owner: "On head option, when I clicked an icon already in game and selected it, I
                        // could not reselect 'generate from the model'"). The head converter draws its icon itself, so
                        // the maker just draws it again - it must NOT call AddTo, which appends to the project and would
                        // add a duplicate head option every time an icon was regenerated.
                        var headSet = set;
                        res.IconOnlyMakers.Add(head.Id);
                        res.Makers[head.Id] = (string itemId, string itemKey, string itemPrefix, IconChoice? _) =>
                            (head, DdHeadConverter.Icon(game, headSet));
                        res.IconStates[head.Id] = IconSettings.ChestDefault;
                        made.Add((head, DdHeadConverter.Bodies(set).covered));
                    }
                    foreach (var body in made.SelectMany(m => m.bodies).Distinct())
                    {
                        var keys = made.Where(m => m.bodies.Contains(body)).Select(m => m.head.Spec.Key).ToList();
                        if (c.BodyOutfit != null && c.BodyOutfit.Spec.Branches.TryGetValue(body, out var br))
                            c.BodyOutfit.Spec.Branches[body] = br with { HeadOptions = [.. br.HeadOptions, .. keys.Where(x => !br.HeadOptions.Contains(x))] };
                        else
                            SnakeHeadAddon.OfferOnVanillaSuits(project, projectId + "_dd", keys, body);
                    }
                    c.Covers("DD head model replacer");
                    string who = string.Join(" and ", made.SelectMany(m => m.bodies).Distinct().Select(LuaWriter.BranchName));
                    res.Findings.Add(new IntakeFinding($"DD head replacer: {made.Count} head option(s) for {who}", true,
                        c.BodyOutfit != null ? "Added to the mod's own outfit." : "Offered on the vanilla DD suits that show a head; the vanilla heads stay."));
                }
            }
            catch (Exception e) when (IsPortError(e))
            {
                res.Findings.Add(new IntakeFinding("DD head replacer", false, "Could not be ported: " + e.Message));
            }
        }
    }

    private static void PortWeaponModels(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Weapon model replacers (chimera receivers): a new V Framework weapon that shoots like the replaced one.
        try
        {
            foreach (var van in WeaponConverter.Detect(mod))
            {
                string id = c.NewId("wp");
                var (key, prefix) = project.NewItemNames(id);
                (ProjectItem item, RgbaImage? icon) MakeWeapon(string itemId, string itemKey, string itemPrefix, IconChoice? _)
                {
                    var o = new WeaponConversionOptions { Id = itemId, Develop = new DevelopRow { Grade = 1, GmpCost = 0, InitiallyAvailable = true } };
                    return (WeaponConverter.ToItem(game, mod, van, o, itemPrefix, itemKey), null);
                }
                var wi = c.Add(id, key, prefix, MakeWeapon);
                c.Covers("Weapon model replacer");
                res.Findings.Add(new IntakeFinding($"Weapon model replacer: {van}", true));
            }
        }
        catch (Exception e) when (IsPortError(e))
        {
            res.Findings.Add(new IntakeFinding("Weapon model replacer", false, "Could not be ported: " + e.Message));
        }
    }

    private static void PortWeaponStatMods(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Stat-table mods (own EquipParameters and R&D tables, vanilla models): each renamed vanilla weapon becomes a new
        // V Framework weapon with the mod's parts, stats, name and icon; the vanilla weapon stays.
        foreach (var (fpk, wp) in WeaponConverter.DetectRenamed(game, mod))
            try
            {
                string id = c.NewId("wp");
                var (key, prefix) = project.NewItemNames(id);
                (ProjectItem item, RgbaImage? icon) MakeWeapon(string itemId, string itemKey, string itemPrefix, IconChoice? _)
                {
                    var o = new WeaponConversionOptions { Id = itemId, Renamed = wp, Icon = wp.Icon != null ? WeaponIconSource.ModIcon : WeaponIconSource.Generated,
                        Develop = new DevelopRow { Grade = 1, GmpCost = 0, InitiallyAvailable = true } };
                    return (WeaponConverter.ToItem(game, mod, fpk, o, itemPrefix, itemKey), null);
                }
                var wi = c.Add(id, key, prefix, MakeWeapon);
                string name = WeaponConverter.WeaponText(game, mod, wp).GetValueOrDefault(("eng", "name")) ?? wp.Wp;
                res.Findings.Add(new IntakeFinding($"Weapon stat mod: {name} ({wp.Wp})", true));
            }
            catch (Exception e) when (IsPortError(e))
            {
                res.Findings.Add(new IntakeFinding($"Weapon stat mod: {wp.Wp}", false, "Could not be ported: " + e.Message));
            }
    }

    private static void PortZetaWeaponScripts(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Zeta weapon scripts that hold only parameter tables (HK 33, Fulton Gun). The owner, Nov 11: they are in scope
        // again, because the model merge gives a custom weapon its part models. Each weapon of the script (gunBasic row)
        // becomes a new weapon: the mod's own package when EquipIdTable() names one, else a copy of the vanilla gun.
        foreach (var (path, data) in mod.GameFiles.Where(kv => kv.Key.StartsWith("/mod/zeta/", StringComparison.OrdinalIgnoreCase) && kv.Key.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string script = Path.GetFileName(path);
            try
            {
                var z = ZetaScript.Parse(System.Text.Encoding.UTF8.GetString(data));
                var weapons = ZetaConverter.Weapons(z);
                if (weapons.Count == 0)
                {
                    // Armor Piercing Sniper Rifles, Tier 3 Infinite Suppressors: rows of parts or damage that the game's guns use.
                    // The port changes the same rows of the game (no new weapon), as the Zeta mod does.
                    if (z.Sections.Count == 0 && z.Damage.Count == 0) continue;
                    var gameTables = EquipTables.Load(game.TryRead)
                        ?? throw new InvalidOperationException("The game's weapon parameter tables (EquipParameters.lua, DamageParameterTables.lua) are needed to port a Zeta weapon script.");
                    var (rowLua, rowChanges) = ZetaConverter.VanillaRowChanges(gameTables, z);
                    if (rowChanges.Count == 0)
                    {
                        res.Findings.Add(new IntakeFinding($"Zeta weapon script: {script}", false, "The script changes no row of the game's weapon tables that the app knows."));
                        continue;
                    }
                    string rowId = c.NewId("rows");
                    var rows = new LuaItem { Id = rowId, Prefix = project.NewItemNames(rowId).prefix, ItemType = ItemKind.Item, Lua = rowLua, Title = z.Name != "" ? z.Name : script };
                    rows.Notes.Add($"Changes {rowChanges.Count} row(s) of the game's weapon tables, as the Zeta script does. No weapon is added: the game's weapons that use these rows change.");
                    rows.Notes.Add("V Framework blocks a weapon that uses a changed row of the game in FOB missions.");
                    rows.Notes.AddRange(rowChanges);
                    project.Items.Add(rows);
                    res.Findings.Add(new IntakeFinding($"Zeta weapon script: {rows.Title} ({rowChanges.Count} changed rows of the game)", true));
                    continue;
                }
                if (ZetaConverter.OtherFunctions(z) is { Count: > 0 } other)
                {
                    res.Findings.Add(new IntakeFinding($"Zeta weapon script: {script}", false, $"The script has behaviour of its own ({string.Join(", ", other)}). The app ports only scripts that hold parameter tables."));
                    continue;
                }
                foreach (string weapon in weapons)
                {
                    string id = c.NewId("wp");
                    var (key, prefix) = project.NewItemNames(id);
                    (ProjectItem item, RgbaImage? icon) MakeWeapon(string itemId, string itemKey, string itemPrefix, IconChoice? _) =>
                        (ZetaConverter.ToItem(game, z, new ZetaConversionOptions { Id = itemId, Weapon = weapon, Develop = new DevelopRow { Grade = 1, GmpCost = 0, InitiallyAvailable = true } }, itemPrefix, itemKey, mod), null);
                    var wi = c.Add(id, key, prefix, MakeWeapon);
                    res.Findings.Add(new IntakeFinding($"Zeta weapon script: {wi.Title} ({weapon})", true));
                }
            }
            catch (Exception e) when (IsPortError(e))
            {
                res.Findings.Add(new IntakeFinding($"Zeta weapon script: {script}", false, "Could not be ported: " + e.Message));
            }
        }
        if (res.Findings.Any(f => f.What.StartsWith("Zeta weapon script"))) c.Covers("Zeta scripts");
    }

    private static void PortZetaOutfits(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Zeta player outfits (LoadPlayerParts: MSF Fatigues, THI-PE-CQB, Snake Scarf and Goggles). The script gives a
        // suit the mod's own body package. Each package is a complete body, so the port is one outfit whose looks are
        // the packages (the choices of the Zeta menu), each copied as installed.
        foreach (var (path, data) in mod.GameFiles.Where(kv => kv.Key.StartsWith("/mod/zeta/", StringComparison.OrdinalIgnoreCase) && kv.Key.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string script = Path.GetFileName(path);
            try
            {
                string lua = System.Text.Encoding.UTF8.GetString(data);
                var (zo, reason) = ZetaConverter.PlayerOutfitOf(mod, lua);
                if (zo == null)
                {
                    if (reason != null) res.Findings.Add(new IntakeFinding($"Zeta player outfit: {script}", false, reason));
                    continue;
                }
                var zs = ZetaScript.Parse(lua);
                string outfitName = zs.Name != "" ? zs.Name : mod.Metadata.Name;
                string id = c.NewId("zo");
                var (key, prefix) = project.NewItemNames(id);
                (ProjectItem item, RgbaImage? icon) MakeOutfit(string itemId, string itemKey, string itemPrefix, IconChoice? choice)
                {
                    OutfitItem? outfit = null;
                    RgbaImage? picture = null;
                    var looks = new List<Look>();
                    for (int i = 0; i < zo.Bodies.Count; i++)
                    {
                        // One package with several bodies (Brothers Keeper): each look gets the package cut down to its body.
                        // shortcut: each look has its own copy of the package's texture pack; share one pack when the size of the mod matters.
                        var source = zo.Bodies[i].PartsFile is { } partsFile ? ZetaConverter.OneBody(mod, zo.Bodies[i].Package, partsFile) : mod;
                        var (item, icon) = BodyConverter.ToItem(game, source, new BodyReplacement(zo.Bodies[i].Package, zo.Parts), new BodyConversionOptions
                        {
                            Id = i == 0 ? itemId : $"{itemId}{i + 1}", Key = itemKey, Name = outfitName,
                            Description = $"{outfitName} by {zs.Author}.", Bodies = [zo.Who],
                            Preset = "fatigues", Dictionary = PathDictionary.Bundled, Icon = i == 0 ? choice ?? new IconChoice.Render() : new IconChoice.Vanilla(),
                        }, i == 0 ? itemPrefix : $"{itemPrefix}{i + 1}");
                        if (i == 0) { (outfit, picture) = (item, icon); if (zo.Bodies.Count > 1) outfit.SetText($"{itemPrefix}_label", zo.Bodies[0].Label); continue; }
                        // The other packages: a look each, with its files and its menu name.
                        foreach (var (p, d) in item.Files.Where(f => !f.Key.Contains("/EquipIcon/", StringComparison.Ordinal))) outfit!.Files[p] = d;
                        outfit!.SetText($"{itemPrefix}{i + 1}_label", zo.Bodies[i].Label);
                        looks.Add(item.Spec.Branches[zo.Who].Base with { DisplayName = $"{itemPrefix}{i + 1}_label" });
                    }
                    if (looks.Count > 0)
                    {
                        var branch = outfit!.Spec.Branches[zo.Who];
                        outfit.Spec.Branches[zo.Who] = branch with { Base = branch.Base with { DisplayName = $"{itemPrefix}_label" }, Variants = [.. branch.Variants, .. looks] };
                    }
                    return (outfit!, picture);
                }
                var it = c.Add(id, key, prefix, MakeOutfit);
                res.IconStates[it.Id] = IconSettings.ChestDefault;
                res.Findings.Add(new IntakeFinding($"Zeta player outfit: {outfitName} ({zo.Parts.Name}, {zo.Who})", true,
                    zo.Bodies.Count > 1 ? $"{zo.Bodies.Count} looks: {string.Join(" · ", zo.Bodies.Select(b => b.Label))}." : null));
            }
            catch (Exception e) when (IsPortError(e))
            {
                res.Findings.Add(new IntakeFinding($"Zeta player outfit: {script}", false, "Could not be ported: " + e.Message));
            }
        }
        if (res.Findings.Any(f => f.What.StartsWith("Zeta player outfit"))) c.Covers("Zeta scripts");
    }

    private static void PortPlayableQuiet(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Quiet buddy costumes as playable Quiet outfits (the owner, item 50: "There is a new wearable playable Quiet
        // outfit designed for V-Framework mod. We need to study it to incorporate it and make it compatible so buddy
        // Quiet outfits can be used for playable Quiet"). A playable Quiet outfit is an ordinary RegisterOutfit on the
        // quiet branch, so the costume's package is copied under a player name and the outfit registered on it. The
        // buddy costume itself is left alone.
        try
        {
            foreach (var van in QuietPlayableConverter.Detect(mod)) AddPlayableQuiet(c, van);
        }
        catch (Exception e) when (IsPortError(e))
        {
            res.Findings.Add(new IntakeFinding("Playable Quiet outfit", false, "Could not be ported: " + e.Message));
        }
    }

    /// <summary>Makes one playable Quiet outfit from the costume package <paramref name="van"/> (no extension): that of the mod, else that of the game.</summary>
    private static void AddPlayableQuiet(PortContext c, string van, DevelopRow? develop = null)
    {
        var (project, res, game, mod) = (c.Project, c.Res, c.Game, c.Mod);
        string id = c.NewId("quiet");
        var (key, prefix) = project.NewItemNames(id);
        (ProjectItem item, RgbaImage? icon) MakeQuiet(string itemId, string itemKey, string itemPrefix, IconChoice? choice)
        {
            var o = new QuietPlayableOptions { Id = itemId, Name = mod.Metadata.Name.ToUpperInvariant(), Develop = develop };
            var made = QuietPlayableConverter.ToItem(project, game, mod, van, o);
            // Generate: Quiet's chest from the outfit's own package, as the buddy costume renders it. At intake the outfit keeps the game's icon.
            if (choice is not IconChoice.Render rr) return (made, null);
            string pkg = made.Spec.Branches[PlayerBranch.Quiet].Base.FpkPath[..^4];
            var pack = made.Files.TryGetValue(pkg + ".pftxs", out var pd) ? TexturePack.Read(pd) : null;
            var fpkd = made.Files.TryGetValue(pkg + ".fpkd", out var dd) ? FoxPackage.Read(dd) : null;
            return (made, BuddyConverter.RenderQuiet(game, PathDictionary.Bundled, mod, FoxPackage.Read(made.Files[pkg + ".fpk"]), pack, rr, null, made.Files, fpkd));
        }
        var quiet = c.QuietByPackage[van] = c.Add(id, key, prefix, MakeQuiet);
        // It starts on the game's costume icon: shown, and a game icon for the icon controls (Generate, upload).
        if (IntakeResult.IconPathOf(quiet) is { } gamePath && res.GameIconImage(gamePath) is { } gameIcon)
            (res.Icons[quiet.Id], res.IconStates[quiet.Id]) = (gameIcon, IconSettings.ChestDefault with { Style = IconStyle.Game, GamePath = gamePath });
        res.Findings.Add(new IntakeFinding($"Playable Quiet outfit: {van.Split('/').Last()}", true));
    }

    /// <summary>Opens what an <see cref="ExtraMod"/> names: a mod file, or a Quiet costume of the game (<see cref="QuietPlayableConverter.GameSource"/>).</summary>
    /// <param name="game">The files of the game.</param>
    /// <param name="x">What to open.</param>
    /// <param name="host">The open project's intake: a copy of one of its outfits (<see cref="OutfitCopies.QuietSource"/>) needs it.</param>
    /// <param name="uniqueIds">The "Unique mod IDs" switch for this mod; null takes <see cref="UniqueIds"/>.</param>
    public static IntakeResult OpenExtra(GameData game, ExtraMod x, IntakeResult? host = null, bool? uniqueIds = null) => x.Path switch
    {
        QuietPlayableConverter.GameSource => OpenGameQuietCostume(game, x.Inner ?? "", uniqueIds),
        OutfitCopies.QuietSource => OutfitCopies.OpenQuiet(game, host ?? throw new ArgumentException("A Quiet copy needs the open project."), x.Inner ?? ""),
        _ => Open(game, MgsvMod.Open(x.Path, x.Inner), uniqueIds),
    };

    /// <summary>
    /// Opens a source of a saved project with the IDs that the project file has. The "Unique mod IDs" switch can be
    /// different now from what it was at the save; the saved edits are found by item ID, so the wrong switch loses them.
    /// When no item of the result is in the file, the other switch value is tried.
    /// </summary>
    /// <param name="open">Opens the source with a switch value (null: the switch as it is now).</param>
    /// <param name="savedIds">The item IDs of the project file, with the removed ones.</param>
    public static IntakeResult OpenAsSaved(Func<bool?, IntakeResult> open, IReadOnlySet<string> savedIds)
    {
        bool Fits(IntakeResult r) => r.Project?.Items.Any(i => savedIds.Contains(i.Id)) == true;
        var res = open(null);
        if (res.Project == null || Fits(res)) return res;
        var other = open(!UniqueIds);
        return Fits(other) ? other : res;
    }

    /// <summary>True for an ExtraMod that the app makes (not a file on disk).</summary>
    public static bool IsMadeByApp(ExtraMod x) => x.Path is QuietPlayableConverter.GameSource or OutfitCopies.QuietSource;

    /// <summary>
    /// A Quiet buddy costume of the game as a playable Quiet outfit, with no mod (the owner, Nov 14). The result has one
    /// item, to add to an open project. The files of the outfit are copies of the files of the game.
    /// </summary>
    /// <param name="game">The files of the game.</param>
    /// <param name="package">The package name of the costume, for example buddy_quiet2_05.</param>
    /// <returns>The result; its project is null, with a finding that says why, when the costume could not be made.</returns>
    /// <param name="uniqueIds">The "Unique mod IDs" switch for this costume; null takes <see cref="UniqueIds"/>.</param>
    public static IntakeResult OpenGameQuietCostume(GameData game, string package, bool? uniqueIds = null)
    {
        string name = QuietPlayableConverter.GameCostumes(game).FirstOrDefault(x => x.Package == package).Name
            ?? throw new ArgumentException($"{package} is not a Quiet costume of the game.");
        var mod = MgsvMod.Empty(name);
        string projectId = IdFrom("quiet " + name, uniqueIds ?? UniqueIds);
        var project = new ModProject { Id = projectId, Name = name + " (V Framework)", Version = "1.0" };
        var res = new IntakeResult { Project = project, ModName = name, ModAuthor = "", Classification = new ModClassification([], []), Game = game };
        try { AddPlayableQuiet(new PortContext(project, res, game, mod, projectId), QuietPlayableConverter.BuddyFolder + package, QuietPlayableConverter.GameCostumeDevelop(package)); }
        catch (Exception e) when (IsPortError(e)) { res.Findings.Add(new IntakeFinding("Playable Quiet outfit: " + name, false, "Could not be made: " + e.Message)); }
        if (!res.Findings.Any(f => f.Supported)) res.Project = null;
        res.ReadItemIcons();
        return res;
    }

    private static void PortBuddiesAndThrownItems(PortContext c)
    {
        var (project, res, game, mod, projectId) = (c.Project, c.Res, c.Game, c.Mod, c.ProjectId);
        // Buddy costumes (Quiet, D-Horse tack, D-Dog gear) and thrown items with their own model.
        try
        {
            // Zeta buddy packages (Blade Wolf, GZ Watch Dog) port the same way: each package becomes its own costume.
            foreach (var r in BuddyConverter.Detect(game, mod).Concat(BuddyConverter.DetectZeta(mod)))
            {
                string id = c.NewId("buddy");
                // A maker, so the icon can be regenerated later (the owner: "I could not attempt to generate an icon for
                // Quiet", and the same for D-Dog and D-Horse). Without one, Intake.IconOf is null, HasIconControls is
                // false and the Generate button never appears. The maker re-runs the whole port with the chosen icon.

                (ProjectItem item, RgbaImage? icon) MakeBuddy(string itemId, string itemKey, string itemPrefix, IconChoice? icon)
                {
                    var o = new BuddyConversionOptions { Id = itemId, Dictionary = PathDictionary.Bundled, Icon = icon };
                    var made = BuddyConverter.ToItem(project, game, mod, r, o);
                    return (made, res.Icons.GetValueOrDefault(made.Id));
                }
                var made = c.Add(id, "", "", MakeBuddy); // the converter takes the names from the project
                res.IconStates[made.Id] = IconSettings.ChestDefault;
                // Twins: the buddy costume and the playable Quiet outfit of one package. Sync is on, so the outfit gets the costume's details.
                if (r.Buddy == "quiet" && c.QuietByPackage.TryGetValue(r.VanillaPackage, out var quiet))
                {
                    (made.TwinId, quiet.TwinId, made.Synced, quiet.Synced) = (quiet.Id, made.Id, true, true);
                    res.ReadItemIcons(); // the buddy's icon is in its files only
                    res.CopyTwin(made, quiet);
                }
                c.Covers("Buddy replacer"); c.Covers("Zeta buddy packages");
                res.Findings.Add(new IntakeFinding($"Buddy costume: {r.Title ?? id}", true));
            }
            // Placed items are not portable (the classifier's route says so); one must not stop the mod's thrown items (Grenades and Mines).
            var thrown = SupportWeaponConverter.Detect(game, mod).Where(r => r.Thrown).ToList();
            // Issue #115: a thrown item of the tool whose declaring module was deleted keeps its script in its _throw module.
            // The port runs that script, not the vanilla effect.
            var throwMods = mod.GameFiles.Where(g => System.Text.RegularExpressions.Regex.IsMatch(g.Key, @"^/mod/modules/.*_throw(_\d+)?\.lua$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            var throwSrc = throwMods.Select(g => new System.Text.UTF8Encoding(false).GetString(g.Value)).ToList();
            var throwConst = throwSrc.SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s, @"(?<![\w.])equipId\s*==\s*(TppEquip\.EQP_\w+)").Select(m => m.Groups[1].Value)).Distinct().ToList();
            foreach (var r in thrown)
            {
                string id = c.NewId("item");
                // shortcut: only one thrown model and one scripted item are matched; with several, which script is which model is unknown, so each gets the vanilla effect.
                ThrowScript? script = null;
                if (thrown.Count == 1 && throwConst.Count == 1
                    && ThrowScriptReader.Read(throwSrc, new ThrowContext(throwConst[0], mod.Metadata.Name, null, null), r.VanillaEquip) is { Lock: null } read
                    && ThrowEvents.All.Any(e => read.Script[e].Count > 0))
                    script = read.Script;
                string? scriptJson = script?.ToJson();
                var made = SupportWeaponConverter.AddTo(project, game, mod, r, new SupportWeaponConversionOptions { Id = id, Script = script });
                if (script != null) KeepThrowText(mod, throwMods[0].Key, made);
                // Issue #106: the maker of a copy makes the item again as ported, with the source's model files.
                res.Makers[made.Id] = (string itemId, string itemKey, string itemPrefix, IconChoice? _) =>
                    (SupportWeaponConverter.AddTo(new ModProject { Id = project.Id, Name = project.Name }, game, mod, r, new SupportWeaponConversionOptions
                    { Id = itemId, Names = (itemKey, itemPrefix), ModelFrom = made, Script = scriptJson == null ? null : ThrowScript.FromJson(scriptJson) }), null);
                res.Findings.Add(new IntakeFinding($"Thrown item: {r.VanillaEquip}", true));
            }
        }
        catch (Exception e) when (IsPortError(e))
        {
            res.Findings.Add(new IntakeFinding("Buddy costume or thrown item", false, "Could not be ported: " + e.Message));
        }
    }

    /// <summary>
    /// Issue #115: the name and description of a thrown item of the tool, from the mod's language files. The tool's text IDs
    /// are {prefix}_name and {prefix}_info, and the module is SVFM_{project}_{item}_throw.lua, so the prefix is a tail of the
    /// module's name, with or without svfm_.
    /// </summary>
    private static void KeepThrowText(MgsvMod mod, string module, SupportWeaponItem item)
    {
        var parts = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(module), @"_throw(_\d+)?\.lua$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).ToLowerInvariant().Split('_');
        var prefixes = Enumerable.Range(0, parts.Length).Select(i => string.Join('_', parts[i..])).SelectMany(t => new[] { t, "svfm_" + t }).ToList();
        foreach (var (path, data) in mod.ArchiveFiles)
        {
            var lang = System.Text.RegularExpressions.Regex.Match(path, @"lang_default_data_(\w+)\.fpk$");
            if (!lang.Success) continue;
            List<LangFile> files;
            try { files = FoxPackage.Read(data).Files.Where(f => f.Path.EndsWith(".lng2", StringComparison.Ordinal)).Select(f => LangFile.Read(f.Data)).ToList(); }
            catch (InvalidDataException) { continue; }
            string? Find(string suffix) => prefixes.SelectMany(p => files.Select(f => f[p + suffix])).FirstOrDefault(t => !string.IsNullOrEmpty(t));
            if (Find("_name") is { } name)
            {
                item.SetText(item.Spec.NameLang, lang.Groups[1].Value, name);
                if (lang.Groups[1].Value == "eng") item.Title = name;
            }
            if (Find("_info") is { } info) item.SetText(item.Spec.InfoLang!, lang.Groups[1].Value, info);
        }
    }
}

/// <summary>How an outfit's icon is made in the app.</summary>
public enum IconStyle { ChestRender, CamoCrop, Custom, Game, Outline }

/// <summary>The icon controls: style, which slot a render draws, and the camo crop's position and tone.</summary>
/// <param name="Style">Chest render or camo crop.</param>
/// <param name="WithScarf">The render draws the scarf slot (else Standard).</param>
/// <param name="CropX">Crop position in the camo texture.</param>
/// <param name="CropY">Crop position in the camo texture.</param>
/// <param name="Contrast">Crop contrast.</param>
/// <param name="Brightness">Crop brightness.</param>
/// <param name="OffsetX">Render framing: move sideways (fraction of the picture).</param>
/// <param name="OffsetY">Render framing: move up or down.</param>
/// <param name="Zoom">Render framing: zoom (1 = the usual).</param>
/// <param name="Custom">For the Custom style: the number of the uploaded picture (kept by the project).</param>
/// <param name="GamePath">For the Game style: the game icon's path without extension.</param>
/// <param name="Parts">For the Outline style: the attachments drawn (suppressor 1, flashlight 2, spare magazine 4).</param>
/// <param name="Yaw">For the Outline style: the turn of the weapon from its side view, in degrees (issue #67); 0 is the default look.</param>
/// <param name="Pitch">For the Outline style: the tilt, in degrees; 0 is the default look.</param>
/// <param name="HiddenModels">For the Outline style: the merged attachment models left out, sorted and joined by ";" (a text, so the record compares by value); null for none.</param>
/// <param name="FromView">For the Custom style: the picture was generated from the view of the 3D preview (a head option, issue #17), not uploaded.</param>
/// <param name="Scale">For the Custom style: the SIZE of the picture about its centre (issue #131); 1 is the picture as made.</param>
public sealed record IconSettings(IconStyle Style, bool WithScarf, int CropX, int CropY, float Contrast, float Brightness, float OffsetX = 0f, float OffsetY = 0f, float Zoom = 1f, int Custom = 0, string? GamePath = null, int Parts = IconSettings.PartSuppressor, bool FromView = false, float Yaw = 0f, float Pitch = 0f, string? HiddenModels = null, float Scale = 1f)
{
    /// <summary>Outline icons: which attachments the picture draws (bits of <c>Parts</c>).</summary>
    public const int PartSuppressor = 1, PartLight = 2, PartMagazine = 4;

    /// <summary>The settings of an outfit that is not a camo replacer: a chest render with the usual framing.</summary>
    public static IconSettings ChestDefault => new(IconStyle.ChestRender, true, 0, 0, Conversion.SuitIcon.DefaultContrast, 1f);

    /// <summary>What the camo's vanilla icon style says: render for BLACK, fabric crop at the vanilla position otherwise.</summary>
    /// <param name="camo">The camo the mod replaces.</param>
    /// <returns>The starting settings.</returns>
    public static IconSettings DefaultFor(Knowledge.CamoInfo camo)
    {
        var d = IconChoice.DefaultFor(camo);
        var o = camo.IconTextureOrigin is [var x, var y] ? (x, y) : Conversion.SuitIcon.DefaultCropOrigin;
        return new IconSettings(d is IconChoice.Render ? IconStyle.ChestRender : IconStyle.CamoCrop, true, o.Item1, o.Item2,
            Conversion.SuitIcon.DefaultContrast, 1f);
    }
}

internal sealed class CamoRecipe(CamoInfo camo, MgsvMod mod)
{
    public CamoInfo Camo { get; } = camo;
    public MgsvMod Mod { get; } = mod;
    public RgbaImage? Texture { get; set; }
}
