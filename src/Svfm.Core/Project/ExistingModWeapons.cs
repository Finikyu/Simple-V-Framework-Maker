using Svfm.Core.Formats;
using Svfm.Core.Knowledge;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

// The settings of an existing mod's weapons and buddy costumes (plan Phase 5): the same fields as the other items, found
// by following the weapon's constants from its R&D row to the Set* calls that make its parts.
public static partial class ExistingMod
{
    /// <summary>A module of the mod, run: its path, its text, what it called and where each call is written.</summary>
    private sealed record ModuleRun(string Path, string Src, SandboxResult Run, Dictionary<string, List<LuaCallSite>> Written);

    /// <summary>The part kinds in the order the Settings tab lists them, with the group name each gets.</summary>
    private static readonly (WeaponPartKind kind, string group)[] PartGroups =
    [
        (WeaponPartKind.Receiver, "Receiver"), (WeaponPartKind.Damage, "Damage"), (WeaponPartKind.Barrel, "Barrel"),
        (WeaponPartKind.Magazine, "Magazine"), (WeaponPartKind.Bullet, "Bullet"), (WeaponPartKind.Stock, "Stock"),
        (WeaponPartKind.MuzzleOption, "Muzzle"), (WeaponPartKind.Sight, "Sight"), (WeaponPartKind.Option, "Option"),
        (WeaponPartKind.UnderBarrel, "Under-barrel"),
    ];

    /// <summary>
    /// Gives each weapon item the settings of its gun and its parts. The weapon's equip constant (from its R&amp;D row) leads
    /// through AddToEquipIdTable to its weapon constant, that to its SetGunBasic call, and the gun's slots to the mod's own
    /// SetReceiver, SetMagazine and the other part calls (the receiver's attackId to SetDamage, the magazine's bulletId to
    /// SetBullet). Every plain value of those calls becomes a field; a value the script sets by a name (a part swap, a
    /// constant) is not listed. A part that several weapons use is one set of fields, shared by those items, so a change
    /// shows on each of them.
    /// </summary>
    /// <returns>The Set* calls whose values became fields of a weapon (the same objects as in the module runs).</returns>
    private static HashSet<RecordedCall> BindWeapons(ModProject project, List<ModuleRun> modules)
    {
        var read = new HashSet<RecordedCall>(ReferenceEqualityComparer.Instance);
        var weapons = project.Items.OfType<ExistingItem>().Where(i => i.ItemType == ItemKind.Weapon && i.EquipId != null).ToList();
        if (weapons.Count == 0) return read;

        var wpOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in modules.SelectMany(m => m.Run.Calls).Where(c => c.Path.EndsWith(".AddToEquipIdTable", StringComparison.Ordinal)))
            if (c.Arguments.FirstOrDefault() is LuaValue.Table t)
                foreach (var e in t.Items.OfType<LuaValue.Table>())
                    if (e.Items.Count >= 3 && e.Items[0] is LuaValue.Name eq && e.Items[2] is LuaValue.Name wp)
                        wpOf.TryAdd(eq.Value, wp.Value);

        // Every Set* call of the mod, by its call name and the constant it sets. A constant set twice is left out: which
        // of the two the game keeps is not the tool's guess.
        var made = new Dictionary<(string set, string id), (ModuleRun module, List<RecordedCall> calls)>();
        var twice = new HashSet<(string, string)>();
        string[] idFields = [.. PartGroups.Select(p => WeaponPartFields.Lua(p.kind).idField), "weaponId"];
        foreach (var m in modules)
            foreach (var calls in Registrations(m.Run.Calls.Where(c => c.Path.StartsWith("V_TppEquip.Set", StringComparison.Ordinal))))
            {
                if (calls.Any(c => c.Arguments.Count == 0 || c.Arguments[0] is not LuaValue.Table)) continue;
                string set = calls[0].Path["V_TppEquip.".Length..];
                string idField = set == "SetGunBasic" ? "weaponId" : SupportIds.TryGetValue(set, out var sid) ? sid
                    : PartGroups.Select(p => WeaponPartFields.Lua(p.kind)).FirstOrDefault(l => l.set == set).idField ?? "";
                if (idField.Length == 0) continue;
                foreach (var g in calls.GroupBy(c => ((LuaValue.Table)c.Arguments[0])[idField] is LuaValue.Name n ? n.Value : ""))
                {
                    if (g.Key.Length == 0) continue;
                    if (!made.TryAdd((set, g.Key), (m, g.ToList()))) twice.Add((set, g.Key));
                }
            }
        foreach (var k in twice) made.Remove(k);

        // The calls of each weapon: its gun, then its parts.
        var partsOf = new Dictionary<ExistingItem, List<(string group, string set, string id)>>();
        foreach (var item in weapons)
        {
            // A thrown or placed item (gap G6): its constant in the equip table is a support weapon, not a gun.
            if (made.ContainsKey(("SetSupportWeapon", item.EquipId!)) || wpOf.TryGetValue(item.EquipId!, out var swp) && made.ContainsKey(("SetSupportWeaponParameter", swp)))
            {
                BindSupport(item, wpOf.GetValueOrDefault(item.EquipId!), made, read);
                ReadScript(item, made, modules);
                continue;
            }
            if (!wpOf.TryGetValue(item.EquipId!, out var wp) || !made.TryGetValue(("SetGunBasic", wp), out var gun)) continue;
            var list = new List<(string, string, string)> { ("Gun", "SetGunBasic", wp) };
            var gunTable = (LuaValue.Table)gun.calls[0].Arguments[0];
            item.Scope1Id = gunTable["scope1Id"]?.ToString();
            item.Scope2Id = gunTable["scope2Id"]?.ToString();
            foreach (var slot in new[] { gunTable["scope1Id"], gunTable["scope2Id"] })
                if (slot is LuaValue.Name sight && made.TryGetValue(("SetSight", sight.Value), out var own))
                {
                    var row = new PartValues { Kind = WeaponPartKind.Sight, Id = EquipTables.Strip(sight.Value) };
                    foreach (var (key, v) in ((LuaValue.Table)own.calls[0].Arguments[0]).Fields) row.Values[key] = v;
                    item.SightRows[row.Id] = row;
                }
            foreach (var (kind, group) in PartGroups)
            {
                var (set, _, _, _, _) = WeaponPartFields.Lua(kind);
                LuaValue? id = kind switch
                {
                    WeaponPartKind.Damage => list.FirstOrDefault(p => p.Item2 == "SetReceiver") is { Item3: { } rc } && made.TryGetValue(("SetReceiver", rc), out var r)
                        ? ((LuaValue.Table)r.calls[0].Arguments[0])["attackId"] : null,
                    WeaponPartKind.Bullet => list.FirstOrDefault(p => p.Item2 == "SetMagazine") is { Item3: { } am } && made.TryGetValue(("SetMagazine", am), out var a)
                        ? ((LuaValue.Table)a.calls[0].Arguments[0])["bulletId"] : null,
                    _ => WeaponPartFields.Slot(kind) is { } slot ? gunTable[slot] : null,
                };
                if (id is LuaValue.Name n && made.ContainsKey((set, n.Value))) list.Add((group, set, n.Value));
            }
            partsOf[item] = list;
        }

        var users = partsOf.Values.SelectMany(l => l.Select(p => (p.set, p.id))).GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        var fieldsOf = new Dictionary<(string, string), (string name, List<ExistingField> fields, List<(string, string, string)> names)>();
        foreach (var (item, list) in partsOf)
            foreach (var (group, set, id) in list)
            {
                if (!fieldsOf.TryGetValue((set, id), out var part))
                {
                    var (module, calls) = made[(set, id)];
                    read.UnionWith(calls);
                    int n = users[(set, id)];
                    string name = n > 1 ? $"{group} · used by {n} weapons" : group;
                    IReadOnlyList<PartField> known = set == "SetGunBasic" ? [] : WeaponPartFields.For(PartGroups.First(p => WeaponPartFields.Lua(p.kind).set == set).kind);
                    var fields = new List<ExistingField>();
                    var names = new List<(string, string, string)>();
                    var args = (LuaValue.Table)calls[0].Arguments[0];
                    // A value set by a name (a part, a damage row, an ammo kind) is not a field: the tool can't offer the
                    // names that would fit, so it is shown only. The tool's own weapons show these the same way.
                    string ownId = set == "SetGunBasic" ? "weaponId" : WeaponPartFields.Lua(PartGroups.First(p => WeaponPartFields.Lua(p.kind).set == set).kind).idField;
                    foreach (var (key, v) in args.Fields)
                        if (key != ownId && v is LuaValue.Name { Value: not ("true" or "false" or "nil") } nv)
                            names.Add((name, known.FirstOrDefault(f => f.Path == key)?.Label ?? key, nv.Value));
                    foreach (var (path, value) in Leaves(args, []))
                    {
                        string dotted = string.Join(".", path);
                        if (path.Length == 1 && idFields.Contains(path[0])) continue;
                        var pf = known.FirstOrDefault(f => f.Path == dotted);
                        var type = pf?.Type == PartFieldType.Flag ? FieldType.Flag
                            : value is LuaValue.Text ? FieldType.Text : value is LuaValue.Name ? FieldType.Bool : FieldType.Number;
                        string label = pf?.Label ?? (dotted == "weaponGrade" ? "Weapon grade" : dotted);
                        fields.Add(MakeField(name, label, type, module.Path, calls, 0, path, module.Src, module.Written, writtenAsFlag: type == FieldType.Flag));
                    }
                    part = fieldsOf[(set, id)] = (name, fields, names);
                }
                item.Fields.AddRange(part.fields);
                item.PartNames.AddRange(part.names);
                if (part.fields.Count + part.names.Count > 0) item.StatGroups.Add(part.name);
            }
        return read;
    }

    /// <summary>The support weapon calls and the constant each one sets: they are found the same way as a gun's part calls.</summary>
    private static readonly Dictionary<string, string> SupportIds = new(StringComparer.Ordinal)
    {
        ["SetSupportWeaponParameter"] = "swpId", ["SetSupportWeapon"] = "equipId", ["SetBlastParameter"] = "blastId",
    };

    /// <summary>The labels of the support weapon keys that the tool writes for its own thrown items (LuaWriter.SupportWeaponBlock), with the group of each.</summary>
    private static readonly Dictionary<string, (string group, string label)> SupportKeys = new(StringComparer.Ordinal)
    {
        ["ammo"] = ("Thrown item", "Carried at once"), ["fuseTime"] = ("Thrown item", "Fuse time (seconds)"),
        ["activeTime"] = ("Thrown item", "Effect time (seconds)"), ["grade"] = ("Thrown item", "Grade"),
        ["damageType"] = ("Sortie Prep details", "Damage type icon (number)"),
        ["barDamage"] = ("Sortie Prep details", "Damage bar (0 empty, 1 full)"),
        ["barDurability"] = ("Sortie Prep details", "Durability bar (0 empty, 1 full)"),
        ["barEffectTime"] = ("Sortie Prep details", "Effect duration bar (0 empty, 1 full)"),
        ["durabilityMax"] = ("Sortie Prep details", "Durability, full bar"),
        ["effectTimeMax"] = ("Sortie Prep details", "Effect duration, full bar (seconds)"),
        ["throwAs"] = ("Thrown item", "Thrown as"), ["attackId"] = ("Thrown item", "Attack"), ["blastId"] = ("Thrown item", "Blast"),
        ["effectRange"] = ("Blast", "Radius for the player's items (metres)"), ["optimalRange"] = ("Blast", "Radius for others' (metres)"),
        ["blastFlag"] = ("Blast", "Noise kind"),
    };

    /// <summary>
    /// Gives a thrown or placed item the settings of its support weapon calls (gap G6): SetSupportWeaponParameter (found by
    /// the item's constant in the equip table), SetSupportWeapon (by its equip constant), and the SetDamage and
    /// SetBlastParameter rows that SetSupportWeapon names, when the mod makes them itself. A row of the game's own is not
    /// the mod's to change and is not listed. A value set by a name (what it is thrown as, a damage row) is shown only, and
    /// so is a Sortie Prep bar the script hides with false: the bar is a number or false, never true.
    /// </summary>
    /// <summary>Gives a thrown item its script, read from every module of the mod (row 14).</summary>
    private static void ReadScript(ExistingItem item, Dictionary<(string set, string id), (ModuleRun module, List<RecordedCall> calls)> made, List<ModuleRun> modules)
    {
        var t = made.TryGetValue(("SetSupportWeapon", item.EquipId!), out var sw) ? (LuaValue.Table)sw.calls[0].Arguments[0] : null;
        string? Name(string key) => t?[key] is LuaValue.Name n ? n.Value : null;
        string constant = item.EquipId!.StartsWith("TppEquip.", StringComparison.Ordinal) ? item.EquipId! : "TppEquip." + item.EquipId;
        item.ThrowAs = Name("throwAs") is { } ta ? ta.Replace("TppEquip.", "") : null;
        item.ScriptContext = new ThrowContext(constant, item.Title, Name("attackId"), Name("blastId"));
        var (script, why) = ThrowScriptReader.Read(modules.Select(m => m.Src).ToList(), item.ScriptContext!, item.ThrowAs);
        item.Script = script;
        item.ScriptOriginal = script.ToJson();
        item.ScriptLock = why;
    }

    private static void BindSupport(ExistingItem item, string? swp, Dictionary<(string set, string id), (ModuleRun module, List<RecordedCall> calls)> made, HashSet<RecordedCall> read)
    {
        void Bind(string set, string id, string? group, IReadOnlyList<PartField> known)
        {
            if (!made.TryGetValue((set, id), out var m)) return;
            var (module, calls) = m;
            read.UnionWith(calls);
            var args = (LuaValue.Table)calls[0].Arguments[0];
            string own = SupportIds.GetValueOrDefault(set, "attackId");
            string Group(string key) => group ?? SupportKeys.GetValueOrDefault(key, (group: "Thrown item", label: key)).group;
            string Label(string key) => known.FirstOrDefault(f => f.Path == key)?.Label ?? SupportKeys.GetValueOrDefault(key, (group: "", label: key)).label;
            foreach (var (key, v) in args.Fields)
                if (key != own && v is LuaValue.Name { Value: not ("true" or "nil") } nv && (nv.Value != "false" || key.StartsWith("bar", StringComparison.Ordinal)))
                    item.PartNames.Add((Group(key), Label(key), nv.Value == "false" ? "hidden" : nv.Value));
            foreach (var (path, value) in Leaves(args, []))
            {
                string dotted = string.Join(".", path);
                if (path.Length == 1 && path[0] == own) continue;
                if (path[0].StartsWith("bar", StringComparison.Ordinal) && value is LuaValue.Name) continue;
                var pf = known.FirstOrDefault(f => f.Path == dotted);
                var type = pf?.Type == PartFieldType.Flag ? FieldType.Flag
                    : value is LuaValue.Text ? FieldType.Text : value is LuaValue.Name ? FieldType.Bool : FieldType.Number;
                item.Fields.Add(MakeField(Group(dotted), Label(dotted), type, module.Path, calls, 0, path, module.Src, module.Written, writtenAsFlag: type == FieldType.Flag));
            }
        }

        if (swp != null) Bind("SetSupportWeaponParameter", swp, null, []);
        Bind("SetSupportWeapon", item.EquipId!, "Thrown item", []);
        if (made.TryGetValue(("SetSupportWeapon", item.EquipId!), out var sw))
        {
            var t = (LuaValue.Table)sw.calls[0].Arguments[0];
            if (t["attackId"] is LuaValue.Name atk) Bind("SetDamage", atk.Value, "Damage", WeaponPartFields.For(WeaponPartKind.Damage));
            if (t["blastId"] is LuaValue.Name bla) Bind("SetBlastParameter", bla.Value, "Blast", []);
        }
        foreach (var g in new[] { "Thrown item", "Sortie Prep details", "Damage", "Blast" })
            if (item.Fields.Any(f => f.Group == g) || item.PartNames.Any(n => n.Group == g)) item.StatGroups.Add(g);
        item.Support = true;
    }

    /// <summary>The plain values of a table (numbers, text, true and false), with their paths; keyed subtables are followed, lists are not.</summary>
    private static IEnumerable<(string[] path, LuaValue value)> Leaves(LuaValue.Table t, string[] at)
    {
        foreach (var (key, v) in t.Fields)
        {
            string[] path = [.. at, key];
            if (v is LuaValue.Number or LuaValue.Text or LuaValue.Name { Value: "true" or "false" }) yield return (path, v);
            else if (v is LuaValue.Table sub && sub.Items.Count == 0)
                foreach (var leaf in Leaves(sub, path)) yield return leaf;
        }
    }

    /// <summary>The behaviour keys of a buddy costume's branches that the tool writes for its own costumes (LuaWriter.BuddyBlock).</summary>
    private static readonly (string branch, string key, string label, FieldType type)[] BuddyKeys =
    [
        ("quiet", "bruise", "Quiet · bruises show", FieldType.Bool),
        ("dog", "bruise", "D-Dog · bruises show", FieldType.Bool),
        ("dog", "commandType", "D-Dog · command type (number)", FieldType.Number),
        ("dog", "damageRate", "D-Dog · damage taken ×", FieldType.Number),
        ("dog", "bulletFlinch", "D-Dog · flinches from bullets", FieldType.Bool),
        ("dog", "armorHitEffect", "D-Dog · armour hit effect", FieldType.Bool),
        ("dog", "armorHitSound", "D-Dog · armour hit sound", FieldType.Bool),
        ("dog", "escapeDistance", "D-Dog · escape distance", FieldType.Number),
        ("dog", "hitRetreatDistance", "D-Dog · retreat distance when hit", FieldType.Number),
        ("dog", "retargetRange", "D-Dog · retarget range", FieldType.Number),
        ("dog", "soldierThreat", "D-Dog · soldiers see it as a threat", FieldType.Bool),
        ("horse", "bruise", "D-Horse · bruises show", FieldType.Bool),
        ("horse", "damageRate", "D-Horse · damage taken ×", FieldType.Number),
        ("horse", "gallopSpeedRate", "D-Horse · gallop speed ×", FieldType.Number),
        ("horse", "bodyAttackId", "D-Horse · body attack (number)", FieldType.Number),
        ("horse", "soundType", "D-Horse · tack sound (0–6)", FieldType.Number),
    ];

    /// <summary>The behaviour settings of each buddy branch the costume's call has, in the group "Buddy behaviour".</summary>
    private static void BindBuddy(ExistingItem item, LuaValue.Table args, string module, List<RecordedCall> calls, string src, Dictionary<string, List<LuaCallSite>> written)
    {
        foreach (var (branch, key, label, type) in BuddyKeys)
            if (args[branch] is LuaValue.Table)
                item.Fields.Add(MakeField("Buddy behaviour", label, type, module, calls, 0, [branch, key], src, written, writtenAsFlag: false));
        if (item.Fields.Any(f => f.Group == "Buddy behaviour")) item.StatGroups.Add("Buddy behaviour");
    }
}
