using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Svfm.Core.Formats;

namespace Svfm.Core.VFramework;

/// <summary>
/// Reads the script of a thrown item of an existing V Framework mod ("What it does", row 14 of
/// .docs/Parity_Existing_VFramework_Mods.md): the steps that the mod's Throwing handlers run for the item. A handler runs
/// a step for the item inside <c>if equipId == &lt;constant&gt; ... then</c>. Each line of that block is a step when the
/// tool writes the same line for it (<see cref="ThrowAction.Lines"/>), and the other lines are own Lua. The lines the tool
/// adds round its steps (the item's position, the once-per-throw helpers, the testing announcements) are not steps. The
/// per-frame check of an SVFM module runs the same blocks again, and they must give the same steps.
/// </summary>
public static class ThrowScriptReader
{
    /// <summary>The check that a handler runs a block for the item: <c>equipId == &lt;constant&gt;</c> (not <c>info.equipId</c>).</summary>
    public static Regex Guard(string constant) => new($@"(?<![\w.])equipId\s*==\s*{Regex.Escape(constant)}(?![\w.])");

    /// <summary>Makes every block of the script that runs for the item never run: each check becomes <c>false</c>.</summary>
    public static string SwitchOff(string src, string constant) => Guard(constant).Replace(src, "false");

    private static readonly string Messages = string.Join("|", ThrowEvents.All.Select(ThrowEvents.Message));
    private static readonly Regex Named = new($@"\bmsg\s*=\s*""({Messages})""\s*,\s*func\s*=(?!\s*function\b)");
    private static readonly Regex Once = new(@"^this\.SvfmOnce\(handle,\s*""(\w+)""\)$");
    private static readonly Regex Library = new(@"^V_\w+$");
    private static readonly Regex Position = new(@"^local x, y, z = V_ThrowingSystem\.GetPosition\(handle\)$");
    private static readonly Regex Result = new(@"^TppUiCommand\.AnnounceLogView\(""[^""]*( -> |: at )"" \.\. tostring\(");
    private static readonly Regex Missing = new(@"^if not V_\w+ then TppUiCommand\.AnnounceLogView\(.*\) end$");
    private static readonly Regex Wrapper = new(@"^(if V_\w+( and V_\w+)* then|do)$");
    private static readonly Regex Local = new(@"^local r\d+ = ");
    private static readonly Regex[] Plumbing =
    [
        Position, new(@"^this\.SvfmForget\(handle\)$"), new(@"^local s = this\.svfmThrows and this\.svfmThrows\[.*\]$"),
        new(@"^if s then s\.track\[handle\] = nil end$"),
    ];

    /// <summary>Reads the item's script from the mod's modules.</summary>
    /// <param name="sources">The text of every module of the mod.</param>
    /// <param name="c">The item: its equip constant (TppEquip.EQP_...), its name, its attack and blast.</param>
    /// <param name="throwAs">What the item is thrown as (EQP_SWP_...), or null: its vanilla effect is tried first.</param>
    /// <returns>The steps, and why the script can't be changed (null when it can).</returns>
    public static (ThrowScript Script, string? Lock) Read(IReadOnlyList<string> sources, ThrowContext c, string? throwAs)
    {
        var script = new ThrowScript();
        var guard = Guard(c.EquipConstant);
        var names = new Regex($@"(?<![\w.""]){Regex.Escape(c.EquipConstant)}(?![\w.])");
        var outside = new List<(string src, Match g)>();
        foreach (var src in sources)
        {
            if (!names.IsMatch(src)) continue;
            if (Named.Match(src) is { Success: true } nm) return (script, $"its {nm.Groups[1].Value} handler is a function of the script's own, and the tool reads only a handler written in place");
            var handlers = new List<(int start, int end)>();
            foreach (Match m in Regex.Matches(src, $@"\bmsg\s*=\s*""({Messages})""\s*,\s*func\s*=\s*function\b"))
            {
                int end = LuaSource.BlockEnd(src, m.Index + m.Length);
                if (end < 0) return (script, $"its {m.Groups[1].Value} handler has no end");
                handlers.Add((m.Index, end));
                var e = ThrowEvents.All.First(x => ThrowEvents.Message(x) == m.Groups[1].Value);
                string h = src[m.Index..end];
                var gs = guard.Matches(h);
                bool named = h.Split('\n').Any(l => !l.Contains("AnnounceLogView", StringComparison.Ordinal) && names.IsMatch(l));
                if (gs.Count == 0 && named) return (script, $"its {m.Groups[1].Value} handler names the item, but not as \"equipId == {c.EquipConstant}\"");
                foreach (Match g in gs)
                {
                    var steps = Block(h, g, c, throwAs, out _, out string? why);
                    if (steps == null) return (script, $"its {m.Groups[1].Value} handler: {why}");
                    script[e].AddRange(steps);
                }
            }
            foreach (Match g in guard.Matches(src))
                if (!handlers.Any(r => g.Index > r.start && g.Index < r.end)) outside.Add((src, g));
            // Any other test of the item's equip ID (a check of the script's own) is not read.
            foreach (var line in src.Split('\n'))
                if (Regex.IsMatch(line, @"equipId\s*(==|~=)") && names.IsMatch(line) && !line.Contains("AnnounceLogView", StringComparison.Ordinal) && !guard.IsMatch(line) && !PollerLine(line, c.EquipConstant))
                    return (script, $"the script checks the item's equip ID in a way the tool can't read (\"{line.Trim()}\")");
        }
        // The per-frame check of an SVFM module: each block runs one moment, as its handler does.
        foreach (var (src, g) in outside)
        {
            var steps = Block(src, g, c, throwAs, out string? moment, out string? why);
            if (steps == null) return (script, why!);
            if (moment == null || !ThrowEvents.All.Any(x => ThrowEvents.Message(x) == moment)) return (script, "the script runs steps for the item outside its Throwing handlers");
            var e = ThrowEvents.All.First(x => ThrowEvents.Message(x) == moment);
            if (script[e].Count == 0) script[e].AddRange(steps);
            else if (JsonSerializer.Serialize(script[e]) != JsonSerializer.Serialize(steps))
                return (script, $"the per-frame check runs other steps {ThrowEvents.Label(e)} than the {moment} handler");
        }
        return (script, null);
    }

    /// <summary>The lines of an SVFM per-frame check that test the item's equip ID (not a step).</summary>
    private static bool PollerLine(string line, string constant)
    {
        string k = Regex.Escape(constant), l = line.Trim();
        return Regex.IsMatch(l, $@"^if info and info\.equipId == {k} and not info\.isDone then$")
            || Regex.IsMatch(l, $@"^if not info or info\.isDone or info\.equipId ~= {k} then$");
    }

    /// <summary>The steps of the block that the check <paramref name="g"/> opens, or null (with why) when it can't be read.</summary>
    private static List<ThrowAction>? Block(string s, Match g, ThrowContext c, string? throwAs, out string? moment, out string? why)
    {
        moment = null; why = null;
        int ifAt = s.LastIndexOf("if", g.Index, StringComparison.Ordinal);
        var then = new Regex(@"\bthen\b").Match(s, g.Index);
        if (ifAt < 0 || !then.Success || s[(ifAt + 2)..g.Index].Trim().Length > 0) { why = "the check of the item is not the start of an if"; return null; }
        // The check may only add the V Framework tables it needs and the once-per-throw helper.
        foreach (var term in Regex.Split(s[(g.Index + g.Length)..then.Index], @"\band\b").Select(t => t.Trim()).Skip(1))
        {
            if (Library.IsMatch(term)) continue;
            if (Once.Match(term) is { Success: true } o) { moment = o.Groups[1].Value; continue; }
            why = $"the check of the item also tests \"{term}\""; return null;
        }
        if (s[(g.Index + g.Length)..then.Index].Trim() is { Length: > 0 } rest && !rest.StartsWith("and", StringComparison.Ordinal)) { why = $"the check of the item also tests \"{rest}\""; return null; }
        int end = LuaSource.BlockEnd(s, then.Index + then.Length);
        if (end < 0) { why = "the block of the item has no end"; return null; }
        return Steps(s[(then.Index + then.Length)..(end - 3)], c, throwAs, out why);
    }

    private static List<ThrowAction>? Steps(string body, ThrowContext c, string? throwAs, out string? why)
    {
        why = null;
        var lines = body.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        bool pos = lines.Any(l => Position.IsMatch(l.Trim()));
        bool testing = lines.Any(l => Result.IsMatch(l.Trim()));
        var steps = new List<ThrowAction>();
        var own = new List<string>();
        int depth = 0;
        bool wrapper = false;
        void Flush()
        {
            if (own.Count == 0) return;
            int cut = own[0].Length - own[0].TrimStart().Length;
            steps.Add(new CustomLuaAction(string.Join("\n", own.Select(l => l.Length - l.TrimStart().Length >= cut ? l[cut..] : l.TrimStart())), pos));
            own.Clear();
        }
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            if (depth == 0)
            {
                if (Plumbing.Any(r => r.IsMatch(l)) || testing && (Result.IsMatch(l) || Missing.IsMatch(l))) continue;
                if (testing && !wrapper && Wrapper.IsMatch(l)) { wrapper = true; continue; }
                if (wrapper && l == "end") { wrapper = false; continue; }
                if (l == "else" || l.StartsWith("elseif", StringComparison.Ordinal)) { why = "the block of the item has an else"; return null; }
                if (Known(Local.Replace(l, ""), c, throwAs) is { } a) { Flush(); steps.Add(a); continue; }
            }
            own.Add(raw.TrimEnd());
            // shortcut: keywords inside strings count too; a quoted "end" in own Lua would cut the block short.
            depth = Math.Max(0, depth + Regex.Matches(l, @"\b(function|if|do|repeat)\b").Count - Regex.Matches(l, @"\b(end|until)\b").Count);
        }
        Flush();
        return steps;
    }

    private static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

    /// <summary>The step that the tool writes as <paramref name="line"/>, or null.</summary>
    private static ThrowAction? Known(string line, ThrowContext c, string? throwAs)
    {
        foreach (var a in Candidates(line, c, throwAs))
            if (a.Problem(c) == null && Norm(string.Join("\n", a.Lines(c))) == Norm(line)) return a;
        return null;
    }

    private static readonly string[] Families = ["SWP_Grenade", "SWP_StunGrenade", "SWP_SmokeGrenade", "SWP_SleepingGusGrenade", "SWP_MolotovCocktail", "SWP_Kibidango", "SWP_FakeSign"];

    private static IEnumerable<ThrowAction> Candidates(string line, ThrowContext c, string? throwAs)
    {
        if (throwAs != null) yield return new VanillaEffectAction(throwAs);
        foreach (var f in Families) yield return new VanillaEffectAction(f);
        yield return new GoOffAction();
        yield return new StickAction();
        yield return new RemoveAction();
        yield return new ReboundNoiseAction();
        yield return new LandingSoundAction();
        yield return new SmokeTrailAction();
        if (Regex.Match(line, @"^V_ThrowingImpl\.SetLifeLeft\(handle,\s*([-0-9.eE]+)\)$") is { Success: true } fuse
            && double.TryParse(fuse.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
            yield return new SetFuseAction(sec);
        if (Regex.Match(line, @"^TppUiCommand\.AnnounceLogView\(("".*"")\)$") is { Success: true } say
            && LuaLiteral.Parse(say.Groups[1].Value) is LuaValue.Text t && t.Value.StartsWith(c.Title + ": ", StringComparison.Ordinal))
            yield return new AnnounceAction(t.Value[(c.Title.Length + 2)..]);

        int brace = line.IndexOf('{');
        if (brace < 0 || !line.EndsWith('}')) yield break;
        if (LuaLiteral.TryParse(line[brace..]) is not LuaValue.Table a) yield break;
        double? Num(string k) => a[k] is LuaValue.Number n ? n.Value : null;
        bool Yes(string k) => a[k] is LuaValue.Name { Value: "true" };
        string? Named(string k, string? item) => a[k] is LuaValue.Name n ? n.Value == item ? ThrowAction.Item : n.Value : null;
        switch (line[..brace].Trim())
        {
            case "V_RangeAttackSystem.RequestToChaff": yield return new ChaffAction(Num("duration") ?? 0, Yes("endless"), Yes("noEffect")); break;
            case "V_RangeAttackSystem.RequestToStun": yield return new StunAction(Num("radius") ?? 0, Named("attackId", c.ItemAttack), (int?)Num("grade")); break;
            case "V_RangeAttackSystem.RequestToSmoke": yield return new CloudAction(Num("duration") ?? 0, Num("radius") ?? 0, a["type"] is LuaValue.Text { Value: "SleepGas" }); break;
            case "V_RangeAttackSystem.RequestToFlame": yield return new FireAction(Num("duration"), Num("radius"), Named("attackId", c.ItemAttack), (int?)Num("grade")); break;
            case "V_BlastSystem.Blast":
                if (Named("blastId", c.ItemBlast) is { } bl && Named("attackId", c.ItemAttack) is { } at)
                    yield return new ExplosionAction(bl, at, Num("radius"), a["shockwave"] is not LuaValue.Name { Value: "false" });
                break;
            case "V_NoiseSystem.AddNoise": if (Num("type") is { } ty) yield return new NoiseAction((int)ty); break;
        }
    }
}
