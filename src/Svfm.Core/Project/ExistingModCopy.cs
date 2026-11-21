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
        if (item.Support)
        {
            // A thrown or placed item (#106): the copy writes its support calls again, and the tool writes its script.
            if (item.Key == null || item.EquipId == null || item.Rows.Count != 1) return "the script gives it no key, no equip ID, or more than one R&D row";
            if (item.ScriptLock != null) return $"the tool can't read its throw script ({item.ScriptLock})";
            return item.Rows.SelectMany(c => c.Arguments).Any(Unwritable) ? "its script gives a value as a function, and the tool can't copy that" : null;
        }
        if (item.ItemType == ItemKind.Weapon)
        {
            // A gun (#113): the copy writes its equip and weapon constants, SetGunBasic and the equip table row again; the parts are shared.
            if (item.Key == null || item.EquipId == null || item.Rows.Count != 1) return "the script gives it no key, no equip ID, or more than one R&D row";
            if (!item.StatGroups.Contains("Gun")) return "the tool can't find the one SetGunBasic call of its gun in the script";
            return item.Rows.SelectMany(c => c.Arguments).Any(Unwritable) ? "its script gives a value as a function, and the tool can't copy that" : null;
        }
        if (item.ItemType is not (ItemKind.Outfit or ItemKind.HeadOption or ItemKind.BuddyOutfit))
            return "the tool can copy only outfits, head options, buddy costumes, guns and thrown items of an existing mod";
        if (item.Key == null) return "the script gives it no key";
        if (item.Calls.Count == 0) return "the script has only an R&D row for it, and no call that registers it";
        if (item.Instances.Count > 0 || item.Calls.Count > 1 || item.Rows.Count > 1 || item.Calls.Concat(item.Rows).Any(c => c.SiteRuns > 1)) return "one call in the script registers it more than once, together with other things";
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
            LuaValue.Name n when map.TryGetValue(n.Value, out var to) => new LuaValue.Name(to),
            LuaValue.Table t => new LuaValue.Table(t.Items.Select(Rename).ToList(), t.Fields.Select(f => new KeyValuePair<string, LuaValue>(f.Key, Rename(f.Value))).ToList()),
            _ => v,
        };

        var block = new StringBuilder();
        bool equip = item.ItemType == ItemKind.Weapon;
        if (equip) EquipCalls(data, item, newId, map, block, Rename);
        foreach (var c in item.Calls.Concat(item.Rows))
            block.Append("  ").Append(c.Path).Append('(').Append(string.Join(", ", c.Arguments.Select(a => WriteLua(Rename(a), "  ")))).Append(")\n");
        string src = LuaWriter.Module([$"A copy of \"{item.Title}\" ({item.Module}), made with Duplicate."], [block.ToString()]);
        // A script of the mod is never written over; a stale copy (removed, or undone) is.
        string module = Free($"/mod/modules/SVFM_{p.Id}_{newId}", m => data.Source.GameFiles.ContainsKey(m + ".lua")
            || (data.Copies.TryGetValue(m + ".lua", out var live) && p.Items.Contains(live))) + ".lua";

        var run = LuaSandbox.Run(src);
        var calls = run.Calls.Where(c => c.Path != Develop).ToList();
        var rows = run.Calls.Where(c => c.Path == Develop).ToList();
        if (run.Error != null || (!equip && calls.Count != item.Calls.Count) || rows.Count != item.Rows.Count)
            throw new InvalidOperationException($"The script of the copy of \"{item.Title}\" could not be read back: {run.Error ?? "its calls are not the item's calls"}.");
        var written = run.Calls.Select(c => c.Path).Distinct().ToDictionary(x => x, x => LuaSource.FindCalls(src, x));
        var newText = map.Values.ToHashSet(StringComparer.Ordinal);
        var temp = new ModProject { Id = p.Id, Name = p.Name };
        BuildItem(temp, data, data.Source, module, src, equip ? [] : calls, rows, item.ItemType, "Copy", key, written, _ => newId,
            newText.Contains, _ => null, langs, [], Stems(data.Source), EquipPackages([run]));
        var copy = (ExistingItem)temp.Items.Single();
        // shortcut: the copy binds only its own module, so its Stats tab shows the gun and not the shared parts until the project is opened again; upgrade when the owner wants the parts there.
        if (equip) BindWeapons(temp, [new ModuleRun(module, src, run, written)]);
        if (equip && !item.Support && !copy.StatGroups.Contains("Gun"))
            throw new InvalidOperationException($"The script of the copy of \"{item.Title}\" could not be read back as a gun.");
        if (item.Support)
        {
            if (!copy.Support || copy.Script == null) throw new InvalidOperationException($"The script of the copy of \"{item.Title}\" could not be read back as a thrown item.");
            // The build writes the copy's steps in a module of their own (the script differs from its original).
            copy.Script.CopyFrom(item.Script!);
        }

        foreach (var (from, to) in map)
            if (item.Text.TryGetValue(from, out var per))
                foreach (var (lang, t) in per) copy.SetText(to, lang, t);
        copy.Title = item.Title + " (copy)";
        if (copy.NameId != null && copy.Text.TryGetValue(copy.NameId, out var name) && name.TryGetValue("eng", out var eng)) copy.SetText(copy.NameId, "eng", eng + " (copy)");

        data.Modules[module] = src;
        data.Copies[module] = copy;
        return copy;
    }

    /// <summary>
    /// Writes the equip calls of a weapon or item again for its copy. Its equip constant and the constant its equip table
    /// row names (SWP or WP) are declared again with <c>_&lt;copy id&gt;</c> added. A thrown or placed item (#106) copies
    /// SetSupportWeaponParameter, the equip table row and SetSupportWeapon; the damage and blast rows stay shared. A gun
    /// (#113) copies SetGunBasic and the equip table row; its parts stay shared (.docs/Research_Weapon_Copy_Registration.md).
    /// </summary>
    private static void EquipCalls(ExistingModData data, ExistingItem item, string newId, Dictionary<string, string> map, StringBuilder block, Func<LuaValue, LuaValue> rename)
    {
        // Every module: a gun's SetGunBasic can be in a module that does not name its equip constant.
        var all = data.Modules.Values.Where(m => item.Support ? m.Contains(item.EquipId!.Replace("TppEquip.", ""), StringComparison.Ordinal) : m.Contains("V_TppEquip.", StringComparison.Ordinal))
            .SelectMany(m => LuaSandbox.Run(m).Calls).ToList();
        LuaValue.Table One(string path, string field, string id)
        {
            var found = all.Where(c => c.Path == path && c.Arguments.FirstOrDefault() is LuaValue.Table t && t[field] is LuaValue.Name n && n.Value == id).ToList();
            return found.Count == 1 ? (LuaValue.Table)found[0].Arguments[0]
                : throw new InvalidOperationException($"\"{item.Title}\" can't be duplicated: the script has {found.Count} {path} calls for {id}, not one.");
        }
        var rows = all.Where(c => c.Path.EndsWith(".AddToEquipIdTable", StringComparison.Ordinal) && c.Arguments.FirstOrDefault() is LuaValue.Table)
            .SelectMany(c => ((LuaValue.Table)c.Arguments[0]).Items.OfType<LuaValue.Table>())
            .Where(e => e.Items.Count >= 3 && e.Items[0] is LuaValue.Name eq && eq.Value == item.EquipId).ToList();
        if (rows.Count != 1 || rows[0].Items[2] is not LuaValue.Name swp)
            throw new InvalidOperationException($"\"{item.Title}\" can't be duplicated: the script has {rows.Count} equip table rows for it, not one.");
        string Bare(string c) => c.StartsWith("TppEquip.", StringComparison.Ordinal) ? c["TppEquip.".Length..] : c;
        map[item.EquipId!] = $"TppEquip.{Bare(item.EquipId!)}_{newId}";
        map[swp.Value] = $"TppEquip.{Bare(swp.Value)}_{newId}";
        block.Append($"  V_TppEquip.RegisterConstantEquipId({LuaLiteral.Quote(Bare(map[item.EquipId!]))})\n");
        if (!item.Support)
        {
            var gun = One("V_TppEquip.SetGunBasic", "weaponId", swp.Value);
            block.Append($"  V_TppEquip.DeclareWPs {{ {LuaLiteral.Quote(Bare(map[swp.Value]))} }}\n");
            block.Append("  V_TppEquip.SetGunBasic(").Append(WriteLua(rename(gun), "  ")).Append(")\n");
            block.Append("  V_TppEquip.AddToEquipIdTable(").Append(WriteLua(new LuaValue.Table([rename(rows[0])], []), "  ")).Append(")\n");
            return;
        }
        var parameters = One("V_TppEquip.SetSupportWeaponParameter", "swpId", swp.Value);
        var set = One("V_TppEquip.SetSupportWeapon", "equipId", item.EquipId!);
        block.Append($"  V_TppEquip.DeclareSWPs {{ {LuaLiteral.Quote(Bare(map[swp.Value]))} }}\n");
        block.Append("  V_TppEquip.SetSupportWeaponParameter(").Append(WriteLua(rename(parameters), "  ")).Append(")\n");
        block.Append("  V_TppEquip.AddToEquipIdTable(").Append(WriteLua(new LuaValue.Table([rename(rows[0])], []), "  ")).Append(")\n");
        block.Append("  V_TppEquip.SetSupportWeapon(").Append(WriteLua(rename(set), "  ")).Append(")\n");
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
