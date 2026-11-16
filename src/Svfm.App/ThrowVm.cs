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
    public string Note { get; init; } = "";
    public StepParam(string label, Func<double?> g, Action<double?> s, Action before, Action changed) { _before = before; Label = label; IsNumber = true; _gn = g; _sn = s; _changed = changed; }
    public StepParam(string label, Func<bool> g, Action<bool> s, Action before, Action changed) { _before = before; Label = label; IsCheck = true; _gb = g; _sb = s; _changed = changed; }
    public StepParam(string label, Func<string> g, Action<string> s, Action before, Action changed) { _before = before; Label = label; IsText = true; _gt = g; _st = s; _changed = changed; }
    public decimal? NumberValue { get => _gn!() is { } d ? Svfm.Core.Formats.LuaLiteral.ForNumberBox(d) : null; set { double? nv = value is { } v ? (double)v : null; if (_gn!() == nv) return; _before(); _sn!(nv); _changed(); Raise(); } }
    public bool BoolValue { get => _gb!(); set { if (_gb!() == value) return; _before(); _sb!(value); _changed(); Raise(); } }
    public string TextValue { get => _gt!(); set { if (_gt!() == value) return; _before(); _st!(value); _changed(); Raise(); } }
}

/// <summary>One step of the script at one moment.</summary>
public sealed class StepVm : Observable
{
    private readonly EventVm _ev;
    public int Index { get; }
    public StepVm(EventVm ev, int index) { _ev = ev; Index = index; Params = BuildParams(); }
    public ThrowAction Action => _ev.Actions[Index];
    public string Text => $"{Index + 1}.  {Action.Describe()}";
    public ObservableCollection<StepParam> Params { get; }
    public bool HasParams => Params.Count > 0;
    public Command Up => new(() => _ev.Move(Index, -1), () => Index > 0);
    public Command Down => new(() => _ev.Move(Index, 1), () => Index < _ev.Actions.Count - 1);
    public Command Remove => new(() => _ev.RemoveAt(Index));
    public void RaiseText() => Raise(nameof(Text));

    private void Replace(ThrowAction a) => _ev.Actions[Index] = a;

    private ObservableCollection<StepParam> BuildParams()
    {
        var p = new ObservableCollection<StepParam>();
        void Bf() => _ev.Before(Index);
        void Ch() => _ev.Changed(Index);
        StepParam N(string l, Func<double?> g, Action<double?> s, string note = "") => new(l, g, s, Bf, Ch) { Note = note };
        StepParam B(string l, Func<bool> g, Action<bool> s) => new(l, g, s, Bf, Ch);
        switch (Action)
        {
            case SetFuseAction a: p.Add(N("Seconds", () => a.Seconds, x => Replace(a with { Seconds = x ?? 0 }))); break;
            case ChaffAction a:
                p.Add(N("Duration (seconds)", () => a.Duration, x => Replace(a with { Duration = x ?? 0 })));
                p.Add(B("Until another is thrown", () => a.Endless, x => Replace(a with { Endless = x })));
                p.Add(B("No effect (the look only)", () => a.NoEffect, x => Replace(a with { NoEffect = x })));
                break;
            case StunAction a:
                p.Add(N("Radius (metres)", () => a.Radius, x => Replace(a with { Radius = x ?? 0 }), "0: the game's own."));
                p.Add(B("Use the item's attack", () => a.AttackId == ThrowAction.Item, x => Replace(a with { AttackId = x ? ThrowAction.Item : null })));
                break;
            case CloudAction a:
                p.Add(N("Duration (seconds)", () => a.Duration, x => Replace(a with { Duration = x ?? 0 })));
                p.Add(N("Radius (metres)", () => a.Radius, x => Replace(a with { Radius = x ?? 0 })));
                p.Add(B("Sleeping gas", () => a.SleepGas, x => Replace(a with { SleepGas = x })));
                break;
            case FireAction a:
                p.Add(N("Duration (seconds)", () => a.Duration ?? 0, x => Replace(a with { Duration = x is > 0 ? x : null }), "0: the game's own (12 s)."));
                p.Add(N("Radius (metres)", () => a.Radius ?? 0, x => Replace(a with { Radius = x is > 0 ? x : null }), "0: the game's own (8.8 m)."));
                p.Add(B("Use the item's attack", () => a.AttackId == ThrowAction.Item, x => Replace(a with { AttackId = x ? ThrowAction.Item : null })));
                break;
            case ExplosionAction a:
                p.Add(N("Radius (metres)", () => a.Radius ?? 0, x => Replace(a with { Radius = x is > 0 ? x : null }), "0: the blast's own."));
                p.Add(B("Shockwave", () => a.Shockwave, x => Replace(a with { Shockwave = x })));
                break;
            case NoiseAction a: p.Add(N("Noise kind (0 to 127)", () => a.Type, x => Replace(a with { Type = (int)(x ?? 0) }), "0x0D grenade, 0x1A small item, 0x1C smoke grenade.")); break;
            case AnnounceAction a: p.Add(new StepParam("Text", () => a.Text, x => Replace(a with { Text = x }), Bf, Ch)); break;
            case CustomLuaAction a:
                p.Add(new StepParam("Lua", () => a.Code, x => Replace(a with { Code = x }), Bf, Ch) { IsCode = true, Note = "Can use handle, equipId and owner." });
                p.Add(B("Uses the item's position (x, y, z)", () => a.UsesPosition, x => Replace(a with { UsesPosition = x })));
                break;
        }
        return p;
    }
}

/// <summary>A kind of step the user can add.</summary>
public sealed record StepKind(string Label, Func<ThrowAction> Make)
{
    public override string ToString() => Label;
}

/// <summary>The steps run at one moment of a throw.</summary>
public sealed class EventVm : Observable
{
    private readonly ScriptVm _s;
    public ThrowEvent Event { get; }
    public EventVm(ScriptVm s, ThrowEvent e) { _s = s; Event = e; Kinds = s.Kinds; }
    public string Label => char.ToUpperInvariant(ThrowEvents.Label(Event)[0]) + ThrowEvents.Label(Event)[1..];
    public string Message => ThrowEvents.Message(Event);
    public List<ThrowAction> Actions => _s.Script[Event];
    public ObservableCollection<StepVm> Steps { get; } = [];
    public bool Empty => Steps.Count == 0;
    public IReadOnlyList<StepKind> Kinds { get; }
    private StepKind? _kind;
    public StepKind? SelectedKind { get => _kind; set { Set(ref _kind, value); AddCommand.Raise(); } }
    public Command AddCommand => _add ??= new(() => { if (_kind != null) { _s.Edit($"{Event}:add"); Actions.Add(_kind.Make()); _s.Done(); } }, () => _kind != null);
    private Command? _add;

    public void Rebuild() { Steps.Clear(); for (int i = 0; i < Actions.Count; i++) Steps.Add(new StepVm(this, i)); Raise(nameof(Empty)); }
    public void Before(int index) => _s.Edit($"{Event}:{index}");
    public void Changed(int index) { _s.Done(false); if (index < Steps.Count) Steps[index].RaiseText(); }
    public void Move(int i, int d) { _s.Edit(""); (Actions[i], Actions[i + d]) = (Actions[i + d], Actions[i]); _s.Done(); }
    public void RemoveAt(int i) { _s.Edit(""); Actions.RemoveAt(i); _s.Done(); }
}

/// <summary>"What it does": the steps a thrown item runs at each moment (V Framework runs no effect on a custom item by itself).</summary>
public sealed class ScriptVm : Observable
{
    private readonly Session _s;
    private readonly ProjectItem _item;
    private readonly Func<ThrowScript> _script;
    private readonly Func<ThrowContext> _context;
    private readonly string? _lock;
    public ScriptVm(Session s, SupportWeaponItem item) : this(s, item, () => item.Spec.Script, item.Spec.ThrowAs, () => item.Spec.ScriptContext(item.Title), null) { }
    /// <summary>The script of a thrown item of an existing mod (read from its Throwing handlers).</summary>
    public ScriptVm(Session s, ExistingItem item) : this(s, item, () => item.Script!, item.ThrowAs, () => item.ScriptContext!, item.ScriptLock) { }
    private ScriptVm(Session s, ProjectItem item, Func<ThrowScript> script, string? throwAs, Func<ThrowContext> context, string? locked)
    {
        _s = s; _item = item; _script = script; _context = context; _lock = locked;
        var kinds = new List<StepKind>();
        if (throwAs != null && SupportWeaponFamilies.EffectCall(throwAs) != null) kinds.Add(new StepKind($"The {SupportWeaponFamilies.Name(throwAs)} effect", () => new VanillaEffectAction(throwAs)));
        kinds.AddRange([
            new StepKind("It goes off now", () => new GoOffAction()),
            new StepKind("It sticks where it lands", () => new StickAction()),
            new StepKind("Remove the item", () => new RemoveAction()),
            new StepKind("Set the fuse", () => new SetFuseAction(3)),
            new StepKind("Enemies hear it land", () => new ReboundNoiseAction()),
            new StepKind("Landing sound", () => new LandingSoundAction()),
            new StepKind("Smoke trail", () => new SmokeTrailAction()),
            new StepKind("Chaff", () => new ChaffAction(10)),
            new StepKind("Stun burst", () => new StunAction()),
            new StepKind("Smoke or sleeping-gas cloud", () => new CloudAction(10, 5)),
            new StepKind("Fire", () => new FireAction()),
            new StepKind("Explosion with the item's blast", () => new ExplosionAction()),
            new StepKind("Noise", () => new NoiseAction(0x1C)),
            new StepKind("Announce on screen", () => new AnnounceAction("hello")),
            new StepKind("Own Lua", () => new CustomLuaAction("")),
        ]);
        Kinds = kinds;
        foreach (var e in ThrowEvents.All) Events.Add(new EventVm(this, e) { SelectedKind = null });
        Reload();
    }
    public ThrowScript Script => _script();
    public IReadOnlyList<StepKind> Kinds { get; }
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
        if (_lock != null) Problems.Add($"The mod's script can't be changed: {_lock}.");
        foreach (var p in Script.Problems(_context())) Problems.Add(p);
        Raise(nameof(HasProblems)); Raise(nameof(NoProblems));
    }
}
