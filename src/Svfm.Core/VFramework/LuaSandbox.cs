using MoonSharp.Interpreter;
using Svfm.Core.Formats;

namespace Svfm.Core.VFramework;

/// <summary>One call a module made to the game or to V Framework, with its arguments as the module built them.</summary>
/// <param name="Path">The dotted name that was called, e.g. <c>V_Player.RegisterOutfit</c>.</param>
/// <param name="Occurrence">0 for the first call of this name, 1 for the second, …</param>
/// <param name="Arguments">The arguments. A game or V Framework name used as a value (<c>TppEquip.EQP_SUIT</c>) appears as a <see cref="LuaValue.Name"/> with its dotted name.</param>
/// <param name="Site">
/// Which written call of <paramref name="Path"/> made it: an index into <see cref="LuaSource.FindCalls"/> of the module's
/// text, or -1 when no call that LuaSource reads made it (the script calls it through another name, or inside the
/// arguments of another call). A call
/// in a loop or in a function the script calls more than once makes several recorded calls with one site.
/// </param>
public sealed record RecordedCall(string Path, int Occurrence, IReadOnlyList<LuaValue> Arguments, int Site = -1);

/// <summary>What running a module recorded.</summary>
public sealed class SandboxResult
{
    public List<RecordedCall> Calls { get; } = [];
    /// <summary>Why the run stopped early (a Lua error, or the time limit); the calls recorded before it are kept.</summary>
    public string? Error { get; set; }
    public bool TimedOut { get; set; }
}

/// <summary>
/// Runs the Lua of a V Framework mod's module without the game: every name the script uses that the script did not define
/// (V_Player, TppEquip, …) is a stand-in that records what is called and what is read from it, and nothing is executed.
/// This is how an existing mod is read: the script itself says what it registers.
/// </summary>
public static class LuaSandbox
{
    /// <summary>Runs the module and its registration function.</summary>
    /// <param name="lua">The module's source.</param>
    /// <param name="entry">The function of the returned table to run (V Framework mods register in <c>LoadLibraries</c>).</param>
    /// <param name="timeout">How long the script may run.</param>
    /// <returns>The recorded calls.</returns>
    public static SandboxResult Run(string lua, string entry = "LoadLibraries", TimeSpan? timeout = null)
    {
        var result = RunOnce(lua, entry, timeout, null);
        var sites = result.Calls.Select(c => c.Path).Distinct().ToDictionary(p => p, p => LuaSource.FindCalls(lua, p), StringComparer.Ordinal);
        // The usual case: every call is written once, so the nth recorded call of a name is its nth written call.
        if (sites.All(kv => kv.Value.Count == result.Calls.Count(c => c.Path == kv.Key)))
        {
            for (int i = 0; i < result.Calls.Count; i++) result.Calls[i] = result.Calls[i] with { Site = result.Calls[i].Occurrence };
            return result;
        }
        // Some call runs more than once (a loop, a function called again) or through another name. Run a copy whose
        // written calls say which they are, and take the sites from it when it made the same calls.
        var marks = sites.SelectMany(kv => kv.Value.Select((site, i) => (kv.Key, site.Start, i))).OrderBy(m => m.Start).ToList();
        var marked = new System.Text.StringBuilder(lua);
        for (int k = marks.Count - 1; k >= 0; k--)
            marked.Insert(marks[k].Start + marks[k].Key.Length, ")").Insert(marks[k].Start, $"{SiteMark}({k},");
        var again = RunOnce(marked.ToString(), entry, timeout, marks.Select(m => (m.Key, m.i)).ToList());
        if (again.Calls.Select(c => c.Path).SequenceEqual(result.Calls.Select(c => c.Path)))
            for (int i = 0; i < result.Calls.Count; i++) result.Calls[i] = result.Calls[i] with { Site = again.Calls[i].Site };
        return result;
    }

    /// <summary>The function a marked copy of a script calls with each written call's number before the call is made.</summary>
    private const string SiteMark = "__svfm_site";

    private static SandboxResult RunOnce(string lua, string entry, TimeSpan? timeout, List<(string path, int site)>? marks)
    {
        var result = new SandboxResult();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + limit;
        var thread = new Thread(() => RunInner(lua, entry, result, marks, deadline)) { IsBackground = true };
        thread.Start();
        // The script stops itself at the deadline; this wait only gives up on one stuck inside a single library call.
        if (!thread.Join(limit + TimeSpan.FromSeconds(1)))
        {
            result.TimedOut = true;
            result.Error = "The script ran too long and was stopped.";
        }
        return result;
    }

    private static void RunInner(string lua, string entry, SandboxResult result, List<(string path, int site)>? marks, DateTime deadline)
    {
        try
        {
            var script = new Script(CoreModules.Preset_SoftSandbox);
            var paths = new Dictionary<Table, string>(ReferenceEqualityComparer.Instance);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            // Marked runs only: the marked call being made now (set just before its function is called, so a call made
            // while its arguments are worked out, written or not, never takes its mark).
            (string path, int site)? making = null;

            static string Join(string path, DynValue key) => path + "." + (key.Type == DataType.String ? key.String : key.CastToString());

            Table Proxy(string path)
            {
                var t = new Table(script);
                var mt = new Table(script);
                mt["__index"] = DynValue.NewCallback((ctx, a) => DynValue.NewTable(Proxy(Join(path, a[1]))));
                mt["__call"] = DynValue.NewCallback((ctx, a) =>
                {
                    var args = Enumerable.Range(1, a.Count - 1).Select(i => Convert(a[i], paths, 0)).ToList();
                    int n = counts.GetValueOrDefault(path);
                    counts[path] = n + 1;
                    int site = making is { } m && m.path == path ? m.site : -1;
                    making = null;
                    lock (result) result.Calls.Add(new RecordedCall(path, n, args, site));
                    return DynValue.NewTable(Proxy(path + "()"));
                });
                mt["__tostring"] = DynValue.NewCallback((ctx, a) => DynValue.NewString(path));
                mt["__concat"] = DynValue.NewCallback((ctx, a) => DynValue.NewString(a[0].CastToString() + a[1].CastToString()));
                t.MetaTable = mt;
                paths[t] = path;
                return t;
            }

            if (marks != null)
                // __svfm_site(k, V_X.Y) gives back a function that makes the call as marked call k.
                script.Globals[SiteMark] = DynValue.NewCallback((ctx, a) =>
                {
                    var mark = marks[(int)a[0].Number];
                    var fn = a[1];
                    // The call is handed back to the script to make next (MoonSharp can't call into the script from
                    // here); the stand-in's __call is the next thing that runs, and it clears the mark.
                    return DynValue.NewCallback((c, args) =>
                    {
                        making = mark;
                        return DynValue.NewTailCallReq(fn, args.GetArray());
                    });
                });

            var globalMeta = new Table(script);
            globalMeta["__index"] = DynValue.NewCallback((ctx, a) => DynValue.NewTable(Proxy(a[1].CastToString())));
            script.Globals.MetaTable = globalMeta;

            // Run as a coroutine that yields every 1000 instructions, so an endless loop ends at the deadline (a thread
            // that is only given up on keeps a CPU core busy until the app closes).
            DynValue RunToEnd(DynValue fn)
            {
                var co = script.CreateCoroutine(fn).Coroutine;
                co.AutoYieldCounter = 1000;
                var r = co.Resume();
                while (r.Type == DataType.YieldRequest)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException();
                    r = co.Resume();
                }
                return r;
            }

            var module = RunToEnd(script.LoadString(lua));
            if (module.Type == DataType.Table && module.Table.Get(entry) is { Type: DataType.Function or DataType.ClrFunction } fn)
                if (fn.Type == DataType.Function) RunToEnd(fn); else script.Call(fn);
            else if (module.Type != DataType.Table) result.Error ??= "The script does not return a module table.";
            // A module without the entry function registers at load time (the calls it made while loading are kept).
        }
        catch (ScriptRuntimeException e) { result.Error = e.DecoratedMessage ?? e.Message; }
        catch (InterpreterException e) { result.Error = e.DecoratedMessage ?? e.Message; }
        catch (TimeoutException) { result.TimedOut = true; result.Error = "The script ran too long and was stopped."; }
        // Anything else escaping this thread would end the whole app, and the script is from a downloaded mod.
        catch (Exception e) { result.Error = e.Message; }
    }

    private static LuaValue Convert(DynValue v, Dictionary<Table, string> paths, int depth)
    {
        switch (v.Type)
        {
            case DataType.Number: return new LuaValue.Number(v.Number);
            case DataType.String: return new LuaValue.Text(v.String);
            case DataType.Boolean: return new LuaValue.Name(v.Boolean ? "true" : "false");
            case DataType.Nil: case DataType.Void: return new LuaValue.Name("nil");
            case DataType.Table:
                if (paths.TryGetValue(v.Table, out var path)) return new LuaValue.Name(path);
                if (depth > 16) return new LuaValue.Name("<too deep>");
                var items = new List<LuaValue>();
                var fields = new List<KeyValuePair<string, LuaValue>>();
                int n = v.Table.Length;
                for (int i = 1; i <= n; i++) items.Add(Convert(v.Table.Get(i), paths, depth + 1));
                foreach (var pair in v.Table.Pairs)
                {
                    if (pair.Key.Type == DataType.String) fields.Add(new(pair.Key.String, Convert(pair.Value, paths, depth + 1)));
                    else if (pair.Key.Type == DataType.Number && (pair.Key.Number < 1 || pair.Key.Number > n || pair.Key.Number != Math.Floor(pair.Key.Number)))
                        fields.Add(new("[" + pair.Key.CastToString() + "]", Convert(pair.Value, paths, depth + 1)));
                }
                return new LuaValue.Table(items, fields);
            default: return new LuaValue.Name("<function>");
        }
    }
}
