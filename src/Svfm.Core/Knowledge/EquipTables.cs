using System.Text;
using Svfm.Core.Formats;
using Svfm.Core.VFramework;

namespace Svfm.Core.Knowledge;

/// <summary>
/// The game's weapon parameter tables, read from its Lua files: EquipParameters.lua (TppEquip.ReloadEquipParameterTables2:
/// gunBasic, the part rows and their parameter sets) and DamageParameterTables.lua (TppDamage.ReloadDamageParameter).
/// Each part comes out as <see cref="PartValues"/> with every field by name, so a copy of a vanilla weapon starts with the
/// vanilla values and any of them can be changed. Row layouts (Oct 26, base 00.dat): receiver {RC, ATK, base, wobbling,
/// system, sound} with 0-based indexes into the parameter sets; barrel {BA, base, length, scope, unk2, side, under};
/// magazine {AM, EQP_AM, capacity, carry, BL}; bullet {BL, speed, npcSpeed, drop, base, npcBase, trail, unk09, ricochet,
/// type, blast, lethal, eqpType}; stock {SK, recovery, sway}; muzzle option {MO, grouping, durability, suppressor};
/// sight {ST, zoom1–3, overlay, 5 flags}; option {LT/LS, light, laser}; under-barrel {UB, RC, AM, grade}; gunBasic
/// {WP, RC, BA, AM, SK, MZ, MO, ST, ST, UB, LT, LT, grade}; damage {ATK, 31 values}.
/// </summary>
public sealed class EquipTables
{
    public const string EquipParametersPath = "/Assets/tpp/level_asset/weapon/ParameterTables/parts/EquipParameters.lua";
    public const string DamageParametersPath = "/Assets/tpp/level_asset/damage/ParameterTables/DamageParameterTables.lua";

    /// <summary>The gunBasic slots in column order (column 1 is the weapon).</summary>
    public static readonly string[] GunBasicSlots = WeaponSlots.Columns.Select(s => s.Key).ToArray();

    private readonly Dictionary<string, LuaValue.Table> _gunBasic = new(StringComparer.Ordinal);
    private readonly Dictionary<(WeaponPartKind, string), PartValues> _parts = [];
    /// <summary>The tables as read (the ReloadEquipParameterTables2 argument and the DamageParameter rows), for overlays.</summary>
    private LuaValue.Table? _equipRoot;
    private List<LuaValue.Table> _damageRows = [];

    /// <summary>Reads the tables; <paramref name="read"/> gives a file's bytes by game path (null when absent). Null when
    /// neither file is there.</summary>
    public static EquipTables? Load(Func<string, byte[]?> read)
    {
        var equip = read(EquipParametersPath);
        var damage = read(DamageParametersPath);
        if (equip == null && damage == null) return null;
        var t = new EquipTables();
        if (equip != null) t.ReadEquip(LuaLiteral.CallArgument(Encoding.UTF8.GetString(equip), "TppEquip.ReloadEquipParameterTables2")
                                       ?? throw new FormatException("No ReloadEquipParameterTables2 call."));
        if (damage != null)
        {
            var root = LuaLiteral.CallArgument(Encoding.UTF8.GetString(damage), "TppDamage.ReloadDamageParameter") ?? throw new FormatException("No ReloadDamageParameter call.");
            t.ReadDamage(Rows(root["DamageParameter"] as LuaValue.Table).ToList());
        }
        return t;
    }

    /// <summary>The parameter-set lists: rows are addressed by their 0-based position (a part row names the index).</summary>
    public static readonly IReadOnlySet<string> SetSections = new HashSet<string>(StringComparer.Ordinal)
    {
        "receiverParamSetsBase", "receiverParamSetsWobbling", "receiverParamSetsSystem", "receiverParamSetsSound",
        "barrelParamSetsBase", "bulletParamSetsBase", "bulletTrailEffectList",
    };

    /// <summary>
    /// These tables with another set of rows laid over them, as Zeta applies a script's EquipParameters() and
    /// DamageParameterTables() to the game's tables: a part or gunBasic row replaces the row with the same ID (first value)
    /// or is added; a parameter-set row with <c>sIndex = n</c> replaces set n (0-based, the index part rows use), one
    /// without is added at the end; a damage row replaces the row with its attack ID or is added. The tables themselves
    /// are not changed.
    /// </summary>
    public EquipTables WithOverlay(LuaValue.Table? equip, IReadOnlyList<LuaValue.Table>? damage)
    {
        var t = new EquipTables();
        if (_equipRoot != null || equip != null)
        {
            var sections = new List<KeyValuePair<string, LuaValue>>();
            var names = (_equipRoot?.Fields.Select(f => f.Key) ?? []).Concat(equip?.Fields.Select(f => f.Key) ?? []).Distinct().ToList();
            foreach (var name in names)
            {
                var baseRows = (_equipRoot?[name] as LuaValue.Table)?.Items.ToList() ?? [];
                var over = (equip?[name] as LuaValue.Table)?.Items.OfType<LuaValue.Table>().ToList() ?? [];
                var rows = new List<LuaValue>(baseRows);
                foreach (var r in over)
                {
                    if (SetSections.Contains(name))
                    {
                        // Parameter sets have no leading flag: positions are the indexes.
                        if ((r["sIndex"]?.AsNumber ?? ZetaSetIndex(r["sIndex"])) is { } si && si >= 0 && si < rows.Count) rows[(int)si] = new LuaValue.Table(r.Items, []);
                        else rows.Add(new LuaValue.Table(r.Items, []));
                        continue;
                    }
                    if (r.Items.Count == 0) continue;
                    string id = Strip(r.Items[0].ToString()!);
                    int at = rows.FindIndex(x => x is LuaValue.Table xt && xt.Items.Count > 0 && Strip(xt.Items[0].ToString()!) == id);
                    if (at >= 0) rows[at] = Sparse(rows[at] as LuaValue.Table, r); else rows.Add(r);
                }
                sections.Add(new(name, new LuaValue.Table(rows, [])));
            }
            t.ReadEquip(new LuaValue.Table([], sections));
        }
        var dmg = new List<LuaValue.Table>(_damageRows);
        foreach (var r in damage ?? [])
        {
            if (r.Items.Count == 0) continue;
            string id = Strip(r.Items[0].ToString()!);
            int at = dmg.FindIndex(x => x.Items.Count > 0 && Strip(x.Items[0].ToString()!) == id);
            if (at >= 0) dmg[at] = Sparse(dmg[at], r); else dmg.Add(r);
        }
        t.ReadDamage(dmg);
        return t;
    }

    /// <summary>
    /// A row of a Zeta script laid over the game's row. A row with index keys changes only those columns:
    /// <c>{TppDamage.ATK_60001,[28]=1}</c> (Armor Piercing Sniper Rifles) sets column 28 (1-based, column 1 is the ID)
    /// and keeps the other columns of the game's row. A row with no index key replaces the game's row.
    /// </summary>
    // INFERENCE from the scripts: a row of 2 values cannot be a complete row, so Zeta must lay it over the game's row.
    private static LuaValue.Table Sparse(LuaValue.Table? game, LuaValue.Table script)
    {
        var keyed = script.Fields.Where(f => int.TryParse(f.Key, out int n) && n >= 1).ToList();
        if (game == null || keyed.Count == 0) return script;
        var items = game.Items.ToList();
        void Set(int index, LuaValue v) { while (items.Count <= index) items.Add(new LuaValue.Number(0)); items[index] = v; }
        for (int i = 0; i < script.Items.Count; i++) Set(i, script.Items[i]);
        foreach (var f in keyed) Set(int.Parse(f.Key) - 1, f.Value);
        return new LuaValue.Table(items, []);
    }

    /// <summary>Each part row of the tables (a copy), in table order: receivers, barrels, magazines, bullets, …, damage rows.</summary>
    public IEnumerable<PartValues> AllParts => _parts.Values.Select(p => p.Copy());

    /// <summary>
    /// The parameter set that a Zeta script names by its receiver: <c>sIndex = ZetaVar.RCSB(TppEquip.RC_30101)</c> (read
    /// by <see cref="ReadZeta"/> as the name ZetaVar.RCSB.RC_30101) is the receiverParamSetsBase index in the game's row
    /// of RC_30101; RCSW is the receiverParamSetsWobbling index. Null for any other value.
    /// </summary>
    // INFERENCE from the function names and the HK 33 script (the Zeta source is not in the test data).
    // shortcut: only RCSB and RCSW (the two that HK 33 uses); add a function when a script uses it.
    private double? ZetaSetIndex(LuaValue? sIndex)
    {
        if (sIndex is not LuaValue.Name { Value: var v } || v.Split('.') is not ["ZetaVar", var function, var receiver]) return null;
        int column = function switch { "RCSB" => 2, "RCSW" => 3, _ => 0 };
        var row = Rows(_equipRoot?["receiver"] as LuaValue.Table).FirstOrDefault(r => r.Items.Count > 3 && Strip(r.Items[0].ToString()!) == receiver);
        return column == 0 ? null : row?.Items[column].AsNumber;
    }

    /// <summary>
    /// The tables a Zeta weapon script hands Zeta (its EquipParameters() and DamageParameterTables() return values), read
    /// as data (no Lua is run). Null parts when the script has none.
    /// </summary>
    public static (LuaValue.Table? equip, List<LuaValue.Table> damage) ReadZeta(string lua)
    {
        // A call such as ZetaVar.RCSB(TppEquip.RC_30101) (HK 33) is not a literal: it becomes the name ZetaVar.RCSB.RC_30101.
        lua = System.Text.RegularExpressions.Regex.Replace(LuaLiteral.BlankComments(lua), @"ZetaVar\.(\w+)\(\s*(?:\w+\.)?(\w+)\s*\)", "ZetaVar.$1.$2");
        LuaValue.Table? After(string function)
        {
            int f = lua.IndexOf(function, StringComparison.Ordinal);
            if (f < 0) return null;
            int ret = lua.IndexOf("return", f, StringComparison.Ordinal);
            int open = ret < 0 ? -1 : lua.IndexOf('{', ret);
            return open < 0 ? null : LuaLiteral.Parse(lua[open..]) as LuaValue.Table;
        }
        var equip = After("function this.EquipParameters");
        var damage = After("function this.DamageParameterTables");
        return (equip, damage?.Items.OfType<LuaValue.Table>().ToList() ?? []);
    }

    public IReadOnlyCollection<string> Weapons => _gunBasic.Keys;

    /// <summary>A weapon's gunBasic row: slot → part constant without the table (RC_10101 …), plus "weaponGrade".</summary>
    public Dictionary<string, string>? GunBasic(string wp)
    {
        if (!_gunBasic.TryGetValue(Strip(wp), out var row)) return null;
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < GunBasicSlots.Length && i + 1 < row.Items.Count; i++) d[GunBasicSlots[i]] = Strip(row.Items[i + 1].ToString()!);
        // The game names an empty under-barrel slot UD_None; Zeta scripts write UB_None.
        if (d.GetValueOrDefault("underBarrelId") == "UB_None") d["underBarrelId"] = "UD_None";
        if (row.Items.Count > 12) d["weaponGrade"] = row.Items[12].ToString()!;
        return d;
    }

    public PartValues? Part(WeaponPartKind kind, string id) => _parts.TryGetValue((kind, Strip(id)), out var p) ? p.Copy() : null;

    /// <summary>A constant without its table: "TppEquip.RC_10101" → "RC_10101".</summary>
    public static string Strip(string s) => s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s;

    private static IEnumerable<LuaValue.Table> Rows(LuaValue.Table? section) =>
        section?.Items.OfType<LuaValue.Table>() ?? [];

    private static List<LuaValue.Table> Sets(LuaValue.Table root, string name) => Rows(root[name] as LuaValue.Table).ToList();

    private void ReadEquip(LuaValue.Table root)
    {
        _equipRoot = root;
        foreach (var row in Rows(root["gunBasic"] as LuaValue.Table)) _gunBasic[Strip(row.Items[0].ToString()!)] = row;

        var rcBase = Sets(root, "receiverParamSetsBase");
        var rcWob = Sets(root, "receiverParamSetsWobbling");
        var rcSys = Sets(root, "receiverParamSetsSystem");
        var rcSound = Sets(root, "receiverParamSetsSound");
        foreach (var r in Rows(root["receiver"] as LuaValue.Table))
        {
            var p = New(WeaponPartKind.Receiver, r);
            p.Values["attackId"] = r.Items[1];
            Fill(p, "receiverParamSetsBase", At(rcBase, r.Items[2]));
            Fill(p, "receiverParamSetsWobbling", At(rcWob, r.Items[3]));
            Fill(p, "receiverParamSetsSystem", At(rcSys, r.Items[4]));
            if (At(rcSound, r.Items[5]) is { Items.Count: > 0 } snd) p.Values["receiverParamSetsSound"] = snd.Items[0];
            // A copy plays the vanilla receiver's animations (V Framework: motionFrom).
            p.Values["motionFrom"] = r.Items[0];
            Add(p);
        }

        var baBase = Sets(root, "barrelParamSetsBase");
        foreach (var r in Rows(root["barrel"] as LuaValue.Table))
        {
            var p = New(WeaponPartKind.Barrel, r);
            Fill(p, "barrelParamSetsBase", At(baBase, r.Items[1]));
            Cols(p, r, 2, ["barrelLength", "hasScopeMount", "unk2", "hasSideMount", "hasUnderMount"]);
            Add(p);
        }
        foreach (var r in Rows(root["magazine"] as LuaValue.Table)) { var p = New(WeaponPartKind.Magazine, r); Cols(p, r, 1, ["equipAmmoId", "capacity", "totalCarry", "bulletId"]); Add(p); }

        var blBase = Sets(root, "bulletParamSetsBase");
        var trails = Sets(root, "bulletTrailEffectList");
        foreach (var r in Rows(root["bullet"] as LuaValue.Table))
        {
            var p = New(WeaponPartKind.Bullet, r);
            Cols(p, r, 1, ["bulletSpeed", "npcBulletSpeed", "dropRate"]);
            Fill(p, "bulletParamSetsBase", At(blBase, r.Items[4]));
            Fill(p, "npcBulletParamSetsBase", At(blBase, r.Items[5]));
            p.Values["bulletTrailEffect"] = At(trails, r.Items[6]) is { Items.Count: > 0 } tr ? tr.Items[0] : new LuaValue.Text("");
            Cols(p, r, 7, ["unk09", "ricochetSize", "bulletType", "blastId", "isLethal", "eqpType"]);
            Add(p);
        }
        foreach (var r in Rows(root["stock"] as LuaValue.Table)) { var p = New(WeaponPartKind.Stock, r); Cols(p, r, 1, ["spreadRecovery", "movementSway"]); Add(p); }
        foreach (var r in Rows(root["muzzleOption"] as LuaValue.Table)) { var p = New(WeaponPartKind.MuzzleOption, r); Cols(p, r, 1, ["grouping", "durability", "suppressor"]); Add(p); }
        foreach (var r in Rows(root["sight"] as LuaValue.Table))
        { var p = New(WeaponPartKind.Sight, r); Cols(p, r, 1, ["zoom1", "zoom2", "zoom3", "scopeUiId", "booster", "nvg", "builtIn", "rangeFinder", "rangeFinderBulletDrop"]); Add(p); }
        foreach (var r in Rows(root["option"] as LuaValue.Table)) { var p = New(WeaponPartKind.Option, r); Cols(p, r, 1, ["isLight", "isLaser"]); Add(p); }
        foreach (var r in Rows(root["underBarrel"] as LuaValue.Table)) { var p = New(WeaponPartKind.UnderBarrel, r); Cols(p, r, 1, ["receiverId", "magazineId", "underBarrelGrade"]); Add(p); }
    }

    private void ReadDamage(List<LuaValue.Table> rows)
    {
        _damageRows = rows;
        var names = WeaponPartFields.Damage.Select(f => f.Path).ToArray();
        foreach (var r in rows)
        {
            var p = New(WeaponPartKind.Damage, r);
            Cols(p, r, 1, names);
            Add(p);
        }
    }

    private static PartValues New(WeaponPartKind k, LuaValue.Table row) => new() { Kind = k, Id = Strip(row.Items[0].ToString()!) };

    private void Add(PartValues p) => _parts[(p.Kind, p.Id)] = p;

    private static LuaValue.Table? At(List<LuaValue.Table> sets, LuaValue index) =>
        index.AsNumber is { } n && n >= 0 && n < sets.Count ? sets[(int)n] : null;

    private static void Fill(PartValues p, string group, LuaValue.Table? set)
    {
        if (set == null) return;
        var fields = WeaponPartFields.For(p.Kind).Where(f => f.Group == group).ToList();
        for (int i = 0; i < fields.Count && i < set.Items.Count; i++) p.Values[fields[i].Path] = set.Items[i];
    }

    private static void Cols(PartValues p, LuaValue.Table row, int first, string[] names)
    {
        for (int i = 0; i < names.Length && first + i < row.Items.Count; i++) p.Values[names[i]] = row.Items[first + i];
    }
}
