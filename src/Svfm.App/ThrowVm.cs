using System.Collections.ObjectModel;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>One thing a step lets the user set (a number, a text or a tick).</summary>
public sealed class StepParam : Observable
{
    private readonly Func<double?>? _gn; private readonly Action<double?>? _sn;
    private readonly Func<bool>? _gb; private readonly Action<bool>? _sb;
    private readonly Func<string>? _gt; private readonly Action<string>? _st;
    private readonly Action _changed; private readonly Action _before;
    public string Label { get; }
    public bool IsNumber { get; }
    public bool IsCheck { get; }
    public bool IsText { get; }
    public bool IsCode { get; init; }
    public bool IsLine => IsText && !IsCode;
    /// <summary>Help on the field, shown behind a blue "!" next to the label (#111).</summary>
    public string Note { get; init; } = "";
    public bool HasNote => Note.Length > 0;
    public decimal Maximum { get; init; } = 100000;

    // Issue #111: a number with named values. The last choice, "Type a number…", shows the number box.
    public const string Custom = "";
    public IReadOnlyList<Choice>? Choices { get; init; }
    public bool IsPick => Choices != null;
    private bool _custom;
    public Choice? Picked
    {
        get => Choices == null ? null
            : !_custom && _gn!() is { } d && Choices.FirstOrDefault(c => c.Id != Custom && c.Id == d.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } c ? c
            : Choices[^1];
        set
        {
            if (value == null) return;
            _custom = value.Id == Custom;
            if (!_custom) NumberValue = decimal.Parse(value.Id, System.Globalization.CultureInfo.InvariantCulture);
            Raise(); Raise(nameof(ShowNumber));
        }
    }
    public bool ShowNumber => IsNumber && (Choices == null || Picked?.Id == Custom);
    public StepParam(string label, Func<double?> g, Action<double?> s, Action before, Action changed) { _before = before; Label = label; IsNumber = true; _gn = g; _sn = s; _changed = changed; }
    public StepParam(string label, Func<bool> g, Action<bool> s, Action before, Action changed) { _before = before; Label = label; IsCheck = true; _gb = g; _sb = s; _changed = changed; }
    public StepParam(string label, Func<string> g, Action<string> s, Action before, Action changed) { _before = before; Label = label; IsText = true; _gt = g; _st = s; _changed = changed; }
    public decimal? NumberValue { get => _gn!() is { } d ? Svfm.Core.Formats.LuaLiteral.ForNumberBox(d) : null; set { double? nv = value is { } v ? (double)v : null; if (_gn!() == nv) return; _before(); _sn!(nv); _changed(); Raise(); } }
    public bool BoolValue { get => _gb!(); set { if (_gb!() == value) return; _before(); _sb!(value); _changed(); Raise(); } }
    public string TextValue { get => _gt!(); set { if (_gt!() == value) return; _before(); _st!(value); _changed(); Raise(); RaiseSyntax(); } }

    // Issue #110: "Load from file…" on an Own Lua step copies a .lua file into it once; the syntax check is a warning.
    public IDialogs? Dialogs { get; init; }
    public bool CanLoad => IsCode && Dialogs != null;
    public string SyntaxNote => IsCode ? OwnLua.SyntaxError(_gt!()) ?? "" : "";
    public bool HasSyntaxNote => SyntaxNote.Length > 0;
    private void RaiseSyntax() { Raise(nameof(SyntaxNote)); Raise(nameof(HasSyntaxNote)); }
    private string _loadMessage = "";
    public string LoadMessage { get => _loadMessage; private set { Set(ref _loadMessage, value); Raise(nameof(HasLoadMessage)); } }
    public bool HasLoadMessage => LoadMessage.Length > 0;
    private string? _pending;
    private bool _confirm;
    public bool Confirming { get => _confirm; private set => Set(ref _confirm, value); }
    private string _confirmText = "";
    public string ConfirmText { get => _confirmText; private set => Set(ref _confirmText, value); }
    public Command LoadCommand => _load ??= new(async () =>
    {
        if (await Dialogs!.PickFile("Load Lua into the step", ("Lua files", ["*.lua"])) is not { } file) return;
        var (code, refused) = OwnLua.Load(file);
        LoadMessage = refused ?? "";
        if (code == null) return;
        if (_gt!().Trim().Length == 0) { Apply(code); return; }
        _pending = code; ConfirmText = $"This replaces the Lua of this step with the text of {Path.GetFileName(file)}."; Confirming = true;
    });
    private Command? _load;
    public Command ReplaceCommand => _replace ??= new(() => { if (_pending is { } c) Apply(c); });
    private Command? _replace;
    public Command CancelCommand => _cancel ??= new(() => { _pending = null; Confirming = false; });
    private Command? _cancel;
    private void Apply(string code)
    {
        _pending = null; Confirming = false;
        if (_gt!() == code) return;
        _before(); _st!(code); _changed(); Raise(nameof(TextValue)); RaiseSyntax();
    }
}

/// <summary>One step of the script at one moment.</summary>
public sealed class StepVm : Observable
{
    private readonly EventVm _ev;
    public int Index { get; }
    public StepVm(EventVm ev, int index, int number) { _ev = ev; Index = index; Number = number; Params = BuildParams(); }
    public ThrowAction Action => _ev.Actions[Index];
    /// <summary>The number shown: hidden developer steps are not counted.</summary>
    public int Number { get; }
    public string Text => $"{Number}.  {Action.Describe()}";
    /// <summary>What the step does (#111), behind a blue "!" next to the title.</summary>
    public string Help => ThrowScript.Help(Action);
    public ObservableCollection<StepParam> Params { get; }
    public bool HasParams => Params.Count > 0;
    public Command Up => new(() => _ev.Move(Index, -1), () => _ev.Target(Index, -1) >= 0);
    public Command Down => new(() => _ev.Move(Index, 1), () => _ev.Target(Index, 1) >= 0);
    public Command Remove => new(() => _ev.RemoveAt(Index));
    public void RaiseText() => Raise(nameof(Text));

    private void Replace(ThrowAction a) => _ev.Actions[Index] = a;

    private ObservableCollection<StepParam> BuildParams()
    {
        var p = new ObservableCollection<StepParam>();
        // The getters and setters read the step as it is now: a record captured at build time goes stale after the first change.
        T Cur<T>(T _) where T : ThrowAction => (T)Action;
        void Bf() => _ev.Before(Index);
        void Ch() => _ev.Changed(Index);
        StepParam N(string l, Func<double?> g, Action<double?> s, string note) => new(l, g, s, Bf, Ch) { Note = note };
        StepParam B(string l, Func<bool> g, Action<bool> s, string note) => new(l, g, s, Bf, Ch) { Note = note };
        const string itemAttack = "On: the attack of this item: its own, or the attack of its thrown-as item.";
        switch (Action)
        {
            case SetFuseAction a: p.Add(N("Seconds", () => Cur(a).Seconds, x => Replace(Cur(a) with { Seconds = x ?? 0 }), "The time before the item goes off, from this moment. Vanilla grenades use 3 to 5 seconds.")); break;
            case ChaffAction a:
                p.Add(N("Duration (seconds)", () => Cur(a).Duration, x => Replace(Cur(a) with { Duration = x ?? 0 }), "How long the chaff cloud stays. The vanilla chaff grenade uses 10 seconds."));
                p.Add(B("Until another is thrown", () => Cur(a).Endless, x => Replace(Cur(a) with { Endless = x }), "On: the chaff cloud stays until the player throws the next chaff item. The duration is not used."));
                p.Add(B("No effect (the look only)", () => Cur(a).NoEffect, x => Replace(Cur(a) with { NoEffect = x }), "On: the chaff cloud shows, but it jams nothing."));
                break;
            case StunAction a:
                p.Add(N("Radius (metres)", () => Cur(a).Radius, x => Replace(Cur(a) with { Radius = x ?? 0 }), "Enemies in this distance from the item are stunned. 0: the radius of the vanilla stun grenade."));
                p.Add(B("Use the item's attack", () => Cur(a).AttackId == ThrowAction.Item, x => Replace(Cur(a) with { AttackId = x ? ThrowAction.Item : null }), itemAttack + " Off: the stun grenade's attack."));
                break;
            case CloudAction a:
                p.Add(N("Duration (seconds)", () => Cur(a).Duration, x => Replace(Cur(a) with { Duration = x ?? 0 }), "How long the cloud stays before it fades. The vanilla smoke grenade uses about 30 seconds."));
                p.Add(N("Radius (metres)", () => Cur(a).Radius, x => Replace(Cur(a) with { Radius = x ?? 0 }), "The size of the cloud. The vanilla smoke grenade uses about 8 metres."));
                p.Add(B("Sleeping gas", () => Cur(a).SleepGas, x => Replace(Cur(a) with { SleepGas = x }), "On: the cloud puts enemies to sleep. Off: the cloud is smoke only."));
                break;
            case FireAction a:
                p.Add(N("Duration (seconds)", () => Cur(a).Duration ?? 0, x => Replace(Cur(a) with { Duration = x is > 0 ? x : null }), "How long the fire burns. 0: the game's own (12 seconds)."));
                p.Add(N("Radius (metres)", () => Cur(a).Radius ?? 0, x => Replace(Cur(a) with { Radius = x is > 0 ? x : null }), "The size of the fire. 0: the game's own (8.8 metres)."));
                p.Add(B("Use the item's attack", () => Cur(a).AttackId == ThrowAction.Item, x => Replace(Cur(a) with { AttackId = x ? ThrowAction.Item : null }), itemAttack + " Off: the Molotov cocktail's attack."));
                break;
            case ExplosionAction a:
                p.Add(N("Radius (metres)", () => Cur(a).Radius ?? 0, x => Replace(Cur(a) with { Radius = x is > 0 ? x : null }), "The distance that the explosion reaches. 0: the radius of the item's blast."));
                p.Add(B("Shockwave", () => Cur(a).Shockwave, x => Replace(Cur(a) with { Shockwave = x }), "On: the explosion pushes people and objects away."));
                break;
            case NoiseAction a:
                p.Add(new StepParam("Noise", () => Cur(a).Type, x => Replace(Cur(a) with { Type = (int)(x ?? 0) }), Bf, Ch)
                {
                    Note = "What guards think they heard. A threat noise makes them more alert than a small item noise.", Maximum = 127,
                    Choices = [.. NoiseAction.Kinds.Select(k => new Choice(k.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), k.Label)), new Choice(StepParam.Custom, "Type a number…")],
                });
                break;
            case AnnounceAction a: p.Add(new StepParam("Text", () => Cur(a).Text, x => Replace(Cur(a) with { Text = x }), Bf, Ch) { Note = "The line that shows in the log." }); break;
            case CustomLuaAction a:
                p.Add(new StepParam("Lua", () => Cur(a).Code, x => Replace(Cur(a) with { Code = x }), Bf, Ch) { IsCode = true, Dialogs = _ev.Dialogs, Note = "Can use handle, equipId and owner. With 'Uses the item's position' ticked, also x, y and z." });
                p.Add(B("Uses the item's position (x, y, z)", () => Cur(a).UsesPosition, x => Replace(Cur(a) with { UsesPosition = x }), "On: the step gets the position of the item as x, y and z."));
                break;
        }
        return p;
    }
}

/// <summary>A kind of step the user can add.</summary>
public sealed record StepKind(string Label, Func<ThrowAction> Make, bool DeveloperOnly = false, string Description = "")
{
    public StepKind(ThrowStepKind k) : this(k.Name, k.New, k.DeveloperOnly, k.Description) { }
    public override string ToString() => Label;
}

/// <summary>The steps run at one moment of a throw.</summary>
public sealed class EventVm : Observable
{
    private readonly ScriptVm _s;
    public ThrowEvent Event { get; }
    public EventVm(ScriptVm s, ThrowEvent e) { _s = s; Event = e; }
    public string Label => char.ToUpperInvariant(ThrowEvents.Label(Event)[0]) + ThrowEvents.Label(Event)[1..];
    public string Message => ThrowEvents.Message(Event);
    public List<ThrowAction> Actions => _s.Script[Event];
    public ObservableCollection<StepVm> Steps { get; } = [];
    public bool Empty => Steps.Count == 0;
    public IReadOnlyList<StepKind> Kinds => _s.Kinds;
    public IDialogs? Dialogs => _s.Dialogs;
    private StepKind? _kind;
    public StepKind? SelectedKind { get => _kind; set { Set(ref _kind, value); AddCommand.Raise(); } }
    public Command AddCommand => _add ??= new(() => { if (_kind != null) { _s.Edit($"{Event}:add"); Actions.Add(_kind.Make()); _s.Done(); } }, () => _kind != null);
    private Command? _add;

    /// <summary>Rebuilds the rows. Outside developer mode the Announce steps stay in the script (they still build) but are not shown.</summary>
    public void Rebuild()
    {
        Steps.Clear();
        for (int i = 0; i < Actions.Count; i++)
            if (Shown(i)) Steps.Add(new StepVm(this, i, Steps.Count + 1));
        if (_kind is { DeveloperOnly: true } && !_s.DeveloperMode) SelectedKind = null;
        Raise(nameof(Empty)); Raise(nameof(Kinds));
    }
    public void Before(int index) => _s.Edit($"{Event}:{index}");
    public void Changed(int index) { _s.Done(false); Steps.FirstOrDefault(s => s.Index == index)?.RaiseText(); }
    /// <summary>The index of the next shown step from <paramref name="i"/> in direction <paramref name="d"/>, or -1. Hidden steps are skipped (#97).</summary>
    public int Target(int i, int d)
    {
        int j = i + d;
        while (j >= 0 && j < Actions.Count && !Shown(j)) j += d;
        return j >= 0 && j < Actions.Count ? j : -1;
    }
    private bool Shown(int i) => _s.DeveloperMode || Actions[i] is not AnnounceAction;
    public void Move(int i, int d) { int j = Target(i, d); if (j < 0) return; _s.Edit(""); (Actions[i], Actions[j]) = (Actions[j], Actions[i]); _s.Done(); }
    public void RemoveAt(int i) { _s.Edit(""); Actions.RemoveAt(i); _s.Done(); }
}

/// <summary>"What it does": the steps a thrown item runs at each moment (V Framework runs no effect on a custom item by itself).</summary>
public sealed class ScriptVm : Observable
{
    private readonly Session _s;
    private readonly ProjectItem _item;
    private readonly Func<ThrowScript> _script;
    private readonly Func<ThrowContext> _context;
    private readonly Func<string?> _lock;
    public ScriptVm(Session s, SupportWeaponItem item) : this(s, item, () => item.Spec.Script, () => item.Spec.ThrowAs, () => item.Spec.ScriptContext(item.Title), () => null) { }
    /// <summary>The script of a thrown item of an existing mod (read from its Throwing handlers).</summary>
    public ScriptVm(Session s, ExistingItem item) : this(s, item, () => item.Script!, () => item.ThrowAs, () => item.ScriptContext!, () => item.ScriptLock) { }
    private ScriptVm(Session s, ProjectItem item, Func<ThrowScript> script, Func<string?> throwAs, Func<ThrowContext> context, Func<string?> locked)
    {
        _s = s; _item = item; _script = script; _throwAs = throwAs; _context = context; _lock = locked;
        _kinds = [.. ThrowScript.Steps.Select(k => new StepKind(k))];
        foreach (var e in ThrowEvents.All) Events.Add(new EventVm(this, e) { SelectedKind = null });
        Reload();
    }
    public ThrowScript Script => _script();
    public IDialogs? Dialogs => _s.Dialogs;
    private readonly List<StepKind> _kinds;
    private readonly Func<string?> _throwAs;
    /// <summary>The effect step of what the item is thrown as now (it follows a change of "Thrown as").</summary>
    private IEnumerable<StepKind> Effect => _throwAs() is { } t && SupportWeaponFamilies.EffectCall(t) != null
        ? [new StepKind(ThrowScript.EffectStep(t))] : [];
    /// <summary>Developer mode (Settings, switched on the Developer screen) shows the developer-only steps.</summary>
    public bool DeveloperMode => _s.Settings.DeveloperMode;
    public IReadOnlyList<StepKind> Kinds => Effect.Concat(DeveloperMode ? _kinds : _kinds.Where(k => !k.DeveloperOnly)).ToList();
    public ObservableCollection<EventVm> Events { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];
    public bool HasProblems => Problems.Count > 0;
    public bool NoProblems => Problems.Count == 0;

    public void Edit(string field) => _s.Checkpoint($"{_item.Id}:script:{field}");
    /// <summary>After a change: tell the session, and rebuild the rows unless only a value changed.</summary>
    public void Done(bool rebuild = true)
    {
        _s.NotifyChanged();
        if (rebuild) Reload(); else ReloadProblems();
    }
    public void Reload() { foreach (var e in Events) e.Rebuild(); ReloadProblems(); }
    private void ReloadProblems()
    {
        Problems.Clear();
        if (_lock() is { } why) Problems.Add($"The mod's script can't be changed: {why}.");
        foreach (var p in Script.Problems(_context())) Problems.Add(p);
        if (_throwAs() is { } t && Script.OtherEffects(t).ToList() is { Count: > 0 } other)
            Problems.Add($"The script runs the {string.Join(", ", other)} effect, but the item is thrown as {SupportWeaponFamilies.Name(t)}. Change the step if the effect must match.");
        Raise(nameof(HasProblems)); Raise(nameof(NoProblems));
    }
}
