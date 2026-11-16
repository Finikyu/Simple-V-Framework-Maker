using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

public sealed partial class ExistingModData
{
    /// <summary>
    /// The scripts the tool wrote for copies of the mod's items (Duplicate): module path → the copy. They are in
    /// <see cref="Modules"/> too. A copy that the project no longer has (removed, or undone) leaves its script out of the build.
    /// </summary>
    public Dictionary<string, ExistingItem> Copies { get; } = new(StringComparer.Ordinal);
}

// Duplicate of an existing item (gap G11, row 23 of .docs/Parity_Existing_VFramework_Mods.md). The copy is a new script
// that the tool writes from the values the item's calls were made with, and then opens like any script of the mod: every
// edit, the build and Remove work on the copy the same way as on the items the mod has.
public static partial class ExistingMod
{
    /// <summary>
    /// Why the item can't be duplicated, or null when it can. A copy repeats the item's registration call and its R&amp;D
    /// row with a key and text IDs of its own, so the item must have one of each that the script makes once, and every
    /// value must be one that can be written again (not a function).
    /// </summary>
    /// <param name="item">An item of an existing mod.</param>
    /// <returns>The reason, or null.</returns>
    public static string? DuplicateLockOf(ExistingItem item)
    {
        if (item.ItemType == ItemKind.ExtraLooks) return "it adds looks to a vanilla suit, and a copy would add the same looks to it again";
        if (item.ItemType is not (ItemKind.Outfit or ItemKind.HeadOption or ItemKind.BuddyOutfit))
            return "a copy of a weapon or item needs an equip ID, a gun and parts of its own, and the tool can't make them for an existing one yet";
        if (item.Key == null) return "the script gives it no key";
        if (item.Calls.Count == 0) return "the script has only an R&D row for it, and no call that registers it";
        if (item.Instances.Count > 0 || item.Calls.Count > 1 || item.Rows.Count > 1) return "one call in the script registers it more than once, together with other things";
        if (item.Calls.Concat(item.Rows).SelectMany(c => c.Arguments).Any(Unwritable)) return "its script gives a value as a function, and the tool can't copy that";
        return null;
    }

    /// <summary>A value that the sandbox could not keep as a value (a function, or a table nested too deep).</summary>
    private static bool Unwritable(LuaValue v) => v switch
    {
        LuaValue.Name n => n.Value.StartsWith('<'),
        LuaValue.Table t => t.Items.Any(Unwritable) || t.Fields.Any(f => Unwritable(f.Value)),
        _ => false,
    };

    /// <summary>
    /// Makes a copy of an item of the mod: a new module (<c>SVFM_&lt;project id&gt;_&lt;copy id&gt;.lua</c>) that makes the
    /// item's calls again with the values the mod made them with, a key of its own and text IDs of its own (each is the
    /// original with <c>_&lt;copy id&gt;</c> added). The copy uses the item's files. It starts with the item's current text.
    /// </summary>
    /// <param name="p">The project of the existing mod.</param>
    /// <param name="item">The item to copy.</param>
    /// <param name="newId">The copy's ID.</param>
    /// <returns>The copy, not yet in the project.</returns>
    public static ExistingItem Duplicate(ModProject p, ExistingItem item, string newId)
    {
        var data = p.Existing ?? throw new InvalidOperationException("Not an existing-mod project.");
        if (DuplicateLockOf(item) is { } why) throw new InvalidOperationException($"\"{item.Title}\" can't be duplicated: {why}.");
        var langs = ReadLanguages(data.Source);
        static string Free(string wanted, Func<string, bool> used) { string s = wanted; for (int n = 2; used(s); n++) s = $"{wanted}_{n}"; return s; }

        // New names: each text ID, then the key (a key that is also a text ID gets the key's new name).
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        bool TextUsed(string id) => map.ContainsValue(id) || p.Items.Any(i => i.Text.ContainsKey(id))
            || langs.Values.Any(l => l.Any(s => s.File.Get(FoxHash.StrCode32(id)) != null));
        foreach (var id in new[] { item.NameId, item.RealNameId, item.InfoId }.Concat(item.OtherText.Select(o => o.Id)).OfType<string>().Distinct())
            map[id] = Free($"{id}_{newId}", TextUsed);
        string key = Free($"{item.Key}_{newId}", k => map.ContainsValue(k) || p.Items.Any(i => i.Key == k) || data.Opened.Any(o => o.Key == k));
        map[item.Key!] = key;
        LuaValue Rename(LuaValue v) => v switch
        {
            LuaValue.Text t when map.TryGetValue(t.Value, out var to) => new LuaValue.Text(to),
            LuaValue.Table t => new LuaValue.Table(t.Items.Select(Rename).ToList(), t.Fields.Select(f => new KeyValuePair<string, LuaValue>(f.Key, Rename(f.Value))).ToList()),
            _ => v,
        };

        var block = new StringBuilder();
        foreach (var c in item.Calls.Concat(item.Rows))
            block.Append("  ").Append(c.Path).Append('(').Append(string.Join(", ", c.Arguments.Select(a => WriteLua(Rename(a), "  ")))).Append(")\n");
        string src = LuaWriter.Module([$"A copy of \"{item.Title}\" ({item.Module}), made with Duplicate."], [block.ToString()]);
        // A script of the mod is never written over; a stale copy (removed, or undone) is.
        string module = Free($"/mod/modules/SVFM_{p.Id}_{newId}", m => data.Source.GameFiles.ContainsKey(m + ".lua")
            || (data.Copies.TryGetValue(m + ".lua", out var live) && p.Items.Contains(live))) + ".lua";

        var run = LuaSandbox.Run(src);
        var calls = run.Calls.Where(c => c.Path != Develop).ToList();
        var rows = run.Calls.Where(c => c.Path == Develop).ToList();
        if (run.Error != null || calls.Count != item.Calls.Count || rows.Count != item.Rows.Count)
            throw new InvalidOperationException($"The script of the copy of \"{item.Title}\" could not be read back: {run.Error ?? "its calls are not the item's calls"}.");
        var written = run.Calls.Select(c => c.Path).Distinct().ToDictionary(x => x, x => LuaSource.FindCalls(src, x));
        var newText = map.Values.ToHashSet(StringComparer.Ordinal);
        var temp = new ModProject { Id = p.Id, Name = p.Name };
        BuildItem(temp, data, data.Source, module, src, calls, rows, item.ItemType, "Copy", key, written, _ => newId,
            newText.Contains, _ => null, langs, [], Stems(data.Source), EquipPackages([run]));
        var copy = (ExistingItem)temp.Items.Single();

        foreach (var (from, to) in map)
            if (item.Text.TryGetValue(from, out var per))
                foreach (var (lang, t) in per) copy.SetText(to, lang, t);
        copy.Title = item.Title + " (copy)";
        if (copy.NameId != null && copy.Text.TryGetValue(copy.NameId, out var name) && name.TryGetValue("eng", out var eng)) copy.SetText(copy.NameId, "eng", eng + " (copy)");

        data.Modules[module] = src;
        data.Copies[module] = copy;
        return copy;
    }

    /// <summary>A value as Lua source, a table over several lines (each line after the first starts with <paramref name="indent"/>).</summary>
    private static string WriteLua(LuaValue v, string indent) => v switch
    {
        LuaValue.Table { Items.Count: 0, Fields.Count: 0 } => "{}",
        LuaValue.Table t => "{\n"
            + string.Concat(t.Items.Select(x => $"{indent}  {WriteLua(x, indent + "  ")},\n"))
            + string.Concat(t.Fields.Select(f => $"{indent}  {LuaKey(f.Key)} = {WriteLua(f.Value, indent + "  ")},\n"))
            + indent + "}",
        // "R": a number is written back exactly as the script gave it.
        LuaValue.Number n => n.Value == Math.Floor(n.Value) && Math.Abs(n.Value) < 1e15 ? ((long)n.Value).ToString(CultureInfo.InvariantCulture) : n.Value.ToString("R", CultureInfo.InvariantCulture),
        _ => v.ToString()!,
    };

    /// <summary>A table key as Lua source: a name as it is, "[n]" as it is, anything else quoted.</summary>
    private static string LuaKey(string k) => Regex.IsMatch(k, @"^[A-Za-z_][A-Za-z0-9_]*$|^\[.*\]$") ? k : $"[{LuaLiteral.Quote(k)}]";
}
