using System.Collections.ObjectModel;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>A choice in a drop-down.</summary>
public sealed record Choice(string Id, string Label)
{
    public override string ToString() => Label;

    /// <summary>The dash attack values: off, on with the default attack, or an attack that V Framework accepts (1 to 0x1BC).</summary>
    public static readonly IReadOnlyList<Choice> DashAttacks =
        [new("off", "Off"), new("on", "On (default attack)"),
         .. GameKnowledge.AttackIds.Where(a => a.Value <= GameKnowledge.DashAttackLast).Select(a => a.Key).Order().Select(n => new Choice(n, n))];

    /// <summary>The dash attack choice for a key value: an attack name, or true / false.</summary>
    public static Choice DashOf(object? v) => DashAttacks.FirstOrDefault(c => c.Id == (v as string ?? (v is true ? "on" : "off"))) ?? DashAttacks[0];

    /// <summary>The key value for a dash attack choice.</summary>
    public static object DashValue(Choice c) => c.Id switch { "off" => false, "on" => true, _ => c.Id };
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
        set { int v = (int)Math.Clamp(value, 0, 100); if (v == _value) return; _value = v; owner.SetMaterial(Name, v); Raise(); Raise(nameof(Bonus)); }
    }
    /// <summary>The in-game camo bonus of this value, e.g. "+5 %".</summary>
    public string Bonus => OutfitVm.CamoBonus(_value);
    public void Reload(int v) { _value = v; Raise(nameof(Value)); Raise(nameof(Bonus)); }
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
        foreach (var n in new[] { nameof(Look), nameof(IsDefault), nameof(Label), nameof(CanLabel), nameof(LabelTip), nameof(Where), nameof(Arm), nameof(ArmOn), nameof(ArmTip), nameof(Logos), nameof(LogoHint), nameof(Logo), nameof(CanUp), nameof(CanDown), nameof(CanRemove), nameof(CanBeDefault), nameof(Up), nameof(Down), nameof(Remove), nameof(MakeDefault) }) Raise(n);
    }
    public bool CanUp => Index > 0;
    public bool CanDown => Index < _o.Looks.Count - 1;
    public bool CanRemove => _o.CanRemoveLook;
    /// <summary>#62: a vanilla-only outfit has no Main look, so no look is worn first.</summary>
    public bool CanBeDefault => !_o.VanillaOnly;
    public string Label { get => _o.LookLabel(Look); set => _o.SetLookLabel(Index, Look, value); }
    /// <summary>False for an outfit with one slot: the build writes no slot name for it (see <see cref="OutfitVm.CanNameLooks"/>).</summary>
    public bool CanLabel => _o.CanNameLooks;
    public string? LabelTip => CanLabel ? null : "The game shows no slot name for an outfit with one slot. Add a second slot to name the slots.";
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
    public string Summary
    {
        get
        {
            var on = Rows.Where(r => r.Value > 0).ToList();
            if (on.Count == 0) return $"{Rows.Count} surfaces, no bonus";
            var v = on.Select(r => (int)r.Value).Distinct().ToList();
            return $"{on.Count} of {Rows.Count} surfaces get " + (v.Count == 1 ? OutfitVm.CamoBonus(v[0]) : "a bonus");
        }
    }
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
    /// <summary>The in-game camo bonus of <see cref="Value"/>, e.g. "+5 %"; empty when mixed.</summary>
    public string Bonus { get { var v = Materials.Select(owner.MaterialValue).Distinct().ToList(); return v.Count == 1 ? OutfitVm.CamoBonus(v[0]) : ""; } }
    public void Reload() { Raise(nameof(On)); Raise(nameof(Partial)); Raise(nameof(Count)); Raise(nameof(Value)); Raise(nameof(Bonus)); Raise(nameof(HasAutoNote)); }
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
    /// <summary>
    /// A camo value as the in-game bonus on the camo index: 10 = 1 %. Owner tests, Nov 17 (#89), on rock with a base of
    /// 30 %: 50 gave 35 %, 100 gave 40 %, 500 gave 80 %. The shown camo % also depends on stance and light, so only the bonus shows.
    /// </summary>
    public static string CamoBonus(int value) => value <= 0 ? "" : $"+{value / 10.0:0.#} %";
    public int MaterialValue(string name) => CamoValuesOf(First).GetValueOrDefault(name);
    private static IReadOnlyDictionary<string, int> CamoValuesOf(OutfitBranch b) => OutfitEdits.CamoValues(b);
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
        Edit(OutfitEdits.SetCamoValues(_o, list, value, Before("c:group:" + list[0])));
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
    public bool Wears(PlayerBranch who) => _o.Spec.Branches.ContainsKey(who);
    public void SetWears(PlayerBranch who, bool on)
    {
        Edit(OutfitEdits.SetWearer(_s.Project!, _o, who, on, OriginalBranches, Before($"wear:{who}")));
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
        get => OutfitEdits.ProstheticOn(_o, ArmLook);
        set { if (Edit(OutfitEdits.SetProsthetic(_o, value, ArmLook, Before($"arm:{_abLook}")))) ReloadLooks(); }
    }
    /// <summary>Abilities tab, VOICE (M8): the outfit has a voice package (voiceFpk), as text or as a table.</summary>
    public bool HasVoice => _o.Spec.Branches.Values.Any(b => b.Voice is { } v && (v.Fpk != null || v.Fpks.Count > 0));
    /// <summary>"One voice package for all types": voiceFpk is text. Off is the table, a package for each voice type.</summary>
    public bool VoiceOne
    {
        get => _o.Spec.Branches.Values.Any(b => b.Voice?.Fpk != null);
        set { if (value && Edit(OutfitEdits.Change(_o, (who, b) => b.Voice is { } v ? b with { Voice = v.OneForAll() } : b, Before("voice")))) RaiseVoice(); }
    }
    public bool VoiceEach
    {
        get => HasVoice && !VoiceOne;
        set { if (value && Edit(OutfitEdits.Change(_o, (who, b) => b.Voice is { } v ? b with { Voice = v.EachType(who) } : b, Before("voice")))) RaiseVoice(); }
    }
    public string VoiceLine => string.Join("\n", _o.Spec.Branches.Values.Select(b => b.Voice).OfType<OutfitVoice>()
        .SelectMany(v => v.Fpk != null ? [v.Fpk] : v.Fpks.Select(kv => $"{kv.Key}: {kv.Value}")).Distinct());
    private void RaiseVoice() { Raise(nameof(HasVoice)); Raise(nameof(VoiceOne)); Raise(nameof(VoiceEach)); Raise(nameof(VoiceLine)); }
    /// <summary>"The outfit" is every look (-1), else the look that "Abilities for" shows.</summary>
    private int ArmLook => _abLook == 0 ? -1 : _abLook;
    public List<Choice> Rattle { get; } = [];
    public List<Choice> HitSound { get; } = [];
    public IReadOnlyList<int> Levels { get; } = Enumerable.Range(0, 10).ToList();

    private OutfitBranch First => _o.Spec.Branches.Values.First();
    public IReadOnlyList<Look> Looks => LookRows.Select(r => r.Look).ToList();

    // ---- changing branches ----

    /// <summary>Makes the undo step of a change of this outfit. <see cref="OutfitEdits"/> calls it only for a real change.</summary>
    private Action Before(string field) => () => _s.Checkpoint($"{_o.Id}:{field}");
    /// <summary>After a function of <see cref="OutfitEdits"/>: tells the session when the outfit changed.</summary>
    private bool Edit(bool changed) { if (changed) _s.NotifyChanged(); return changed; }

    private void Mutate(string field, Func<OutfitBranch, OutfitBranch> f) => Edit(OutfitEdits.Change(_o, (_, b) => f(b), Before(field)));

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
        Raise(nameof(SilentFootsteps)); Raise(nameof(QuietMovement)); Raise(nameof(SprintSound)); Raise(nameof(SelectedDash)); Raise(nameof(RaidenRun));
        Raise(nameof(RaidenAllowed)); Raise(nameof(RaidenNote));
        Raise(nameof(SprintOn)); Raise(nameof(SprintSpeed)); Raise(nameof(Defense)); Raise(nameof(LifeRecovery)); Raise(nameof(SelectedRattle)); Raise(nameof(SelectedHit));
        Raise(nameof(SelectedPreset)); Raise(nameof(PresetNote)); Raise(nameof(PresetTicks));
        if (_abLook > First.Variants.Count) _abLook = 0;
        Raise(nameof(AbilityLooks)); Raise(nameof(AbilityLook)); Raise(nameof(HasAbilityLooks)); Raise(nameof(LookChosen)); Raise(nameof(LookAbilityMode)); Raise(nameof(AbilitiesEditable)); Raise(nameof(EnableProsthetic));
        Raise(nameof(GradeOn)); Raise(nameof(Grade)); Raise(nameof(GradeHigh)); Raise(nameof(GradeMax)); Raise(nameof(DevelopCost)); Raise(nameof(UsageCost)); Raise(nameof(AlreadyDeveloped)); Raise(nameof(Unlocked)); Raise(nameof(DevelopNote));
        RaiseVoice(); ReloadCamo(); ReloadLooks(); ReloadHeads(); BuildWearers();
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
    public IReadOnlyList<Choice> DashAttacks => Choice.DashAttacks;
    public Choice SelectedDash { get => Choice.DashOf(DashKey.Get(Cur)); set { if (value != null) MutateAbilities("a:da", a => DashKey.Set(a, Choice.DashValue(value))); } }
    private static readonly AbilityKey DashKey = Abilities.Keys.Single(k => k.Name == "dashAttack");
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
    // #124: the game supports grades 1 to 12. A new value is limited to that; an old value above 12 stays until the user changes it.
    public decimal Grade { get => Dev.Grade ?? 1; set { if (value == Grade) return; MutateDev("d:g", d => d with { Grade = (int)Math.Clamp(value, 1, CopyFamily.MaxGrade) }); Raise(nameof(GradeHigh)); Raise(nameof(GradeMax)); } }
    public bool GradeHigh => Dev.Grade > CopyFamily.MaxGrade;
    /// <summary>The Grade box maximum: 12, or the old value above 12, so that the box does not change it when the item opens.</summary>
    public decimal GradeMax => Math.Max(CopyFamily.MaxGrade, Grade);
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
            Edit(OutfitEdits.SetCamoPreset(_o, value.Id.Length == 0 ? null : value.Id, Before("c:type")));
            ReloadCamo();
        }
    }
    public string CamoNote => First.CamoBonusType != null ? $"Hides like the vanilla camo {First.CamoBonusType}: its values from the game show below. Change a value and the choice becomes Custom." : "Each ground gets the number you set. 10 adds 1 % to the camo index in game; the vanilla camos use 50 (+5 %).";

    public void SetMaterial(string name, int value)
    {
        Edit(OutfitEdits.SetCamoValues(_o, [name], value, Before("c:" + name)));
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
    /// <summary>
    /// True when a wearer has 2 or more slots. The build writes the slot names only then: V Framework 1.7 draws the name
    /// of a one-slot outfit over the grade bar of its row (owner test, 17 Nov), so <see cref="LuaWriter"/> leaves it out.
    /// </summary>
    public bool CanNameLooks => LuaWriter.IsMultiLook(_o.Spec.Branches);
    public string LookLabel(Look l) => l.DisplayName == null ? "" : _item.GetText(l.DisplayName, "eng", false);
    public void SetLookLabel(int index, Look l, string text)
    {
        if (l.DisplayName != null) { _item.SetText(l.DisplayName, "eng", text); return; }
        // Issue #18: a look with no lang id (a one-look port) dropped the typed name, so the mockup and the build never got
        // it. The first letter gives the look a lang id on every character; the undo field is the one SetText uses for the
        // later letters, so one undo takes the whole name off.
        Edit(OutfitEdits.NameLook(_o, _item.Item, index, text, id => _s.Checkpoint($"{_item.Item.Id}:{id}:eng")) != null);
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

    /// <summary>After a change of the own slots: the "Abilities for" picker follows its look, and the rows are made again.</summary>
    /// <param name="order">For each new position, the old position of its look; null when nothing changed.</param>
    private void Moved(IReadOnlyList<int>? order)
    {
        if (order == null) return;
        // A look moved to row 0 is the outfit's (it has no own abilities); a removed look gives the outfit's too.
        if (_abLook > 0) _abLook = Math.Max(0, order.ToList().IndexOf(_abLook));
        _s.NotifyChanged();
        ReloadLooks();
    }

    public void MoveLook(int index, int delta) => Moved(SortieSlots.MoveOwn(_o, index, index + delta, () => _s.Checkpoint($"{_o.Id}:l:move")));

    /// <summary>
    /// Moves the look at <paramref name="from"/> to <paramref name="to"/>, shifting the ones in between. This is what a
    /// drag does; <see cref="MoveLook"/> only steps one place at a time.
    /// </summary>
    public void MoveLookTo(int from, int to) => Moved(SortieSlots.MoveOwn(_o, from, to, () => _s.Checkpoint($"{_o.Id}:l:drag")));

    // #62: the last own slot has its own undo field: it is not merged with an earlier Remove.
    public void RemoveLook(int index) =>
        Moved(SortieSlots.RemoveOwn(_s.Project, _o, index, () => _s.Checkpoint($"{_o.Id}:" + (First.Variants.Count == 0 ? "l:vanilla-only" : "l:remove"))));

    /// <summary>Issue #30: puts a copy of the look at <paramref name="index"/> after it, with its own name, for every character.</summary>
    public void CloneLook(int index) => Moved(SortieSlots.CloneOwn(_o, _item.Item, index, () => _s.Checkpoint())); // no field: two clones in a row are two undo steps

    /// <summary>Issue #30: Add slot in the Sortie mockup. The new slot is a copy of the main look at the end, named "New slot".</summary>
    public void AddLook() => Moved(SortieSlots.AddOwn(_o, _item.Item, () => _s.Checkpoint(VanillaOnly ? $"{_o.Id}:l:own-slot" : "")));

    /// <summary>#62: the outfit has no own slot, only bonus slots under vanilla suits.</summary>
    public bool VanillaOnly => _o.Spec.VanillaOnly;
    /// <summary>#87: the outfit's own icon (the ICON local), the icon a look with no icon of its own shows.</summary>
    public string OutfitIcon => _o.Spec.IconFtexPath;
    /// <summary>#62: an own slot can go while another slot stays; an existing V Framework mod keeps its last own slot.</summary>
    public bool CanRemoveLook => SortieSlots.CanRemoveOwn(_s.Project, _o);

    /// <summary>Sets enableArm on the look at this position, for every character.</summary>
    public void SetLookArm(int index, int v)
    {
        if (Edit(OutfitEdits.SetLookArm(_o, index, v switch { 1 => true, 2 => false, _ => null }, Before($"l:arm:{index}")))) ReloadLooks();
    }

    /// <summary>
    /// Sets the icon the look at this position shows in the suit list and on its sortie slot, for every character.
    /// This is V Framework's per-look <c>iconFtexPath</c>, an extensionless .ftex path; null clears it so the look
    /// inherits the outfit's own icon.
    /// </summary>
    public void SetLookLogo(int index, string? logo)
    {
        if (Edit(OutfitEdits.SetLookLogo(_o, index, logo, Before($"l:logo:{index}")))) ReloadLooks();
    }

    public void SetDefaultLook(int index) => Moved(SortieSlots.SetWornFirst(_o, index, () => _s.Checkpoint($"{_o.Id}:l:default")));

    // ---- Heads ----

    public bool HasHead(PlayerBranch who, string id) => OutfitEdits.HasHead(_o, who, id);

    public void SetHead(PlayerBranch who, string id, bool on)
    {
        if (Edit(OutfitEdits.SetHead(_o, who, id, on, Before($"h:{who}:{id}")))) ReloadHeads();
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
    public static string AvatarBandanaNote => "The Avatar wears its own face with the Avatar's bandana. The bandana is available after the Bandana is developed on the R&D tab, the same as for Snake.";

    private void SetBandana(bool on, PlayerBranch who)
    {
        // Issue #98: Snake's bandana is always the head that follows the vanilla development; the switch is now
        // "Follows a vanilla development" on the R&D tab of the BANDANA head item. An old project keeps its form.
        GatedBandanas.List(_s.Project!, _o, on, true, who);
        _s.NotifyChanged();
        Raise(nameof(ListBandana)); Raise(nameof(ListBandanaAvatar));
    }

    public static string BandanaNote => "The bandana is added as a head of its own. By default it is wearable once the vanilla bandana is developed. To change this, select the BANDANA head item and use \"Follows a vanilla development\" on its R&D tab.";

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
        Raise(nameof(HasHeads)); Raise(nameof(HasModHeads)); Raise(nameof(ListBandana)); Raise(nameof(CanListBandana)); Raise(nameof(CanListBandanaAvatar)); Raise(nameof(ListBandanaAvatar)); Raise(nameof(NoHeadNote)); Raise(nameof(HideHead)); Raise(nameof(HideHeadWarning));
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
        get => OutfitEdits.HeadHidden(_o);
        set
        {
            if (!Edit(OutfitEdits.HideHead(_o, value, Before("head")))) return;
            ReloadLooks();
            Raise(); Raise(nameof(HideHeadWarning)); Raise(nameof(NoHeadNote));
        }
    }
    public string HideHeadWarning => HideHead
        ? "Set by SVFM: the body model has its own head, so the wearer's head is hidden. Turn this off only if the head is missing in game."
        : "";
}
