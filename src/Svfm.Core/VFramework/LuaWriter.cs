using System.Globalization;
using System.Text;

namespace Svfm.Core.VFramework;

/// <summary>
/// Writes the V Framework 1.7 Infinite Heaven module for an outfit: RegisterOutfit plus its
/// AddToEquipDevelopTable row, inside this.LoadLibraries, guarded so the module does nothing
/// when V Framework is not installed. Field names follow the 1.7 wiki (Custom_Outfits).
/// </summary>
public static class LuaWriter
{
    /// <summary>A whole module for one outfit (the single-item case).</summary>
    public static string Write(OutfitSpec o) => Module(o.Header, [OutfitBlock(o)]);

    /// <summary>The text on one line, so that it can not end a Lua comment early.</summary>
    static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>
    /// A whole Infinite Heaven module: the header comments, then every item's block inside one
    /// this.LoadLibraries, which does nothing when V Framework is not installed. Each block is its own
    /// <c>do … end</c> scope, so items can use the same local names.
    /// </summary>
    public static string Module(IEnumerable<string> header, IReadOnlyList<string> blocks,
        IReadOnlyDictionary<string, int>? followVanillaDevelop = null,
        IReadOnlyList<MessageHandler>? messages = null, IReadOnlyList<string>? updates = null, IReadOnlyList<string>? inits = null,
        bool diagnoseMessages = false, IReadOnlyList<string>? helpers = null)
    {
        messages ??= [];
        updates ??= [];
        inits ??= [];
        bool follow = followVanillaDevelop is { Count: > 0 };
        var w = new StringBuilder();
        foreach (var line in header) w.Append("-- ").Append(OneLine(line)).Append('\n');
        w.Append("-- Made with Simple V Framework Maker. Needs Infinite Heaven, IHHook and V Framework 1.7.\n");
        w.Append("local this = {}\n\n");
        if (followVanillaDevelop is { Count: > 0 })
            WriteFollowVanillaDevelop(w, followVanillaDevelop);
        w.Append("function this.LoadLibraries()\n");
        w.Append("  if not (V_Player and V_TppMotherBaseManagement) then return end\n");
        foreach (var block in blocks)
        {
            w.Append('\n');
            if (blocks.Count == 1) { w.Append(block); continue; }
            w.Append("  do\n");
            foreach (var line in block.TrimEnd('\n').Split('\n')) w.Append(line.Length > 0 ? "  " + line : line).Append('\n');
            w.Append("  end\n");
        }
        w.Append("end\n");
        foreach (var h in (helpers ?? []).Distinct()) w.Append('\n').Append(h);
        if (messages.Count > 0)
        {
            // Messages (V Framework queues Throwing messages to modules): one entry per message, running every item's handler.
            w.Append("\nfunction this.Messages()\n  return Tpp.StrCode32Table{\n");
            foreach (var cls in messages.GroupBy(m => m.Class))
            {
                w.Append($"    {cls.Key} = {{\n");
                foreach (var msg in cls.GroupBy(m => (m.Message, m.Parameters)))
                {
                    w.Append($"      {{ msg = {Str(msg.Key.Message)}, func = function({msg.Key.Parameters})\n");
                    foreach (var h in msg) w.Append("        do\n").Append(Indent(h.Body, "          ")).Append("        end\n");
                    w.Append("      end },\n");
                }
                w.Append("    },\n");
            }
            w.Append("  }\nend\n");
        }
        // Infinite Heaven calls these after the save is loaded (InfMain CallOnModules "Init" / "OnMissionCanStart").
        if (follow || messages.Count > 0 || inits.Count > 0)
        {
            w.Append("\nfunction this.Init(missionTable)\n");
            if (follow) w.Append("  this.FollowVanillaDevelop(false)\n");
            foreach (var i in inits) w.Append(Indent(i, "  "));
            if (messages.Count > 0) w.Append("  this.messageExecTable = Tpp.MakeMessageExecTable(this.Messages())\n");
            if (diagnoseMessages) w.Append("  if InfCore then InfCore.Log(\"SVFM: Init, messageExecTable \" .. tostring(this.messageExecTable), false, true) end\n");
            w.Append("end\n");
        }
        if (follow) w.Append("\nfunction this.OnMissionCanStart()\n  this.FollowVanillaDevelop(false)\nend\n");
        if (messages.Count > 0)
        {
            w.Append("\nfunction this.OnReload(missionTable)\n  this.messageExecTable = Tpp.MakeMessageExecTable(this.Messages())\nend\n");
            w.Append("\nfunction this.OnMessage(sender, messageId, arg0, arg1, arg2, arg3, strLogText)\n");
            // Testing: every Throwing message the module gets is announced, before anything can drop it.
            if (diagnoseMessages)
            {
                w.Append("  this.svfmSeen = (this.svfmSeen or 0) + 1\n  if this.svfmSeen <= 5 and InfCore then InfCore.Log(\"SVFM: OnMessage \" .. tostring(sender) .. \" \" .. tostring(messageId), false, true) end\n");
                w.Append("  if sender == Fox.StrCode32(\"Throwing\") then\n    TppUiCommand.AnnounceLogView(\"SVFM: Throwing message \" .. tostring(messageId) .. \", table \" .. tostring(this.messageExecTable ~= nil))\n    if InfCore then InfCore.Log(\"SVFM: Throwing message \" .. tostring(messageId) .. \" \" .. tostring(arg0) .. \" \" .. tostring(arg1), false, true) end\n  end\n");
            }
            w.Append("  if not this.messageExecTable then return end\n");
            w.Append("  Tpp.DoMessage(this.messageExecTable, TppMission.CheckMessageOption, sender, messageId, arg0, arg1, arg2, arg3, strLogText)\nend\n");
        }
        if (updates.Count > 0)
        {
            // Infinite Heaven runs a module's Update every frame in game (InfMain.Update → ExecUpdate).
            w.Append("\nfunction this.Update(currentChecks, currentTime, execChecks, execState)\n");
            foreach (var u in updates) w.Append("  do\n").Append(Indent(u, "    ")).Append("  end\n");
            w.Append("end\n");
        }
        w.Append("\nreturn this\n");
        return w.ToString();
    }

    /// <summary>
    /// Rows that stand in for vanilla items copy the vanilla development state (V_TppMotherBaseManagement.GetDevelopId,
    /// IsEquipDeveloped, SetEquipDeveloped/SetEquipUndeveloped; vanilla state from TppMotherBaseManagement).
    /// From the R&amp;D gate it only ever develops (that check can run before the save is loaded, when every vanilla
    /// row reads as undeveloped); from Init/OnMissionCanStart it copies both ways.
    /// </summary>
    private static void WriteFollowVanillaDevelop(StringBuilder w, IReadOnlyDictionary<string, int> follow)
    {
        w.Append("-- Rows that follow a vanilla R&D row: never shown in R&D, developed when the vanilla one is.\n");
        w.Append("local FOLLOW_VANILLA = {\n");
        foreach (var (key, id) in follow.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            w.Append($"  [{Str(key)}] = {id},\n");
        w.Append("}\n\n");
        w.Append("function this.FollowVanillaDevelop(developOnly)\n");
        w.Append("  local mb = V_TppMotherBaseManagement\n");
        w.Append("  if not (mb and mb.GetDevelopId and mb.IsEquipDeveloped and mb.SetEquipDeveloped and TppMotherBaseManagement) then return end\n");
        w.Append("  for key, vanillaId in pairs(FOLLOW_VANILLA) do\n");
        w.Append("    local id = mb.GetDevelopId(key)\n");
        w.Append("    if id then\n");
        w.Append("      local want = TppMotherBaseManagement.IsEquipDevelopedFromDevelopID{ equipDevelopID = vanillaId } and true or false\n");
        w.Append("      if want ~= mb.IsEquipDeveloped(id) then\n");
        w.Append("        if want then mb.SetEquipDeveloped(id)\n");
        w.Append("        elseif not developOnly and mb.SetEquipUndeveloped then mb.SetEquipUndeveloped(id) end\n");
        w.Append("      end\n");
        w.Append("    end\n");
        w.Append("  end\n");
        w.Append("end\n\n");
    }

    private static string Indent(string lua, string by) =>
        string.Concat(lua.TrimEnd('\n').Split('\n').Select(l => (l.Length > 0 ? by + l : l) + "\n"));

    /// <summary>A custom support weapon: IDs, stats, equip row, throwAs and its R&amp;D row, indented for LoadLibraries.</summary>
    public static string SupportWeaponBlock(SupportWeaponSpec s)
    {
        var w = new StringBuilder();
        foreach (var line in s.Header) w.Append("  -- ").Append(OneLine(line)).Append('\n');
        if (s.Script.Summary() is { } summary) w.Append("  -- ").Append(OneLine(summary)).Append('\n');
        w.Append($"  V_TppEquip.RegisterConstantEquipId({Str(s.EquipConstant)})\n");
        w.Append($"  V_TppEquip.DeclareSWPs {{ {Str(s.SwpConstant)} }}\n");
        if (s.OwnAttackConstant is { } atk) w.Append($"  V_TppEquip.DeclareDamages {{ {Str(atk)} }}\n");
        if (s.OwnBlastConstant is { } bla) w.Append($"  V_TppEquip.DeclareBLAs {{ {Str(bla)} }}\n");
        w.Append('\n');
        var stats = new List<string> { $"swpId = TppEquip.{s.SwpConstant}", $"ammo = {s.Ammo}", $"fuseTime = {Num(s.FuseTime)}", $"activeTime = {Num(s.ActiveTime)}", $"grade = {s.Grade}" };
        if (s.DamageType is { } dt) stats.Add($"damageType = {dt}");
        void Bar(string name, double? fill, bool hidden) { if (hidden) stats.Add($"{name} = false"); else if (fill is { } f) stats.Add($"{name} = {Num(f)}"); }
        Bar("barDamage", s.BarDamage, s.HideBarDamage);
        Bar("barDurability", s.BarDurability, s.HideBarDurability);
        Bar("barEffectTime", s.BarEffectTime, s.HideBarEffectTime);
        if (s.DurabilityMax is { } dm) stats.Add($"durabilityMax = {Num(dm)}");
        if (s.EffectTimeMax is { } em) stats.Add($"effectTimeMax = {Num(em)}");
        w.Append($"  V_TppEquip.SetSupportWeaponParameter{{ {string.Join(", ", stats)} }}\n\n");
        if (s.OwnAttack is { } oa)
            w.Append($"  -- {s.OwnAttackConstant}: copy of {oa.Id}.\n").Append(oa.SetCall(s.OwnAttackConstant!)).Append('\n');
        if (s.OwnBlast is { } ob)
            w.Append($"  V_TppEquip.SetBlastParameter{{ blastId = TppEquip.{s.OwnBlastConstant}, effectRange = {Num(ob.EffectRange)}, optimalRange = {Num(ob.OptimalRange)}, blastFlag = {ob.BlastFlag} }}\n\n");
        w.Append("  V_TppEquip.AddToEquipIdTable{\n");
        w.Append($"    {{ TppEquip.{s.EquipConstant}, TppEquip.EQP_TYPE_{s.EquipType}, TppEquip.{s.SwpConstant}, TppEquip.{s.Block},\n");
        w.Append($"      {Str(s.PartsPath)}, {Str(s.FpkPath)} }},\n");
        w.Append("  }\n\n");
        w.Append($"  V_TppEquip.SetSupportWeapon{{ equipId = TppEquip.{s.EquipConstant}, throwAs = TppEquip.{s.ThrowAs}" +
                 (s.Attack is { } at ? $", attackId = {at}" : "") + (s.Blast is { } bl ? $", blastId = {bl}" : "") + " }\n\n");
        w.Append($"  V_TppMotherBaseManagement.AddToEquipDevelopTable({Str(s.Key)}, {{\n");
        w.Append("    const = {\n");
        w.Append($"      equipID            = TppEquip.{s.EquipConstant},\n");
        w.Append($"      equipDevelopTypeID = TppMbDev.EQP_DEV_TYPE_{s.DevelopType},\n");
        if (s.DevelopGroup != null) w.Append($"      equipDevelopGroupID = TppMbDev.{s.DevelopGroup},\n");
        w.Append($"      langEquipName      = {Str(s.NameLang)},\n");
        if (s.RealNameLang != null) w.Append($"      langEquipRealName  = {Str(s.RealNameLang)},\n");
        if (s.InfoLang != null) w.Append($"      langEquipInfo      = {Str(s.InfoLang)},\n");
        w.Append($"      iconFtexPath       = {Str(s.IconFtexPath)},\n");
        WriteConstExtras(w, s.Develop);
        w.Append("    },\n");
        w.Append("    flow = {\n");
        WriteFlow(w, s.Develop);
        w.Append("    },\n");
        w.Append("  })\n");
        return w.ToString();
    }

    /// <summary>RegisterBuddyOutfit and its R&amp;D row, indented for LoadLibraries.</summary>
    public static string BuddyBlock(BuddyOutfitSpec b)
    {
        if (b.Buddy is not ("quiet" or "horse" or "dog")) throw new ArgumentException("A buddy outfit is for quiet, horse or dog.");
        var w = new StringBuilder();
        foreach (var line in b.Header) w.Append("  -- ").Append(OneLine(line)).Append('\n');
        w.Append("  V_TppBuddyService.RegisterBuddyOutfit({\n");
        w.Append($"    key = {Str(b.Key)},\n");
        var dog = DogFields(b.Dog);
        var horse = HorseFields(b.Horse);
        if (horse.Count > 0)
        {
            if (b.Buddy != "horse") throw new ArgumentException("D-Horse tack behaviour is for horse outfits only.");
            w.Append($"    horse = {{\n      fpkPath = {Str(b.FpkPath)},\n");
            foreach (var f in horse) w.Append("      ").Append(f).Append(",\n");
            w.Append("    },\n");
        }
        else if (dog.Count == 0)
            w.Append($"    {b.Buddy} = {{ fpkPath = {Str(b.FpkPath)}{(b.Bruise ? ", bruise = true" : "")} }},\n");
        else
        {
            if (b.Buddy != "dog") throw new ArgumentException("D-Dog gear behaviour is for dog outfits only.");
            w.Append($"    dog = {{\n      fpkPath = {Str(b.FpkPath)},\n");
            foreach (var f in dog) w.Append("      ").Append(f).Append(",\n");
            w.Append("    },\n");
        }
        w.Append("  })\n\n");
        w.Append($"  V_TppMotherBaseManagement.AddToEquipDevelopTable({Str(b.Key)}, {{\n");
        w.Append("    const = {\n");
        w.Append($"      equipDevelopTypeID = TppMbDev.EQP_DEV_TYPE_{b.DevelopType},\n");
        w.Append($"      langEquipName      = {Str(b.NameLang)},\n");
        w.Append($"      langEquipRealName  = {Str(b.RealNameLang ?? b.NameLang)},\n");
        if (b.InfoLang != null) w.Append($"      langEquipInfo      = {Str(b.InfoLang)},\n");
        for (int i = 0; i < b.PowerUpInfoLangs.Count; i++) w.Append($"      langPowerUpInfo{i}   = {Str(b.PowerUpInfoLangs[i])},\n");
        w.Append($"      iconFtexPath       = {Str(b.IconFtexPath)},\n");
        WriteConstExtras(w, b.Develop);
        w.Append("    },\n");
        w.Append("    flow = {\n");
        WriteFlow(w, b.Develop);
        w.Append("    },\n");
        w.Append("  })\n");
        return w.ToString();
    }

    /// <summary>The dog branch's behaviour fields ("commandType = 4 --[[ V_DogCommandType.KILL ]]", …), only those set.</summary>
    private static List<string> DogFields(DogGearBehaviour? d)
    {
        var f = new List<string>();
        if (d == null) return f;
        if (d.CommandType != null)
        {
            if (!DogGearBehaviour.CommandTypes.Contains(d.CommandType)) throw new ArgumentException($"Unknown D-Dog command type \"{d.CommandType}\".");
            // The number V_DogCommandType gives the name (V Framework reads any whole number 0–6), so the module doesn't
            // depend on that table being there when it loads.
            f.Add($"commandType = {Array.IndexOf(DogGearBehaviour.CommandTypes, d.CommandType)} --[[ V_DogCommandType.{d.CommandType} ]]");
        }
        static string B(bool v) => v ? "true" : "false";
        if (d.DamageRate is { } dr) f.Add($"damageRate = {Num(dr)}");
        if (d.BulletFlinch is { } bf) f.Add($"bulletFlinch = {B(bf)}");
        if (d.ArmorHitEffect is { } he) f.Add($"armorHitEffect = {B(he)}");
        if (d.ArmorHitSound is { } hs) f.Add($"armorHitSound = {B(hs)}");
        if (d.EscapeDistance is { } ed) f.Add($"escapeDistance = {Num(ed)}");
        if (d.HitRetreatDistance is { } hr) f.Add($"hitRetreatDistance = {Num(hr)}");
        if (d.RetargetRange is { } rr) f.Add($"retargetRange = {Num(rr)}");
        if (d.SoldierThreat is { } st) f.Add($"soldierThreat = {B(st)}");
        return f;
    }

    /// <summary>The horse branch's behaviour fields, only those set (bodyAttackId as its TppDamage.ATK_* constant).</summary>
    private static List<string> HorseFields(HorseTackBehaviour? h)
    {
        var f = new List<string>();
        if (h == null) return f;
        if (h.DamageRate is { } dr) f.Add($"damageRate = {Num(dr)}");
        if (h.GallopSpeedRate is { } gr) f.Add($"gallopSpeedRate = {Num(gr)}");
        if (h.BodyAttack is { } ba)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(ba, "^ATK_[A-Za-z0-9_]+$")) throw new ArgumentException($"D-Horse body attack \"{ba}\" is not a TppDamage.ATK_* name.");
            // A name of the attack table (GameKnowledge.AttackIds) as its number, so the module doesn't depend on TppDamage
            // having the constant when it loads; any other row by its constant.
            int? id = Knowledge.GameKnowledge.AttackIds.TryGetValue(ba, out int known) ? known : null;
            f.Add(id is { } n ? $"bodyAttackId = {n} --[[ TppDamage.{ba} ]]" : $"bodyAttackId = TppDamage.{ba}");
        }
        if (h.SoundType is { } st)
        {
            if (st is < 0 or > 6) throw new ArgumentException("D-Horse soundType is a vanilla tack, 0–6.");
            f.Add($"soundType = {st} --[[ {HorseTackBehaviour.TackNames[st]} ]]");
        }
        return f;
    }

    private static void WriteVoice(StringBuilder w, OutfitVoice? v, string ind)
    {
        if (v == null) return;
        foreach (var t in v.Fpks.Keys.Append(v.Type).OfType<string>())
            if (!OutfitVoice.Types.Contains(t)) throw new ArgumentException($"Unknown voice type \"{t}\" (known: {string.Join(", ", OutfitVoice.Types)}).");
        if (v.Type != null) w.Append($"{ind}voiceType = {Str(v.Type)},\n");
        if (v.Fpk != null) w.Append($"{ind}voiceFpk = {Str(v.Fpk)},\n");
        else if (v.Fpks.Count > 0)
            w.Append($"{ind}voiceFpk = {{ {string.Join(", ", v.Fpks.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} = {Str(kv.Value)}"))} }},\n");
    }

    private static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when a wearer has 2 or more looks: the outfit then needs the look-name group (79) in its R&amp;D row.</summary>
    public static bool IsMultiLook(IReadOnlyDictionary<PlayerBranch, OutfitBranch> branches) => branches.Values.Any(b => b.Variants.Count > 0);

    /// <summary>The line that sets a one-look outfit up at registration (see <see cref="OutfitBlock"/>).</summary>
    public static string OutfitInfoLine(string keyExpr) => $"  if V_Player.GetOutfitInfo then V_Player.GetOutfitInfo({keyExpr}) end\n";

    /// <summary>RegisterOutfit and its R&amp;D row, indented for the body of LoadLibraries.</summary>
    /// <param name="o">The outfit.</param>
    /// <param name="multiLook">The look count of the whole outfit, when <paramref name="o"/> holds only some of its wearers.</param>
    public static string OutfitBlock(OutfitSpec o, bool? multiLook = null)
    {
        var w = new StringBuilder();
        foreach (var line in o.Header) w.Append("  -- ").Append(OneLine(line)).Append('\n');
        w.Append($"  local KEY  = {Str(o.Key)}\n");
        w.Append($"  local ICON = {Str(o.IconFtexPath)}\n\n");
        w.Append("  V_Player.RegisterOutfit({\n");
        w.Append("    key = KEY,\n");
        bool multi = multiLook ?? IsMultiLook(o.Branches);
        foreach (var (who, b) in o.Branches.OrderBy(kv => kv.Key))
        {
            w.Append($"    {BranchName(who)} = {{\n");
            // V Framework 1.7 writes the main look's displayName over the row sub-line; with no look-name group that is
            // the grade bar (owner, Nov 17, build C), so a one-look outfit leaves it out. The R&D row still names it.
            WriteLook(w, multi ? b.Base : b.Base with { DisplayName = null }, "      ", isBase: true);
            if (b.CamoBonusType != null) w.Append($"      camoBonusType = {Str(b.CamoBonusType)},\n");
            else if (b.CamoBonusValues is { Count: > 0 } cv)
            {
                // Issue #89: V Framework 1.7 reads each value one material early, so each value goes under the next name.
                w.Append("      camoBonusValues = {\n");
                foreach (var (key, v) in cv.Where(kv => kv.Value != 0).Select(kv => (Key: Knowledge.GameKnowledge.CamoValueKey(kv.Key), kv.Value))
                             .Where(kv => kv.Key != null).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    w.Append($"        {key} = {Math.Clamp(v, 0, 255)},\n");
                w.Append("      },\n");
            }
            if (b.PrimaryColors.Count > 0 || b.SecondaryColors.Count > 0)
            {
                w.Append("      colors = {\n");
                foreach (var (name, list) in new[] { ("primary", b.PrimaryColors), ("secondary", b.SecondaryColors) })
                {
                    if (list.Count == 0) continue;
                    w.Append($"        {name} = {{\n");
                    foreach (var c in list)
                        w.Append($"          {{ fv2 = {Str(c.Fv2)}{(c.Fpk != null ? $", fpk = {Str(c.Fpk)}" : "")}{(c.Icon != null ? $", icon = {Str(c.Icon)}" : "")} }},\n");
                    w.Append("        },\n");
                }
                w.Append("      },\n");
            }
            if (b.HeadOptions.Count > 0) w.Append($"      headOptions = {{ {string.Join(", ", b.HeadOptions.Select(Str))} }},\n");
            WriteVoice(w, b.Voice, "      ");
            if (!b.Abilities.IsEmpty) WriteAbilities(w, b.Abilities, "      ");
            WriteVariants(w, b.Variants, "      ");
            w.Append("    },\n");
        }
        w.Append("  })\n\n");
        // The look-name group (79) makes the game fill a row with the fatigues cells (Naked/Standard/Scarf);
        // V Framework 1.7 writes its own cells over them only for rows with 2 or more looks. A one-look outfit
        // leaves the group out, as V Framework's own one-look outfits (Ocelot, Quiet) do, so its row has one cell.
        // Confirmed in game Nov 17 (M5 builds): 2 looks without the group cannot be cycled; 1 look with it fills the
        // row with the fatigues cells, and they select fatigues.
        WriteDevelopRow(w, "KEY", "TppEquip.EQP_SUIT", o.NameLang, o.RealNameLang, o.InfoLang, "ICON", o.Develop, outfitGroup: multi);
        if (o.Branches.Values.Any(b => b.Variants.Count == 0))
        {
            // V Framework 1.7 sets an outfit up at registration only from the ids it saved last session, and in the
            // list only when it has 2+ looks; a one-look outfit stays unset on its first launch (worn as fatigues,
            // wrong head). GetOutfitInfo sets it up now, so the first launch matches the later ones.
            w.Append("\n  -- Set up the outfit now: V Framework 1.7 otherwise waits until the second launch for one-look outfits.\n");
            w.Append(OutfitInfoLine("KEY"));
        }
        return w.ToString();
    }

    /// <summary>A custom weapon from parts: constants, SetGunBasic, its equip-table row and its R&amp;D row.</summary>
    public static string WeaponBlock(WeaponSpec wp)
    {
        if (!wp.Parts.ContainsKey("ammoId")) throw new ArgumentException("A weapon needs ammoId (V Framework rejects it without one).");
        if (wp.EquipType == "" || wp.DevelopType == "")
            throw new ArgumentException($"Weapon {wp.Key} has no weapon type (V Framework drops the row: \"AddToEquipIdTable: malformed row\", and the gun is not in the sortie list).");
        var w = new StringBuilder();
        foreach (var line in wp.Header) w.Append("  -- ").Append(OneLine(line)).Append('\n');
        w.Append($"  V_TppEquip.RegisterConstantEquipId({Str(wp.EquipConstant)})\n");
        w.Append($"  V_TppEquip.DeclareWPs {{ {Str(wp.WeaponConstant)} }}\n");
        foreach (var (fn, name) in wp.Declarations) w.Append($"  V_TppEquip.{fn} {{ {Str(name)} }}\n");
        w.Append('\n');
        if (wp.ExtraLua != null) w.Append(wp.ExtraLua.EndsWith('\n') ? wp.ExtraLua : wp.ExtraLua + "\n").Append('\n');
        if (!string.IsNullOrEmpty(wp.TuningLua)) w.Append(wp.TuningLua);
        w.Append("  V_TppEquip.SetGunBasic{\n");
        w.Append($"    weaponId    = TppEquip.{wp.WeaponConstant},\n");
        // A part left out is "none" (V Framework's default), so the *_None values are not written.
        foreach (var (field, value) in wp.Parts)
            if (field == "ammoId" || !value.EndsWith("_None", StringComparison.Ordinal)) w.Append($"    {field} = TppEquip.{value},\n");
        w.Append($"    weaponGrade = {wp.WeaponGrade},\n");
        w.Append("  }\n\n");
        w.Append("  V_TppEquip.AddToEquipIdTable{\n");
        w.Append($"    {{ TppEquip.{wp.EquipConstant}, TppEquip.EQP_TYPE_{wp.EquipType}, TppEquip.{wp.WeaponConstant}, TppEquip.{wp.Block},\n");
        w.Append($"      {Str(wp.PartsPath)}, {Str(wp.FpkPath)} }},\n");
        w.Append("  }\n\n");
        w.Append($"  V_TppMotherBaseManagement.AddToEquipDevelopTable({Str(wp.Key)}, {{\n");
        w.Append("    const = {\n");
        w.Append($"      equipID            = TppEquip.{wp.EquipConstant},\n");
        w.Append($"      equipDevelopTypeID = TppMbDev.EQP_DEV_TYPE_{wp.DevelopType},\n");
        w.Append($"      langEquipName      = {Str(wp.NameLang)},\n");
        if (wp.RealNameLang != null) w.Append($"      langEquipRealName  = {Str(wp.RealNameLang)},\n");
        if (wp.InfoLang != null) w.Append($"      langEquipInfo      = {Str(wp.InfoLang)},\n");
        w.Append($"      iconFtexPath       = {Str(wp.IconFtexPath)},\n");
        WriteConstExtras(w, wp.Develop);
        w.Append("    },\n");
        w.Append("    flow = {\n");
        WriteFlow(w, wp.Develop);
        w.Append("    },\n");
        w.Append("  })\n");
        return w.ToString();
    }

    /// <summary>ExtendVanillaOutfit: extra looks, heads or abilities on a vanilla suit.</summary>
    public static string ExtendBlock(ExtendVanillaSpec e)
    {
        if ((e.Outfit == null) == (e.PartsType == null)) throw new ArgumentException("ExtendVanillaOutfit needs exactly one of outfit or partsType.");
        if (e.PartsType != null && e.Branches.Values.Any(b => b.Variants.Count > 0))
            throw new ArgumentException("Variants don't work with a raw partsType; name the vanilla outfit instead.");
        var w = new StringBuilder();
        // #90: V Framework 1.7 writes a look's displayName over the grade stars of a graded suit's row, so leave it out.
        bool graded = e.Outfit != null && Knowledge.GameKnowledge.HasGrades(e.Outfit);
        foreach (var line in e.Header) w.Append("  -- ").Append(OneLine(line)).Append('\n');
        w.Append("  V_Player.ExtendVanillaOutfit({\n");
        if (e.Outfit != null)
            w.Append(int.TryParse(e.Outfit, out int n) ? $"    outfit = {n},\n" : $"    outfit = {Str(e.Outfit)},\n");
        else w.Append($"    partsType = {e.PartsType},\n");
        foreach (var (who, b) in e.Branches.OrderBy(kv => kv.Key))
        {
            w.Append($"    {BranchName(who)} = {{\n");
            if (b.EnableArm != null) w.Append($"      enableArm = {Bool(b.EnableArm.Value)},\n");
            if (b.EnableHead != null) w.Append($"      enableHead = {Bool(b.EnableHead.Value)},\n");
            if (b.HeadOptions.Count > 0) w.Append($"      headOptions = {{ {string.Join(", ", b.HeadOptions.Select(Str))} }},\n");
            WriteVoice(w, b.Voice, "      ");
            if (!b.Abilities.IsEmpty) WriteAbilities(w, b.Abilities, "      ");
            WriteVariants(w, b.Variants, "      ", noName: graded);
            w.Append("    },\n");
        }
        w.Append("  })\n");
        return w.ToString();
    }

    /// <summary>RegisterHeadOption, plus its R&amp;D row when the head has a name.</summary>
    public static string HeadBlock(HeadOptionSpec h)
    {
        if (h.Key.Length > 63 || h.Key.IndexOfAny(['\n', '\r', '"', '\\']) >= 0)
            throw new ArgumentException("A head key is at most 63 characters, without line breaks, double quotes or backslashes.");
        var w = new StringBuilder();
        foreach (var line in h.Header) w.Append("  -- ").Append(OneLine(line)).Append('\n');
        w.Append("  V_Player.RegisterHeadOption({\n");
        w.Append($"    key = {Str(h.Key)},\n");
        foreach (var (who, b) in h.Branches.OrderBy(kv => kv.Key))
        {
            w.Append($"    {BranchName(who)} = {{\n");
            if (b.Fv2 != null) w.Append($"      fv2 = {Str(b.Fv2)},\n");
            if (b.Fpk != null) w.Append($"      fpk = {Str(b.Fpk)},\n");
            if (b.EnemyFaceId != null) w.Append($"      TppEnemyFaceId = {b.EnemyFaceId},\n");
            if (b.FaceStages.Count > 0)
            {
                w.Append("      faceStages = {\n");
                foreach (var (stage, (fv2, fpk)) in b.FaceStages.OrderBy(kv => kv.Key))
                    w.Append($"        [{stage}] = {{ fv2 = {Str(fv2)}, fpk = {Str(fpk)} }},\n");
                w.Append("      },\n");
            }
            w.Append("    },\n");
        }
        if (h.ShowInDevelopMenu) w.Append("    showInDevelopMenu = true,\n");
        if (h.InfiniteAmmo) w.Append("    abilities = { infiniteAmmo = true },\n");
        w.Append("  })\n");
        if (h.NameLang != null)
        {
            w.Append('\n');
            WriteDevelopRow(w, Str(h.Key), "TppEquip.EQP_None", h.NameLang, h.RealNameLang, h.InfoLang, h.IconFtexPath != null ? Str(h.IconFtexPath) : null, h.Develop, outfitGroup: false);
        }
        return w.ToString();
    }

    private static void WriteVariants(StringBuilder w, List<Look> variants, string ind, bool noName = false)
    {
        if (variants.Count == 0) return;
        w.Append($"{ind}variants = {{\n");
        foreach (var v in variants)
        {
            w.Append($"{ind}  {{\n");
            WriteLook(w, noName ? v with { DisplayName = null } : v, ind + "    ", isBase: false);
            w.Append($"{ind}  }},\n");
        }
        w.Append($"{ind}}},\n");
    }

    private static void WriteFlow(StringBuilder w, DevelopRow d)
    {
        if (d.Grade is { } grade) w.Append($"      grade            = {grade},\n");
        w.Append($"      developGmpCost   = {d.GmpCost},\n");
        if (d.UsageGmpCost > 0) w.Append($"      usageGmpCost     = {d.UsageGmpCost},\n");
        w.Append($"      initialAvailable = {(d.InitiallyAvailable ? 1 : 0)},\n");
        if (d.TimeMinutes > 0) w.Append($"      developTimeMinute = {d.TimeMinutes},\n");
        if (d.SectionId == DevelopRow.RdSection) w.Append($"      sectionLvForDevelop = {Math.Max(0, d.SectionLevel)},\n");
        else if (d.SectionId > 0) { w.Append($"      sectionIDForDevelop = {d.SectionId},\n"); w.Append($"      developSectionLv = {Math.Max(0, d.SectionLevel)},\n"); }
        if (d.Section2Id > 0) { w.Append($"      sectionID2ForDevelop = {d.Section2Id},\n"); w.Append($"      sectionLv2ForDevelop = {Math.Max(0, d.Section2Level)},\n"); }
        if (!string.IsNullOrEmpty(d.Resource1)) { w.Append($"      resourceType1 = {Str(d.Resource1)},\n      resourceType1Count = {d.Resource1Count},\n"); }
        if (!string.IsNullOrEmpty(d.Resource2)) { w.Append($"      resourceType2 = {Str(d.Resource2)},\n      resourceType2Count = {d.Resource2Count},\n"); }
        if (!string.IsNullOrEmpty(d.Usage1)) { w.Append($"      resourceUsageType1 = {Str(d.Usage1)},\n      resourceUsageType1Count = {d.Usage1Count},\n"); }
        if (!string.IsNullOrEmpty(d.Usage2)) { w.Append($"      resourceUsageType2 = {Str(d.Usage2)},\n      resourceUsageType2Count = {d.Usage2Count},\n"); }
        if (d.HiddenUntilObtained || d.AfterMission != null) w.Append("      displayInfo      = 1,\n");
    }

    private static void WriteConstExtras(StringBuilder w, DevelopRow d)
    {
        if (d.UpgradeOfDevelopId is { } up) w.Append($"      baseEquipDevelopId  = {up},\n");
        if (d.Skill is { } sk) w.Append($"      skill               = {(DevelopRow.IsNumber(sk) ? sk : Str(sk))},\n");
        if (d.RankLimited) w.Append("      isResultRankLimited = 1,\n");
        if (d.RankLimited && d.ResultRankLang != null) w.Append($"      langResultRankLimited = {Str(d.ResultRankLang)},\n");
        // A row that follows a vanilla row is never shown in R&D; that bluePrintId wins (issue #98: every item kind).
        if (d.FollowsVanillaDevelopId != null)
            w.Append("      bluePrintId         = function() this.FollowVanillaDevelop(true) return false end,   -- never shown in R&D\n");
        else if (d.AfterMission is { } m)
            w.Append($"      bluePrintId         = function() return TppStory.IsMissionCleard({m}) end,\n");
        else if (d.BluePrint is { } bp)
            w.Append($"      bluePrintId         = {DevelopRow.BluePrintLua(bp)},\n");
    }

    private static void WriteDevelopRow(StringBuilder w, string keyExpr, string equipId, string nameLang, string? realLang, string? infoLang,
        string? iconExpr, DevelopRow d, bool outfitGroup)
    {
        w.Append($"  V_TppMotherBaseManagement.AddToEquipDevelopTable({keyExpr}, {{\n");
        w.Append("    const = {\n");
        w.Append($"      equipID             = {equipId},\n");
        w.Append("      equipDevelopTypeID  = TppMbDev.EQP_DEV_TYPE_Suit,\n");
        w.Append($"      langEquipName       = {Str(nameLang)},\n");
        if (realLang != null) w.Append($"      langEquipRealName   = {Str(realLang)},\n");
        if (infoLang != null) w.Append($"      langEquipInfo       = {Str(infoLang)},\n");
        if (iconExpr != null) w.Append($"      iconFtexPath        = {iconExpr},\n");
        if (outfitGroup) w.Append("      equipDevelopGroupID = V_TppMbDev.EQP_OUTFIT_VARIANT_NAME,\n");
        WriteConstExtras(w, d);
        w.Append("    },\n");
        w.Append("    flow = {\n");
        WriteFlow(w, d);
        w.Append("    },\n");
        w.Append("  })\n");
    }

    private static void WriteLook(StringBuilder w, Look l, string ind, bool isBase)
    {
        foreach (var k in Look.Keys)
        {
            // iconFtexPath is the only per-look icon key V Framework has, and it takes a path (no extension), never a
            // V_CallMenuIcon constant. The base look falls back to the outfit's own ICON local; a variant that leaves it
            // out inherits its branch's icon, which is what V Framework does when the key is absent.
            string? v = k.Get(l) switch { string s => Str(s), bool b => Bool(b), _ => isBase && k.Name == "iconFtexPath" ? "ICON" : null };
            if (v != null) w.Append($"{ind}{k.Name,-12} = {v},\n");
        }
        if (!isBase && l.HeadOptions.Count > 0) w.Append($"{ind}headOptions  = {{ {string.Join(", ", l.HeadOptions.Select(Str))} }},\n");
        if (!isBase && l.Default) w.Append($"{ind}default      = true,\n");
        if (!isBase && l.Abilities != null) WriteAbilities(w, l.Abilities, ind);
    }

    private static void WriteAbilities(StringBuilder w, Abilities a, string ind)
    {
        w.Append($"{ind}abilities = {{\n");
        string i2 = ind + "  ";
        foreach (var k in Abilities.Keys)
        {
            // A key that is off, 0 or not set is left out, so V Framework uses its default. A level stays in 0-9.
            string? v = k.Get(a) switch
            {
                true => "true", string atk when k.Type == Project.FieldType.Bool => DashAttackLua(atk), float s => s.ToString("0.0##", CultureInfo.InvariantCulture), int n when n > 0 => Math.Clamp(n, 0, 9).ToString(CultureInfo.InvariantCulture),
                string s => Str(s), _ => null,
            };
            if (v != null) w.Append($"{i2}{k.Name} = {v},\n");
        }
        w.Append($"{ind}}},\n");
    }

    /// <summary>The Lua value of a dash attack by its attack name: the number, as V Framework reads it, and the name in a comment.</summary>
    public static string DashAttackLua(string name) =>
        Abilities.IsDashAttackId(name)
            ? $"{Knowledge.GameKnowledge.AttackIds[name]} --[[ TppDamage.{name} ]]"
            : throw new ArgumentException($"Dash attack \"{name}\" is not a TppDamage.ATK_ name with a number up to {Knowledge.GameKnowledge.DashAttackLast}.");

    public static string BranchName(PlayerBranch b) => b switch
    {
        PlayerBranch.Snake => "snake",
        PlayerBranch.Avatar => "avatar",
        PlayerBranch.DdMale => "ddMale",
        PlayerBranch.DdFemale => "ddFemale",
        PlayerBranch.Ocelot => "ocelot",
        _ => "quiet",
    };

    private static string Bool(bool b) => b ? "true" : "false";

    /// <summary>A Lua string literal.</summary>
    public static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append('\\').Append(((int)c).ToString("000", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
