using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Svfm.Core.Formats;

namespace Svfm.Core.VFramework;

/// <summary>
/// The moments of a thrown item V Framework 1.7 sends to Lua (Lua_API "Throwing messages", all with
/// <c>handle, equipId, owner</c>), in the order they happen.
/// </summary>
public enum ThrowEvent { Thrown, Bounces, Stops, GoesOff, Removed }

public static class ThrowEvents
{
    public static readonly IReadOnlyList<ThrowEvent> All = [ThrowEvent.Thrown, ThrowEvent.Bounces, ThrowEvent.Stops, ThrowEvent.GoesOff, ThrowEvent.Removed];

    public static string Message(ThrowEvent e) => e switch
    {
        ThrowEvent.Thrown => "OnThrow", ThrowEvent.Bounces => "OnBounceThrowing", ThrowEvent.Stops => "OnStopThrowing",
        ThrowEvent.GoesOff => "OnActivateThrowing", _ => "OnRemoveThrowing",
    };

    public static string Label(ThrowEvent e) => e switch
    {
        ThrowEvent.Thrown => "when it is thrown", ThrowEvent.Bounces => "when it hits a surface", ThrowEvent.Stops => "when it comes to rest",
        ThrowEvent.GoesOff => "when it goes off", _ => "when it is gone",
    };
}

/// <summary>What a script needs from its item: the equip constant it answers to, the item's name (announcements), and
/// the item's attack and blast (its own or set ones; null when it has none, i.e. it uses its throwAs item's).</summary>
public sealed record ThrowContext(string EquipConstant, string Title, string? ItemAttack, string? ItemBlast);

/// <summary>
/// One step a script runs on a thrown item, with the V Framework 1.7 call it writes (Lua_API "Throwing"). Range
/// effects happen where the item is (<c>V_ThrowingSystem.GetPosition</c>) and belong to its thrower (<c>owner</c>).
/// Attack and blast fields take a <c>TppDamage.ATK_*</c> / <c>TppEquip.BLA_*</c> constant, or <see cref="Item"/> for
/// the item's own (<see cref="ThrowContext"/>).
/// </summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(VanillaEffectAction), "vanillaeffect")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(GoOffAction), "gooff")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(StickAction), "stick")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RemoveAction), "remove")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SetFuseAction), "setfuse")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ReboundNoiseAction), "reboundnoise")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(LandingSoundAction), "landingsound")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SmokeTrailAction), "smoketrail")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ChaffAction), "chaff")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(StunAction), "stun")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(CloudAction), "cloud")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(FireAction), "fire")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ExplosionAction), "explosion")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(NoiseAction), "noise")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(AnnounceAction), "announce")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(CustomLuaAction), "customlua")]
public abstract record ThrowAction
{
    /// <summary>"The item's attack / blast" in an attack or blast field.</summary>
    public const string Item = "item";

    /// <summary>Short text for the editor and the module's header.</summary>
    public abstract string Describe();
    /// <summary>The Lua lines, not indented.</summary>
    public abstract IEnumerable<string> Lines(ThrowContext c);
    /// <summary>The V Framework table the lines call (the handler only runs when it is there).</summary>
    public virtual string? Library => "V_ThrowingImpl";
    public virtual bool NeedsPosition => false;
    /// <summary>Why the step can't be written, or null.</summary>
    public virtual string? Problem(ThrowContext c) => null;

    protected static string N(double v) => LuaLiteral.FormatNumber(v);
    protected static string Pos => "pos = { x, y, z }";
    protected static string? Resolve(string? v, string? item) => v == Item ? item : v;
}

/// <summary>The vanilla effect of a kind of thrown item, run on this one (V_ThrowingImpl.UpdateAction*).</summary>
public sealed record VanillaEffectAction(string Swp) : ThrowAction
{
    public override string Describe() => $"{SupportWeaponFamilies.Name(Swp)} effect";
    public override IEnumerable<string> Lines(ThrowContext c) => [SupportWeaponFamilies.EffectCall(Swp)?.call ?? throw new InvalidOperationException(Problem(c))];
    public override string? Problem(ThrowContext c) => SupportWeaponFamilies.EffectCall(Swp) == null
        ? $"V Framework 1.7 has no call that runs {SupportWeaponFamilies.Family(Swp)}'s effect on a custom item." : null;
}

/// <summary>The item goes off now (its fuse is cut short): V_ThrowingImpl.Activate. An impact fuse when it hits a surface.</summary>
public sealed record GoOffAction : ThrowAction
{
    public override string Describe() => "it goes off";
    public override IEnumerable<string> Lines(ThrowContext c) => ["V_ThrowingImpl.Activate(handle)"];
}

/// <summary>The item stops where it is, its fuse still running: V_ThrowingImpl.Stop. Sticky when it hits a surface.</summary>
public sealed record StickAction : ThrowAction
{
    public override string Describe() => "it sticks";
    public override IEnumerable<string> Lines(ThrowContext c) => ["V_ThrowingImpl.Stop(handle)"];
}

/// <summary>The item is removed (used up): V_ThrowingImpl.Deactivate.</summary>
public sealed record RemoveAction : ThrowAction
{
    public override string Describe() => "it is removed";
    public override IEnumerable<string> Lines(ThrowContext c) => ["V_ThrowingImpl.Deactivate(handle)"];
}

/// <summary>Sets the time the item has left (its fuse): V_ThrowingImpl.SetLifeLeft.</summary>
public sealed record SetFuseAction(double Seconds) : ThrowAction
{
    public override string Describe() => $"fuse set to {N(Seconds)} s";
    public override IEnumerable<string> Lines(ThrowContext c) => [$"V_ThrowingImpl.SetLifeLeft(handle, {N(Seconds)})"];
}

/// <summary>Enemies hear the item land (V_ThrowingImpl.AddReboundNoise).</summary>
public sealed record ReboundNoiseAction : ThrowAction
{
    public override string Describe() => "enemies hear it land";
    public override IEnumerable<string> Lines(ThrowContext c) => ["V_ThrowingImpl.AddReboundNoise(handle)"];
}

/// <summary>Plays the item's landing sound (V_ThrowingImpl.PostReboundSound).</summary>
public sealed record LandingSoundAction : ThrowAction
{
    public override string Describe() => "landing sound";
    public override IEnumerable<string> Lines(ThrowContext c) => ["V_ThrowingImpl.PostReboundSound(handle)"];
}

/// <summary>The item gives off smoke (V_ThrowingSystem.RequestSmoke).</summary>
public sealed record SmokeTrailAction : ThrowAction
{
    public override string Describe() => "it gives off smoke";
    public override string? Library => "V_ThrowingSystem";
    public override IEnumerable<string> Lines(ThrowContext c) => ["V_ThrowingSystem.RequestSmoke(handle)"];
}

/// <summary>Chaff where the item is: jams cameras, UAVs and Sahelanthropus, cuts CP radio and lock-on
/// (V_RangeAttackSystem.RequestToChaff; one at a time, a new one replaces the old).</summary>
public sealed record ChaffAction(double Duration, bool Endless = false, bool NoEffect = false) : ThrowAction
{
    public override string Describe() => Endless ? "chaff until replaced" : $"chaff for {N(Duration)} s";
    public override string? Library => "V_RangeAttackSystem";
    public override bool NeedsPosition => true;
    public override IEnumerable<string> Lines(ThrowContext c)
    {
        var f = new List<string> { Pos };
        if (Endless) f.Add("endless = true"); else f.Add($"duration = {N(Duration)}");
        if (NoEffect) f.Add("noEffect = true");
        f.Add("owner = owner");
        return [$"V_RangeAttackSystem.RequestToChaff{{ {string.Join(", ", f)} }}"];
    }
    public override string? Problem(ThrowContext c) => !Endless && Duration <= 0 ? "Chaff needs a duration above 0 (or endless)." : null;
}

/// <summary>A stun grenade burst where the item is (V_RangeAttackSystem.RequestToStun). Radius 0 and no attack: the
/// framework's defaults (ATK_StunGrenade).</summary>
public sealed record StunAction(double Radius = 0, string? AttackId = null, int? Grade = null) : ThrowAction
{
    public override string Describe() => Radius > 0 ? $"stun burst, {N(Radius)} m" : "stun burst";
    public override string? Library => "V_RangeAttackSystem";
    public override bool NeedsPosition => true;
    public override IEnumerable<string> Lines(ThrowContext c)
    {
        var f = new List<string> { Pos };
        if (Radius > 0) f.Add($"radius = {N(Radius)}");
        if (Resolve(AttackId, c.ItemAttack) is { } a) f.Add($"attackId = {a}");
        if (Grade is { } g) f.Add($"grade = {g}");
        f.Add("owner = owner");
        return [$"V_RangeAttackSystem.RequestToStun{{ {string.Join(", ", f)} }}"];
    }
    public override string? Problem(ThrowContext c) => AttackId == Item && c.ItemAttack == null ? "The stun burst uses the item's attack, but the item has none set." : null;
}

/// <summary>A smoke or sleeping-gas cloud where the item is (V_RangeAttackSystem.RequestToSmoke; it fades after the duration).</summary>
public sealed record CloudAction(double Duration, double Radius, bool SleepGas = false) : ThrowAction
{
    public override string Describe() => $"{(SleepGas ? "sleeping gas" : "smoke")} cloud, {N(Radius)} m for {N(Duration)} s";
    public override string? Library => "V_RangeAttackSystem";
    public override bool NeedsPosition => true;
    public override IEnumerable<string> Lines(ThrowContext c) =>
        [$"V_RangeAttackSystem.RequestToSmoke{{ {Pos}, duration = {N(Duration)}, radius = {N(Radius)}{(SleepGas ? ", type = \"SleepGas\"" : "")}, owner = owner }}"];
    public override string? Problem(ThrowContext c) => Duration <= 0 || Radius <= 0 ? "A cloud needs a duration and a radius above 0." : null;
}

/// <summary>Molotov fire where the item is (V_RangeAttackSystem.RequestToFlame; unset: 12 s, 8.8 m, ATK_MolotovCocktailFire).</summary>
public sealed record FireAction(double? Duration = null, double? Radius = null, string? AttackId = null, int? Grade = null) : ThrowAction
{
    public override string Describe() => "fire" + (Radius is { } r ? $", {N(r)} m" : "") + (Duration is { } d ? $" for {N(d)} s" : "");
    public override string? Library => "V_RangeAttackSystem";
    public override bool NeedsPosition => true;
    public override IEnumerable<string> Lines(ThrowContext c)
    {
        var f = new List<string> { Pos };
        if (Duration is { } d) f.Add($"duration = {N(d)}");
        if (Radius is { } r) f.Add($"radius = {N(r)}");
        if (Resolve(AttackId, c.ItemAttack) is { } a) f.Add($"attackId = {a}");
        if (Grade is { } g) f.Add($"grade = {g}");
        f.Add("owner = owner");
        return [$"V_RangeAttackSystem.RequestToFlame{{ {string.Join(", ", f)} }}"];
    }
    public override string? Problem(ThrowContext c) => AttackId == Item && c.ItemAttack == null ? "The fire uses the item's attack, but the item has none set." : null;
}

/// <summary>An explosion's damage and push where the item is, without its look or sound (V_BlastSystem.Blast). The
/// blast gives the noise and the radius (unless <see cref="Radius"/> is set).</summary>
public sealed record ExplosionAction(string BlastId = ThrowAction.Item, string AttackId = ThrowAction.Item, double? Radius = null, bool Shockwave = true) : ThrowAction
{
    public override string Describe() => "explosion" + (Radius is { } r ? $", {N(r)} m" : "");
    public override string? Library => "V_BlastSystem";
    public override bool NeedsPosition => true;
    public override IEnumerable<string> Lines(ThrowContext c)
    {
        var f = new List<string> { Pos, $"blastId = {Resolve(BlastId, c.ItemBlast)}", $"attackId = {Resolve(AttackId, c.ItemAttack)}" };
        if (Radius is { } r) f.Add($"radius = {N(r)}");
        if (!Shockwave) f.Add("shockwave = false");
        f.Add("owner = owner");
        return [$"V_BlastSystem.Blast{{ {string.Join(", ", f)} }}"];
    }
    public override string? Problem(ThrowContext c) =>
        Resolve(BlastId, c.ItemBlast) == null ? "The explosion uses the item's blast, but the item has none set (pick a blast)." :
        Resolve(AttackId, c.ItemAttack) == null ? "The explosion uses the item's attack, but the item has none set (pick an attack)." :
        Radius is > 100 ? "An explosion's radius is at most 100 m." : null;
}

/// <summary>A noise guards hear and check, with no actual sound (V_NoiseSystem.AddNoise). The known kinds are in
/// <see cref="Kinds"/>; .docs/Noise_kinds.md gives the source of each.</summary>
public sealed record NoiseAction(int Type) : ThrowAction
{
    /// <summary>The noise kinds that have a known name (#111), for a pick list.</summary>
    public static readonly IReadOnlyList<(int Value, string Label)> Kinds =
    [
        (0x0D, "Grenade bouncing (guards treat it as a threat)"),
        (0x1A, "Small item bouncing"),
        (0x1C, "Smoke grenade going off"),
        (0x0B, "A soldier's hands-up cry"),
        (0x1E, "A soldier collapsing"),
        (0x50, "Special noise of the game"),
    ];
    public override string Describe() => $"noise 0x{Type:X2}";
    public override string? Library => "V_NoiseSystem";
    public override bool NeedsPosition => true;
    public override IEnumerable<string> Lines(ThrowContext c) => [$"V_NoiseSystem.AddNoise{{ {Pos}, type = 0x{Type:X2}, owner = owner }}"];
    public override string? Problem(ThrowContext c) => Type is < 0 or > 127 ? "A noise kind is 0-127." : null;
}

/// <summary>Shows a line in the announce log (testing: shows when each moment happens).</summary>
public sealed record AnnounceAction(string Text) : ThrowAction
{
    public override string Describe() => $"announce \"{Text}\"";
    public override string? Library => null;
    public override IEnumerable<string> Lines(ThrowContext c) => [$"TppUiCommand.AnnounceLogView({LuaLiteral.Quote($"{c.Title}: {Text}")})"];
}

/// <summary>Lua of the user's own. It can use <c>handle</c>, <c>equipId</c>, <c>owner</c>, and <c>x, y, z</c> (the
/// item's position) when <see cref="UsesPosition"/>.</summary>
public sealed record CustomLuaAction(string Code, bool UsesPosition = false) : ThrowAction
{
    public override string Describe() => "own Lua";
    public override string? Library => null;
    public override bool NeedsPosition => UsesPosition;
    public override IEnumerable<string> Lines(ThrowContext c) => Code.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}

/// <summary>
/// A support weapon's script: the steps it runs at each moment of a throw. V Framework 1.7 never runs the throwAs item's
/// effect on a custom item, so this is the whole of what the item does (Custom_Weapons "Support weapons"). Written as
/// one handler per moment in the module's Messages(), answering only to the item's equip ID.
/// </summary>
public sealed class ThrowScript
{
    public Dictionary<ThrowEvent, List<ThrowAction>> On { get; } = [];

    public List<ThrowAction> this[ThrowEvent e] => On.TryGetValue(e, out var l) ? l : On[e] = [];

    public ThrowScript Add(ThrowEvent e, params ThrowAction[] steps) { this[e].AddRange(steps); return this; }

    public bool IsEmpty => On.Values.All(l => l.Count == 0);

    public ThrowScript Clone()
    {
        var c = new ThrowScript();
        foreach (var (e, l) in On) c.On[e] = [.. l];
        return c;
    }

    /// <summary>
    /// The item is now thrown as <paramref name="newSwp"/> (issue #112). A default script (only the effect of
    /// <paramref name="oldSwp"/> when it goes off, used up after it where that item is) becomes the default of the new
    /// item, in the same ID form. Any other script stays as it is. True when the script changed.
    /// </summary>
    public bool SwapDefaultEffect(string oldSwp, string newSwp)
    {
        static string Fam(string s) => SupportWeaponFamilies.Family(s).Replace("EQP_", "");
        var steps = On.SelectMany(kv => kv.Value.Select(a => (e: kv.Key, a))).ToList();
        if (Fam(oldSwp) == Fam(newSwp) || steps.Count == 0 || steps.Any(s => s.e != ThrowEvent.GoesOff)
            || steps[0].a is not VanillaEffectAction v || Fam(v.Swp) != Fam(oldSwp)
            || steps.Count != (SupportWeaponFamilies.EffectCall(oldSwp)?.removeAfter == true ? 2 : 1) || steps.Skip(1).Any(s => s.a is not RemoveAction))
            return false;
        CopyFrom(VanillaEffect(v.Swp.StartsWith("EQP_", StringComparison.Ordinal) ? newSwp : newSwp.Replace("EQP_", "")));
        return true;
    }

    /// <summary>The vanilla effect steps whose item is not <paramref name="throwAs"/>'s kind, by name (for a note).</summary>
    public IEnumerable<string> OtherEffects(string throwAs) => On.Values.SelectMany(l => l).OfType<VanillaEffectAction>()
        .Where(v => SupportWeaponFamilies.Family(v.Swp).Replace("EQP_", "") != SupportWeaponFamilies.Family(throwAs).Replace("EQP_", ""))
        .Select(v => SupportWeaponFamilies.Name(v.Swp)).Distinct();

    /// <summary>Replaces this script's steps with <paramref name="from"/>'s.</summary>
    public void CopyFrom(ThrowScript from)
    {
        On.Clear();
        foreach (var (e, l) in from.On) On[e] = [.. l];
    }

    private sealed record Step(ThrowEvent Event, ThrowAction Action);
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>The script as text (kept in the project file).</summary>
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(On.SelectMany(kv => kv.Value.Select(a => new Step(kv.Key, a))).ToList(), JsonOptions);

    /// <summary>Reads a script written by <see cref="ToJson"/>.</summary>
    public static ThrowScript FromJson(string json)
    {
        var s = new ThrowScript();
        foreach (var st in System.Text.Json.JsonSerializer.Deserialize<List<Step>>(json, JsonOptions) ?? []) s[st.Event].Add(st.Action);
        return s;
    }

    /// <summary>The vanilla effect of <paramref name="swp"/>'s kind when it goes off, used up after it where the vanilla
    /// item is (grenades, stun grenades). Empty when V Framework has no call for that kind.</summary>
    public static ThrowScript VanillaEffect(string swp)
    {
        var s = new ThrowScript();
        if (SupportWeaponFamilies.EffectCall(swp) is { } fx)
        {
            s.Add(ThrowEvent.GoesOff, new VanillaEffectAction(swp));
            if (fx.removeAfter) s.Add(ThrowEvent.GoesOff, new RemoveAction());
        }
        return s;
    }

    /// <summary>The wiki's chaff grenade: chaff where it goes off, a smoke-grenade noise, then removed.</summary>
    public static ThrowScript ChaffGrenade(double duration = 10) =>
        new ThrowScript().Add(ThrowEvent.GoesOff, new ChaffAction(duration), new NoiseAction(0x1C), new RemoveAction());

    /// <summary>A plain grenade's damage without the vanilla effect call: the item's blast and attack, then removed.</summary>
    public static ThrowScript Explosive() =>
        new ThrowScript().Add(ThrowEvent.GoesOff, new ExplosionAction(), new NoiseAction(0x0D), new RemoveAction());

    /// <summary>Goes off when it hits a surface (impact fuse).</summary>
    public ThrowScript WithImpactFuse() => Add(ThrowEvent.Bounces, new GoOffAction());

    /// <summary>Sticks where it first hits, its fuse still running.</summary>
    public ThrowScript WithSticky() => Add(ThrowEvent.Bounces, new StickAction());

    /// <summary>Announce-log lines when it is thrown, goes off and is gone (in-game testing).</summary>
    public ThrowScript WithDiagnostics()
    {
        Add(ThrowEvent.Thrown, new AnnounceAction("thrown"));
        Add(ThrowEvent.GoesOff, new AnnounceAction("went off"));
        Add(ThrowEvent.Removed, new AnnounceAction("gone"));
        return this;
    }

    public List<string> Problems(ThrowContext c) =>
        ThrowEvents.All.SelectMany(e => this[e].Select(a => a.Problem(c) is { } p ? $"{ThrowEvents.Label(e)}: {p}" : null)).OfType<string>().ToList();

    /// <summary>
    /// The Messages() handlers: one per moment with steps. Each moment runs once per throw (<see cref="Helpers"/>): the
    /// same code also runs from the module's per-frame check (<see cref="Poller"/>), because V Framework's Throwing
    /// messages did not reach the module in game (Oct 27: none in IH's message log while the item was thrown).
    /// </summary>
    public IEnumerable<MessageHandler> Handlers(ThrowContext c)
    {
        if (IsEmpty) yield break;
        foreach (var e in ThrowEvents.All)
        {
            // When the item is gone, its moments may run again on the next throw that gets the same handle, and the
            // per-frame check stops following it.
            string forget = e == ThrowEvent.Removed
                ? $"if equipId == {c.EquipConstant} then\n  this.SvfmForget(handle)\n  local s = this.svfmThrows and this.svfmThrows[{LuaLiteral.Quote(c.EquipConstant)}]\n  if s then s.track[handle] = nil end\nend"
                : "";
            if (this[e].Count == 0 && forget.Length == 0) continue;
            string body = this[e].Count > 0 ? MomentBody(e, c) + (forget.Length > 0 ? "\n" + forget : "") : forget;
            yield return new MessageHandler("Throwing", ThrowEvents.Message(e), "handle, equipId, owner", body);
        }
    }

    private bool Diagnostics => On.Values.Any(l => l.Any(a => a is AnnounceAction));

    /// <summary>The Lua of one moment, for a handler with <c>handle, equipId, owner</c> in scope.</summary>
    private string MomentBody(ThrowEvent e, ThrowContext c)
    {
        var steps = this[e];
        bool diagnostics = Diagnostics;
        string once = $"this.SvfmOnce(handle, {LuaLiteral.Quote(ThrowEvents.Message(e))})";
        var libs = new List<string>();
        if (steps.Any(s => s.NeedsPosition)) libs.Add("V_ThrowingSystem");
        foreach (var s in steps) if (s.Library is { } l && !libs.Contains(l)) libs.Add(l);
        string guard = string.Concat(libs.Select(l => " and " + l));
        var body = new StringBuilder();
        if (!diagnostics)
        {
            body.Append($"if equipId == {c.EquipConstant}{guard} and {once} then\n");
            if (steps.Any(s => s.NeedsPosition)) body.Append("  local x, y, z = V_ThrowingSystem.GetPosition(handle)\n");
            foreach (var line in steps.SelectMany(s => s.Lines(c))) body.Append(line.Length > 0 ? "  " + line : "").Append('\n');
            body.Append("end");
            return body.ToString();
        }
        // Testing: the announcements run first and outside the check for the V Framework tables, a missing table
        // is announced, and each call's result too (V Framework returns false or nothing when it refuses one).
        string Say(string text) => $"TppUiCommand.AnnounceLogView({LuaLiteral.Quote($"{c.Title}: {text}")})";
        // Every Throwing message is announced, whatever was thrown (a vanilla grenade too): shows whether the
        // module gets the messages at all, and the equip ID they carry against the item's.
        body.Append($"TppUiCommand.AnnounceLogView({LuaLiteral.Quote($"{c.Title} [{ThrowEvents.Message(e)}] equipId ")} .. tostring(equipId) .. {LuaLiteral.Quote(", item ")} .. tostring({c.EquipConstant}))\n");
        body.Append($"if equipId == {c.EquipConstant} and {once} then\n");
        foreach (var a in steps.OfType<AnnounceAction>()) foreach (var line in a.Lines(c)) body.Append("  ").Append(line).Append('\n');
        var work = steps.Where(a => a is not AnnounceAction).ToList();
        if (work.Count > 0)
        {
            foreach (var l in libs) body.Append($"  if not {l} then {Say($"{l} is missing (V Framework too old?)")} end\n");
            body.Append(libs.Count > 0 ? $"  if {string.Join(" and ", libs)} then\n" : "  do\n");
            if (work.Any(s => s.NeedsPosition))
            {
                body.Append("    local x, y, z = V_ThrowingSystem.GetPosition(handle)\n");
                body.Append($"    TppUiCommand.AnnounceLogView({LuaLiteral.Quote(c.Title + ": at ")} .. tostring(x) .. \", \" .. tostring(y) .. \", \" .. tostring(z))\n");
            }
            int k = 0;
            foreach (var step in work)
                foreach (var line in step.Lines(c))
                {
                    if (step is CustomLuaAction) { body.Append("    ").Append(line).Append('\n'); continue; }
                    string call = line[..Math.Max(0, line.IndexOfAny(['{', '(']))];
                    body.Append($"    local r{k} = {line}\n");
                    body.Append($"    TppUiCommand.AnnounceLogView({LuaLiteral.Quote($"{c.Title}: {call} -> ")} .. tostring(r{k}))\n");
                    k++;
                }
            body.Append("  end\n");
        }
        body.Append("end");
        return body.ToString();
    }

    /// <summary>
    /// The per-frame check (the module's Update): finds the player's thrown item by V_ThrowingSystem
    /// (GetHandleLatestThrownByPlayer, GetInfo: reboundCount, isStopped, isActivated, isDone) and runs each moment once,
    /// as the Throwing messages would. Moments already run by a message are skipped (<see cref="Helpers"/>). Null when
    /// the script has no steps.
    /// </summary>
    public string? Poller(ThrowContext c)
    {
        if (IsEmpty) return null;
        string key = LuaLiteral.Quote(c.EquipConstant);
        var w = new StringBuilder();
        w.Append("local T = V_ThrowingSystem\n");
        w.Append("if T and T.GetHandleLatestThrownByPlayer and T.GetInfo then\n");
        w.Append("  this.svfmThrows = this.svfmThrows or {}\n");
        w.Append($"  local s = this.svfmThrows[{key}] or {{ track = {{}} }}\n");
        w.Append($"  this.svfmThrows[{key}] = s\n");
        w.Append("  local latest = T.GetHandleLatestThrownByPlayer()\n");
        if (Diagnostics)
            w.Append($"  if latest ~= s.seen then s.seen = latest; local li = latest ~= nil and T.GetInfo(latest) or nil; TppUiCommand.AnnounceLogView({LuaLiteral.Quote(c.Title + " [check] latest thrown ")} .. tostring(latest) .. \", equipId \" .. tostring(li and li.equipId) .. \", item \" .. tostring({c.EquipConstant})) end\n");
        w.Append("  if latest ~= nil and not s.track[latest] then\n");
        w.Append("    local info = T.GetInfo(latest)\n");
        w.Append($"    if info and info.equipId == {c.EquipConstant} and not info.isDone then\n");
        w.Append("      s.track[latest] = { rebound = 0 }\n");
        Moment(w, ThrowEvent.Thrown, c, "latest", "info", "      ");
        w.Append("    end\n");
        w.Append("  end\n");
        w.Append("  for h, t in pairs(s.track) do\n");
        w.Append("    local info = T.GetInfo(h)\n");
        w.Append($"    if not info or info.isDone or info.equipId ~= {c.EquipConstant} then\n");
        w.Append("      s.track[h] = nil\n");
        Moment(w, ThrowEvent.Removed, c, "h", null, "      ");
        w.Append("      this.SvfmForget(h)\n");
        w.Append("    else\n");
        w.Append("      if (info.reboundCount or 0) > t.rebound then\n        t.rebound = info.reboundCount or 0\n");
        Moment(w, ThrowEvent.Bounces, c, "h", "info", "        ");
        w.Append("      end\n");
        w.Append("      if info.isStopped and not t.stopped then\n        t.stopped = true\n");
        Moment(w, ThrowEvent.Stops, c, "h", "info", "        ");
        w.Append("      end\n");
        w.Append("      if info.isActivated and not t.activated then\n        t.activated = true\n");
        Moment(w, ThrowEvent.GoesOff, c, "h", "info", "        ");
        w.Append("      end\n");
        w.Append("    end\n");
        w.Append("  end\n");
        w.Append("end\n");
        return w.ToString();
    }

    private void Moment(StringBuilder w, ThrowEvent e, ThrowContext c, string handle, string? info, string indent)
    {
        if (this[e].Count == 0) return;
        w.Append(indent).Append("do\n");
        w.Append(indent).Append($"  local handle, equipId, owner = {handle}, {c.EquipConstant}, {(info != null ? info + ".owner or 0" : "0")}\n");
        foreach (var line in MomentBody(e, c).Split('\n')) w.Append(indent).Append("  ").Append(line).Append('\n');
        w.Append(indent).Append("end\n");
    }

    /// <summary>Module-level helpers the moments use: each moment of a throw runs once, from its message or the per-frame check.</summary>
    public const string Helpers =
        "-- Thrown items: each moment of a throw runs once, from its Throwing message or from the per-frame check.\n" +
        "function this.SvfmOnce(handle, moment)\n" +
        "  this.svfmDone = this.svfmDone or {}\n" +
        "  local key = tostring(handle) .. \":\" .. moment\n" +
        "  if this.svfmDone[key] then return false end\n" +
        "  this.svfmDone[key] = true\n" +
        "  return true\n" +
        "end\n" +
        "\n" +
        "function this.SvfmForget(handle)\n" +
        "  if not this.svfmDone then return end\n" +
        "  for _, m in ipairs({ \"OnThrow\", \"OnBounceThrowing\", \"OnStopThrowing\", \"OnActivateThrowing\", \"OnRemoveThrowing\" }) do\n" +
        "    this.svfmDone[tostring(handle) .. \":\" .. m] = nil\n" +
        "  end\n" +
        "end\n";

    /// <summary>One line for the module's header (null with no effect): the vanilla-effect form names the call, as the
    /// first version of the port did; otherwise each moment and its steps (announcements left out).</summary>
    public string? Summary()
    {
        bool Plain(ThrowAction a) => a is AnnounceAction;
        var parts = ThrowEvents.All.Select(e => (e, steps: this[e].Where(a => !Plain(a)).ToList())).Where(x => x.steps.Count > 0).ToList();
        if (parts.Count == 0) return null;
        if (parts.Count == 1 && parts[0].e == ThrowEvent.GoesOff && parts[0].steps[0] is VanillaEffectAction v
            && parts[0].steps.Skip(1).All(a => a is RemoveAction) && SupportWeaponFamilies.EffectCall(v.Swp) is { } fx)
            return $"Effect: {fx.call} when it goes off (V Framework runs no native effect for a custom item).";
        return "Script: " + string.Join("; ", parts.Select(p => $"{ThrowEvents.Label(p.e)}, {string.Join(", ", p.steps.Select(a => a.Describe()))}")) + ".";
    }

    /// <summary>The steps a script can use, in editor order: name, what it does, and a new step with the editor's
    /// default values. The effect step depends on the item: see <see cref="EffectStep"/>. One source for the editor (#111).</summary>
    public static readonly IReadOnlyList<ThrowStepKind> Steps =
    [
        new("It goes off now", "The item goes off at once, before its fuse ends. At \"Hits a surface\" this makes an impact fuse.", () => new GoOffAction()),
        new("It sticks where it lands", "The item stops where it is. Its fuse keeps running. At \"Hits a surface\" this makes it sticky.", () => new StickAction()),
        new("Remove the item", "The item is used up and goes away. Put this step last.", () => new RemoveAction()),
        new("Set the fuse", "Sets the time that is left before the item goes off.", () => new SetFuseAction(3)),
        new("Enemies hear it land", "Guards near the item hear it land and come to look.", () => new ReboundNoiseAction()),
        new("Landing sound", "Plays the landing sound of the item. Guards do not hear it.", () => new LandingSoundAction()),
        new("Smoke trail", "The item gives off a trail of smoke.", () => new SmokeTrailAction()),
        new("Chaff", "A chaff cloud where the item is. It jams cameras, UAVs and Sahelanthropus, and cuts the radio of the enemy base and lock-on. A new chaff cloud replaces the old one.", () => new ChaffAction(10)),
        new("Stun burst", "A stun grenade flash where the item is. Enemies in the radius are stunned.", () => new StunAction()),
        new("Smoke or sleeping-gas cloud", "A cloud where the item is. Smoke hides the player. Sleeping gas puts enemies to sleep.", () => new CloudAction(10, 5)),
        new("Fire", "Molotov fire where the item is. It burns enemies that walk into it.", () => new FireAction()),
        new("Explosion with the item's blast", "The damage and the push of an explosion where the item is. There is no flash and no sound. Add a Noise step if guards must hear it.", () => new ExplosionAction()),
        new("Noise", "A noise where the item is. Guards hear it and come to look. Nobody hears a real sound.", () => new NoiseAction(0x1C)),
        new("Announce on screen", "Shows a line of text in the log at the bottom left. For tests.", () => new AnnounceAction("hello"), DeveloperOnly: true),
        new("Own Lua", "Your own Lua code runs at this moment.", () => new CustomLuaAction("")),
    ];

    /// <summary>The step that runs the vanilla effect of <paramref name="swp"/> (the item's "Thrown as").</summary>
    public static ThrowStepKind EffectStep(string swp)
    {
        var n = SupportWeaponFamilies.Name(swp);
        return new($"The {n} effect", $"The item does what the vanilla {n} does when it goes off.", () => new VanillaEffectAction(swp));
    }

    /// <summary>What a step does, in plain words (the description of its kind).</summary>
    public static string Help(ThrowAction a) => a is VanillaEffectAction v ? EffectStep(v.Swp).Description
        : Steps.FirstOrDefault(k => k.New().GetType() == a.GetType())?.Description ?? "";
}

/// <summary>A kind of step: its name, what it does, and a new step with default values.</summary>
public sealed record ThrowStepKind(string Name, string Description, Func<ThrowAction> New, bool DeveloperOnly = false);

/// <summary>Vanilla support weapon kinds: family names and the V Framework call that runs a kind's effect.</summary>
public static class SupportWeaponFamilies
{
    /// <summary>An item's family name without its grade: EQP_SWP_Grenade_G02 → EQP_SWP_Grenade.</summary>
    public static string Family(string id) => Regex.Replace(id, "_G0[0-9]$", "");

    /// <summary>A readable name: SWP_SleepingGusGrenade → "sleeping gas grenade".</summary>
    public static string Name(string swp) => Family(swp).Replace("EQP_", "").Replace("SWP_", "") switch
    {
        "Grenade" => "grenade", "StunGrenade" => "stun grenade", "SmokeGrenade" => "smoke grenade", "SleepingGusGrenade" => "sleeping gas grenade",
        "MolotovCocktail" or "MolotovCocktailPlaced" => "Molotov", "Kibidango" => "bait", "FakeSign" => "fake sign",
        var other => Regex.Replace(other, "(?<=[a-z])(?=[A-Z])", " ").ToLower(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The V Framework call that runs a vanilla thrown item's effect on a custom item (Lua_API "Throwing"), and whether the
    /// item is used up by it. Null: V Framework 1.7 has no such call for the item (flares, the empty magazine).
    /// </summary>
    public static (string call, bool removeAfter)? EffectCall(string swp) => Family(swp).Replace("EQP_", "") switch
    {
        "SWP_Grenade" => ("V_ThrowingImpl.UpdateActionGrenade(handle, 1)", true),
        "SWP_StunGrenade" => ("V_ThrowingImpl.UpdateActionGrenade(handle, 3)", true),
        "SWP_SmokeGrenade" or "SWP_SleepingGusGrenade" => ("V_ThrowingImpl.UpdateActionSmoke(handle)", false),
        "SWP_MolotovCocktail" or "SWP_MolotovCocktailPlaced" => ("V_ThrowingImpl.UpdateActionMolotovCocktail(handle)", false),
        "SWP_Kibidango" => ("V_ThrowingImpl.UpdateActionKibidango(handle)", false),
        "SWP_FakeSign" => ("V_ThrowingImpl.UpdateActionFakeSign(handle)", false),
        _ => null,
    };
}
