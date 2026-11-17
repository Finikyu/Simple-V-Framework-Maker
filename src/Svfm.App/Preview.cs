using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Svfm.Core.Archives;
using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Project;
using Svfm.Core.Rendering;
using Svfm.Core.VFramework;

namespace Svfm.App;

public enum PreviewMode { Off, Stills }

/// <summary>The 3D preview beside the editor: the outfit as it will be built, turned with the mouse.</summary>
public sealed class PreviewVm : Observable
{
    public const int PictureWidth = 360, PictureHeight = 540;
    /// <summary>
    /// The weapon picture is drawn at the size of the box it is shown in (360×180), not at the outfit's 360×540. It used
    /// to be drawn tall and then squeezed into a short box, which left the gun small and hard to read - the owner saw
    /// that as guns "missing their textures" when the texture was simply too small to make out.
    /// </summary>
    public const int WeaponWidth = 360, WeaponHeight = 180;
    /// <summary>
    /// The yaw a weapon is drawn at when the slider reads 0. A gun model lies along Z, so 0 would look straight down the
    /// barrel; 90 turns it side on and the extra 180 turns it to face left, which is the default the owner asked for.
    /// </summary>
    public const float WeaponBaseYaw = 270f;
    private readonly Session _s;
    private ItemVm? _item;
    private OutfitPreview? _scene;
    private Bitmap? _image;
    private double _yaw;
    private bool _busy;
    private string _error = "";
    private int _renderTicket, _loadTicket;
    private Choice? _who, _look;
    private bool _dragging;
    private double _panX, _panY;
    private bool _maximised;

    public PreviewVm(Session s)
    {
        _s = s;
        _s.Changed += () =>
        {
            RefreshCharacters(); RefreshLooks(); RefreshHeads();
            // A camo picked or a sight swapped on the Stats tab (or an undo of one): draw the gun again with it.
            if (Shown is WeaponItem w && Scene != null && (w.Spec.Tuning?.Camo != _sceneCamo || WeaponConverter.MergeSignature(w.Spec) != _sceneSlots)) _ = RepaintAsync(w);
        };
        Reload = new Command(async () => await LoadAsync(_item), () => _item?.IsOutfit == true || IsWeapon);
        TurnLeft = new Command(() => Yaw -= 45);
        TurnRight = new Command(() => Yaw += 45);
        Front = new Command(() => Yaw = 0);
    }

    /// <summary>Below this window width the 3D panel starts collapsed, so the editor keeps its room (issue #8).</summary>
    public const double OpenFromWidth = 1280;
    private bool _dockOpen = true, _dockChosen;
    /// <summary>Whether the 3D panel on the right of the editor is open (collapsed, it is a narrow strip with one button).</summary>
    public bool DockOpen
    {
        get => _dockOpen;
        set
        {
            if (!Set(ref _dockOpen, value)) return;
            Raise(nameof(DockLabel));
            // A collapsed panel cannot stay maximised: it would cover the editor with an empty panel.
            if (!value) Maximised = false;
        }
    }
    public string DockLabel => _dockOpen ? "3D ▸" : "◂ 3D";
    private Command? _toggleDock;
    public Command ToggleDock => _toggleDock ??= new(() => { _dockChosen = true; DockOpen = !DockOpen; });
    /// <summary>Opens or collapses the panel to suit the window width, until the user opens or collapses it.</summary>
    public void FitWidth(double width)
    {
        _editorWidth = width; Raise(nameof(DockWidth));
        if (!_dockChosen) DockOpen = width >= OpenFromWidth;
    }
    private double _editorWidth;
    public Command Reload { get; }
    public Command TurnLeft { get; }
    public Command TurnRight { get; }
    public Command Front { get; }

    public PreviewMode Mode
    {
        get => _forced ? PreviewMode.Stills : _s.Settings.EffectivePreview;
        set
        {
            if (!_forced && Mode == value) return;
            _forced = false;
            _s.Settings.PreviewChosen = true;
            _s.Settings.Preview = value.ToString(); _s.Settings.Save();
            RaiseMode();
            if (value != PreviewMode.Off && _scene == null && _weaponModels == null) Fire(LoadAsync(_item));
            else if (value == PreviewMode.Stills) _ = IsWeapon ? RenderWeaponAsync() : RenderAsync(false);
        }
    }

    public event Action? ModeChanged;

    // True while Generate mode shows the preview and the saved setting is Off: the end of the mode turns it off again.
    private bool _forced;

    public void RaiseMode() { ModeChanged?.Invoke(); Raise(nameof(Mode)); Raise(nameof(Visible)); Raise(nameof(StillsOn)); Raise(nameof(WeaponStillsOn)); }
    /// <summary>
    /// Items drawn as an outline of their model: weapons and thrown items. Buddy costumes used to be in this group, which
    /// is why Quiet, D-Dog and D-Horse came out as white wireframes (the outline pass draws no textures). They are
    /// <see cref="IsBuddy"/> now and go through the textured renderer.
    /// </summary>
    public bool IsWeapon => Shown is WeaponItem or SupportWeaponItem;
    /// <summary>A buddy costume (Quiet, D-Dog, D-Horse): a textured model, drawn like an outfit.</summary>
    public bool IsBuddy => Shown is BuddyOutfitItem;
    /// <summary>
    /// A head option selected in the Heads drop-down: its own head model, drawn on its own. The owner: "Head options do not
    /// appear on the 3D model, they should." A head option with no model of its own (a soldier face the game already has)
    /// has nothing to draw, and says so instead of showing an empty box.
    /// </summary>
    public bool IsHead => Shown is HeadOptionItem;
    /// <summary>
    /// The item the preview draws: the item itself, or for an item of an existing V Framework mod its model as the tool's
    /// own kind (<see cref="ExistingItem.Model"/>), read from the mod's script and sharing the mod's files.
    /// </summary>
    private ProjectItem? Shown => ModelOf(_item?.Item);
    private static ProjectItem? ModelOf(ProjectItem? i) => i is ExistingItem e ? e.Model : i;
    /// <summary>
    /// The ATTACHMENTS row: one toggle for each attachment the weapon has (owner, Nov 7: the laser sights were missing, and
    /// a part the model does not have did nothing). Each toggle changes the preview and the icon that Generate now makes,
    /// not the weapon in the game. All are on when a weapon is opened.
    /// </summary>
    public bool ShowParts => Shown is WeaponItem;
    public ObservableCollection<AttachmentToggle> Attachments { get; } = [];
    public bool NoAttachments => Attachments.Count == 0;
    /// <summary>The keys of the attachments that are switched off.</summary>
    public HashSet<string> HiddenAttachments => Attachments.Where(a => !a.On).Select(a => a.Key).ToHashSet();
    private void FillAttachments(IReadOnlyList<WeaponAttachment> list)
    {
        Attachments.Clear();
        foreach (var a in list) Attachments.Add(new AttachmentToggle(a.Key, a.Name, () => _ = RenderWeaponAsync()));
        if (_gen is { IsWeaponIcon: true, Draft: { } d }) ShowAttachmentsOf(d);
        Raise(nameof(ShowParts)); Raise(nameof(NoAttachments));
    }
    /// <summary>Switches the attachments to the state of an icon (Generate mode starts from the icon).</summary>
    private void ShowAttachmentsOf(IconSettings s)
    {
        var off = WeaponAttachments.HiddenKeys(s.Parts, s.HiddenModels);
        foreach (var a in Attachments) a.SetQuietly(!off.Contains(a.Key));
    }
    private static string? PackageOf(ProjectItem? i) => i switch { WeaponItem w => w.Spec.FpkPath, SupportWeaponItem t => t.Spec.FpkPath, BuddyOutfitItem b => b.Spec.FpkPath, _ => null };
    public bool IsOutfitItem => Shown is OutfitItem;
    public bool Visible => Mode != PreviewMode.Off && (IsOutfitItem || IsWeapon || IsBuddy || IsHead);
    public bool StillsOn => Visible && Mode == PreviewMode.Stills && !IsWeapon;
    public bool WeaponStillsOn => Visible && Mode == PreviewMode.Stills && IsWeapon;

    // ---- Weapons: the model drawn in outline style, turned with the same controls ----
    private List<FmdlModel>? _weaponModels;
    private List<string> _weaponPaths = [];

    private string? _sceneCamo, _sceneSlots;

    /// <summary>Loads the gun again in its new camo; the attachments switched off stay off.</summary>
    private async Task RepaintAsync(WeaponItem w)
    {
        var off = Attachments.Where(a => !a.On).Select(a => a.Key).ToHashSet();
        await LoadWeaponAsync(w);
        if (off.Count == 0) return;
        foreach (var a in Attachments) if (off.Contains(a.Key)) a.SetQuietly(false);
        await RenderWeaponAsync();
    }

    private async Task LoadWeaponAsync(ProjectItem w)
    {
        int ticket = ++_loadTicket;
        _sceneCamo = (w as WeaponItem)?.Spec.Tuning?.Camo;
        _sceneSlots = w is WeaponItem wi ? WeaponConverter.MergeSignature(wi.Spec) : null;
        Busy = true; Error = "";
        var game = _s.Game;
        try
        {
            // Textured, like an outfit and a buddy: the outline pass draws no textures, so a gun came out as a white
            // wireframe (the owner: "The gun also suffers from the same 3D preview issue as the Buddy, though this one
            // facing to the side by default is preferable"). The side-on default is kept by the yaw below.
            var scene = game == null ? null : await Task.Run(() => OutfitPreview.ForWeapon(game, PathDictionary.Bundled, w));
            if (ticket != _loadTicket) return;
            if (scene == null)
            {
                // No package of its own (a model already in the game): fall back to the outline pass, which needs no textures.
                var models = await Task.Run(() =>
                {
                    if (PackageOf(w) is not { } pk || !w.Files.TryGetValue(pk, out var fpk)) return (new List<FmdlModel>(), new List<string>());
                    var fm = FoxPackage.Read(fpk).Files.Where(f => f.Extension == "fmdl").ToList();
                    return (fm.Select(f => FmdlModel.Read(f.Data)).ToList(), fm.Select(f => f.Path).ToList());
                });
                if (ticket != _loadTicket) return;
                (_weaponModels, _weaponPaths) = models;
                FillAttachments(WeaponAttachments.Of((w as WeaponItem)?.Spec, _weaponPaths,
                    _weaponModels.SelectMany(m => m.MeshGroups.Select(g => g.hash & 0xFFFFFFFFFFFF)).ToHashSet()));
                if (_weaponModels.Count == 0)
                    Error = w is SupportWeaponItem t
                        ? $"This item is thrown as the {SupportWeaponFamilies.Name(t.Spec.ThrowAs)}, and the game's model for it could not be read. Check the game folder in Settings."
                        : "This item uses a model that is already in the game, so there is nothing of its own to show.";
                else await RenderWeaponAsync();
                return;
            }
            Scene = scene;
            _weaponModels = null;
            // Which attachments this weapon actually has, so the row only offers the ones that do something.
            FillAttachments(WeaponAttachments.Of((w as WeaponItem)?.Spec, scene.ModelPaths, scene.MeshGroups));
            await RenderWeaponAsync();
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (ticket == _loadTicket) Error = e.Message;
        }
        finally { if (ticket == _loadTicket) Busy = false; }
    }

    /// <summary>
    /// Loads a buddy costume's own model and textures. This is the fix for the white wireframes: a buddy used to be drawn
    /// by the outline pass, which draws no textures at all.
    /// </summary>
    private async Task LoadBuddyAsync(BuddyOutfitItem b)
    {
        int ticket = ++_loadTicket;
        Busy = true; Error = "";
        var game = _s.Game;
        try
        {
            var scene = game == null ? null : await Task.Run(() => OutfitPreview.ForBuddy(game, PathDictionary.Bundled, b));
            if (ticket != _loadTicket) return;
            if (scene == null) { Error = "This costume has no model of its own to show."; return; }
            Scene = scene;
            if (Mode == PreviewMode.Stills) await RenderAsync(false);
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (ticket == _loadTicket) Error = "The preview can't be made: " + e.Message;
        }
        finally { if (ticket == _loadTicket) Busy = false; }
    }

    /// <summary>
    /// Loads a head option's own model, so selecting one in the Heads drop-down shows the head. The owner: "Head options do not
    /// appear on the 3D model, they should."
    /// </summary>
    private async Task LoadHeadAsync(HeadOptionItem h)
    {
        int ticket = ++_loadTicket;
        Busy = true; Error = "";
        var game = _s.Game;
        try
        {
            var scene = game == null ? null : await Task.Run(() => OutfitPreview.ForHeadOption(game, PathDictionary.Bundled, h));
            if (ticket != _loadTicket) return;
            if (scene == null)
            {
                Error = h.Spec.Branches.Values.Any(b => b.EnemyFaceId is { Length: > 0 })
                    ? "This head is a soldier face the game already has, so there is no model of its own to show."
                    : "This head has no model of its own to show.";
                return;
            }
            Scene = scene;
            if (Mode == PreviewMode.Stills) await RenderAsync(false);
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (ticket == _loadTicket) Error = "The preview can't be made: " + e.Message;
        }
        finally { if (ticket == _loadTicket) Busy = false; }
    }

    private async Task RenderWeaponAsync()
    {
        if (Mode != PreviewMode.Stills || !IsWeapon) return;
        int ticket = ++_renderTicket;
        float yaw = (float)_yaw, pitch = (float)_pitch, zoom = (float)_zoom;
        var off = HiddenAttachments;
        var groupsOff = WeaponAttachments.HiddenGroups(off);
        var scene = _scene;
        var models = _weaponModels;
        var paths = _weaponPaths;
        // Generate mode: the live result is the outline icon that Generate now makes from this view.
        var live = _gen is { IsWeaponIcon: true, Draft: { } d } g ? (g.Item, WeaponIconOf(d)) : default;
        if (scene == null && (models == null || models.Count == 0)) return;
        // While the mouse is down, draw a cheap picture: no supersampling and half the pixels. A drag fires dozens of
        // renders a second and the full-quality one cannot keep up, which is what made the preview feel slow. The sharp
        // picture is drawn once on release (EndDrag).
        bool quick = _dragging;
        int ss = 1;
        int rw = WeaponRenderWidth, rh = WeaponRenderHeight;
        if (quick || _s.Settings.FastPreview) { rw = Math.Max(1, rw / 2); rh = Math.Max(1, rh / 2); }
        byte[] png;
        try { png = Png.Encode(await Task.Run(() =>
        {
            if (scene != null)
            {
                // Textured. A gun model lies along Z, so a yaw of 0 looks straight down the barrel and the gun is a dot.
                // The base offset turns it side on, and the owner asked for the other side of that: "The gun should by
                // default be 180 so facing left". So the base is 270 (90 + 180) and the slider's 0 is the default view.
                // The parts are hidden by mesh group name hash, the same groups the outline pass uses.
                return scene.RenderStill(yaw + WeaponBaseYaw, rw, rh, ss, pitch, zoom, groupsOff, (float)_panX, (float)_panY, off);
            }
            var shown = models!.Where((m, i) => i >= paths.Count || !WeaponAttachments.IsHidden(paths[i], off)).ToList();
            var o = PreviewOutline;
            if (yaw != 0 || pitch != 0) o = o with { View = OutlineView.Custom, Yaw = yaw + WeaponBaseYaw, Pitch = pitch };
            else o = o with { View = OutlineView.Custom, Yaw = WeaponBaseYaw };
            if (zoom != 1) o = o with { Zoom = zoom };
            // The outline pass takes its offset in output pixels (always 256x128), the same move as the icon gets.
            if (_panX != 0 || _panY != 0) { var (ox, oy) = IconOffsetOfPan(_panX, _panY); o = o with { OffsetX = ox * OutlineIconRenderer.Width, OffsetY = oy * OutlineIconRenderer.Height }; }
            return OutlineIconRenderer.Render(shown, o, groupsOff);
        })); }
        catch (Exception e) { RenderFailed(ticket, e); return; }
        if (ticket != _renderTicket) return;
        using var ms = new MemoryStream(png);
        Image = new Bitmap(ms);
        if (live.Item1 == null) return;
        try
        {
            var icon = await Task.Run(() => IntakeResult.RenderOutline(live.Item1, live.Item2));
            if (ticket == _renderTicket) Result = ToBitmap(icon);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or KeyNotFoundException) { if (ticket == _renderTicket) Error = "The icon can't be drawn: " + e.Message; }
    }

    /// <summary>The weapon icon that Generate now makes: the preview's turn, tilt, attachments, zoom (1:1) and move (owner, Nov 7).</summary>
    /// <param name="d">The draft.</param>
    /// <returns>The settings.</returns>
    public IconSettings WeaponIconOf(IconSettings d)
    {
        var off = HiddenAttachments;
        float yaw = (float)Math.Round(_yaw), pitch = (float)Math.Round(_pitch), z = (float)_zoom;
        var (k, x0, y0) = IconMatchOf(yaw, pitch);
        var (ox, oy) = IconOffsetOfPan(_panX, _panY);
        return d with { Style = IconStyle.Outline, Yaw = yaw, Pitch = pitch, Parts = WeaponAttachments.PartsBits(off), HiddenModels = WeaponAttachments.HiddenModels(off),
            Zoom = z * k, OffsetX = z * x0 + ox, OffsetY = z * y0 + oy };
    }

    /// <summary>The outline pass of the preview when the weapon has no textured model.</summary>
    private static readonly OutlineIconOptions PreviewOutline = new() { MaxWidth = 230f, MaxHeight = 110f, MaxPixelsPerMetre = 340f };

    /// <summary>
    /// How the outline icon matches the preview picture at this turn and tilt (owner, Nov 7: the icon did not follow the
    /// preview's zoom, because the icon frames the gun on its own size limits). At preview zoom z with no move, the icon
    /// that shows the gun as the preview does has zoom z*K and offset z*(X, Y); a move adds <see cref="IconOffsetOfPan"/>.
    /// </summary>
    private (float K, float X, float Y) IconMatchOf(float yaw, float pitch)
    {
        var off = HiddenAttachments;
        var groupsOff = WeaponAttachments.HiddenGroups(off);
        var icon = new OutlineIconOptions { View = OutlineView.Custom, Yaw = yaw + WeaponBaseYaw, Pitch = pitch };
        if (_scene is { } sc)
            return sc.OutlineIconOf(icon, yaw + WeaponBaseYaw, WeaponRenderWidth, WeaponRenderHeight, pitch, 1f, 0f, 0f, groupsOff, off) is { } q ? (q.Zoom, q.OffsetX, q.OffsetY) : (1f, 0f, 0f);
        // The outline preview frames the gun the way the icon does, only on larger size limits: same centre, other scale.
        // shortcut: before the models load (Generate mode opened with the preview closed) this is 1:1; upgrade if the owner sees a jump.
        if (_weaponModels is not { Count: > 0 } models) return (1f, 0f, 0f);
        var shown = models.Where((m, i) => i >= _weaponPaths.Count || !WeaponAttachments.IsHidden(_weaponPaths[i], off))
            .SelectMany(m => OutlineIconRenderer.VisibleMeshes(m, groupsOff)).ToList();
        return OutlineIconRenderer.FitOf(shown, PreviewOutline with { View = OutlineView.Custom, Yaw = yaw + WeaponBaseYaw, Pitch = pitch }) is { } p
            && OutlineIconRenderer.FitOf(shown, icon) is { } i ? (p.PixelsPerMetre / i.PixelsPerMetre, 0f, 0f) : (1f, 0f, 0f);
    }

    /// <summary>
    /// The outline icon offset (fractions of 256x128, +X right, +Y up) that moves the gun as the pan moves it in the 2:1
    /// picture. The pan is a camera move: +X is half a picture width to the left, +Y one picture height down (OutfitPreview.ViewFraming).
    /// </summary>
    private static (float X, float Y) IconOffsetOfPan(double panX, double panY) => ((float)(-panX / 2), (float)-panY);

    /// <summary>Weapon view presets (owner, Nov 7: the angle belongs to the 3D preview). Each keeps the attachments.</summary>
    public Command SideView => new(() => { Yaw = 0; Pitch = 0; });
    public Command ThreeQuarterView => new(() => { Yaw = 45; Pitch = 20; });
    public Command TopView => new(() => { Yaw = 0; Pitch = 80; });

    /// <summary>The models read for the picture; null until they are loaded.</summary>
    public OutfitPreview? Scene { get => _scene; private set { Set(ref _scene, value); LabelOwnArm(); Raise(nameof(ArmNote)); } }

    /// <summary>
    /// The first arm choice is "Own arm", or "No arm" when the model has no left arm: the game has no plain arm for
    /// Snake, so the picture shows nothing there (owner, Nov 5). The entry is replaced in place, with no reload.
    /// </summary>
    private void LabelOwnArm()
    {
        if (Arms.Count < 2 || Arms[0].Id.Length != 0) return;
        string label = _scene is { HasOwnLeftArm: false } ? "No arm" : "Own arm";
        if (Arms[0].Label == label) return;
        bool selected = _arm == Arms[0];
        Arms[0] = new Choice("", label);
        if (selected) { _arm = Arms[0]; Raise(nameof(ArmChoice)); }
    }
    public Bitmap? Image { get => _image; private set { Set(ref _image, value); Raise(nameof(Note)); } }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(Note)); } }
    public string Error { get => _error; private set { Set(ref _error, value); Raise(nameof(Note)); } }
    // After a picture is drawn, the scene knows which textures it could not find; those parts are drawn plain, which
    // looks like white hair or a white strap. The note says so (see OutfitPreview.MissingTexturesNote).
    public string Note => Error.Length > 0 ? Error : Busy ? "Working…" : Image != null && Scene != null ? Scene.MissingTexturesNote : "";
    public ObservableCollection<Choice> Characters { get; } = [];
    public ObservableCollection<Choice> Looks { get; } = [];

    public double Yaw
    {
        get => _yaw;
        set { value = ((value % 360) + 360) % 360; if (Set(ref _yaw, value)) { Raise(nameof(YawText)); Redraw(_dragging); } }
    }
    public string YawText => $"{_yaw:0}°";

    private double _pitch;
    /// <summary>
    /// How far above (+) or below (−) the model the camera sits, in degrees. The owner asked for it: "3D previewer does
    /// not allow for zooming nor the ability to rotate from above/underneath". Clamped to ±80 so the model never flips.
    /// </summary>
    public double Pitch
    {
        get => _pitch;
        set
        {
            value = Math.Clamp(value, -80, 80);
            if (Set(ref _pitch, value)) { Raise(nameof(PitchText)); Redraw(_dragging); }
        }
    }
    public string PitchText => $"{_pitch:0}°";

    private double _zoom = 1;
    /// <summary>
    /// 1 = the whole model in frame; up to 5 moves the camera in. The owner asked for zooming, then for more of it:
    /// "Zoom should be higher than 300, go to 500".
    /// </summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            value = Math.Clamp(value, 0.4, 5);
            if (Set(ref _zoom, value)) { Raise(nameof(ZoomText)); RaiseFrame(); Redraw(_dragging); }
        }
    }
    public string ZoomText => $"{_zoom * 100:0}%";

    /// <summary>
    /// How far the model has been dragged off centre, in picture fractions. The owner asked for this: "We also need a way
    /// to drag the model in the 3D viewer so we can look at different parts of the model." Turning alone cannot reach the
    /// far end of a long model, so a right-drag (or Shift + left-drag) slides the model instead of spinning it.
    /// </summary>
    public double PanX => _panX;
    public double PanY => _panY;
    public bool Panned => _panX != 0 || _panY != 0;

    /// <summary>Puts the camera back where it started (front, level, whole model, centred).</summary>
    public Command ResetView => new(() => { Yaw = 0; Pitch = 0; Zoom = 1; PanTo(0, 0); });

    /// <summary>
    /// The turn and tilt of the game's icon of an item (issue #23): a head option's head-icon turn (<see cref="HeadIcon.Gear"/>),
    /// D-Dog's and D-Horse's left side (<see cref="IconFraming.DogBuddy"/>: camera on +X, which is a turn of -90), else the
    /// default view (Quiet's chest from the front; a weapon's 0 is already its side view, see <see cref="WeaponBaseYaw"/>).
    /// </summary>
    public static (double Yaw, double Pitch) IconViewOf(ProjectItem? item) => ModelOf(item) switch
    {
        HeadOptionItem => ((HeadIcon.Gear.Yaw + 360) % 360, HeadIcon.Gear.Pitch),
        BuddyOutfitItem { Spec.Buddy: "dog" or "horse" } => (270, 0),
        _ => (0, 0),
    };

    /// <summary>
    /// "Default view" and "Default framing" in Generate mode: the 3D preview turns to the view of the game's icon, with no
    /// zoom or pan, and an outfit or buddy draft gets its default framing back. The owner (issue #23): "have a default head
    /// icon view button" and "Buddy icon too as they're specific zoom/angle".
    /// </summary>
    public Command DefaultView => new(() =>
    {
        if (_gen is { FollowsView: false } g) g.DefaultFraming.Execute(null);
        // Issue #67: a weapon's default is the automatic icon (yaw 0, zoom 1, no offset), so the preview shows that picture; the attachments stay.
        if (_gen is { IsWeaponIcon: true }) { ShowWeaponIconView(0, 0, 1, 0, 0); return; }
        var (y, p) = IconViewOf(_gen?.Item ?? _item?.Item);
        Yaw = y; Pitch = p; Zoom = 1; PanTo(0, 0);
    });

    /// <summary>
    /// Slides the model by a drag in pixels, as a fraction of the picture. Clamped to ±1.5 pictures so the model can be
    /// pushed right out of frame and found again, but not lost for good.
    ///
    /// The sign is "pull": dragging right moves the model right, dragging down moves it down. The pan is a camera offset,
    /// so moving the camera right would move the model left - hence the minus. The owner reported the first version as
    /// inverted: "it should be you press right click and then 'pull' the model. leading it to being inverted."
    /// </summary>
    public void PanBy(double dx, double dy)
    {
        int w = RenderWidth, h = RenderHeight;
        PanTo(_panX - dx / w, _panY + dy / h);
    }

    private void PanTo(double x, double y)
    {
        x = Math.Clamp(x, -1.5, 1.5); y = Math.Clamp(y, -1.5, 1.5);
        if (x == _panX && y == _panY) return;
        _panX = x; _panY = y;
        Raise(nameof(PanX)); Raise(nameof(PanY)); Raise(nameof(Panned)); RaiseFrame();
        Redraw(_dragging);
    }

    /// <summary>
    /// Whether the 3D preview is enlarged on the right (see <see cref="DockWidth"/>). The owner asked for this:
    /// "An option to maximise and go back for the 3d Preview is also ideal as larger more detailed models can be squished
    /// easily." The picture is drawn at twice the resolution while maximised, so it is genuinely sharper and not just a
    /// stretched bitmap.
    /// </summary>
    public bool Maximised
    {
        get => _maximised;
        set
        {
            if (!Set(ref _maximised, value)) return;
            Raise(nameof(MaximiseLabel)); Raise(nameof(BoxWidth)); Raise(nameof(BoxHeight));
            Raise(nameof(WeaponBoxWidth)); Raise(nameof(WeaponBoxHeight));
            Raise(nameof(DockWidth)); RaiseFrame();
            Redraw(false);
        }
    }
    public string MaximiseLabel => _maximised ? "Restore" : "Maximise";
    public Command ToggleMaximise => new(() => Maximised = !Maximised);

    /// <summary>
    /// Draws the preview at half resolution, for a slower machine. The owner asked for a speed switch beside "All 3D: on":
    /// "The 3D Preview is very slow, it was faster in mock ups. Anyway to make it optionally GPU Bound? Put it by All 3D:on."
    /// See <see cref="Settings.FastPreview"/> for why this is a resolution switch rather than GPU acceleration.
    /// </summary>
    public bool FastPreview
    {
        get => _s.Settings.FastPreview;
        set
        {
            if (_s.Settings.FastPreview == value) return;
            _s.Settings.FastPreview = value; _s.Settings.Save();
            Raise(nameof(FastPreview)); Raise(nameof(FastPreviewLabel));
            Redraw(false);
        }
    }
    public string FastPreviewLabel => _s.Settings.FastPreview ? "Fast 3D: on" : "Fast 3D: off";
    public Command ToggleFastPreview => new(() => FastPreview = !FastPreview);

    /// <summary>The size the outfit picture is drawn at: the box size, doubled while maximised.</summary>
    public int RenderWidth => _maximised ? PictureWidth * 2 : PictureWidth;
    public int RenderHeight => _maximised ? PictureHeight * 2 : PictureHeight;
    public int WeaponRenderWidth => _maximised ? WeaponWidth * 2 : WeaponWidth;
    public int WeaponRenderHeight => _maximised ? WeaponHeight * 2 : WeaponHeight;

    /// <summary>The size of the picture box on screen. 220×330 fits the side panel; 360×540 is the maximised view.</summary>
    public double BoxWidth => _maximised ? 360 : 220;
    public double BoxHeight => _maximised ? 540 : 330;
    public double WeaponBoxWidth => _maximised ? 720 : 320;
    public double WeaponBoxHeight => _maximised ? 360 : 160;
    /// <summary>
    /// Issue #31: maximised, the panel stays on the right and is 60 % of the editor width; it does not cover the item
    /// list. Otherwise NaN: the panel takes the width of its contents.
    /// </summary>
    public double DockWidth => _maximised && _editorWidth > 0 ? Math.Round(_editorWidth * 0.6) : double.NaN;

    private bool _showIconFrame;
    /// <summary>
    /// Draws the 256×128 icon border over the picture, so the user can see which part of the model the icon is cut from.
    /// The owner asked: "We cannot see where the icon is being made relatively in the 3d preview, nor toggle that on/off".
    /// </summary>
    public bool ShowIconFrame
    {
        get => _showIconFrame;
        set { if (Set(ref _showIconFrame, value)) { Raise(nameof(IconFrameNote)); RaiseFrame(); Redraw(false); } }
    }
    public string IconFrameNote => _showIconFrame
        ? "The white box is the icon border: the 256×128 part of the picture that the icon shows. It shows the border. It does not change the icon."
        : "";
    /// <summary>
    /// Where the icon border sits in the picture, in picture fractions (0-1), for the overlay to draw. For an outfit it is
    /// the window of the icon's own chest framing in the front view (issue #9), so it follows the zoom and the pan. In
    /// Generate mode the picture is drawn with the icon framing, so the border is the fixed 256×128 middle of the picture.
    /// </summary>
    public (double X, double Y, double W, double H) IconFrameRect
    {
        get
        {
            // A weapon's Generate icon is the whole preview picture (WeaponIconOf), which has the icon's 2:1 shape.
            if (GenerateMode && IsWeapon) return (0, 0, 1, 1);
            if (GenerateMode) return (0.5 - 128.0 / PictureWidth, 0.5 - 64.0 / PictureHeight, 256.0 / PictureWidth, 128.0 / PictureHeight);
            if (Shown is OutfitItem or BuddyOutfitItem && _scene is { } sc && _item is { } it
                && sc.IconRect(ResultFraming(_s.Intake?.IconOf(it.Item) ?? _s.Intake?.DefaultIconOf(it.Item) ?? IconSettings.ChestDefault), RenderWidth, RenderHeight, (float)_zoom, (float)_panX, (float)_panY) is { } r) return r;
            // A weapon, or a model without chest bones: the window is the whole width of the picture at zoom 1 and
            // shrinks as the camera moves in. It does not move with the pan.
            double w = 1.0 / Math.Max(0.05, _zoom);
            double h = (IsWeapon ? 128.0 / WeaponRenderHeight : 180.0 / RenderHeight) / Math.Max(0.05, _zoom);
            return (0.5 - w / 2, 0.5 - h / 2, w, h);
        }
    }

    // The same rectangle in the pixels of the box on screen, which is what the Canvas overlay positions with.
    private double FrameBoxWidth => IsWeapon ? WeaponBoxWidth : BoxWidth;
    private double FrameBoxHeight => IsWeapon ? WeaponBoxHeight : BoxHeight;
    public double IconFrameLeft => IconFrameRect.X * FrameBoxWidth;
    public double IconFrameTop => IconFrameRect.Y * FrameBoxHeight;
    public double IconFrameWidth => IconFrameRect.W * FrameBoxWidth;
    public double IconFrameHeight => IconFrameRect.H * FrameBoxHeight;
    private void RaiseFrame()
    {
        foreach (var n in new[] { nameof(IconFrameRect), nameof(IconFrameLeft), nameof(IconFrameTop), nameof(IconFrameWidth), nameof(IconFrameHeight), nameof(FrameShown) }) Raise(n);
    }

    // ---- Generate mode (issue #9) ----
    private ItemVm? _gen;
    private Bitmap? _result;
    /// <summary>The item whose icon Generate mode makes; null outside the mode.</summary>
    public ItemVm? Generating => _gen;
    public bool GenerateMode => _gen != null;
    /// <summary>The turn, from-above, zoom and reset controls move the view only, so they hide in the 3D render of Generate mode.
    /// A head option's icon is the view itself (issue #17), so they stay.</summary>
    public bool ViewControlsOn => _gen == null || _gen.CropOn || _gen.FollowsView;
    /// <summary>The icon border: always in the 3D render of Generate mode, never in its camo crop or for a head option (its
    /// icon is drawn in the head-icon style, not cut from the picture: issue #23), else the Icon toggle.</summary>
    public bool FrameShown => GenerateMode ? _gen is { CropOn: false, FollowsView: false } : _showIconFrame;
    /// <summary>The live 256×128 result of Generate mode.</summary>
    public Bitmap? Result { get => _result; private set => Set(ref _result, value); }

    /// <summary>Opens the 3D panel in Generate mode for an item: the picture shows the icon framing from the front.</summary>
    /// <param name="item">The item.</param>
    public void StartGenerate(ItemVm item)
    {
        if (_gen != null && _gen != item) _gen.CancelGenerate.Execute(null);
        if (_gen != item) { _gen = item; item.DraftChanged += OnDraft; }
        _dragging = false;
        // The view starts at the game icon's view. A head option keeps a view the user set (its icon is that view), but the
        // untouched front view turns to the head-icon view, so Generate now gives the vanilla three-quarter icon (issue #23).
        if (item.IsWeaponIcon) { }
        else if (!item.GeneratesFromView || (_yaw == 0 && _pitch == 0 && _zoom == 1 && !Panned)) { (_yaw, _pitch) = IconViewOf(item.Item); _zoom = 1; _panX = 0; _panY = 0; }
        foreach (var n in new[] { nameof(Yaw), nameof(YawText), nameof(Pitch), nameof(PitchText), nameof(Zoom), nameof(ZoomText), nameof(PanX), nameof(PanY), nameof(Panned) }) Raise(n);
        _dockChosen = true; DockOpen = true;
        if (Mode != PreviewMode.Stills)
        {
            _forced = true; RaiseMode();
            if (_scene == null && _weaponModels == null) Fire(LoadAsync(_item));
        }
        item.BeginGenerate();
        // A weapon starts at the view, the attachments, the zoom and the move of its icon (the icon is a snapshot of the preview).
        if (item.IsWeaponIcon && item.Draft is { } d)
        {
            ShowAttachmentsOf(d);
            ShowWeaponIconView(d.Yaw, d.Pitch, d.Zoom, d.OffsetX, d.OffsetY);
        }
    }

    /// <summary>Sets the preview to the view that <see cref="WeaponIconOf"/> turns into an icon with this turn, tilt, zoom and offset.</summary>
    private void ShowWeaponIconView(float yaw, float pitch, float zoom, float offsetX, float offsetY)
    {
        Yaw = yaw; Pitch = pitch;
        var (k, x0, y0) = IconMatchOf(yaw, pitch);
        double z = zoom / k;
        Zoom = z; PanTo(-2.0 * (offsetX - z * x0), -(offsetY - z * y0));
        _ = RenderWeaponAsync();
    }

    private void OnDraft()
    {
        if (_gen is { InGenerate: false } g) { g.DraftChanged -= OnDraft; _gen = null; _renderTicket++; Result = null; if (_forced) { _forced = false; RaiseMode(); } }
        foreach (var n in new[] { nameof(Generating), nameof(GenerateMode), nameof(ViewControlsOn) }) Raise(n);
        // Issue #88: the picture shows the look of the slot that Generate now draws.
        if (_gen is { InGenerate: true, SlotIndex: > 0 and var k } && k <= Looks.Count && LookChoice != Looks[k - 1]) LookChoice = Looks[k - 1];
        RaiseFrame();
        Redraw(_dragging);
    }

    /// <summary>The framing the icon of the shown outfit is drawn with: the one the port uses, moved by the settings.</summary>
    /// <param name="s">The icon settings.</param>
    /// <returns>The framing.</returns>
    public IconFraming ResultFraming(IconSettings s) => _item != null && _s.Intake != null
        ? _s.Intake.ChestFraming(_item.Item, s)
        : new IconFraming { OffsetX = s.OffsetX, OffsetY = s.OffsetY, Zoom = s.Zoom };

    /// <summary>
    /// The framing of the 3D picture in Generate mode: the icon framing widened to the picture height, so the 256×128
    /// middle of the 360×540 picture is the icon (the renderer scales X by the picture height too).
    /// </summary>
    /// <param name="s">The icon settings.</param>
    /// <returns>The framing.</returns>
    public IconFraming PictureFraming(IconSettings s)
    {
        const float k = PictureHeight / 128f;
        var f = ResultFraming(s);
        return f with { Zoom = f.Zoom / k, OffsetX = f.OffsetX / k, OffsetY = f.OffsetY / k };
    }

    // A drag in the 3D render of Generate mode moves the icon framing. The sign is "pull", as for the view pan.
    private bool GenerateDrag(double dx, double dy, double boxWidth, double boxHeight)
    {
        if (_gen is not { GeneratesFromView: false, Draft: { Style: IconStyle.ChestRender } d } g) return false;
        g.FrameX = (d.OffsetX - dx / boxWidth * PictureWidth / 256) * 100;
        g.FrameY = (d.OffsetY + dy / boxHeight * PictureHeight / 128) * 100;
        return true;
    }

    public Choice? Character { get => _who; set { if (Set(ref _who, value) && value != null) { FillLooks(); FillHeads(); FillArms(); Fire(LoadSceneAsync()); } } }
    public Choice? LookChoice { get => _look; set { if (Set(ref _look, value) && value != null) Fire(LoadSceneAsync()); } }

    private Choice? _head;
    /// <summary>
    /// The head option drawn on the body, or "The outfit's own head". The owner asked for this: "We need to be able to
    /// display head options in the 3D preview". Only the head options that belong to the project are listed, because a
    /// head option's model is a file the mod carries.
    /// </summary>
    public Choice? HeadChoice
    {
        get => _head;
        set { if (Set(ref _head, value)) { Raise(nameof(HeadNote)); Fire(LoadSceneAsync()); } }
    }
    public ObservableCollection<Choice> Heads { get; } = [];
    public bool HeadsOn => Heads.Count > 1;

    private Choice? _arm;
    /// <summary>
    /// The arm drawn on Snake or the Avatar (issue #66): "Own arm" (the model's own left arm, never a bionic arm) or one of the bionic arms. The
    /// owner asked for this: "Snake 3d preview should have the arm visible, and switch between them unless otherwise
    /// overridden by a mod." Only Snake and the Avatar have these arms, so the list is empty for
    /// everyone else.
    /// </summary>
    public Choice? ArmChoice
    {
        get => _arm;
        set { if (Set(ref _arm, value)) { Raise(nameof(ArmNote)); Fire(LoadSceneAsync()); } }
    }
    public ObservableCollection<Choice> Arms { get; } = [];
    public bool ArmsOn => Arms.Count > 1;

    /// <summary>
    /// The arm package the preview is drawing: empty for "Own arm" (no arm package, so the look's bionic arm is not
    /// added), or null when there is no arm list (the look decides). Issue #34: null for "Own arm" showed the bionic arm.
    /// </summary>
    private string? ArmPackage => _arm?.Id;

    /// <summary>
    /// What the picture shows for "Own arm" when the model has no left arm. The game has no plain (not bionic) arm
    /// package for Snake (the arm packages arm0-arm7 are all bionic), so there is nothing to draw in its place.
    /// </summary>
    public string ArmNote => _arm is { Id.Length: 0 } && ArmsOn && _scene is { HasOwnLeftArm: false }
        ? "This model has no left arm, and the game has no plain arm for Snake or the Avatar. The preview shows no left arm."
        : "";

    /// <summary>Lists the arms for the character being previewed (Snake and Avatar).</summary>
    private void FillArms()
    {
        Arms.Clear();
        Arms.Add(new Choice("", "Own arm"));
        // Issue #66: the Avatar wears the same arm packages as Snake (the game has no Avatar-only arm).
        if (_who?.Id is nameof(PlayerBranch.Snake) or nameof(PlayerBranch.Avatar))
            foreach (var (name, package) in SuitRender.Arms) Arms.Add(new Choice(package, name));
        _arm = Arms.Count > 1 ? Arms[0] : null;
        Raise(nameof(ArmChoice)); Raise(nameof(ArmsOn)); Raise(nameof(ArmNote));
    }

    /// <summary>A tick on the Heads worn tab changes the list; the chosen head stays when it is still ticked, else the picture goes back to the outfit's own head.</summary>
    private void RefreshHeads()
    {
        if (_who == null || Shown is not OutfitItem) return;
        var before = Heads.Select(c => c.Id).ToList();
        string keep = _head?.Id ?? "";
        FillHeads(keep, before);
        if (before.SequenceEqual(Heads.Select(c => c.Id)) && keep == _head?.Id) return;
        if (keep != _head?.Id) Fire(LoadSceneAsync());
    }

    /// <summary>The Heads drop-down entries for the character being previewed (issue #40): every ticked head.</summary>
    private List<PreviewHead> _headEntries = [];

    /// <summary>Lists the heads the outfit ticks and the mod's head options for the character being previewed.</summary>
    private void FillHeads(string? keep = null, List<string>? unchangedIfSame = null)
    {
        var entries = new List<PreviewHead>();
        if (_s.Project != null && _who != null && Enum.TryParse<PlayerBranch>(_who.Id, out var who) && Shown is OutfitItem o && o.Spec.Branches.TryGetValue(who, out var branch))
            entries = PreviewHeads.For(_s.Project.Items.Select(ModelOf).OfType<HeadOptionItem>(), branch, who);
        _headEntries = entries;
        // Same list as before: keep the drop-down as it is, so an edit does not reset the user's selection.
        if (unchangedIfSame != null && unchangedIfSame.SequenceEqual(new[] { "" }.Concat(entries.Select(e => e.Id)))) return;
        Heads.Clear();
        Heads.Add(new Choice("", "The outfit's own head"));
        foreach (var e in entries) Heads.Add(new Choice(e.Id, e.Title));
        _head = Heads.FirstOrDefault(c => c.Id == keep) ?? Heads[0];
        Raise(nameof(HeadChoice)); Raise(nameof(HeadsOn)); Raise(nameof(HeadNote));
    }

    /// <summary>
    /// What the picture is showing for the head, when it is not simply the chosen head's own model. A head option that is
    /// only a soldier face id (the vanilla headgear the tool lists as a head) has no model of its own to draw, so the
    /// preview keeps the wearer's default soldier face; saying so is better than a picture that silently ignores the
    /// choice.
    /// </summary>
    public string HeadNote
    {
        get
        {
            return CurrentHead?.Note ?? "";
        }
    }

    /// <summary>The head option the preview is drawing, or null for the outfit's own head.</summary>
    private HeadOptionItem? HeadItem => CurrentHead is { Kind: PreviewHeadKind.Mod } e ? e.Mod : null;
    private PreviewHead? CurrentHead => _head is { Id.Length: > 0 } c ? _headEntries.FirstOrDefault(e => e.Id == c.Id) : null;

    public void BeginDrag() => _dragging = true;
    public void EndDrag() { _dragging = false; Redraw(false); }
    /// <summary>
    /// Turns the model by a drag in pixels. Sideways turns it about its upright axis; up and down tips the camera over the
    /// top or under the bottom. The owner asked for this: "3D preview should be able to be controlled by the mouse, so
    /// mouse wheel to zoom in, click and hold to spin".
    ///
    /// The drag is scaled by the size of the box it happened in, so a full-width drag is always half a turn and a
    /// full-height drag is always a quarter turn. Before this the turn was a flat 0.8°/px, so the same drag turned the
    /// model twice as far in the maximised view as in the strip - which is what the owner meant by "When I tried to click
    /// and drag after maximise it instead controlled 'from above' so the window isnt matching the mouse": in the tall
    /// maximised box a mostly-sideways drag carries enough vertical movement to tip the camera as well.
    /// </summary>
    public void DragBy(double dx, double dy, double boxWidth = 0, double boxHeight = 0)
    {
        double w = boxWidth > 1 ? boxWidth : BoxWidth;
        double h = boxHeight > 1 ? boxHeight : BoxHeight;
        if (GenerateDrag(dx, dy, w, h)) return;
        if (dx != 0) Yaw += dx / w * 180;
        // Vertical is deliberately half as sensitive as horizontal: tipping the camera is a coarse adjustment, and a
        // little stray vertical movement during a sideways drag should not swing the view over the top.
        if (dy != 0) Pitch -= dy / h * 90;
    }

    /// <summary>
    /// Slides the model instead of turning it, for a right-drag or Shift + left-drag. The owner asked for this: "We also
    /// need a way to drag the model in the 3D viewer so we can look at different parts of the model."
    /// </summary>
    public void DragPanBy(double dx, double dy) { if (!GenerateDrag(dx, dy, BoxWidth, BoxHeight)) PanBy(dx, dy); }

    /// <summary>
    /// Zooms by a wheel notch: one notch is 10% of the current zoom, so it feels the same near and far. The owner asked
    /// for the wheel to zoom.
    /// </summary>
    public void ZoomBy(int notches)
    {
        if (notches == 0) return;
        if (_gen is { GeneratesFromView: false, Draft: { Style: IconStyle.ChestRender } d } g) { g.FrameZoom = d.Zoom * Math.Pow(1.1, notches) * 100; return; }
        Zoom = _zoom * Math.Pow(1.1, notches);
    }

    /// <summary>The item the editor now shows.</summary>
    public async Task LoadAsync(ItemVm? item)
    {
        // Another item ends Generate mode; its draft is not kept.
        if (_gen != null && _gen != item) _gen.CancelGenerate.Execute(null);
        _item = item;
        Reload.Raise();
        Raise(nameof(Visible)); Raise(nameof(StillsOn));
        Characters.Clear(); Looks.Clear(); Heads.Clear(); Arms.Clear(); _head = null; _arm = null; Scene = null; Image = null; Error = "";
        Raise(nameof(HeadChoice)); Raise(nameof(HeadsOn)); Raise(nameof(ArmChoice)); Raise(nameof(ArmsOn));
        Raise(nameof(IsWeapon)); Raise(nameof(ShowParts)); Raise(nameof(IsOutfitItem)); Raise(nameof(WeaponStillsOn)); Raise(nameof(IsHead)); _weaponModels = null;
        // An existing mod's item: its model's loose textures are found the first time it is drawn.
        if (item?.Item is ExistingItem { Model: not null } ex && _s.Project?.Existing is { } data && Mode != PreviewMode.Off)
        {
            await Task.Run(() => ExistingMod.LoadPreviewTextures(data, ex));
            if (_item != item) return;
        }
        var shown = Shown;
        if (shown is BuddyOutfitItem bi && Mode != PreviewMode.Off) { await LoadBuddyAsync(bi); return; }
        if (shown is HeadOptionItem hi && Mode != PreviewMode.Off) { await LoadHeadAsync(hi); return; }
        if (PackageOf(shown) != null && shown is { } wi && Mode != PreviewMode.Off) { await LoadWeaponAsync(wi); return; }
        if (shown is not OutfitItem o || Mode == PreviewMode.Off) return;
        foreach (var c in CharacterChoices(o)) Characters.Add(c);
        _who = Characters.FirstOrDefault(); Raise(nameof(Character));
        FillLooks();
        // The HEAD and ARM lists have to be filled here as well as from the Character setter. Setting _who directly above
        // does not run that setter, so on opening an outfit the lists stayed empty and the HEAD drop-down was hidden until
        // the character was switched - which is why a head option showed for DD female but not for DD male (the male
        // branch sorts first, so it is the one shown on open).
        FillHeads();
        FillArms();
        await LoadSceneAsync();
    }

    private List<Choice> LookChoices()
    {
        if (Shown is not OutfitItem o || _who == null || !Enum.TryParse<PlayerBranch>(_who.Id, out var who) || !o.Spec.Branches.TryGetValue(who, out var b)) return [];
        // The labels are the Sortie slot names, numbered as the Sortie mock-up does when a slot has no name (issue #78).
        string Name(string? id, int k) => (id == null || _item == null ? "" : _item.GetText(id, "eng", false)).Trim() is { Length: > 0 } t ? t : $"Look {k}";
        var list = new List<Choice> { new("base", Name(b.Base.DisplayName, 1)) };
        // DisplayName is a lang ID; the text is held by the item the user edits (the ExistingItem for an existing mod).
        for (int i = 0; i < b.Variants.Count; i++)
        {
            list.Add(new Choice(i.ToString(), Name(b.Variants[i].DisplayName, i + 2)));
        }
        return list;
    }

    private void FillLooks(string keep = "base")
    {
        Looks.Clear();
        foreach (var c in LookChoices()) Looks.Add(c);
        _look = Looks.FirstOrDefault(l => l.Id == keep) ?? Looks.FirstOrDefault(l => l.Id == "base") ?? Looks.FirstOrDefault(); Raise(nameof(LookChoice));
    }

    /// <summary>A look added, removed, moved or renamed on the Sortie tab (or an undo): the LOOK list follows, and the chosen look stays when it is still there.</summary>
    private static List<Choice> CharacterChoices(OutfitItem o) => o.Spec.Branches.Keys.Order().Select(k => new Choice(k.ToString(), LuaWriter.BranchName(k))).ToList();

    /// <summary>A tick or an untick of a wearer changes the Character list at once, with no reload (issue #78).</summary>
    private void RefreshCharacters()
    {
        if (_who == null || Shown is not OutfitItem o || Mode == PreviewMode.Off) return;
        var now = CharacterChoices(o);
        if (now.SequenceEqual(Characters)) return;
        string keep = _who.Id; // clearing the list can make the drop-down set Character to null
        Characters.Clear();
        foreach (var c in now) Characters.Add(c);
        var pick = Characters.FirstOrDefault(c => c.Id == keep);
        if (pick != null) { _who = pick; Raise(nameof(Character)); }
        else { _who = null; Character = Characters.FirstOrDefault(); }
    }

    private void RefreshLooks()
    {
        if (_who == null || Shown is not OutfitItem) return;
        var now = LookChoices();
        if (now.SequenceEqual(Looks)) return;
        FillLooks(_look?.Id ?? "base");
        // A rename changes only the name; a move or a removal can put another look at the chosen index, so the picture is made again.
        if (ChosenLook() is not { } cur || _sceneLook == null || cur with { DisplayName = null } != _sceneLook with { DisplayName = null }) Fire(LoadSceneAsync());
    }

    private Look? _sceneLook;

    private Look? ChosenLook()
    {
        if (Shown is not OutfitItem o || _who == null || _look == null || !Enum.TryParse<PlayerBranch>(_who.Id, out var who) || !o.Spec.Branches.TryGetValue(who, out var branch)) return null;
        return _look.Id == "base" ? branch.Base : int.TryParse(_look.Id, out int li) ? branch.Variants.ElementAtOrDefault(li) : null;
    }

    private async Task LoadSceneAsync()
    {
        if (Shown is not OutfitItem o || _who == null || _look == null || Mode == PreviewMode.Off || _s.Game == null) return;
        if (!Enum.TryParse<PlayerBranch>(_who.Id, out var who) || !o.Spec.Branches.TryGetValue(who, out var branch)) return;
        if (ChosenLook() is not { } look) return;
        _sceneLook = look;
        int ticket = ++_loadTicket;
        Busy = true; Error = "";
        var game = _s.Game;
        try
        {
            var head = HeadItem;
            var arm = ArmPackage;
            var vanilla = CurrentHead is { Kind: PreviewHeadKind.Vanilla } v ? v : null;
            var scene = await Task.Run(() => OutfitPreview.For(game, PathDictionary.Bundled, o, who, look, head, arm, vanilla?.GamePackage, vanilla?.Groups));
            if (ticket != _loadTicket) return;
            Scene = scene;
            if (Mode == PreviewMode.Stills) await RenderAsync(false);
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (ticket == _loadTicket) Error = "The preview can't be made: " + e.Message;
        }
        finally { if (ticket == _loadTicket) Busy = false; }
    }

    /// <summary>Draws the still again after a change of the view. RenderAsync draws a weapon with RenderWeaponAsync.</summary>
    private void Redraw(bool quick) { if (Mode == PreviewMode.Stills) _ = RenderAsync(quick); }

    private async Task RenderAsync(bool quick)
    {
        if (IsWeapon) { await RenderWeaponAsync(); return; }
        var scene = _scene;
        if (_gen is { GeneratesFromView: false, Draft: { } draft } gen && Mode == PreviewMode.Stills) { await RenderGenerateAsync(gen, draft, scene, quick); return; }
        if (scene == null || Mode != PreviewMode.Stills) return;
        int ticket = ++_renderTicket;
        float yaw = (float)_yaw, pitch = (float)_pitch, zoom = (float)_zoom;
        // A drag fires dozens of renders a second. Drawing those at full quality cannot keep up, which is what made the
        // preview feel slow; the sharp picture is drawn once on release (EndDrag).
        //
        // Supersampling is 1 here and 2-3 only in the icon renderer. The preview is a 360×540 picture on screen, where
        // 2× supersampling costs four times the pixels for a difference nobody can see; the icon is 256×128 and is the
        // thing that actually ships, so it keeps its quality. This alone is the single biggest speed-up.
        int ss = 1;
        int w = RenderWidth, h = RenderHeight;
        if (quick || _s.Settings.FastPreview) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        double panX = _panX, panY = _panY;
        try
        {
            var img = await Task.Run(() => scene.RenderStill(yaw, w, h, ss, pitch, zoom, null, (float)panX, (float)panY));
            if (ticket != _renderTicket) return;
            Image = ToBitmap(img);
            // A head option in Generate mode: the live result is the head icon that Generate now makes at this view (issue #23).
            if (_gen is { GeneratesFromView: true, InGenerate: true })
            {
                var icon = await Task.Run(() => scene.RenderViewIcon(yaw, PictureWidth, PictureHeight, pitch, zoom, (float)panX, (float)panY, quick ? 2 : 0));
                if (ticket != _renderTicket) return;
                Result = ToBitmap(icon);
            }
        }
        catch (Exception e) { RenderFailed(ticket, e); return; }
        RaiseFrame();
    }

    /// <summary>The renders run without an await, so an error in them is shown and logged here, or it is lost.</summary>
    /// <summary>Runs a preview load that nobody awaits. An error the load does not catch is logged and shown, not lost.</summary>
    internal async void Fire(Task load)
    {
        try { await load; }
        catch (Exception e) { DevLog.Error("preview", e); Error = "The preview can't be made: " + e.Message; Busy = false; }
    }

    private void RenderFailed(int ticket, Exception e)
    {
        DevLog.Error("preview", e);
        if (ticket == _renderTicket) Error = "The preview can't be drawn: " + e.Message;
    }

    private Command? _generateIcon;
    /// <summary>
    /// "Generate now" in Generate mode: makes the icon and keeps the mode open (issue #17). An outfit's icon is made from
    /// the framing; a head option's icon is drawn in the head-icon style at the 3D preview's view (issue #23).
    /// </summary>
    public Command GenerateIcon => _generateIcon ??= new Command(async () =>
    {
        if (_gen is not { InGenerate: true } g || g.IconBusy || _drawingIcon) return;
        if (g.IsWeaponIcon) { if (g.Draft is { } wd) await g.GenerateWeaponAsync(WeaponIconOf(wd)); return; }
        if (!g.GeneratesFromView) { g.GenerateFromDraft.Execute(null); return; }
        if (_scene is not { } scene) { Error = "This head has no model of its own to make an icon from."; return; }
        float yaw = (float)_yaw, pitch = (float)_pitch, zoom = (float)_zoom, panX = (float)_panX, panY = (float)_panY;
        // IconBusy goes on only once the picture is drawn, so a second click during the drawing would make a second icon.
        _drawingIcon = true;
        try
        {
            RgbaImage img;
            try { img = await Task.Run(() => scene.RenderViewIcon(yaw, PictureWidth, PictureHeight, pitch, zoom, panX, panY)); }
            catch (Exception e) when (e is InvalidDataException or FileNotFoundException or InvalidOperationException or KeyNotFoundException or IOException) { Error = "The icon can't be drawn: " + e.Message; return; }
            if (_gen == g) await g.UseViewIconAsync(img);
        }
        finally { _drawingIcon = false; }
    });
    private bool _drawingIcon;

    private static Bitmap ToBitmap(RgbaImage img) { using var ms = new MemoryStream(Png.Encode(img)); return new Bitmap(ms); }

    // Generate mode: the picture (the icon framing, or the normal view for a camo crop) and the live 256×128 result.
    private async Task RenderGenerateAsync(ItemVm gen, IconSettings d, OutfitPreview? scene, bool quick)
    {
        int ticket = ++_renderTicket;
        int w = RenderWidth, h = RenderHeight;
        if (quick || _s.Settings.FastPreview) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        bool crop = d.Style == IconStyle.CamoCrop;
        var picture = PictureFraming(d); var result = ResultFraming(d);
        float yaw = (float)_yaw, pitch = (float)_pitch, zoom = (float)_zoom, panX = (float)_panX, panY = (float)_panY;
        var intake = _s.Intake!;
        try
        {
            var (pic, res) = await Task.Run(() =>
            {
                var p = scene == null ? null : crop ? scene.RenderStill(yaw, w, h, 1, pitch, zoom, null, panX, panY) : scene.RenderIcon(picture, w, h, 1);
                var r = crop ? intake.PreviewCrop(gen.Item, d) : scene == null ? null : SuitIcon.Frame(scene.RenderIcon(result, 256, 128, quick ? 1 : 2), SuitIcon.DrawnMask());
                return (p, r);
            });
            if (ticket != _renderTicket) return;
            if (pic != null) Image = ToBitmap(pic);
            Result = res == null ? null : ToBitmap(res);
            Error = "";
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (ticket == _renderTicket) Error = "The icon can't be drawn: " + e.Message;
        }
    }
}

/// <summary>One attachment toggle of the weapon preview.</summary>
public sealed class AttachmentToggle(string key, string name, Action changed) : Observable
{
    private bool _on = true;
    public string Key { get; } = key;
    public string Name { get; } = name;
    public bool On { get => _on; set { if (Set(ref _on, value)) changed(); } }
    /// <summary>Sets the state with no new render (the caller draws once for all toggles).</summary>
    public void SetQuietly(bool on) { if (Set(ref _on, on)) Raise(nameof(On)); }
}
