using System.Collections.ObjectModel;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>A choice in a drop-down.</summary>
public sealed record Choice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One ground material's concealment value.</summary>
public sealed class MaterialRow(OutfitVm owner, string name, int value) : Observable
{
    private int _value = value;
    public string Name { get; } = name;
    public string Label { get; } = name.Replace("MTR_", "").Replace('_', ' ').ToLowerInvariant();
    public decimal Value
    {
        get => _value;
        set { int v = (int)Math.Clamp(value, 0, 100); if (v == _value) return; _value = v; owner.SetMaterial(Name, v); Raise(); }
    }
    public void Reload(int v) { _value = v; Raise(nameof(Value)); }
}

/// <summary>One look (sortie slot) in the list.</summary>
public sealed class LookRow : Observable
{
    private readonly OutfitVm _o;
    public int Index { get; }
    public Look Look { get; private set; }
    public LookRow(OutfitVm o, int index, Look look, bool isDefault) { _o = o; Index = index; Look = look; IsDefault = isDefault; }
    public bool IsDefault { get; private set; }
    /// <summary>Takes the look again after an edit or an undo. The row stays, so a box being typed in keeps its focus (issue #18).</summary>
    public void Reload(Look look, bool isDefault)
    {
        Look = look; IsDefault = isDefault;
        foreach (var n in new[] { nameof(Look), nameof(IsDefault), nameof(Label), nameof(Where), nameof(Arm), nameof(ArmOn), nameof(ArmTip), nameof(Logos), nameof(LogoHint), nameof(Logo), nameof(CanUp), nameof(CanDown), nameof(CanRemove), nameof(CanBeDefault), nameof(Up), nameof(Down), nameof(Remove), nameof(MakeDefault) }) Raise(n);
    }
    public bool CanUp => Index > 0;
    public bool CanDown => Index < _o.Looks.Count - 1;
    public bool CanRemove => _o.CanRemoveLook;
    /// <summary>#62: a vanilla-only outfit has no Main look, so no look is worn first.</summary>
    public bool CanBeDefault => !_o.VanillaOnly;
    public string Label { get => _o.LookLabel(Look); set => _o.SetLookLabel(Index, Look, value); }
    public string Where => Look.FpkPath.Split('/')[^1].Replace(".fpk", "");
    /// <summary>
    /// The arm choices. V Framework 1.7 has one bool per look (<c>enableArm</c>), and it is the <b>bionic</b> left arm,
    /// not the arm of the suit: true shows Snake's prosthetic, false hides it and also turns its functions off. The
    /// labels say so, because "Show the arm" / "Hide the arm" read as if they were about the suit's own sleeve.
    /// </summary>
    public static IReadOnlyList<string> Arms { get; } = ["As the suit (default)", "Bionic arm shown", "Bionic arm hidden"];
    public int Arm { get => Look.EnableArm switch { true => 1, false => 2, _ => 0 }; set { _o.SetLookArm(Index, value); Raise(); } }
    public bool ArmOn => _o.ArmAllowed;
    public string ArmTip => ArmOn ? "What the left arm does on this look" : PlayerBranches.NoBionicArmReason;
    /// <summary>
    /// The icon this look shows in the suit list and on its sortie slot. This is V Framework's per-look
    /// <c>iconFtexPath</c>, the only per-look icon key it has: the owner asked for "the logos displaying there" in the
    /// sortie, and that is this field. "As the outfit" leaves the key out so the look inherits the outfit's own icon.
    /// </summary>
    public static IReadOnlyList<string> BuiltInLogos { get; } = ["As the outfit"];

    /// <summary>
    /// The icon choices: "As the outfit", then the mod's own .ftex sheets by file name, then the vanilla suit icons.
    /// The owner asked for this: "Ideally icons we can preselect for the logos will have the names of what they are,
    /// their file names". A sheet is shown as its file name ("ui_st_sna4_alp") with its full path as the tooltip, so
    /// two sheets with the same name in different folders can still be told apart.
    /// </summary>
    public IReadOnlyList<string> Logos => _o.LogoChoices;

    /// <summary>The tooltip for the icon drop-down, naming the sheet currently chosen.</summary>
    public string LogoHint
    {
        get
        {
            var p = Look.IconFtexPath;
            if (string.IsNullOrEmpty(p)) return "Inherits the outfit's own icon.";
            if (IntakeResult.IsSlotIconPath(p)) return $"This slot's own icon: {p}";
            return p.Contains("EquipIcon") ? $"Vanilla suit icon: {p}" : $"This mod's own sheet: {p}";
        }
    }

    /// <summary>The .ftex path behind a choice, or null for "As the outfit".</summary>
    public string? LogoPath(int index) => _o.LogoPathAt(index);

    public int Logo
    {
        get
        {
            var p = Look.IconFtexPath ?? "";
            if (p.Length == 0) return 0;
            int i = _o.LogoIndex(p);
            return i < 0 ? 0 : i;
        }
        set
        {
            // A box whose list was replaced can report -1 before Logo is raised again: that is not a pick.
            if (value < 0 || value == Logo) return;
            var path = _o.LogoPathAt(value);
            _o.SetLookLogo(Index, path);
            Raise();
        }
    }
    public Command Up => new(() => _o.MoveLook(Index, -1), () => CanUp);
    public Command Down => new(() => _o.MoveLook(Index, 1), () => CanDown);
    public Command Remove => new(() => _o.RemoveLook(Index), () => CanRemove);
    public Command MakeDefault => new(() => _o.SetDefaultLook(Index));
}

/// <summary>A group of ground surfaces (greenery, rock...) with one bonus for all of them and a list of each surface.</summary>
public sealed class MaterialGroup(OutfitVm owner, string title, IReadOnlyList<MaterialRow> rows) : Observable
{
    private bool _open;
    public string Title { get; } = title;
    public IReadOnlyList<MaterialRow> Rows { get; } = rows;
    public bool Open { get => _open; set { if (Set(ref _open, value)) { Raise(nameof(ToggleLabel)); } } }
    public string ToggleLabel => _open ? "Hide surfaces" : "Each surface";
    public Command Toggle => new(() => Open = !Open);
    public string Value
    {
        get { var v = Rows.Select(r => (int)r.Value).Distinct().ToList(); return v.Count == 1 ? v[0].ToString() : "mixed"; }
        set { if (int.TryParse(value, out int n)) owner.SetMaterials(Rows.Select(r => r.Name), Math.Clamp(n, 0, 100)); Raise(); }
    }
    public string Summary { get { int n = Rows.Count(r => r.Value > 0); return n == 0 ? $"{Rows.Count} surfaces, no bonus" : $"{n} of {Rows.Count} surfaces get a bonus"; } }
    public void Reload() { Raise(nameof(Value)); Raise(nameof(Summary)); }
}

/// <summary>
/// One ground type the outfit can hide on (greenery, rock, sand...). Ticking it gives every surface of that ground the
/// game's own bonus (50), which is what the vanilla camo presets do. The owner asked for this instead of a list of every
/// MTR_ surface: "that was supposed to change back and be based on their camo value presets, not the outfit".
/// </summary>
public sealed class GroundPreset(OutfitVm owner, string title, string note, IReadOnlyList<string> materials, string fits = "", string autoNote = "") : Observable
{
    public string Title { get; } = title;
    public string Note { get; } = note;
    public IReadOnlyList<string> Materials { get; } = materials;
    /// <summary>The vanilla outfits that hide on this Environment, for the blue "!" (issue #89); empty for none.</summary>
    public string Fits { get; } = fits;
    public bool HasFits => Fits.Length > 0;
    /// <summary>"Ticked from the mod's description" when the port auto-filled this Environment (issue #89).</summary>
    public string AutoNote { get; } = autoNote;
    public bool HasAutoNote => AutoNote.Length > 0 && On; // an unticked Environment shows no "ticked" note
    /// <summary>Ticked when every surface of this ground has a bonus.</summary>
    public bool On
    {
        get => Materials.Count > 0 && Materials.All(m => owner.MaterialValue(m) > 0);
        set { owner.SetGround(Materials, value ? 50 : 0); Raise(); }
    }
    /// <summary>Partly ticked: some surfaces of this ground have a bonus.</summary>
    public bool Partial => !On && Materials.Any(m => owner.MaterialValue(m) > 0);
    public string Count => $"{Materials.Count(m => owner.MaterialValue(m) > 0)} of {Materials.Count} surfaces";
    /// <summary>The concealment value of this ground, also for a preset (issue #22): one number, or "mixed".</summary>
    public string Value { get { var v = Materials.Select(owner.MaterialValue).Distinct().ToList(); return v.Count == 1 ? v[0].ToString() : "mixed"; } }
    public void Reload() { Raise(nameof(On)); Raise(nameof(Partial)); Raise(nameof(Count)); Raise(nameof(Value)); Raise(nameof(HasAutoNote)); }
}

/// <summary>
/// One character the outfit could be worn by. Every character is listed, so the tab shows who can wear the item and
/// who can't: a character the mod has no model for is greyed out and can't be ticked.
/// </summary>
public sealed class WearerRow(OutfitVm o, PlayerBranch who, bool eligible) : Observable
{
    public PlayerBranch Who { get; } = who;
    /// <summary>
    /// The character's name, kept short so the six rows line up and read as one list. The owner asked for this:
    /// "The names in Wearer could be neater." The longer wording ("DD soldier (male)") is in the tooltip instead.
    /// </summary>
    public string Label { get; } = who switch { PlayerBranch.Snake => "Snake", PlayerBranch.Avatar => "Avatar", PlayerBranch.DdMale => "DD male", PlayerBranch.DdFemale => "DD female", PlayerBranch.Ocelot => "Ocelot", _ => "Quiet" };
    /// <summary>True when the mod has a model for this character, so the outfit can be worn by them.</summary>
    public bool Eligible { get; } = eligible;
    public bool NotEligible => !Eligible;
    /// <summary>Greyed out when the mod has no model for this character.</summary>
    public double RowOpacity => Eligible ? 1.0 : 0.42;
    /// <summary>True for the only ticked wearer: it can't be unticked (issue #15).</summary>
    public bool Locked => PlayerBranches.IsLastWearer(Wears, o.WearerCount);
    public bool CanTick => Eligible && !Locked;
    public string Note => !Eligible
        ? "The mod has no model for this character, so they can't wear it."
        : Locked ? PlayerBranches.LastOutfitWearerReason
        : Who == PlayerBranch.Avatar ? "V Framework 1.7 has Avatar read Snake's branch, so Avatar wears the same model."
        : Who == PlayerBranch.DdMale ? "DD soldier, male body."
        : Who == PlayerBranch.DdFemale ? "DD soldier, female body." : "";
    /// <summary>True when the note is a restriction rather than a remark, so only those are shown inline.</summary>
    public bool HasNote => Note.Length > 0;
    public bool Wears { get => o.Wears(Who); set { if (!Eligible) { Raise(); return; } o.SetWears(Who, value); Raise(); } }
    public void Reload() { Raise(nameof(Wears)); Raise(nameof(Locked)); Raise(nameof(CanTick)); Raise(nameof(Note)); Raise(nameof(HasNote)); }
}

/// <summary>One copy tick box: a separate item for other wearers, named "&lt;name&gt; (Ocelot)" and so on. Off removes the copy.</summary>
public sealed class CopyRow(OutfitVm o, CopyOption option) : Observable
{
    public CopyKind Kind => option.Kind;
    public string Label { get; } = option.Kind switch
    {
        CopyKind.Ocelot => "Ocelot copy",
        CopyKind.Quiet => "Quiet copy (playable and buddy)",
        _ => "Snake and Avatar copy",
    };
    public bool CanTick => option.Available;
    public double RowOpacity => option.Available ? 1.0 : 0.42;
    /// <summary>The one-line reason when the copy is not available (Q20).</summary>
    public string Reason => option.Reason ?? "";
    public bool HasReason => option.Reason != null;
    public bool Ticked { get => o.HasCopy(Kind); set { if (CanTick && value != Ticked) o.SetCopy(Kind, value); Raise(); } }
    public void Reload() => Raise(nameof(Ticked));
}

/// <summary>One vanilla head the outfit can list.</summary>
public sealed class HeadRow(OutfitVm o, PlayerBranch who, string id) : Observable
{
    public string Label { get; } = id switch
    {
        "bandana" => "Bandana", "infinitebandana" => "Infinity Bandana", "balaclava" => "Balaclava",
        "spheadgear" => "Soldier headgear (SP)", "hpheadgear" => "Soldier headgear (HP)", _ => id,
    };
    public bool Listed { get => o.HasHead(who, id); set { o.SetHead(who, id, value); Raise(); } }
    /// <summary>No note: whether a bandana respects development is a setting on the tab, not a fixed warning.</summary>
    public string Note => "";
}

/// <summary>One head option of the mod, for one wearer of the outfit ("Heads in this mod", issue #21).</summary>
public sealed class ModHeadRow(OutfitVm o, PlayerBranch who, string key, string label) : Observable
{
    public string Label { get; } = label;
    public bool Listed { get => o.HasHead(who, key); set { o.SetHead(who, key, value); Raise(); } }
}

public sealed class BranchHeads(string title, IReadOnlyList<HeadRow> rows)
{
    public string Title { get; } = title;
    public IReadOnlyList<HeadRow> Rows { get; } = rows;
}

/// <summary>The outfit-specific tabs: abilities, R&amp;D, camo concealment, sortie slots, heads.</summary>
public sealed class OutfitVm : Observable
{
    private readonly Session _s;
    private readonly ItemVm _item;
    private readonly OutfitItem _o;

    /// <summary>
    /// The "also under other outfits" panel, shown inside the Sortie slots tab rather than as its own tab. The owner
    /// asked for this: "'Also under outfits' should be naturally part of the sortie as we would need to see the outfits
    /// added to other costumes in the mock up sortie, as well as the ability to not have any new slots used if they are
    /// added to existing outfits". Null only if the item has no Also view model.
    /// </summary>
    public AlsoVm? Also => _item.Also;
    /// <summary>False only for looks on a vanilla suit: an outfit, the tool's own or an existing one, has the panel.</summary>
    public bool HasAlso => _item.Also != null;

    /// <summary>
    /// The logo choices for the sortie slots: the built-in constants, then every .ftex the mod ships, by file name, then
    /// any other path a look holds. A slot icon adds a file while the editor is open, so the list is compared on each read;
    /// the list object changes only when its content does.
    /// </summary>
    private List<string>? _logoChoices;
    private List<string?>? _logoPaths;

    private void BuildLogos()
    {
        List<string> choices = [.. LookRow.BuiltInLogos];
        List<string?> paths = [.. LookRow.BuiltInLogos.Select(_ => (string?)null)];
        void Add(string name, string path)
        {
            if (paths.Contains(path)) return;
            choices.Add(IntakeResult.IsSlotIconPath(path) ? $"This slot's own icon ({name})" : name);
            paths.Add(path);
        }
        string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
        // The mod's own sheets first: those are the ones a port is most likely to want. iconFtexPath takes the path
        // without its extension, so the .ftex suffix is trimmed here rather than at write time.
        foreach (var path in _item.Item.Files.Keys.Where(k => k.EndsWith(".ftex", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            Add(NameOf(path[..^5]), path[..^5]);
        // Then the vanilla suit icons, so a port can point a look at an icon the game already ships.
        foreach (var (name, path) in GameKnowledge.VanillaSuitIcons) Add(name, path);
        // A path no list has (a script's own, or one undo put back) keeps its own row: the box must not read "As the outfit".
        foreach (var path in _o.Spec.Branches.Values.SelectMany(AllLooks).Select(l => l.IconFtexPath).OfType<string>().Where(p => p.Length > 0))
            Add(NameOf(path), path);
        if (_logoPaths != null && paths.SequenceEqual(_logoPaths)) return;
        _logoChoices = choices;
        _logoPaths = paths;
    }

    /// <summary>The icon choices ("As the outfit", the mod's own sheets, then the vanilla suit icons).</summary>
    public IReadOnlyList<string> LogoChoices { get { BuildLogos(); return _logoChoices!; } }

    /// <summary>The value to store for an icon choice: null for "As the outfit", else the extensionless .ftex path.</summary>
    public string? LogoPathAt(int index)
    {
        BuildLogos();
        return index >= 0 && index < _logoPaths!.Count ? _logoPaths[index] : null;
    }

    /// <summary>Which choice a stored icon path is, or -1 when it is not in the list.</summary>
    public int LogoIndex(string stored)
    {
        BuildLogos();
        for (int i = 0; i < _logoPaths!.Count; i++)
            if (_logoPaths[i] == stored) return i;
        return -1;
    }

    public OutfitVm(Session s, ItemVm item, OutfitItem o)
    {
        _s = s; _item = item; _o = o;
        Presets.Add(new Choice("", "Custom (your own choices below)"));
        foreach (var p in GameKnowledge.Presets) Presets.Add(new Choice(p.Id, p.Label));
        CamoTypes.Add(new Choice("", "Custom (your own values below)"));
        // Issue #89: Black and Splitter have no camo values in the game, so they are not offered.
        foreach (var c in GameKnowledge.Camos.Where(c => c.Name.Length > 0 && c.Name is not ("BLACK" or "SPLITTER")).OrderBy(c => c.Name)) CamoTypes.Add(new Choice(c.Name, $"{c.Description}  ({c.Name})"));
        foreach (var m in GameKnowledge.CamoMaterials) Materials.Add(new MaterialRow(this, m, 0));
        BuildMaterialGroups();
        Rattle.AddRange([new Choice("", "Original"), new Choice("nom", "Fatigues (nom)"), new Choice("bony", "Bony"), new Choice("snk", "Sneaking Suit (snk)"), new Choice("amr", "Battle Dress (amr)")]);
        HitSound.AddRange([new Choice("", "Original"), new Choice("default", "Default"), new Choice("battledress", "Battle Dress")]);
        Reload();
    }

    public Session Session => _s;
    public ObservableCollection<Choice> Presets { get; } = [];
    public ObservableCollection<Choice> CamoTypes { get; } = [];
    public ObservableCollection<MaterialRow> Materials { get; } = [];
    public ObservableCollection<MaterialGroup> MaterialGroups { get; } = [];
    /// <summary>The ground types the concealment tab offers, one tick each (the owner's request: presets, not 100 surfaces).</summary>
    public ObservableCollection<GroundPreset> Grounds { get; } = [];
    /// <summary>
    /// The Environments of the game's camo table (issue #89), one tick each. The surfaces that no vanilla camo uses are
    /// only in the list of every surface, under "Other surfaces".
    /// </summary>
    private void BuildMaterialGroups()
    {
        var left = Materials.ToList();
        foreach (var e in GameKnowledge.CamoEnvironments)
        {
            var rows = Materials.Where(m => e.Surfaces.Contains(m.Name)).ToList();
            MaterialGroups.Add(new MaterialGroup(this, e.Title, rows));
            Grounds.Add(new GroundPreset(this, e.Title, e.Note, e.Surfaces,
                e.FitCamos.Count > 0 ? "Vanilla outfits that hide here: " + string.Join(", ", e.FitCamos) : "",
                _o.CamoFromDescription.Contains(e.Title) ? "Ticked from the mod's description." : ""));
            left.RemoveAll(rows.Contains);
        }
        if (left.Count > 0) MaterialGroups.Add(new MaterialGroup(this, "Other surfaces", left));
    }
    /// <summary>The bonus one surface has now (0 when it has none), from the preset when one is picked.</summary>
    public int MaterialValue(string name) => CamoValuesOf(First).GetValueOrDefault(name);
    /// <summary>The values a branch hides with: its preset's table, or its own values (Custom).</summary>
    private static IReadOnlyDictionary<string, int> CamoValuesOf(OutfitBranch b) =>
        (b.CamoBonusType != null ? GameKnowledge.CamoValues(b.CamoBonusType) : null) ?? b.CamoBonusValues ?? new Dictionary<string, int>();
    /// <summary>
    /// Changes values from what the branch hides with now. A changed preset becomes Custom (issue #22); a change that
    /// leaves the preset's values as they are keeps the preset.
    /// </summary>
    private static OutfitBranch WithCamoValues(OutfitBranch b, Action<Dictionary<string, int>> change)
    {
        var d = new Dictionary<string, int>(CamoValuesOf(b));
        change(d);
        var type = GameKnowledge.CamoPresetFor(b.CamoBonusType, d);
        return type != null ? b : b with { CamoBonusType = null, CamoBonusValues = d };
    }
    /// <summary>Ticks or unticks a whole ground in one undo step.</summary>
    public void SetGround(IReadOnlyList<string> names, int value)
    {
        SetMaterials(names, value);
        foreach (var g in Grounds) g.Reload();
    }
    /// <summary>Sets one bonus on several surfaces in a single undo step.</summary>
    public void SetMaterials(IEnumerable<string> names, int value)
    {
        var list = names.ToList();
        Mutate("c:group:" + list[0], b => WithCamoValues(b, d => { foreach (var n in list) if (value == 0) d.Remove(n); else d[n] = value; }));
        ReloadCamo();
    }
    public ObservableCollection<LookRow> LookRows { get; } = [];
    public ObservableCollection<BranchHeads> HeadGroups { get; } = [];

    // ---- Wearers: the characters the outfit was ported for; one can be switched off, and back on ----
    public ObservableCollection<WearerRow> Wearers { get; } = [];
    private Dictionary<PlayerBranch, OutfitBranch> OriginalBranches => _s.Edits?.OriginalOf(_item.Item)?.Spec?.Branches ?? _o.Spec.Branches;
    private void BuildWearers()
    {
        // Every character is listed, in a fixed order, so the tab always shows the whole picture: who the mod has a
        // model for (tickable) and who it doesn't (greyed out). The owner reported the old list only showed the
        // characters the mod happened to have, which read as "everyone can wear it".
        var all = new[] { PlayerBranch.Snake, PlayerBranch.Avatar, PlayerBranch.DdMale, PlayerBranch.DdFemale, PlayerBranch.Ocelot, PlayerBranch.Quiet };
        BuildCopies();
        if (Wearers.Count == all.Length) { foreach (var w in Wearers) w.Reload(); return; }
        Wearers.Clear();
        foreach (var k in all) Wearers.Add(new WearerRow(this, k, OriginalBranches.ContainsKey(k) || _o.Spec.Branches.ContainsKey(k) || k == PlayerBranch.Avatar && SnakeBranch != null));
    }

    // ---- Copies for other wearers (the owner, Nov 15, Q12-Q20): body ports only ----
    public ObservableCollection<CopyRow> Copies { get; } = [];
    public bool HasCopies => Copies.Count > 0;
    private string _copyStatus = "";
    /// <summary>Why the last tick of a copy failed, else empty.</summary>
    public string CopyStatus { get => _copyStatus; private set { _copyStatus = value; Raise(); Raise(nameof(HasCopyStatus)); } }
    public bool HasCopyStatus => CopyStatus.Length > 0;
    private void BuildCopies()
    {
        var options = _s.Intake is { } res ? OutfitCopies.Options(res, _o) : [];
        if (Copies.Count == options.Count) { foreach (var c in Copies) c.Reload(); return; }
        Copies.Clear();
        foreach (var opt in options) Copies.Add(new CopyRow(this, opt));
        Raise(nameof(HasCopies));
    }
    public bool HasCopy(CopyKind kind) => _s.Edits?.CopiesFor(_o, kind).Count > 0;
    public async void SetCopy(CopyKind kind, bool on)
    {
        CopyStatus = "";
        try { await _s.SetCopyAsync(_o, kind, on); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException or KeyNotFoundException) { CopyStatus = e.Message; }
        foreach (var c in Copies) c.Reload();
    }
    private OutfitBranch? SnakeBranch => _o.Spec.Branches.GetValueOrDefault(PlayerBranch.Snake) ?? OriginalBranches.GetValueOrDefault(PlayerBranch.Snake);
    private HashSet<string> SnakeFaceHeads() => _s.Project!.Items.Select(ProjectEdits.HeadOf).OfType<HeadOptionItem>().Where(h => h.Spec.SnakeFaceOnly).Select(h => h.Spec.Key).ToHashSet(StringComparer.Ordinal);
    public bool Wears(PlayerBranch who) => _o.Spec.Branches.ContainsKey(who);
    public void SetWears(PlayerBranch who, bool on)
    {
        if (on == Wears(who)) return;
        if (!on && _o.Spec.Branches.Count < 2) { foreach (var w in Wearers) w.Reload(); return; }
        _s.Checkpoint($"{_o.Id}:wear:{who}");
        if (on)
        {
            // Avatar wears Snake's outfit: a mod without an Avatar branch gets one made from Snake's (issue #45).
            if (OriginalBranches.TryGetValue(who, out var b)) _o.Spec.Branches[who] = b;
            else if (who == PlayerBranch.Avatar && SnakeBranch is { } sb) _o.Spec.Branches[who] = PlayerBranches.AvatarFrom(sb, SnakeFaceHeads());
        }
        else _o.Spec.Branches.Remove(who);
        _s.NotifyChanged();
        foreach (var w in Wearers) w.Reload();
        ReloadHeads(); ReloadLooks(); Also?.Refresh();
    }
    public bool OnlyOneWearer => _o.Spec.Branches.Count < 2;
    public int WearerCount => _o.Spec.Branches.Count;
    /// <summary>False when every wearer is a DD soldier: only Snake and Avatar have the prosthetic arm (issue #12).</summary>
    public bool ArmAllowed => PlayerBranches.HasBionicArm(_o.Spec.Branches.Keys);
    public string ArmReason => PlayerBranches.NoBionicArmReason + ", so the arm choice is greyed out.";
    /// <summary>
    /// Abilities tab, "Enable prosthetic", for the slot that "Abilities for" shows: "The outfit" is every look, else that
    /// one look, for every wearer. V Framework 1.7 has no ability-only key, so off writes <c>enableArm = false</c> (the
    /// game loads no prosthetic arm). On removes those false values. Under a vanilla outfit there is no switch: V Framework 1.7 hides only the arm model there and keeps the ability.
    /// </summary>
    public bool EnableProsthetic
    {
        get => _o.Spec.Branches.Values.SelectMany(b => ArmScope(b.Variants.Prepend(b.Base))).All(l => l.EnableArm != false);
        set
        {
            // Not "value == EnableProsthetic": with some looks on and some off the getter says off, and off must still reach every look.
            if (_o.Spec.Branches.Values.SelectMany(b => ArmScope(b.Variants.Prepend(b.Base))).All(l => (l.EnableArm != false) == value)) return;
            _s.Checkpoint($"{_o.Id}:arm:{_abLook}");
            Look Set(Look l, int i) => _abLook != 0 && i != _abLook ? l : l with { EnableArm = value ? (l.EnableArm == false ? null : l.EnableArm) : false };
            foreach (var (who, b) in _o.Spec.Branches.ToList())
                _o.Spec.Branches[who] = b with { Base = Set(b.Base, 0), Variants = [.. b.Variants.Select((v, i) => Set(v, i + 1))] };
            _s.NotifyChanged();
            ReloadLooks();
        }
    }
    private IEnumerable<Look> ArmScope(IEnumerable<Look> looks) => _abLook == 0 ? looks : looks.Skip(_abLook).Take(1);
    public List<Choice> Rattle { get; } = [];
    public List<Choice> HitSound { get; } = [];
    public IReadOnlyList<int> Levels { get; } = Enumerable.Range(0, 10).ToList();

    private OutfitBranch First => _o.Spec.Branches.Values.First();
    public IReadOnlyList<Look> Looks => LookRows.Select(r => r.Look).ToList();

    // ---- changing branches ----

    private void Mutate(string field, Func<OutfitBranch, OutfitBranch> f, Action? alongside = null)
    {
        var next = _o.Spec.Branches.ToDictionary(kv => kv.Key, kv => f(kv.Value));
        // A control can write back the value it shows: the same value is not an edit, and an undo step for it would make
        // Ctrl+Z undo nothing the user did.
        if (next.All(kv => Equals(kv.Value, _o.Spec.Branches[kv.Key]))) return;
        _s.Checkpoint($"{_o.Id}:{field}");
        foreach (var (k, v) in next) _o.Spec.Branches[k] = v;
        alongside?.Invoke();
        _s.NotifyChanged();
    }

    private void MutateAbilities(string field, Func<Abilities, Abilities> f)
    {
        int k = _abLook - 1;
        if (k < 0) { Mutate(field, b => b with { Abilities = f(b.Abilities) }); return; }
        // A look with "Same as the outfit" shows the outfit's set and is not edited here (the controls are greyed out).
        Mutate($"{field}:{_abLook}", b => k < b.Variants.Count && b.Variants[k].Abilities is { } va ? b with { Variants = SetAt(b.Variants, k, b.Variants[k] with { Abilities = f(va) }) } : b);
    }

    private static List<Look> SetAt(List<Look> l, int k, Look v) { var c = l.ToList(); c[k] = v; return c; }

    // ---- Abilities for each look (#61) ----
    private int _abLook; // 0 = the outfit; n = look n + 1 (variant n - 1)
    /// <summary>The abilities the tab shows: the outfit's, or the chosen look's own set.</summary>
    private Abilities Cur => LookOwn ?? First.Abilities;
    /// <summary>The chosen look's own abilities; null for the outfit or a look that follows it.</summary>
    private Abilities? LookOwn => _abLook > 0 && _abLook - 1 < First.Variants.Count ? First.Variants[_abLook - 1].Abilities : null;
    /// <summary>"The outfit" plus one entry for each look after the main one (the main look always has the outfit's abilities).</summary>
    public List<Choice> AbilityLooks
    {
        get
        {
            List<Choice> now = [new("0", "The outfit (every look)"), .. First.Variants.Select((v, i) => new Choice($"{i + 1}", $"Look {i + 2}: {(LookLabel(v) is { Length: > 0 } n ? n : "no name")}"))];
            // A new list on each Reload makes the ComboBox write a stale selection back; keep the old one while the looks are the same.
            if (_abLooks == null || !_abLooks.SequenceEqual(now)) _abLooks = now;
            return _abLooks;
        }
    }
    private List<Choice>? _abLooks;
    public bool HasAbilityLooks => First.Variants.Count > 0;
    public Choice? AbilityLook
    {
        get => AbilityLooks.FirstOrDefault(c => c.Id == $"{_abLook}");
        set { if (value != null && int.TryParse(value.Id, out int n) && n != _abLook) { _abLook = n; Reload(); } }
    }
    public bool LookChosen => _abLook > 0;
    public static IReadOnlyList<string> LookAbilityModes { get; } = ["Same as the outfit", "Own abilities"];
    /// <summary>0 = "Same as the outfit" (writes nothing), 1 = "Own abilities" (starts as a copy of the outfit's set; the build writes it whole).</summary>
    public int LookAbilityMode
    {
        get => LookOwn != null ? 1 : 0;
        set
        {
            int k = _abLook - 1;
            if (k < 0 || value < 0 || value == LookAbilityMode) return;
            Mutate($"a:mode:{_abLook}", b => k < b.Variants.Count ? b with { Variants = SetAt(b.Variants, k, b.Variants[k] with { Abilities = value == 1 ? b.Abilities : null }) } : b);
            Reload();
        }
    }
    /// <summary>False while a look follows the outfit: its set is the outfit's, so it is edited with "The outfit".</summary>
    public bool AbilitiesEditable => _abLook == 0 || LookAbilityMode == 1;

    public void Reload()
    {
        var a = First.Abilities;
        Raise(nameof(SilentFootsteps)); Raise(nameof(QuietMovement)); Raise(nameof(SprintSound)); Raise(nameof(DashAttack)); Raise(nameof(RaidenRun));
        Raise(nameof(RaidenAllowed)); Raise(nameof(RaidenNote));
        Raise(nameof(SprintOn)); Raise(nameof(SprintSpeed)); Raise(nameof(Defense)); Raise(nameof(LifeRecovery)); Raise(nameof(SelectedRattle)); Raise(nameof(SelectedHit));
        Raise(nameof(SelectedPreset)); Raise(nameof(PresetNote)); Raise(nameof(PresetTicks));
        if (_abLook > First.Variants.Count) _abLook = 0;
        Raise(nameof(AbilityLooks)); Raise(nameof(AbilityLook)); Raise(nameof(HasAbilityLooks)); Raise(nameof(LookChosen)); Raise(nameof(LookAbilityMode)); Raise(nameof(AbilitiesEditable)); Raise(nameof(EnableProsthetic));
        Raise(nameof(GradeOn)); Raise(nameof(Grade)); Raise(nameof(DevelopCost)); Raise(nameof(UsageCost)); Raise(nameof(AlreadyDeveloped)); Raise(nameof(Unlocked)); Raise(nameof(DevelopNote));
        ReloadCamo(); ReloadLooks(); ReloadHeads(); BuildWearers();
        _ = a;
    }

    // ---- Abilities ----

    public bool SilentFootsteps { get => Cur.SilentFootsteps; set => MutateAbilities("a:sf", a => a with { SilentFootsteps = value }); }
    /// <summary>
    /// Quiet's movement. V Framework 1.7 can't give a suit Quiet's movement and Raiden's run at once (they replace the
    /// same motion set), so turning this on turns Raiden run off and greys it out.
    /// </summary>
    public bool QuietMovement
    {
        get => Cur.QuietMovement;
        set
        {
            if (value == QuietMovement) return;
            MutateAbilities("a:qm", a => a with { QuietMovement = value, RaidenRun = value ? false : a.RaidenRun });
            Raise(nameof(RaidenRun)); Raise(nameof(RaidenAllowed)); Raise(nameof(RaidenNote));
        }
    }
    /// <summary>Raiden's run can't be used together with Quiet's movement.</summary>
    public bool RaidenAllowed => !QuietMovement;
    public string RaidenNote => QuietMovement ? "Not with Quiet movement: both replace the same motion set." : "";
    public static string RaidenRunTip => "Raiden's run animation. It does not work together with Quiet movement.";
    public static string SprintSoundTip => "Plays a sprint sound when the player sprints.";
    public bool SprintSound { get => Cur.SprintSound; set => MutateAbilities("a:ss", a => a with { SprintSound = value }); }
    public bool DashAttack { get => Cur.DashAttack; set => MutateAbilities("a:da", a => a with { DashAttack = value }); }
    public bool RaidenRun { get => Cur.RaidenRun; set { if (!RaidenAllowed) { Raise(); return; } MutateAbilities("a:rr", a => a with { RaidenRun = value }); } }
    public bool SprintOn { get => Cur.SprintSpeed != null; set { if (value != SprintOn) { MutateAbilities("a:sp", a => a with { SprintSpeed = value ? 1.3f : null }); Raise(); Raise(nameof(SprintSpeed)); } } }
    public decimal SprintSpeed { get => (decimal)(Cur.SprintSpeed ?? 1f); set { MutateAbilities("a:sps", a => a with { SprintSpeed = (float)Math.Clamp(value, 0.1m, 10m) }); } }
    public int Defense { get => Cur.Defense; set => MutateAbilities("a:def", a => a with { Defense = Math.Clamp(value, 0, 9) }); }
    public int LifeRecovery { get => Cur.LifeRecovery; set => MutateAbilities("a:lr", a => a with { LifeRecovery = Math.Clamp(value, 0, 9) }); }
    public Choice? SelectedRattle { get => Rattle.FirstOrDefault(c => c.Id == (Cur.RattleSuit ?? "")); set { if (value != null) MutateAbilities("a:rs", a => a with { RattleSuit = value.Id.Length == 0 ? null : value.Id }); } }
    public Choice? SelectedHit { get => HitSound.FirstOrDefault(c => c.Id == (Cur.DamageSe ?? "")); set { if (value != null) MutateAbilities("a:hs", a => a with { DamageSe = value.Id.Length == 0 ? null : value.Id }); } }

    public Choice SelectedPreset
    {
        get
        {
            var m = MatchedPreset;
            return m == null ? Presets[0] : Presets.First(c => c.Id == m.Id);
        }
        set
        {
            if (value == null || value.Id.Length == 0) return;
            var p = GameKnowledge.Preset(value.Id);
            MutateAbilities("a:preset", a => p.Abilities with { SprintSpark = a.SprintSpark }); // issue #69: sparks are the mod's, not the preset's
        }
    }
    /// <summary>What the chosen preset ticks, made from the preset's own settings.</summary>
    public string PresetTicks { get { var m = MatchedPreset; return m == null ? "" : "This preset ticks: " + string.Join(", ", m.Abilities.Ticked()) + "."; } }
    public string PresetNote => MatchedPreset?.Note ?? "";
    /// <summary>The preset with the same settings as the outfit. Sparks do not count: they are the mod's (issue #69).</summary>
    private BehaviourPreset? MatchedPreset => GameKnowledge.Presets.FirstOrDefault(p => p.Abilities == Cur with { SprintSpark = null });

    // ---- R&D ----

    private DevelopRow Dev => _o.Spec.Develop;
    private void MutateDev(string field, Func<DevelopRow, DevelopRow> f) { _s.Checkpoint($"{_o.Id}:{field}"); _o.Spec.Develop = f(Dev); _s.NotifyChanged(); }
    public bool GradeOn { get => Dev.Grade != null; set { if (value != GradeOn) { MutateDev("d:grade", d => d with { Grade = value ? 1 : null }); Raise(); Raise(nameof(Grade)); } } }
    public decimal Grade { get => Dev.Grade ?? 1; set => MutateDev("d:g", d => d with { Grade = (int)Math.Clamp(value, 1, 15) }); }
    public decimal DevelopCost { get => Dev.GmpCost; set => MutateDev("d:c", d => d with { GmpCost = (int)Math.Max(0, value) }); }
    public decimal UsageCost { get => Dev.UsageGmpCost; set => MutateDev("d:u", d => d with { UsageGmpCost = (int)Math.Max(0, value) }); }
    /// <summary>Already developed: the item is available from the start, so costs and unlock conditions don't apply.</summary>
    public bool AlreadyDeveloped { get => Dev.InitiallyAvailable; set { MutateDev("d:i", d => d with { InitiallyAvailable = value }); Raise(nameof(Unlocked)); Raise(nameof(DevelopNote)); } }
    public bool Unlocked => !Dev.InitiallyAvailable;
    public string DevelopNote => Dev.InitiallyAvailable ? "Available from the start. The development cost is not charged." : "Must be developed in R&D first, for the development cost.";

    // ---- Camo concealment ----

    public Choice SelectedCamo
    {
        get => CamoTypes.FirstOrDefault(c => c.Id == (First.CamoBonusType ?? "")) ?? CamoTypes[0];
        set
        {
            if (value == null) return;
            if (value.Id.Length == 0)
            {
                var from = First.CamoBonusType != null ? GameKnowledge.CamoValues(First.CamoBonusType) : null;
                var vals = new Dictionary<string, int>(from ?? First.CamoBonusValues ?? new Dictionary<string, int>());
                Mutate("c:type", b => b with { CamoBonusType = null, CamoBonusValues = new Dictionary<string, int>(vals) });
            }
            else Mutate("c:type", b => b with { CamoBonusType = value.Id, CamoBonusValues = null });
            ReloadCamo();
        }
    }
    public string CamoNote => First.CamoBonusType != null ? $"Hides like the vanilla camo {First.CamoBonusType}: its values from the game show below. Change a value and the choice becomes Custom." : "Each ground gets the number you set. The game uses 0 or 50.";

    public void SetMaterial(string name, int value)
    {
        Mutate("c:" + name, b => WithCamoValues(b, d => { if (value == 0) d.Remove(name); else d[name] = value; }));
        ReloadCamo();
    }

    private void ReloadCamo()
    {
        Raise(nameof(SelectedCamo)); Raise(nameof(CamoNote));
        var vals = CamoValuesOf(First);
        foreach (var m in Materials) m.Reload(vals.GetValueOrDefault(m.Name));
        foreach (var g in MaterialGroups) g.Reload();
        foreach (var g in Grounds) g.Reload();
    }

    // ---- Sortie slots (looks) ----

    private static IEnumerable<Look> AllLooks(OutfitBranch b) => new[] { b.Base }.Concat(b.Variants);
    public string LookLabel(Look l) => l.DisplayName == null ? "" : _item.GetText(l.DisplayName, "eng", false);
    public void SetLookLabel(int index, Look l, string text)
    {
        if (l.DisplayName != null) { _item.SetText(l.DisplayName, "eng", text); return; }
        // Issue #18: a look with no lang id (a one-look port) dropped the typed name, so the mockup and the build never got
        // it. The first letter gives the look a lang id on every character; the undo field is the one SetText uses for the
        // later letters, so one undo takes the whole name off.
        if (text.Length == 0) return;
        string id = NewLookLang();
        _s.Checkpoint($"{_item.Item.Id}:{id}:eng");
        foreach (var (who, b) in _o.Spec.Branches.ToList())
        {
            if (index == 0) _o.Spec.Branches[who] = b with { Base = b.Base with { DisplayName = id } };
            else if (index - 1 < b.Variants.Count) { var vs = b.Variants.ToList(); vs[index - 1] = vs[index - 1] with { DisplayName = id }; _o.Spec.Branches[who] = b with { Variants = vs }; }
        }
        _item.Item.Text[id] = new(StringComparer.Ordinal) { ["eng"] = text };
        _s.NotifyChanged();
    }

    private string NewLookLang()
    {
        for (int n = 1; ; n++)
        {
            string id = $"{_o.Spec.NameLang}_s{n}";
            if (!_item.Item.Text.ContainsKey(id) && !_o.Spec.Branches.Values.SelectMany(AllLooks).Any(x => x.DisplayName == id)) return id;
        }
    }

    private void ReloadLooks()
    {
        var all = AllLooks(First).ToList();
        int def = all.FindIndex(l => l.Default);
        if (def < 0) def = 0;
        // Every edit (each typed letter too) comes here. New rows would drop the focus of the slot name box after one
        // letter (issue #18), so rows are made again only when the number of looks changes.
        if (LookRows.Count == all.Count) for (int i = 0; i < all.Count; i++) LookRows[i].Reload(all[i], i == def);
        else { LookRows.Clear(); for (int i = 0; i < all.Count; i++) LookRows.Add(new LookRow(this, i, all[i], i == def)); }
        Raise(nameof(LookCount)); Raise(nameof(ArmAllowed)); Raise(nameof(EnableProsthetic)); Raise(nameof(VanillaOnly));
    }
    public int LookCount => LookRows.Count;

    private void SetLooks(string field, List<Look> list, int defaultIndex)
    {
        // #62: the bonus slots keep showing their look; order[new position] is the look's old position.
        var old = AllLooks(First).ToList();
        var order = list.Select(l => old.FindIndex(o => ReferenceEquals(o, l))).ToList();
        var also = _o.Spec.AlsoUnder;
        list = list.Select((l, i) => l with { Default = i == defaultIndex && i > 0 }).ToList();
        Mutate(field, b => b with { Base = list[0], Variants = list.Skip(1).ToList() },
            () => { if (!order.SequenceEqual(Enumerable.Range(0, old.Count))) _o.Spec.AlsoUnder = BonusSlots.FollowLooks(also, order); });
        ReloadLooks();
    }

    public void MoveLook(int index, int delta)
    {
        var all = AllLooks(First).ToList(); int def = LookRows.ToList().FindIndex(r => r.IsDefault);
        int to = index + delta; if (to < 0 || to >= all.Count) return;
        (all[index], all[to]) = (all[to], all[index]);
        if (def == index) def = to; else if (def == to) def = index;
        // The "Abilities for" picker follows its look too; a look moved to row 0 is the outfit's (it has no own abilities).
        if (_abLook > 0 && _abLook == index) _abLook = to; else if (_abLook > 0 && _abLook == to) _abLook = index;
        SetLooks("l:move", all, def);
    }

    /// <summary>
    /// Moves the look at <paramref name="from"/> to <paramref name="to"/>, shifting the ones in between. This is what a
    /// drag does; <see cref="MoveLook"/> only steps one place at a time.
    /// </summary>
    public void MoveLookTo(int from, int to)
    {
        var all = AllLooks(First).ToList();
        if (from == to || from < 0 || to < 0 || from >= all.Count || to >= all.Count) return;
        int def = LookRows.ToList().FindIndex(r => r.IsDefault);
        var moved = all[from];
        all.RemoveAt(from);
        all.Insert(to, moved);
        // The "worn first" look follows its own row rather than staying at a position.
        if (def == from) def = to;
        else if (from < def && to >= def) def--;
        else if (from > def && to <= def) def++;
        if (_abLook > 0)
        {
            if (_abLook == from) _abLook = to;
            else if (from < _abLook && to >= _abLook) _abLook--;
            else if (from > _abLook && to <= _abLook) _abLook++;
        }
        SetLooks("l:drag", all, def);
    }

    public void RemoveLook(int index)
    {
        if (!CanRemoveLook) return;
        var all = AllLooks(First).ToList();
        if (all.Count < 2)
        {
            // #62: the last own slot goes while a bonus slot stays. The look stays as the source of the bonus slots.
            Mutate("l:vanilla-only", b => b with { VanillaOnly = true }); // its own field: not merged with an earlier Remove
            ReloadLooks();
            return;
        }
        int def = LookRows.ToList().FindIndex(r => r.IsDefault);
        all.RemoveAt(index);
        def = def == index ? 0 : (def > index ? def - 1 : def);
        _abLook = _abLook == index ? 0 : (_abLook > index ? _abLook - 1 : _abLook);
        SetLooks("l:remove", all, def);
    }

    /// <summary>Issue #30: puts a copy of the look at <paramref name="index"/> after it, with its own name, for every character.</summary>
    public void CloneLook(int index) => InsertLookCopy(index, index + 1, LookRows.ElementAtOrDefault(index)?.Label ?? "");

    /// <summary>Issue #30: Add slot in the Sortie mockup. The new slot is a copy of the main look at the end, named "New slot".</summary>
    public void AddLook()
    {
        // #62: a vanilla-only outfit gets its own slot back first: its Main look.
        if (VanillaOnly) { Mutate("l:own-slot", b => b with { VanillaOnly = false }); ReloadLooks(); return; }
        InsertLookCopy(0, int.MaxValue, "New slot");
    }

    /// <summary>#62: the outfit has no own slot, only bonus slots under vanilla suits.</summary>
    public bool VanillaOnly => _o.Spec.VanillaOnly;
    /// <summary>#87: the outfit's own icon (the ICON local), the icon a look with no icon of its own shows.</summary>
    public string OutfitIcon => _o.Spec.IconFtexPath;
    /// <summary>#62: an own slot can go while another slot stays; an existing V Framework mod keeps its last own slot.</summary>
    public bool CanRemoveLook => _o.Spec.CanRemoveSlot(own: true, ownCanGo: _s.Project?.Existing == null);

    private void InsertLookCopy(int from, int at, string label)
    {
        if (from < 0 || from >= LookRows.Count) return;
        string id = NewLookLang();
        _s.Checkpoint(); // no field: two clones in a row are two undo steps
        // #62: a bonus slot keeps showing its look when the new look pushes it down a place (-1: the new look has no old position).
        if (at < LookRows.Count)
        {
            var order = Enumerable.Range(0, LookRows.Count).ToList();
            order.Insert(at, -1);
            _o.Spec.AlsoUnder = BonusSlots.FollowLooks(_o.Spec.AlsoUnder, order);
        }
        _item.Item.Text[id] = new(StringComparer.Ordinal) { ["eng"] = label };
        foreach (var (who, b) in _o.Spec.Branches.ToList())
        {
            var all = AllLooks(b).ToList();
            if (from >= all.Count) continue;
            all.Insert(Math.Min(at, all.Count), all[from] with { DisplayName = id, Default = false });
            _o.Spec.Branches[who] = b with { Base = all[0], Variants = all.Skip(1).ToList() };
        }
        if (_abLook > 0 && _abLook >= Math.Min(at, LookRows.Count)) _abLook++; // LookRows still has the old count here
        _s.NotifyChanged();
        ReloadLooks();
    }

    /// <summary>Sets enableArm on the look at this position, for every character.</summary>
    public void SetLookArm(int index, int v)
    {
        bool? arm = v switch { 1 => true, 2 => false, _ => null };
        _s.Checkpoint($"{_o.Id}:l:arm:{index}");
        foreach (var (who, b) in _o.Spec.Branches.ToList())
        {
            if (index == 0) _o.Spec.Branches[who] = b with { Base = b.Base with { EnableArm = arm } };
            else if (index - 1 < b.Variants.Count) { var vs = b.Variants.ToList(); vs[index - 1] = vs[index - 1] with { EnableArm = arm }; _o.Spec.Branches[who] = b with { Variants = vs }; }
        }
        _s.NotifyChanged();
        ReloadLooks();
    }

    /// <summary>
    /// Sets the icon the look at this position shows in the suit list and on its sortie slot, for every character.
    /// This is V Framework's per-look <c>iconFtexPath</c>, an extensionless .ftex path; null clears it so the look
    /// inherits the outfit's own icon.
    /// </summary>
    public void SetLookLogo(int index, string? logo)
    {
        _s.Checkpoint($"{_o.Id}:l:logo:{index}");
        foreach (var (who, b) in _o.Spec.Branches.ToList())
        {
            if (index == 0) _o.Spec.Branches[who] = b with { Base = b.Base with { IconFtexPath = logo } };
            else if (index - 1 < b.Variants.Count) { var vs = b.Variants.ToList(); vs[index - 1] = vs[index - 1] with { IconFtexPath = logo }; _o.Spec.Branches[who] = b with { Variants = vs }; }
        }
        _s.NotifyChanged();
        ReloadLooks();
    }

    public void SetDefaultLook(int index) => SetLooks("l:default", AllLooks(First).ToList(), index);

    // ---- Heads ----

    public bool HasHead(PlayerBranch who, string id) => _o.Spec.Branches.TryGetValue(who, out var b) && (b.Base.HeadOptions.Contains(id) || b.HeadOptions.Contains(id));

    public void SetHead(PlayerBranch who, string id, bool on)
    {
        if (on == HasHead(who, id)) return;
        _s.Checkpoint($"{_o.Id}:h:{who}:{id}");
        var b = _o.Spec.Branches[who];
        var heads = b.HeadOptions.Concat(b.Base.HeadOptions).Distinct().ToList();
        if (on) heads.Add(id); else heads.Remove(id);
        // Every look repeats the list (V Framework 1.7 gives a look without its own list no HEAD row).
        _o.Spec.Branches[who] = b with { HeadOptions = heads.ToList(), Base = b.Base with { HeadOptions = heads.ToList() }, Variants = b.Variants.Select(v => v with { HeadOptions = heads.ToList() }).ToList() };
        _s.NotifyChanged();
        ReloadHeads();
    }

    // ---- The bandana (issue #21): listed or not, and whether it respects development ----

    /// <summary>False when Snake is not a wearer of the outfit. The Snake and Avatar sections are separate (issue #65).</summary>
    public bool CanListBandana => _o.Spec.Branches.ContainsKey(PlayerBranch.Snake);
    public bool CanListBandanaAvatar => _o.Spec.Branches.ContainsKey(PlayerBranch.Avatar);

    /// <summary>True when Snake's heads list the bandana (Bandana and Infinity Bandana).</summary>
    public bool ListBandana
    {
        get => GatedBandanas.Listed(_s.Project!, _o, PlayerBranch.Snake);
        set { if (value != ListBandana) { _s.Checkpoint($"{_o.Id}:h:bandana"); SetBandana(value, PlayerBranch.Snake); } }
    }

    /// <summary>True when the Avatar's heads list the bandana. The Avatar has its own list; it is the game's own head, worn at once.</summary>
    public bool ListBandanaAvatar
    {
        get => GatedBandanas.Listed(_s.Project!, _o, PlayerBranch.Avatar);
        set { if (value != ListBandanaAvatar) { _s.Checkpoint($"{_o.Id}:h:bandana:avatar"); SetBandana(value, PlayerBranch.Avatar); } }
    }
    /// <summary>Issue #86: why the Avatar card has no development setting (.docs/Research_Avatar_Gated_Bandana.md).</summary>
    public static string AvatarBandanaNote => "The Avatar's bandana is the game's own head, so the player can wear it at once. The wait for development is not available for the Avatar: the bandana that waits is built on Snake's bandana faces, and V Framework 1.7 can give the Avatar such a head only with an Avatar model file. The game file of the Avatar's bandana is not known.";

    private void SetBandana(bool on, PlayerBranch who)
    {
        GatedBandanas.List(_s.Project!, _o, on, GateBandanas, who);
        _s.NotifyChanged();
        Raise(nameof(ListBandana)); Raise(nameof(ListBandanaAvatar)); Raise(nameof(GateEnabled)); Raise(nameof(BandanaNote));
    }

    /// <summary>The development setting applies only when the bandana is listed; it greys out when it is not.</summary>
    public bool GateEnabled => ListBandana;

    /// <summary>
    /// True (the default): a bandana is only wearable once the vanilla one is developed, by adding it as a custom head
    /// whose R&amp;D row follows the vanilla row. False: it is listed as the vanilla head, which V Framework 1.7 lets the
    /// player wear straight away. The owner asked for this choice instead of the old fixed note. Changing it lists this
    /// outfit's bandana again in the chosen form.
    /// </summary>
    public bool GateBandanas
    {
        get => _s.Settings.GateBandanas;
        set
        {
            if (value == GateBandanas) return;
            _s.Settings.GateBandanas = value; _s.Settings.Save();
            if (ListBandana) { _s.Checkpoint($"{_o.Id}:h:bandana:gate"); SetBandana(true, PlayerBranch.Snake); }
            Raise(); Raise(nameof(BandanaNote));
        }
    }
    public string BandanaNote => !ListBandana
        ? "The bandana isn't listed, so the development setting doesn't apply."
        : GateBandanas
        ? "A bandana is only wearable once the vanilla one is developed: it is added as a head of its own whose R&D row follows the vanilla row."
        : "A bandana can be worn straight away: V Framework 1.7 doesn't check development for the game's own heads.";

    // ---- The mod's own heads (issue #21) ----

    public ObservableCollection<ModHeadRow> ModHeadRows { get; } = [];
    public bool HasModHeads => ModHeadRows.Count > 0;

    private static string WearerTitle(PlayerBranch who) => who switch { PlayerBranch.Snake => "Snake", PlayerBranch.Avatar => "Avatar", PlayerBranch.DdMale => "Soldier (male)", PlayerBranch.DdFemale => "Soldier (female)", _ => who.ToString() };

    private void ReloadHeads()
    {
        HeadGroups.Clear();
        foreach (var (who, _) in _o.Spec.Branches)
        {
            // The bandanas have their own tick box (ListBandana), so only the other game heads are rows.
            var ids = VanillaHeads.For(who);
            if (ids.Count == 0) continue;
            HeadGroups.Add(new BranchHeads(WearerTitle(who), ids.Select(i => new HeadRow(this, who, i)).ToList()));
        }
        // Every head option of the mod (the tool's own or an existing mod's), for each wearer it has a model for. The
        // bandanas that respect development are not the mod's heads: the tick box lists them.
        ModHeadRows.Clear();
        var heads = _s.Project!.Items.Where(i => ProjectEdits.HeadOf(i) is { } h && !GatedBandanas.IsGated(h)).ToList();
        foreach (var (who, _) in _o.Spec.Branches.OrderBy(kv => kv.Key))
            foreach (var i in heads)
                if (ModHeads.Fits(ProjectEdits.HeadOf(i)!.Spec, who))
                    ModHeadRows.Add(new ModHeadRow(this, who, ProjectEdits.HeadOf(i)!.Spec.Key, $"{(i.Title.Length > 0 ? i.Title : i.Id)} ({WearerTitle(who)})"));
        Raise(nameof(HasHeads)); Raise(nameof(HasModHeads)); Raise(nameof(ListBandana)); Raise(nameof(GateEnabled)); Raise(nameof(CanListBandana)); Raise(nameof(CanListBandanaAvatar)); Raise(nameof(ListBandanaAvatar)); Raise(nameof(BandanaNote)); Raise(nameof(NoHeadNote)); Raise(nameof(HideHead)); Raise(nameof(HideHeadWarning));
    }
    public bool HasHeads => HeadGroups.Count > 0;

    /// <summary>
    /// Why Sortie Prep shows no head option: every look has its head in the body model (enableHead = false), as an MGS1
    /// type suit does. Empty when a look shows the character's head. In game (Nov 13) the head could not be taken off:
    /// the game ignores a hidden head group, and with the head meshes removed the neck shows a gap.
    /// </summary>
    public string NoHeadNote => _o.Spec.Branches.Count > 0 && !_o.Spec.Branches.Values.Any(b => b.AnyLookShowsHead())
        ? "This outfit has its head in the body model (a helmet, a mask or its own face). The game hides the character's head, so Sortie Prep shows no head option for this outfit, and the heads on this tab are not worn. The head cannot be made optional: without it, the body shows a gap at the neck."
        : "";

    /// <summary>
    /// Heads tab, "Hide the wearer's head" (the owner, Nov 15, Q21): writes <c>enableHead = false</c> on every look, so
    /// V Framework loads no face. SVFM sets it when the port opens (a body with the head in its model); the user can
    /// change it, for example for a separate-head body worn by Ocelot. Off removes the false values.
    /// </summary>
    public bool HideHead
    {
        get => _o.Spec.Branches.Count > 0 && _o.Spec.Branches.Values.All(b => !b.AnyLookShowsHead());
        set
        {
            if (value == HideHead) return;
            _s.Checkpoint($"{_o.Id}:head");
            Look Set(Look l) => l with { EnableHead = value ? false : null };
            foreach (var (who, b) in _o.Spec.Branches.ToList())
                _o.Spec.Branches[who] = b with { Base = Set(b.Base), Variants = [.. b.Variants.Select(Set)] };
            _s.NotifyChanged();
            ReloadLooks();
            Raise(); Raise(nameof(HideHeadWarning)); Raise(nameof(NoHeadNote));
        }
    }
    public string HideHeadWarning => HideHead
        ? "Set by SVFM: the body model has its own head, so the wearer's head is hidden. Turn this off only if the head is missing in game."
        : "";
}
