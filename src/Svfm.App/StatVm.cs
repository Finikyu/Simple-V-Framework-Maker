using System.Collections.ObjectModel;
using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

public enum StatKind { Number, Check, Choice, Info }

/// <summary>One editable row of a Stats or behaviour tab: a number, a checkbox or a choice.</summary>
public sealed class StatRowVm : Observable
{
    private readonly Session _s;
    private readonly string _id;
    private readonly Func<double?>? _getN; private readonly Action<double?>? _setN;
    private readonly Func<bool>? _getB; private readonly Action<bool>? _setB;
    private readonly Func<string>? _getC; private readonly Action<string>? _setC;

    private StatRowVm(Session s, string id, string label, string note, StatKind kind, double min, double max, double step, IReadOnlyList<Choice>? choices,
        Func<double?>? gn, Action<double?>? sn, Func<bool>? gb, Action<bool>? sb, Func<string>? gc, Action<string>? sc)
    { _s = s; _id = id; _label = label; _note = note; Kind = kind; Minimum = (decimal)min; Maximum = (decimal)max; Increment = (decimal)step; Choices = choices ?? []; _getN = gn; _setN = sn; _getB = gb; _setB = sb; _getC = gc; _setC = sc; }

    public static StatRowVm Number(Session s, string id, string label, double min, double max, double step, Func<double?> get, Action<double?> set, string note = "",
        Func<string>? labelOf = null, Func<string>? noteOf = null, Func<string>? warnOf = null) =>
        new(s, id, label, note, StatKind.Number, min, max, step, null, get, set, null, null, null, null) { LabelOf = labelOf, NoteOf = noteOf, WarnOf = warnOf };
    public static StatRowVm Check(Session s, string id, string label, Func<bool> get, Action<bool> set, string note = "") =>
        new(s, id, label, note, StatKind.Check, 0, 0, 0, null, null, null, get, set, null, null);
    public static StatRowVm Pick(Session s, string id, string label, IReadOnlyList<Choice> choices, Func<string> get, Action<string> set, string note = "") =>
        new(s, id, label, note, StatKind.Choice, 0, 0, 0, choices, null, null, null, null, get, set);

    /// <summary>The id of the last choice of <see cref="PickNumber"/>: "Type a number…".</summary>
    public const string TypeNumber = "#";
    /// <summary>A number with named values (#111): a value that is not in the list shows as "Type a number…" with a number box.</summary>
    public static StatRowVm PickNumber(Session s, string id, string label, IReadOnlyList<Choice> choices, Func<double> get, Action<double> set, double max, string note = "") =>
        new(s, id, label, note, StatKind.Choice, 0, max, 1, [.. choices, new(TypeNumber, "Type a number…")], () => get(), x => set(x ?? 0), null, null,
            () => get().ToString(System.Globalization.CultureInfo.InvariantCulture), c => set(double.Parse(c, System.Globalization.CultureInfo.InvariantCulture)));
    private bool _typed;
    /// <summary>The number box shows: a number row, or a <see cref="PickNumber"/> row on "Type a number…".</summary>
    public bool ShowNumber => IsNumber || (IsChoice && _getN != null && Picked?.Id == TypeNumber);

    /// <summary>A value shown but not editable (constants, names).</summary>
    public static StatRowVm Info(Session s, string id, string label, Func<string> get, string note = "") =>
        new(s, id, label, note, StatKind.Info, 0, 0, 0, null, null, null, null, null, get, null);
    public string InfoText => _getC?.Invoke() ?? "";
    public bool IsInfo => Kind == StatKind.Info;
    private readonly string _label, _note;
    /// <summary>A label and a help text that follow other values (the p1 row follows the thrown-as item); null: the fixed ones.</summary>
    public Func<string>? LabelOf { get; init; }
    public Func<string>? NoteOf { get; init; }
    /// <summary>A warning (note level "warn"); empty: none. The "!" then shows it above the help, in orange.</summary>
    public Func<string>? WarnOf { get; init; }
    public string Label => LabelOf?.Invoke() ?? _label;
    public string Warn => WarnOf?.Invoke() ?? "";
    /// <summary>The text of the row's "!" (help, and a warning first when there is one).</summary>
    public string Note => string.Join("\n\n", new[] { Warn, NoteOf?.Invoke() ?? _note }.Where(n => n.Length > 0));
    public bool HasNote => Note.Length > 0;
    public bool NoteWarn => Warn.Length > 0;
    public StatKind Kind { get; }
    public bool IsNumber => Kind == StatKind.Number;
    public bool IsCheck => Kind == StatKind.Check;
    public bool IsChoice => Kind == StatKind.Choice;
    public decimal Minimum { get; }
    public decimal Maximum { get; }
    public decimal Increment { get; }
    public IReadOnlyList<Choice> Choices { get; }

    public decimal? NumberValue
    {
        get => _getN!() is { } d ? Svfm.Core.Formats.LuaLiteral.ForNumberBox(d) : null;
        set { var now = _getN!(); double? nv = value is { } v ? (double)v : null; if (now == nv) return; _s.Checkpoint($"{_id}"); _setN!(nv); _s.NotifyChanged(); Raise(); }
    }
    public bool BoolValue { get => _getB!(); set { if (_getB!() == value) return; _s.Checkpoint($"{_id}"); _setB!(value); _s.NotifyChanged(); Raise(); } }
    public Choice? Picked
    {
        get
        {
            var id = _typed ? TypeNumber : _getC!();
            return Choices.FirstOrDefault(c => c.Id == id) ?? (_getN != null && IsChoice ? Choices[^1] : Choices.FirstOrDefault());
        }
        set
        {
            if (value == null) return;
            _typed = value.Id == TypeNumber && _getN != null;
            if (!_typed && value.Id != _getC!()) { _s.Checkpoint($"{_id}"); _setC!(value.Id); _s.NotifyChanged(); }
            Raise(); Raise(nameof(ShowNumber));
        }
    }
    /// <summary>A button after the value (#119): its label now ("" hides it), and what a click does (one undo step).</summary>
    public Func<string>? ActionOf { get; set; }
    public Action? OnAction { get; set; }
    public string ActionLabel => ActionOf?.Invoke() ?? "";
    public bool HasAction => ActionLabel.Length > 0;
    private Command? _act;
    public Command ActionCommand => _act ??= new Command(() => { _s.Checkpoint($"{_id}:action"); OnAction!(); _s.NotifyChanged(); Reload(); });
    public void Reload() { foreach (var n in new[] { nameof(NumberValue), nameof(BoolValue), nameof(Picked), nameof(ShowNumber), nameof(InfoText), nameof(Label), nameof(Note), nameof(HasNote), nameof(NoteWarn), nameof(ActionLabel), nameof(HasAction) }) Raise(n); }
}

public sealed class StatGroupVm(string title, IEnumerable<StatRowVm> rows)
{
    /// <summary>Whether the group starts unfolded.</summary>
    public bool Open { get; init; } = true;
    public string Title { get; } = title;
    public string Note { get; init; } = "";
    public ObservableCollection<StatRowVm> Rows { get; } = new(rows);
}

/// <summary>One camo in the weapon's camo picker: its id ("" = the default paint), name and the game's menu swatch.</summary>
public sealed record CamoSwatchVm(string Id, string Label, Avalonia.Media.IImage? Image)
{
    public override string ToString() => Label;
}

/// <summary>The behaviour tab of a weapon, thrown item, buddy costume or head: what the item does, as the tool knows it.</summary>
public sealed class StatsVm : Observable
{
    /// <summary>Called after the "Thrown as" row changes the item (the script tab then reloads its steps).</summary>
    public Action? ThrowAsChanged { get; set; }
    /// <summary>The vanilla throwing items a thrown item can be thrown as, by EQP id.</summary>
    private static List<Choice> ThrowChoices(Session s) => GameKnowledge.SupportWeapons.Where(w => w.Value.EquipType == "Throwing")
        .Select(w => new Choice(w.Key, GameKnowledge.EnglishName(s.Game, w.Value.Develop?.Name) is { Length: > 0 } n ? $"{n} ({w.Key})" : w.Key))
        .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
    private static StatRowVm ThrowAsRow(Session s, StatsVm v, string id, Func<string> get, Action<string> set)
    {
        var choices = ThrowChoices(s);
        const string note = "The game holds and throws the item like this one. A script that is the default effect changes with it.";
        return choices.Any(c => c.Id == get()) ? StatRowVm.Pick(s, id, "Thrown as", choices, get, x => { set(x); v.ThrowAsChanged?.Invoke(); }, note)
            : StatRowVm.Info(s, id, "Thrown as", get);
    }
    /// <summary>
    /// Issue #107: the Preset pick copies the values of a vanilla thrown item. A script that the preset would lose (edited,
    /// or the mod's own) asks first; Replace is one undo step, Cancel changes nothing.
    /// </summary>
    private static StatRowVm PresetRow(Session s, StatsVm v, string id, Func<string?> shown, Action<string?> setPick, Func<string, string?> confirm, Action<string> apply)
    {
        List<Choice> choices = [new("", "Custom"), .. ThrowChoices(s)];
        return StatRowVm.Pick(s, id, "Preset", choices, () => shown() ?? "", x =>
        {
            if (x == "") { setPick(null); return; }
            void Do() { apply(x); v.Reload(); v.ThrowAsChanged?.Invoke(); }
            if (confirm(choices.First(c => c.Id == x).Label) is { } text) v.Ask(text, id, Do); else Do();
        }, "Copies what it is thrown as, the ammo, fuse, effect time, grade, attack, blast and default script of a vanilla thrown item. The model, name, description, icon and R&D row stay.");
    }
    private Action? _pending; private string _askId = "";
    /// <summary>The question shown before a preset replaces a script; Replace runs the change.</summary>
    public string ConfirmText { get; private set; } = "";
    public bool Confirming => _pending != null;
    private void Ask(string text, string id, Action run) { ConfirmText = text; _pending = run; _askId = id; Raise(nameof(ConfirmText)); Raise(nameof(Confirming)); }
    private void EndAsk() { _pending = null; Raise(nameof(Confirming)); }
    public Command ReplaceCommand => _replace ??= new Command(() =>
    {
        if (_pending is not { } run || _session is not { } s) return;
        EndAsk(); s.Checkpoint(_askId + ":replace"); run(); s.NotifyChanged(); Reload();
    });
    public Command CancelCommand => _cancel ??= new Command(() => { EndAsk(); Reload(); });
    private Command? _replace, _cancel;
    /// <summary>A weapon's camo choices (the default paint first); empty for other items.</summary>
    public IReadOnlyList<CamoSwatchVm> Camos { get; private set; } = [];
    public bool HasCamo => Camos.Count > 0;
    private Func<string?>? _getCamo; private Action<string?>? _setCamo; private Session? _session; private string _camoId = "";
    public CamoSwatchVm? PickedCamo
    {
        get { var c = _getCamo?.Invoke() ?? ""; return Camos.FirstOrDefault(x => x.Id == c) ?? Camos.FirstOrDefault(); }
        set
        {
            if (value == null || _setCamo == null || value.Id == (_getCamo!() ?? "")) return;
            _session!.Checkpoint(_camoId); _setCamo(value.Id == "" ? null : value.Id); _session.NotifyChanged(); Raise(); Raise(nameof(CamoLine));
        }
    }
    public string CamoLine => PickedCamo is { } c ? $"Paint: {c.Label}" : "";
    /// <summary>What the import found about the mod's own paint textures (the weapon's "Camo: …" notes), one per line.</summary>
    public string CamoNote { get; private set; } = "";
    public bool HasCamoNote => CamoNote.Length > 0;

    /// <summary>Swatch bitmaps per camo id, read once from the game data.</summary>
    private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> Swatches = [];
    private static List<string>? _camoIds;
    private static Svfm.Core.Archives.GameData? _camoGame;
    private static Avalonia.Media.Imaging.Bitmap? Swatch(Svfm.Core.Archives.GameData game, string ftex)
    {
        lock (Swatches)
        {
            if (Swatches.TryGetValue(ftex, out var b)) return b;
            try { b = game.TryReadTexture(ftex) is { } img ? new Avalonia.Media.Imaging.Bitmap(new MemoryStream(Png.Encode(img))) : null; }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException) { b = null; }
            return Swatches[ftex] = b;
        }
    }

    private void AddCamo(Session s, string id, Func<string?> get, Action<string?> set)
    {
        if (s.Game is not { } game) return;
        lock (Swatches)
        {
            // Another game folder was opened: its camos and swatches can differ.
            if (_camoGame != game) { _camoGame = game; _camoIds = null; Swatches.Clear(); }
            _camoIds ??= Svfm.Core.Conversion.WeaponConverter.Camos(game);
        }
        var list = new List<CamoSwatchVm> { new("", "Default paint", Swatch(game, "/Assets/tpp/ui/texture/Customize/color/ui_cstm_color_default.ftex")) };
        list.AddRange(_camoIds.Select(c => new CamoSwatchVm(c, $"Camo {c[1..]}", Swatch(game, Svfm.Core.Conversion.WeaponConverter.CamoIcon(c)))));
        (Camos, _getCamo, _setCamo, _session, _camoId) = (list, get, set, s, id);
    }

    public string TabId { get; init; } = "stats";
    public string TabTitle { get; init; } = "Stats";
    public string Intro { get; init; } = "";
    public ObservableCollection<StatGroupVm> Groups { get; } = [];
    /// <summary>Adds or removes groups that come and go with the item's values (the own attack and blast, #119); runs on Reload.</summary>
    public Action? Sync { get; set; }
    /// <summary>A weapon's sight kind, one read-only line above the groups. Null for other items.</summary>
    public Func<string>? Sight { get; init; }
    public bool HasSight => Sight != null;
    public string SightLine => Sight?.Invoke() ?? "";
    public string SightTip { get; init; } = "";
    public void RefreshSight() => Raise(nameof(SightLine));
    public void Reload() { Sync?.Invoke(); foreach (var g in Groups) foreach (var r in g.Rows) r.Reload(); RefreshSight(); Raise(nameof(PickedCamo)); Raise(nameof(CamoLine)); }

    private static readonly Choice Default = new("", "Default");
    private static readonly IReadOnlyList<Choice> YesNo = [new("", "Default"), new("yes", "Yes"), new("no", "No")];
    private static string Tri(bool? b) => b == null ? "" : b.Value ? "yes" : "no";
    private static bool? FromTri(string s) => s == "" ? null : s == "yes";

    /// <summary>The part kind of each group name of an existing weapon's settings (ExistingMod.StatGroups).</summary>
    private static readonly Dictionary<string, WeaponPartKind> PartKinds = new()
    {
        ["Receiver"] = WeaponPartKind.Receiver, ["Damage"] = WeaponPartKind.Damage, ["Barrel"] = WeaponPartKind.Barrel, ["Magazine"] = WeaponPartKind.Magazine,
        ["Bullet"] = WeaponPartKind.Bullet, ["Stock"] = WeaponPartKind.Stock, ["Muzzle"] = WeaponPartKind.MuzzleOption, ["Sight"] = WeaponPartKind.Sight,
        ["Option"] = WeaponPartKind.Option, ["Under-barrel"] = WeaponPartKind.UnderBarrel,
    };

    /// <summary>The row label and number range of each behaviour key of an existing buddy costume (ExistingModWeapons.BuddyKeys),
    /// worded as the rows of a costume the tool makes.</summary>
    private static readonly Dictionary<string, (string label, double min, double max, double step)> BuddyRows = new()
    {
        ["quietBruise"] = ("Show the bruises of NAKED", 0, 0, 0), ["bruise"] = ("Bruises show", 0, 0, 0),
        ["commandType"] = ("Order he carries out", 0, 0, 0), ["damageRate"] = ("Share of damage taken (1 = full)", 0, 1.275, 0.05),
        ["bulletFlinch"] = ("Flinches from bullets", 0, 0, 0), ["armorHitEffect"] = ("Armour hit effect", 0, 0, 0),
        ["armorHitSound"] = ("Armour hit sound", 0, 0, 0), ["soldierThreat"] = ("Soldiers see him as a threat", 0, 0, 0),
        ["escapeDistance"] = ("Escape distance (metres)", 0, 100, 1), ["hitRetreatDistance"] = ("Retreat distance when hit (metres)", 0, 100, 1),
        ["retargetRange"] = ("Retarget range (metres)", 0, 100, 1), ["gallopSpeedRate"] = ("Gallop speed ×", 0, 3, 0.05),
        ["bodyAttackId"] = ("Ram attack", 0, 0, 0), ["soundType"] = ("Movement sounds of", 0, 0, 0),
    };

    private static readonly IReadOnlyList<Choice> DamageTypes =
        [new("", "Automatic"), new("1", "Damage"), new("2", "Sleep (ZZZ)"), new("3", "Stun"), new("4", "Smoke"), new("6", "None (---)"), new("0", "Hidden")];

    /// <summary>The p1 row of a thrown item: its label and help for what p1 means (issue #118, .docs/Thrown_p1_p2.md).</summary>
    public static string P1Label(P1Kind k) => k switch { P1Kind.SensorRange => "Sensor range (metres)", P1Kind.DecoyStrength => "Decoy strength", _ => "Fuse time (seconds)" };
    private static string P1Help(P1Kind k) => k switch
    {
        P1Kind.SensorRange => "The distance in metres at which the item finds a target. The vanilla mines use 0.8 to 1.2 metres.",
        P1Kind.DecoyStrength => "How strongly the decoy pulls the attention of enemies. The vanilla decoys use 300 to 700.",
        _ => "The time from the throw until the item goes off. An item that goes off when it hits a surface needs a fuse longer than its flight, for example 10 seconds. 0: the item goes off when it leaves the hand.",
    };
    public const string ShortFuseNote = "The fuse ends before the item lands, so 'Hits a surface' never runs. Use a fuse longer than the flight, for example 10 seconds.";
    private const string EffectTimeHelp = "The time in seconds that the effect of the item stays, for example the smoke of a smoke grenade (vanilla 30 to 60 seconds) or the gas of a sleeping gas grenade (vanilla 10 seconds). 0: the effect does not stay. Not yet tested in game on a V Framework item.";

    /// <summary>Help for the rows of a thrown item's own attack, by DamageParameterTables field. A field with no entry gets no "!".</summary>
    private static readonly Dictionary<string, string> AttackHelp = new()
    {
        ["lethalDamage"] = "The health damage of the attack.",
        ["staminaDamage"] = "The stamina damage of the attack. A target with no stamina falls unconscious.",
        ["blowPower"] = "How hard the attack pushes the target. 1000 or more makes the target stagger.",
        ["shieldDamage"] = "The damage that the attack does to a shield.",
        ["injureMaxDistance"] = "The largest distance in metres at which the attack can injure the target.",
        ["injureRate"] = "The chance in percent that the attack injures the target.",
        ["isExplosive"] = "The attack is an explosion.",
        ["isStun"] = "The attack stuns the target.",
        ["isTranq"] = "The attack puts the target to sleep.",
        ["isFire"] = "The attack sets the target on fire.",
        ["isGas"] = "The attack is a gas.",
        ["isElectric"] = "The attack is electric.",
        ["unk25"] = "The use of this value is not known.",
    };

    /// <summary>The extra tabs of a thrown item: how it shows in Sortie Prep, and its attack and blast.</summary>
    public static List<StatsVm> More(Session s, ProjectItem item)
    {
        var list = new List<StatsVm>();
        if (item is not SupportWeaponItem t) return list;
        var sp = t.Spec; var id = t.Id;
        var prep = new StatsVm { TabId = "sortie", TabTitle = "Sortie Prep details", Intro = "How the item looks in the details panel of Sortie Prep: the damage type icon and the three bars. Left on automatic, the game fills them from the item's stats." };
        prep.Groups.Add(new StatGroupVm("Damage type",
        [
            StatRowVm.Pick(s, id + ":dt", "Icon shown", DamageTypes, () => sp.DamageType?.ToString() ?? "", x => sp.DamageType = x == "" ? null : int.Parse(x)),
        ]));
        StatGroupVm Bar(string title, string key, Func<double?> get, Action<double?> set, Func<bool> hid, Action<bool> sethid) => new(title,
        [
            StatRowVm.Check(s, id + ":" + key + "show", "Show the bar", () => !hid(), x => sethid(!x)),
            StatRowVm.Check(s, id + ":" + key + "auto", "Fill automatically", () => get() == null, x => set(x ? null : (get() ?? 0.5))),
            StatRowVm.Number(s, id + ":" + key + "fill", "Fill (0 empty, 1 full)", 0, 1, 0.05, () => get(), x => set(x), "Setting a value turns automatic off."),
        ]);
        prep.Groups.Add(Bar("Damage bar", "bd", () => sp.BarDamage, x => sp.BarDamage = x, () => sp.HideBarDamage, x => sp.HideBarDamage = x));
        prep.Groups.Add(Bar("Durability bar", "bu", () => sp.BarDurability, x => sp.BarDurability = x, () => sp.HideBarDurability, x => sp.HideBarDurability = x));
        prep.Groups.Add(Bar("Effect duration bar", "be", () => sp.BarEffectTime, x => sp.BarEffectTime = x, () => sp.HideBarEffectTime, x => sp.HideBarEffectTime = x));
        prep.Groups.Add(new StatGroupVm("What a full bar stands for",
        [
            StatRowVm.Number(s, id + ":dmax", "Durability, full bar", 0, 100000, 1, () => sp.DurabilityMax, x => sp.DurabilityMax = x,
                "The durability value that fills the whole Durability bar. Empty: the game's own value."),
            StatRowVm.Number(s, id + ":emax", "Effect duration, full bar (seconds)", 0, 100000, 1, () => sp.EffectTimeMax, x => sp.EffectTimeMax = x,
                "The effect time in seconds that fills the whole Effect duration bar. Empty: the game's own value."),
        ]));
        list.Add(prep);

        var atk = new StatsVm { TabId = "attack", TabTitle = "Attack and blast", Intro = "The damage and explosion the item uses. Left alone, it uses the ones named for the item it is thrown as; only a mod that changed them gives the item its own copy, and the game's own rows are never changed." };
        // #119: "Make own copy" copies the thrown-as item's row; the copy is not linked to the thrown-as item after that.
        const string useAgain = "Use the thrown-as item's again";
        string ThrownName(string eqp) => GameKnowledge.SupportWeapons.TryGetValue(eqp, out var w) && GameKnowledge.EnglishName(s.Game, w.Develop?.Name) is { Length: > 0 } n ? n : eqp;
        object? shownAttack = null; bool shownBlast = false; StatGroupVm? attackGroup = null, blastGroup = null;
        static StatRowVm Act(StatRowVm r, Func<string> label, Action run) { r.ActionOf = label; r.OnAction = run; return r; }
        atk.Groups.Add(new StatGroupVm("Used now",
        [
            Act(StatRowVm.Info(s, id + ":ua", "Attack", () => sp.OwnAttack != null && sp.OwnAttackFrom is { } f ? $"Own (copied from {ThrownName(f)})"
                    : sp.Attack ?? (sp.ThrowAsAttack is { } a ? $"the thrown-as item's ({a})" : "the thrown-as item's"),
                "The damage row that the item uses: its own copy, or the row of the item it is thrown as."),
                () => sp.OwnAttack != null ? useAgain : sp.ThrowAsAttack != null && s.Game != null ? "Make own copy" : "",
                () => { if (sp.OwnAttack != null) { sp.OwnAttack = null; sp.OwnAttackFrom = null; } else SupportWeaponConverter.CopyAttack(s.Game!, sp); Sync(); }),
            Act(StatRowVm.Info(s, id + ":ub", "Blast", () => sp.OwnBlast != null && sp.OwnBlastFrom is { } f ? $"Own (copied from {ThrownName(f)})"
                    : sp.Blast ?? (sp.ThrowAsBlast is { } b ? $"the thrown-as item's ({b})" : "none"),
                "The explosion row that the item uses: its own copy, the row of the item it is thrown as, or none."),
                () => sp.OwnBlast != null ? useAgain : sp.ThrowAsBlast != null && s.Game != null ? "Make own copy" : "",
                () => { if (sp.OwnBlast != null) { sp.OwnBlast = null; sp.OwnBlastFrom = null; } else SupportWeaponConverter.CopyBlast(s.Game!, sp); Sync(); }),
        ]));
        void Sync()
        {
            if (!ReferenceEquals(shownAttack, sp.OwnAttack))
            {
                if (attackGroup != null) atk.Groups.Remove(attackGroup);
                attackGroup = sp.OwnAttack is { } oa ? AttackGroup(oa) : null;
                if (attackGroup != null) atk.Groups.Insert(1, attackGroup);
                shownAttack = sp.OwnAttack;
            }
            if (shownBlast != (sp.OwnBlast != null))
            {
                if (blastGroup != null) atk.Groups.Remove(blastGroup);
                blastGroup = sp.OwnBlast != null ? BlastGroup() : null;
                if (blastGroup != null) atk.Groups.Add(blastGroup);
                shownBlast = blastGroup != null;
            }
        }
        StatGroupVm AttackGroup(PartValues oa)
        {
            var rows = new List<StatRowVm>();
            foreach (var f in WeaponPartFields.For(WeaponPartKind.Damage))
            {
                if (!oa.Values.TryGetValue(f.Path, out var cur)) continue;
                var field = f; string rid = $"{id}:oa:{field.Path}";
                void Put(LuaValue nv) => oa.Values[field.Path] = nv;
                // The field's own explanation, where it has one: the owner found the technical ones unhelpful ("There are
                // also model flags/mesh values and alternative magazine socket I'm unfamiliar with, is that a V-Framework
                // option?").
                string note = AttackHelp.GetValueOrDefault(field.Path) ?? field.Note ?? "";
                if (field.Type is PartFieldType.Constant or PartFieldType.Text) rows.Add(StatRowVm.Info(s, rid, field.Label, () => oa.Values.GetValueOrDefault(field.Path)?.ToString() ?? "", note));
                else if (field.Type == PartFieldType.Number) rows.Add(StatRowVm.Number(s, rid, field.Label, -100000, 1000000, 1, () => oa.Values.GetValueOrDefault(field.Path)?.AsNumber, x => Put(new LuaValue.Number(x ?? 0)), note));
                else
                {
                    bool asName = cur is LuaValue.Name;
                    rows.Add(StatRowVm.Check(s, rid, field.Label, () => oa.Values.GetValueOrDefault(field.Path) is { } c && (c.AsNumber is > 0 || c.ToString() == "true"), x => Put(asName ? new LuaValue.Name(x ? "true" : "false") : new LuaValue.Number(x ? 1 : 0)), note));
                }
            }
            return new StatGroupVm($"The item's own attack ({sp.OwnAttackConstant})", rows) { Open = sp.OwnAttackFrom != null };
        }
        StatGroupVm BlastGroup() => new($"The item's own blast ({sp.OwnBlastConstant})",
            [
                StatRowVm.Number(s, id + ":bf", "Noise kind", 0, 255, 1, () => sp.OwnBlast!.BlastFlag, x => sp.OwnBlast = sp.OwnBlast! with { BlastFlag = (int)(x ?? 0) },
                    "The kind of noise that the blast makes. Enemies react to this noise. The vanilla rows use 0 to 4."),
                StatRowVm.Number(s, id + ":br", "Radius for the player's items (metres)", 0, 1000, 0.5, () => sp.OwnBlast!.EffectRange, x => sp.OwnBlast = sp.OwnBlast! with { EffectRange = x ?? 0 },
                    "The radius of the blast in metres when the player uses the item."),
                StatRowVm.Number(s, id + ":bo", "Radius for others' (metres)", 0, 1000, 0.5, () => sp.OwnBlast!.OptimalRange, x => sp.OwnBlast = sp.OwnBlast! with { OptimalRange = x ?? 0 },
                    "The radius of the blast in metres when a person other than the player uses the item."),
            ]) { Open = sp.OwnBlastFrom != null };
        Sync();
        atk.Sync = Sync;
        list.Add(atk);
        return list;
    }

    /// <summary>Builds the tab for an item the tool made; null when the item has nothing to edit here.</summary>
    public static StatsVm? For(Session s, ProjectItem item)
    {
        switch (item)
        {
            case SupportWeaponItem t:
            {
                var sp = t.Spec; var id = t.Id;
                var v = new StatsVm { TabTitle = "Stats", Intro = "How the thrown item behaves. It has the mod's model and is held and thrown like the item it is thrown as.", _session = s };
                v.Groups.Add(new StatGroupVm("Carried and timing",
                [
                    PresetRow(s, v, id + ":preset", () => ThrownPresets.Shown(sp, s.Game), x => sp.Preset = x,
                        name => ThrownPresets.Confirm(sp.Script, sp.ThrowAs, false, name), x => ThrownPresets.Apply(sp, s.Game, x)),
                    ThrowAsRow(s, v, id + ":throwAs", () => sp.ThrowAs, x =>
                    {
                        var (atk, bla) = s.Game is { } g ? SupportWeaponConverter.ThrowAsRows(g, x) : (null, null);
                        sp.SetThrowAs(x, atk, bla);
                    }),
                    StatRowVm.Number(s, id + ":ammo", "Carried at once", 1, 99, 1, () => sp.Ammo, x => sp.Ammo = (int)(x ?? 1), "The number of these items that Snake carries."),
                    StatRowVm.Number(s, id + ":fuse", P1Label(P1Kind.Fuse), 0, 600, 0.5, () => sp.FuseTime, x => sp.FuseTime = x ?? 0,
                        labelOf: () => P1Label(sp.P1), noteOf: () => P1Help(sp.P1), warnOf: () => sp.ShortFuse ? ShortFuseNote : ""),
                    StatRowVm.Number(s, id + ":active", "Effect time (seconds)", 0, 600, 0.5, () => sp.ActiveTime, x => sp.ActiveTime = x ?? 0, EffectTimeHelp),
                ]));
                return v;
            }
            case ExistingItem { ItemType: ItemKind.BuddyOutfit, StatGroups.Count: > 0 } x:
            {
                // An existing buddy costume: the behaviour keys of each branch its call has, the same rows as a costume the tool makes.
                var branches = x.Fields.Where(f => f.Group == "Buddy behaviour").GroupBy(f => f.Path[0]).ToList();
                string title = branches.Count == 1 ? branches[0].Key switch { "dog" => "How the gear plays", "horse" => "How the horse plays", _ => "How the costume plays" } : "How the costume plays";
                var v = new StatsVm { TabTitle = title, Intro = "The costume's behaviour, as the mod's script sets it. A value the script leaves out shows as 0 or off, and the build writes it only when you change it." };
                var cmds = DogGearBehaviour.CommandTypes.Select((c, i) => new Choice(i.ToString(), c)).ToList();
                var tacks = HorseTackBehaviour.TackNames.Select((t, i) => new Choice(i.ToString(), t)).ToList();
                var rams = GameKnowledge.AttackIds.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new Choice(a.Value.ToString(), a.Key)).ToList();
                foreach (var branch in branches)
                {
                    var rows = new List<StatRowVm>();
                    foreach (var f in branch)
                    {
                        var field = f; string key = f.Path[1]; string rid = $"{x.Id}:{f.Group}:{f.Label}";
                        string note = f.Present ? "" : "Not set in the script.";
                        var (label, min, max, step) = BuddyRows.GetValueOrDefault(key == "bruise" && branch.Key == "quiet" ? "quietBruise" : key, (f.Label, 0, 100, 1));
                        IReadOnlyList<Choice>? pick = key switch { "commandType" => cmds, "soundType" => tacks, "bodyAttackId" => rams, _ => null };
                        string Whole() => ((int)(double)field.Value).ToString();
                        if (f.Locked != null || f.Type == FieldType.Text || (pick != null && f.Present && !pick.Any(c => c.Id == Whole())))
                            rows.Add(StatRowVm.Info(s, rid, label, () => field.Value?.ToString() ?? ""));
                        else if (pick != null) rows.Add(StatRowVm.Pick(s, rid, label, pick, Whole, c => field.Value = (double)int.Parse(c), note));
                        else if (f.Type == FieldType.Number) rows.Add(StatRowVm.Number(s, rid, label, min, max, step, () => (double)field.Value, n => field.Value = Math.Round(n ?? 0, 3), note));
                        else rows.Add(StatRowVm.Check(s, rid, label, () => (bool)field.Value, b => field.Value = b, note));
                    }
                    v.Groups.Add(new StatGroupVm(branch.Key switch { "dog" => "D-Dog", "horse" => "D-Horse", _ => "Quiet" }, rows));
                }
                return v;
            }
            case ExistingItem { ItemType: ItemKind.Weapon, StatGroups.Count: > 0 } x:
            {
                // An existing weapon: the values its gun and part calls set, read from the script and written back by splice.
                var v = new StatsVm { TabTitle = "Stats", Sight = x.Support ? null : () => x.SightText(s.VanillaEquip), SightTip = "A vanilla sight id reads the game's table. A custom sight id reads the SetSight call of the mod.", Intro = x.Support
                    ? "The thrown item's stats, Sortie Prep details, attack and blast, as the mod's script sets them. What it is thrown as can be changed. A value the script sets by another name (a damage row) or a hidden bar is shown only."
                    : "The weapon's gun and parts, as the mod's script sets them. A part that several weapons use is changed for each of them. A value the script sets by a name (a part, a damage row, an ammo kind) is shown only.", _session = s };
                foreach (var group in x.StatGroups)
                {
                    string part = group.Split(" · ")[0];
                    var known = PartKinds.TryGetValue(part, out var kind) ? WeaponPartFields.For(kind) : [];
                    var rows = new List<StatRowVm>();
                    if (x.Support && group == "Thrown item" && x.Script != null && x.ThrowAsField is { Locked: null })
                        rows.Add(PresetRow(s, v, x.Id + ":preset", () => ThrownPresets.Shown(x, s.Game), p => x.Preset = p,
                            name => ThrownPresets.Confirm(x.Script, x.ThrowAs, x.ScriptLock != null, name), p => ThrownPresets.Apply(x, s.Game, p)));
                    foreach (var (_, label, value) in x.PartNames.Where(n => n.Group == group))
                        rows.Add(StatRowVm.Info(s, $"{x.Id}:{group}:{label}", label, () => value));
                    foreach (var f in x.Fields.Where(f => f.Group == group))
                    {
                        var field = f; string rid = $"{x.Id}:{f.Group}:{f.Label}";
                        string note = known.FirstOrDefault(k => k.Label == f.Label)?.Note ?? (f.Present ? "" : "Not set in the script.");
                        if (f.EquipName && f.Locked == null) rows.Add(ThrowAsRow(s, v, rid, () => x.ThrowAs ?? "", x.SetThrowAs));
                        // #111: the damage type icon is the same pick list as on a thrown item the tool makes; -1 is "not set" (ExistingModWeapons.NotSet).
                        else if (x.Support && f.Path is ["damageType"] && f.Locked == null && f.Type == FieldType.Number)
                            rows.Add(StatRowVm.PickNumber(s, rid, f.Label, [.. f.Present ? [] : new[] { new Choice("-1", "Not set in the script") }, .. DamageTypes.Skip(1)],
                                () => (double)field.Value, n => field.Value = Math.Round(n, 3), 255, f.Present ? "" : "The build writes it only when you pick a value."));
                        else if (f.Locked != null || f.Type == FieldType.Text) rows.Add(StatRowVm.Info(s, rid, f.Label, () => field.Value?.ToString() ?? ""));
                        else if (f.Type == FieldType.Number) rows.Add(StatRowVm.Number(s, rid, f.Label, -100000, 1000000, 1, () => (double)field.Value, n => { field.Value = Math.Round(n ?? 0, 3); v.RefreshSight(); }, note));
                        else rows.Add(StatRowVm.Check(s, rid, f.Label, () => (bool)field.Value, b => { field.Value = b; v.RefreshSight(); }, note));
                    }
                    if (rows.Count > 0) v.Groups.Add(new StatGroupVm(group, rows) { Open = part is "Gun" or "Receiver" or "Magazine" or "Thrown item" });
                }
                return v;
            }
            case WeaponItem w when w.Spec.Tuning is { } tuning:
            {
                var sp = w.Spec; var id = w.Id;
                var v = new StatsVm { TabTitle = "Stats", Sight = () => WeaponSight.Describe(sp.Parts.GetValueOrDefault("scope1Id"), sp.Parts.GetValueOrDefault("scope2Id"), s.VanillaEquip), SightTip = $"The port starts with the sight of the weapon that it copies ({tuning.Weapon}). Under Parts, pick another sight that the game allows on this gun.", Intro = "The weapon's parts and their values, as the game has them for the weapon it copies (or as the mod changed them). A changed part becomes the copy's own; the vanilla weapon and every other weapon stay as they are. Under Parts, the sight can be swapped for another sight that the game allows on this gun; a sight keeps the game's values. The weapon type is the type of the weapon it copies: the game takes the hold, aim, reload and carry spot from it, so it cannot change." };
                string[] titles = ["Receiver", "Barrel", "Magazine", "Bullet", "Damage row", "Stock", "Muzzle and suppressor", "Sight (values as the game has them)", "Flashlight or laser", "Under-barrel"];
                WeaponPartKind[] order = [WeaponPartKind.Receiver, WeaponPartKind.Barrel, WeaponPartKind.Magazine, WeaponPartKind.Bullet, WeaponPartKind.Damage, WeaponPartKind.Stock, WeaponPartKind.MuzzleOption, WeaponPartKind.Sight, WeaponPartKind.Option, WeaponPartKind.UnderBarrel];
                string type = sp.EquipType switch { "Submachinegun" => "Submachine gun", "Assault" => "Assault rifle", "Machinegun" => "Machine gun", "Sniper" => "Sniper rifle", "Missile" => "Missile launcher", "GrenadeLauncher" => "Grenade launcher", var t => t };
                v.Groups.Add(new StatGroupVm("Type", [StatRowVm.Info(s, id + ":w:type", "Weapon type", () => type)]));
                // shortcut: checked once when the tab is made; a later part swap does not show or hide the card.
                if (s.Game is { } cg && Svfm.Core.Conversion.WeaponConverter.CanTakeCamo(cg, w)) { v.AddCamo(s, id + ":w:camo", () => tuning.Camo, c => tuning.Camo = c); v.CamoNote = string.Join("\n", w.Notes.Where(n => n.StartsWith("Camo: ", StringComparison.Ordinal)).Select(n => n[6..])); }

                // The parts the game allows on this gun (WeaponPartsCombinationSettings.lua), per swappable slot.
                var allowed = s.Game is { } game ? WeaponPartChoices.For(game.TryRead, tuning.GunBasic.GetValueOrDefault("receiverId") ?? "") : [];
                string PartName(string part)
                {
                    if (part.StartsWith("ST_", StringComparison.Ordinal)) return WeaponSight.Name(part, s.VanillaEquip);
                    if (part.EndsWith("_None", StringComparison.Ordinal)) return "none";
                    string model = GameKnowledge.ChimeraParts.TryGetValue(part, out var cp) ? cp.Parts : "";
                    return model.Contains("/flt/", StringComparison.Ordinal) ? $"flashlight ({part})" : model.Contains("/lsr/", StringComparison.Ordinal) ? $"laser sight ({part})" : part;
                }
                var slotRows = new List<StatRowVm>();
                foreach (var slot in WeaponTuning.SwapSlots)
                {
                    if (!allowed.TryGetValue(slot, out var parts)) continue;
                    string sl = slot, none = WeaponPartChoices.None[slot];
                    string Now() => tuning.GunBasic.GetValueOrDefault(sl) ?? none;
                    var ids = new List<string> { Now() };
                    if (sl != "scope1Id") ids.Add(none);
                    var list = ids.Concat(parts).Distinct(StringComparer.Ordinal).Select(p => new Choice(p, PartName(p))).ToList();
                    string label = sl == "scope1Id" ? "Sight" : "Second sight";
                    slotRows.Add(StatRowVm.Pick(s, $"{id}:w:slot:{sl}", label, list, Now, x => { tuning.SetSlot(sl, x); v.Reload(); }));
                }
                if (slotRows.Count > 0) v.Groups.Add(new StatGroupVm("Parts", slotRows) { Note = "Only the sights that the game allows on this gun. The 3D preview and the build merge the model of the new sight into the gun model." });

                for (int gi = 0; gi < order.Length; gi++)
                {
                    var kind = order[gi];
                    // A swappable kind shows its group even with an empty slot, so the rows follow a swap.
                    bool swappable = kind switch
                    {
                        WeaponPartKind.Sight => allowed.ContainsKey("scope1Id"),
                        _ => false,
                    };
                    var st0 = tuning.Parts.GetValueOrDefault(kind);
                    if (st0 == null && !swappable) continue;
                    LuaValue? Get(string path) => tuning.Parts.GetValueOrDefault(kind)?.Values.Values.GetValueOrDefault(path);
                    var rows = new List<StatRowVm>();
                    foreach (var f in WeaponPartFields.For(kind))
                    {
                        if (!swappable && !st0!.Values.Values.ContainsKey(f.Path)) continue;
                        var field = f; string rid = $"{id}:w:{kind}:{field.Path}";
                        void Put(LuaValue nv) => tuning.SetValue(kind, field.Path, nv);
                        string note = field.Note ?? "";
                        if (kind == WeaponPartKind.Sight || field.Type is PartFieldType.Constant or PartFieldType.Text) rows.Add(StatRowVm.Info(s, rid, field.Label, () => Get(field.Path)?.ToString() ?? ""));
                        else if (field.Type == PartFieldType.Number) rows.Add(StatRowVm.Number(s, rid, field.Label, -100000, 1000000, 1, () => Get(field.Path)?.AsNumber, x => Put(new LuaValue.Number(x ?? 0)), note));
                        else
                            rows.Add(StatRowVm.Check(s, rid, field.Label, () => Get(field.Path) is { } c && (c.AsNumber is > 0 || c.ToString() == "true"), x => Put(Get(field.Path) is LuaValue.Name ? new LuaValue.Name(x ? "true" : "false") : new LuaValue.Number(x ? 1 : 0)), note));
                    }
                    if (rows.Count > 0) v.Groups.Add(new StatGroupVm(titles[gi], rows) { Open = kind is WeaponPartKind.Receiver or WeaponPartKind.Magazine, Note = swappable ? "Empty when the slot has no part." : "" });
                }
                return v;
            }
            case HeadOptionItem h:
            {
                var sp = h.Spec; var id = h.Id;
                var v = new StatsVm { TabTitle = "Abilities", Intro = "What wearing the head changes." };
                v.Groups.Add(new StatGroupVm("Abilities", [StatRowVm.Check(s, id + ":ammo", "Infinite ammo", () => sp.InfiniteAmmo, x => sp.InfiniteAmmo = x)]));
                return v;
            }
            case BuddyOutfitItem b:
            {
                var sp = b.Spec; var id = b.Id;
                if (sp.Buddy == "quiet")
                {
                    var q = new StatsVm { TabTitle = "How the costume plays", Intro = "Quiet's costumes only change her look." };
                    q.Groups.Add(new StatGroupVm("Look", [StatRowVm.Check(s, id + ":bruise", "Show the bruises of NAKED", () => sp.Bruise, x => sp.Bruise = x)]));
                    return q;
                }
                if (sp.Buddy == "horse")
                {
                    var v = new StatsVm { TabTitle = "How the horse plays", Intro = "Left at Default, the tack plays like NORMAL." };
                    var tacks = new List<Choice> { Default }; for (int i = 0; i < HorseTackBehaviour.TackNames.Length; i++) tacks.Add(new Choice(i.ToString(), HorseTackBehaviour.TackNames[i]));
                    var rams = new[] { "" }.Concat(GameKnowledge.AttackIds.Keys.Order(StringComparer.Ordinal)).Select(a => new Choice(a, a == "" ? "Default" : a)).ToList();
                    HorseTackBehaviour H() => sp.Horse ?? new HorseTackBehaviour();
                    v.Groups.Add(new StatGroupVm("Horse",
                    [
                        StatRowVm.Number(s, id + ":hd", "Share of damage taken (1 = full)", 0, 1.275, 0.05, () => H().DamageRate, x => sp.Horse = H() with { DamageRate = x }, "At most 1.275."),
                        StatRowVm.Number(s, id + ":hg", "Gallop speed ×", 0.1, 3, 0.05, () => H().GallopSpeedRate, x => sp.Horse = H() with { GallopSpeedRate = x }),
                        StatRowVm.Pick(s, id + ":hb", "Ram attack", rams, () => H().BodyAttack ?? "", x => sp.Horse = H() with { BodyAttack = x == "" ? null : x }),
                        StatRowVm.Pick(s, id + ":hs", "Movement sounds of", tacks, () => H().SoundType?.ToString() ?? "", x => sp.Horse = H() with { SoundType = x == "" ? null : int.Parse(x) }),
                    ]));
                    return v;
                }
                else
                {
                    var v = new StatsVm { TabTitle = "How the gear plays", Intro = "Left at Default, the gear plays like NAKED." };
                    DogGearBehaviour D() => sp.Dog ?? new DogGearBehaviour();
                    var cmds = new List<Choice> { Default }; cmds.AddRange(DogGearBehaviour.CommandTypes.Select(c => new Choice(c, c)));
                    v.Groups.Add(new StatGroupVm("Orders and damage",
                    [
                        StatRowVm.Pick(s, id + ":dc", "Order he carries out", cmds, () => D().CommandType ?? "", x => sp.Dog = D() with { CommandType = x == "" ? null : x }),
                        StatRowVm.Number(s, id + ":dd", "Share of damage taken", 0, 1, 0.05, () => D().DamageRate, x => sp.Dog = D() with { DamageRate = x }),
                        StatRowVm.Pick(s, id + ":df", "Flinches from bullets", YesNo, () => Tri(D().BulletFlinch), x => sp.Dog = D() with { BulletFlinch = FromTri(x) }),
                        StatRowVm.Pick(s, id + ":de", "Armour hit effect", YesNo, () => Tri(D().ArmorHitEffect), x => sp.Dog = D() with { ArmorHitEffect = FromTri(x) }),
                        StatRowVm.Pick(s, id + ":dh", "Armour hit sound", YesNo, () => Tri(D().ArmorHitSound), x => sp.Dog = D() with { ArmorHitSound = FromTri(x) }),
                        StatRowVm.Pick(s, id + ":dt", "Soldiers see him as a threat", YesNo, () => Tri(D().SoldierThreat), x => sp.Dog = D() with { SoldierThreat = FromTri(x) }),
                    ]));
                    v.Groups.Add(new StatGroupVm("Distances (metres)",
                    [
                        StatRowVm.Number(s, id + ":dx", "Escape distance", 0, 100, 1, () => D().EscapeDistance, x => sp.Dog = D() with { EscapeDistance = x }),
                        StatRowVm.Number(s, id + ":dr", "Retreat distance when hit", 0, 100, 1, () => D().HitRetreatDistance, x => sp.Dog = D() with { HitRetreatDistance = x }),
                        StatRowVm.Number(s, id + ":dg", "Retarget range", 0, 100, 1, () => D().RetargetRange, x => sp.Dog = D() with { RetargetRange = x }),
                    ]));
                    return v;
                }
            }
        }
        return null;
    }
}
