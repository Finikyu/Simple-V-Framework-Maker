using System.Globalization;
using System.Text;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;

namespace Svfm.Core.VFramework;

/// <summary>Where a part's values came from: the vanilla tables, the mod's own copy of a table, or the user.</summary>
public enum PartSource { Vanilla, Mod, User }

/// <summary>
/// One part of a weapon copy: the vanilla part it starts from, and the values the copy uses. A part whose values equal the
/// vanilla ones is used as the vanilla part (its ID in SetGunBasic); a changed one becomes the copy's own part (Declare +
/// Set* with every field written out), so the vanilla weapon and every other weapon using the part stay as they are.
/// </summary>
public sealed class PartState
{
    public required PartValues Vanilla { get; init; }
    public required PartValues Values { get; init; }
    public PartSource Source { get; set; } = PartSource.Vanilla;
    /// <summary>Own part because a part it points at is the copy's own (a changed damage row needs its own receiver).</summary>
    public bool Dependent { get; set; }
    /// <summary>The copy's own constant, set when it is written as its own part.</summary>
    public string? Constant { get; set; }
    public List<string> Changed => Values.Differences(Vanilla);
    public bool Own => Dependent || Changed.Count > 0;
}

/// <summary>
/// The editable stats of a weapon copy (V Framework 1.7 Custom_Weapons: SetDamage, SetReceiver, SetBarrel, SetMagazine,
/// SetBullet, SetStock, SetMuzzle, SetOption, SetUnderBarrel), filled from the game's tables for the weapon it copies,
/// then from the mod's own copy of those tables (the mod as installed), then from the user's edits. Only copies are
/// made: the vanilla weapon's rows are never written. A sight keeps its vanilla values (a custom sight needs the vanilla
/// sight's UI files in its package, or the scope draws nothing: not handled yet), but the swappable slots (SwapSlots) can
/// take another vanilla part that the game allows for the receiver (WeaponPartChoices).
/// </summary>
public sealed class WeaponTuning
{
    public Dictionary<WeaponPartKind, PartState> Parts { get; } = [];
    /// <summary>The SetGunBasic slots of the weapon copied (constants without the table), weaponGrade included.</summary>
    public Dictionary<string, string> GunBasic { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = [];
    /// <summary>The weapon copied (WP_…).</summary>
    public string Weapon { get; private set; } = "";
    /// <summary>A fixed camo ("c00" to "c94") painted into the model at build time (WeaponConverter.Current); null = the
    /// default paint. Held here so that undo, the project file, Compare and Reset carry it with the other Stats-tab values.</summary>
    public string? Camo { get; set; }

    /// <summary>
    /// The tuning of a copy of <paramref name="weapon"/> (WP_…): its parts as the game has them (<paramref name="vanilla"/>),
    /// the mod's versions where it ships its own tables (<paramref name="mod"/>), then <paramref name="edits"/> (part kind →
    /// field path → value).
    /// </summary>
    public static WeaponTuning For(string weapon, EquipTables vanilla, EquipTables? mod,
        IReadOnlyDictionary<WeaponPartKind, Dictionary<string, LuaValue>>? edits = null, IReadOnlyDictionary<string, string>? slots = null)
    {
        var t = new WeaponTuning { Weapon = weapon, _vanilla = vanilla, _mod = mod };
        var source = mod?.GunBasic(weapon) ?? vanilla.GunBasic(weapon) ?? throw new ArgumentException($"{weapon} is not in the game's gunBasic table.");
        foreach (var (k, v) in source) t.GunBasic[k] = v;
        foreach (var (k, v) in slots ?? new Dictionary<string, string>()) t.GunBasic[k] = v;
        var gb = t.GunBasic;
        if (mod?.GunBasic(weapon) is { } mg && vanilla.GunBasic(weapon) is { } vg && mg.Any(kv => vg.GetValueOrDefault(kv.Key) != kv.Value))
            t.Notes.Add($"The mod changes {weapon}'s parts ({string.Join(", ", mg.Where(kv => vg.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => $"{kv.Key} {vg.GetValueOrDefault(kv.Key)} → {kv.Value}"))}); the copy uses the mod's.");

        void Take(WeaponPartKind kind, string? id) => t.Take(kind, id);
        Take(WeaponPartKind.Receiver, gb.GetValueOrDefault("receiverId"));
        if (t.Parts.TryGetValue(WeaponPartKind.Receiver, out var rc) && rc.Values.Values.GetValueOrDefault("attackId") is { } atk)
            Take(WeaponPartKind.Damage, EquipTables.Strip(atk.ToString()!));
        Take(WeaponPartKind.Barrel, gb.GetValueOrDefault("barrelId"));
        Take(WeaponPartKind.Magazine, gb.GetValueOrDefault("ammoId"));
        if (t.Parts.TryGetValue(WeaponPartKind.Magazine, out var am) && am.Values.Values.GetValueOrDefault("bulletId") is { } bl)
            Take(WeaponPartKind.Bullet, EquipTables.Strip(bl.ToString()!));
        Take(WeaponPartKind.Stock, gb.GetValueOrDefault("stockId"));
        Take(WeaponPartKind.MuzzleOption, gb.GetValueOrDefault("muzzleOptionId"));
        foreach (var kind in new[] { WeaponPartKind.Sight, WeaponPartKind.UnderBarrel, WeaponPartKind.Option }) Take(kind, t.SlotPart(kind));

        foreach (var (kind, fields) in edits ?? new Dictionary<WeaponPartKind, Dictionary<string, LuaValue>>())
        {
            if (!t.Parts.TryGetValue(kind, out var st)) { t.Notes.Add($"The copied weapon has no {kind}; the edits to it are ignored."); continue; }
            var known = WeaponPartFields.For(kind).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var (path, value) in fields)
            {
                if (!known.Contains(path)) throw new ArgumentException($"{kind} has no field {path}.");
                st.Values.Values[path] = value;
            }
            if (st.Changed.Count > 0) st.Source = PartSource.User;
        }
        return t;
    }

    private int? _declStart;
    private WeaponSpec? _spec;
    private string? _id;
    private EquipTables? _vanilla, _mod;

    private void Take(WeaponPartKind kind, string? id)
    {
        Parts.Remove(kind);
        if (id == null || id.EndsWith("_None", StringComparison.Ordinal) || _vanilla == null) return;
        var van = _vanilla.Part(kind, id);
        var mine = _mod?.Part(kind, id);
        if (van == null && mine == null) { Notes.Add($"{id}: not in the game's tables; used as it is."); return; }
        var state = new PartState { Vanilla = van ?? mine!, Values = (mine ?? van)!.Copy() };
        if (mine != null && van != null && mine.Differences(van).Count > 0) state.Source = PartSource.Mod;
        Parts[kind] = state;
    }

    /// <summary>The slots whose part the user can swap for another part the game allows (WeaponPartChoices). Only the
    /// sights: a V Framework custom weapon draws no separate attachment model (in game, Nov 10), so a sight still gives
    /// its zoom, but a flashlight, laser or under-barrel does nothing. Mods such as Eft Gunpack model those into the gun.</summary>
    public static readonly string[] SwapSlots = WeaponSlots.Swappable;

    /// <summary>The part in the slots that give <paramref name="kind"/> its values: Option is the first flashlight or laser.</summary>
    private string? SlotPart(WeaponPartKind kind) => kind switch
    {
        WeaponPartKind.Sight => GunBasic.GetValueOrDefault("scope1Id"),
        WeaponPartKind.UnderBarrel => GunBasic.GetValueOrDefault("underBarrelId"),
        WeaponPartKind.Option => WeaponSlots.Lasers.Select(GunBasic.GetValueOrDefault).FirstOrDefault(p => p != null && !p.EndsWith("_None", StringComparison.Ordinal)),
        _ => null,
    };

    /// <summary>
    /// Puts <paramref name="part"/> in a swappable slot. The part whose values the slot gives (sight, option, under-barrel)
    /// starts again from the new part's values when the part changes; the other parts keep their edits. The spec follows.
    /// </summary>
    public void SetSlot(string slot, string part) { PutSlot(slot, part); Reapply(); }

    /// <summary>Sets one value of a part the weapon has. The spec follows.</summary>
    public void SetValue(WeaponPartKind kind, string path, LuaValue value)
    {
        if (!Parts.TryGetValue(kind, out var st)) return;
        st.Values.Values[path] = value;
        Reapply();
    }

    private void PutSlot(string slot, string part)
    {
        if (!SwapSlots.Contains(slot)) throw new ArgumentException($"{slot} is not a slot that can be swapped.");
        var before = Parts.Keys.ToDictionary(k => k, SlotPart);
        GunBasic[slot] = part;
        foreach (var kind in new[] { WeaponPartKind.Sight, WeaponPartKind.UnderBarrel, WeaponPartKind.Option })
            if (SlotPart(kind) is var now && (before.TryGetValue(kind, out var was) ? was != now : now != null)) Take(kind, now);
    }

    /// <summary>
    /// Writes the tuning into the spec again after a value was edited: the copy's own parts are worked out afresh (a part set
    /// back to the vanilla values is the vanilla part again) and the spec's Declare calls, slots and Lua follow. The spec
    /// is the one that <see cref="Apply"/> wrote; before the first Apply there is no spec and nothing is written.
    /// </summary>
    private void Reapply()
    {
        if (_spec is not { } spec || _id is not { } id) return;
        if (_declStart is { } n && spec.Declarations.Count > n) spec.Declarations.RemoveRange(n, spec.Declarations.Count - n);
        foreach (var st in Parts.Values)
        {
            st.Dependent = false; st.Constant = null;
            foreach (var link in new[] { "attackId", "bulletId" })
                if (st.Values.Values.TryGetValue(link, out var cur) && cur.ToString()!.Contains("_SVFM_", StringComparison.Ordinal))
                    st.Values.Values[link] = st.Vanilla.Values.GetValueOrDefault(link) ?? cur;
            st.Source = st.Changed.Count > 0 ? PartSource.User : PartSource.Vanilla;
        }
        spec.TuningLua = Apply(spec, id);
    }

    /// <summary>The snapshot key prefix of a swapped slot ("Slot|scope1Id").</summary>
    public const string SlotKey = "Slot";

    /// <summary>The snapshot key of the camo ("Camo|camo"); absent = the default paint.</summary>
    public const string CamoKey = "Camo|camo";

    /// <summary>True for "c00" to "c94" (the game's fova/color/wfv_camo_cNN packages).</summary>
    public static bool IsCamo(string? camo) => camo != null && System.Text.RegularExpressions.Regex.IsMatch(camo, "^c[0-9][0-9]$") && string.CompareOrdinal(camo, "c94") <= 0;

    /// <summary>Every field of every part as Lua text, keyed "Kind|path": for undo, comparing and saving.</summary>
    public Dictionary<string, string> Snapshot()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, st) in Parts) foreach (var (path, v) in st.Values.Values) d[$"{kind}|{path}"] = v.ToString()!;
        foreach (var slot in SwapSlots) if (GunBasic.TryGetValue(slot, out var p)) d[$"{SlotKey}|{slot}"] = p;
        if (Camo != null) d[CamoKey] = Camo;
        return d;
    }

    /// <summary>Puts values back from a <see cref="Snapshot"/>. The spec follows. Bad text (a hand-edited project file)
    /// throws InvalidDataException before any value changes.</summary>
    public void Restore(Dictionary<string, string> snapshot)
    {
        var slots = snapshot.Where(kv => kv.Key.StartsWith(SlotKey + "|", StringComparison.Ordinal) && SwapSlots.Contains(kv.Key[(SlotKey.Length + 1)..])).ToList();
        foreach (var (key, part) in slots)
            if (part == null || !System.Text.RegularExpressions.Regex.IsMatch(part, @"^\w+$")) throw new InvalidDataException($"Bad part \"{part}\" for {key}.");
        string? camo = snapshot.GetValueOrDefault(CamoKey);
        if (camo != null && !IsCamo(camo)) throw new InvalidDataException($"Bad camo \"{camo}\": not one of c00 to c94.");
        foreach (var (key, part) in slots) PutSlot(key[(SlotKey.Length + 1)..], part);
        Camo = camo;
        var put = new List<(PartState St, string Path, LuaValue V)>();
        foreach (var (key, text) in snapshot)
        {
            int bar = key.IndexOf('|');
            if (bar < 0 || !Enum.TryParse<WeaponPartKind>(key[..bar], out var kind) || !Parts.TryGetValue(kind, out var st)) continue;
            put.Add((st, key[(bar + 1)..], (text == null ? null : LuaLiteral.TryParse(text)) ?? throw new InvalidDataException($"Bad weapon value \"{text}\" for {key}.")));
        }
        foreach (var (st, path, v) in put) st.Values.Values[path] = v;
        Reapply();
    }

    /// <summary>
    /// Writes the tuning into <paramref name="spec"/>: SetGunBasic slots, the Declare calls and the Set* calls of the copy's
    /// own parts (constants <c>{prefix}_SVFM_{id}</c>, e.g. RC_SVFM_Banana). Returns the Lua for <see cref="WeaponSpec.TuningLua"/>.
    /// </summary>
    public string Apply(WeaponSpec spec, string id)
    {
        _declStart ??= spec.Declarations.Count;
        _spec = spec; _id = id;
        foreach (var slot in EquipTables.GunBasicSlots)
            if (GunBasic.TryGetValue(slot, out var v)) spec.Parts[slot] = v;
        if (GunBasic.TryGetValue("weaponGrade", out var g) && int.TryParse(g, NumberStyles.Integer, CultureInfo.InvariantCulture, out int grade)) spec.WeaponGrade = grade;

        if (Parts.TryGetValue(WeaponPartKind.Sight, out var st) && st.Changed.Count > 0)
        {
            Notes.Add($"Sight changes ({string.Join(", ", st.Changed)}) are not ported yet: a custom sight needs the vanilla sight's UI files in its package. The copy keeps {st.Vanilla.Id}.");
            st.Values.Values.Clear();
            foreach (var (k, v) in st.Vanilla.Values) st.Values.Values[k] = v;
        }
        // The receiver's weapon family stays the borrowed receiver's: motionFrom plays that receiver's animations, which
        // fit its own family only (wiki: "use the same weapon family"). Fulton Gun 0.4c: the script's submachine-gun family
        // on a handgun receiver broke the player's firing animation; 0.5, the handgun family kept, fires correctly.
        if (Parts.TryGetValue(WeaponPartKind.Receiver, out var rcv) && rcv.Values.Values.GetValueOrDefault("receiverParamSetsSystem.eqpType") is { } fam
            && rcv.Vanilla.Values.GetValueOrDefault("receiverParamSetsSystem.eqpType") is { } vfam && fam.ToString() != vfam.ToString())
        {
            Notes.Add($"The receiver's weapon family stays {vfam} (asked: {fam}): the copy plays {rcv.Vanilla.Id}'s animations, which only fit its own family (the firing animation broke otherwise).");
            rcv.Values.Values["receiverParamSetsSystem.eqpType"] = vfam;
        }
        // A changed damage row needs its own receiver to point at it; a changed bullet its own magazine.
        if (Parts.TryGetValue(WeaponPartKind.Damage, out var dm) && dm.Own && Parts.TryGetValue(WeaponPartKind.Receiver, out var rc)) rc.Dependent = true;
        if (Parts.TryGetValue(WeaponPartKind.Bullet, out var bu) && bu.Own && Parts.TryGetValue(WeaponPartKind.Magazine, out var am)) am.Dependent = true;

        var w = new StringBuilder();
        string Own(WeaponPartKind k, PartState s)
        {
            var (_, _, _, _, prefix) = WeaponPartFields.Lua(k);
            if (k == WeaponPartKind.Option) prefix = s.Values.Values.GetValueOrDefault("isLaser")?.AsNumber is > 0 ? "LS_" : "LT_";
            return $"{prefix}SVFM_{id}";
        }
        foreach (var kind in new[] { WeaponPartKind.Damage, WeaponPartKind.Bullet, WeaponPartKind.Magazine, WeaponPartKind.Receiver, WeaponPartKind.Barrel,
                                     WeaponPartKind.Stock, WeaponPartKind.MuzzleOption, WeaponPartKind.Option, WeaponPartKind.UnderBarrel })
        {
            if (!Parts.TryGetValue(kind, out var s) || !s.Own) continue;
            s.Constant = Own(kind, s);
            var (_, _, declare, _, _) = WeaponPartFields.Lua(kind);
            spec.Declarations.Add((declare, s.Constant));
            // Point at the copy's own damage row and bullet.
            if (kind == WeaponPartKind.Receiver && Parts.TryGetValue(WeaponPartKind.Damage, out var d) && d.Constant != null)
                s.Values.Values["attackId"] = new LuaValue.Name($"TppDamage.{d.Constant}");
            if (kind == WeaponPartKind.Magazine && Parts.TryGetValue(WeaponPartKind.Bullet, out var b) && b.Constant != null)
                s.Values.Values["bulletId"] = new LuaValue.Name($"TppEquip.{b.Constant}");
            string why = s.Dependent && s.Changed.Count == 0 ? "points at the copy's own part" : $"{s.Source.ToString().ToLowerInvariant()} values: {string.Join(", ", s.Changed)}";
            w.Append($"  -- {s.Constant}: copy of {s.Vanilla.Id} ({why}).\n");
            w.Append(s.Values.SetCall(s.Constant)).Append('\n');
            if (WeaponPartFields.Slot(kind) is { } slot) spec.Parts[slot] = s.Constant;
            if (kind == WeaponPartKind.Option)
                foreach (var lt in WeaponSlots.Lasers)
                    if (spec.Parts.GetValueOrDefault(lt) == s.Vanilla.Id) { spec.Parts[lt] = s.Constant; break; }
        }
        return w.ToString();
    }
}
