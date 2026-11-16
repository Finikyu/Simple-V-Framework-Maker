using System.Text;

namespace Svfm.Core.Conversion;

/// <summary>One row of a Zeta parameter table: positional values and named ones (<c>sIndex=5</c>), as written.</summary>
public sealed class ZetaRow
{
    public List<string> Values { get; } = [];
    public Dictionary<string, string> Named { get; } = new(StringComparer.Ordinal);
    /// <summary>The row's sIndex (a parameter-set row it overrides), or null when it is appended.</summary>
    public int? SIndex => Named.TryGetValue("sIndex", out var s) && int.TryParse(s, out int i) ? i : null;
    /// <summary>A value without the TppEquip. prefix (TppDamage.* stays qualified).</summary>
    public string Part(int i) => Values[i].StartsWith("TppEquip.", StringComparison.Ordinal) ? Values[i]["TppEquip.".Length..] : Values[i];
}

/// <summary>
/// A Zeta weapon script (/mod/zeta/*.lua): the tables returned by EquipParameters() and DamageParameterTables(), read as
/// data (no Lua is run). Tables are sections of rows; a row is a flat list of numbers and TppEquip/TppDamage names.
/// </summary>
public sealed class ZetaScript
{
    public string Name { get; private init; } = "";
    public string Author { get; private init; } = "";
    /// <summary>The script as given.</summary>
    public string Source { get; private init; } = "";
    public Dictionary<string, List<ZetaRow>> Sections { get; } = new(StringComparer.Ordinal);
    public List<ZetaRow> Damage { get; } = [];
    /// <summary>The rows of EquipIdTable(): equip ID, EQP_TYPE, weapon (WP_…), block, .parts path, package path (.fpk).</summary>
    public List<ZetaRow> EquipIds { get; } = [];
    /// <summary>The rows of EquipDevelopConstSetting() (p00 … p36, named): p01 = equip ID, p06/p07/p30 = lang IDs, p08 = icon.</summary>
    public List<ZetaRow> DevelopConst { get; } = [];

    public IReadOnlyList<ZetaRow> Section(string name) => Sections.TryGetValue(name, out var s) ? s : [];

    /// <summary>The EquipIdTable() row of a weapon (WP_…), or null: the script gives the weapon no package of its own.</summary>
    public ZetaRow? EquipIdOf(string weapon) => EquipIds.FirstOrDefault(r => r.Values.Count >= 6 && r.Part(2) == weapon);

    /// <summary>
    /// The packages (.fpk path without the extension) that a script names and the mod ships. A path that the script
    /// builds from a menu value (<c>"…/scarf"..costumeType..".fpk"</c>) gives each shipped package that fits, with its
    /// number (the menu choice, 1-based). Key = the table key in front of a path written in full (DEFAULT="….fpk").
    /// </summary>
    public static List<(string Package, int? Number, string? Key)> ShippedPackages(string lua, IReadOnlyDictionary<string, byte[]> archive)
    {
        lua = Formats.LuaLiteral.BlankComments(lua);
        var found = new List<(string, int?, string?)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lua, @"(?:([A-Za-z_][A-Za-z0-9_]*)\s*=\s*)?""(/Assets/[^""]+)\.fpk"""))
            if (archive.ContainsKey(m.Groups[2].Value + ".fpk")) found.Add((m.Groups[2].Value, null, m.Groups[1].Success ? m.Groups[1].Value : null));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lua, @"""(/Assets/[^""]*)""\s*\.\.\s*[\w.]+\s*\.\.\s*""([^""]*)\.fpk"""))
        {
            var fit = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(m.Groups[1].Value) + @"(\d+)" + System.Text.RegularExpressions.Regex.Escape(m.Groups[2].Value) + @"\.fpk$");
            found.AddRange(archive.Keys.Select(k => fit.Match(k)).Where(x => x.Success)
                .Select(x => (Package: x.Value[..^4], Number: (int?)int.Parse(x.Groups[1].Value), Key: (string?)null)).OrderBy(x => x.Number));
        }
        return found.GroupBy(x => x.Item1).Select(g => g.First()).ToList();
    }

    /// <summary>The choices of the script's first menu list (ModMenu: <c>list={"Bionic Arm", "Two Arms"}</c>), in order.</summary>
    public static List<string> MenuList(string lua) =>
        System.Text.RegularExpressions.Regex.Match(Formats.LuaLiteral.BlankComments(lua), @"\blist\s*=\s*\{([^}]*)\}") is { Success: true } m
            ? System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"""([^""]*)""").Select(x => x.Groups[1].Value.Trim()).ToList() : [];

    public static ZetaScript Parse(string source)
    {
        // Comments are blanked (same length), so a commented-out table or a --[[ ]] block is not read; Source keeps them.
        var lua = Formats.LuaLiteral.BlankComments(source);
        var p = new Parser(lua);
        var z = new ZetaScript { Name = Field(lua, "modName"), Author = Field(lua, "modAuthor"), Source = source };
        int eq = lua.IndexOf("EquipParameters", StringComparison.Ordinal);
        if (eq >= 0)
        {
            var t = p.TableAfterReturn(eq);
            if (t != null)
                foreach (var (key, v) in t.Items)
                    if (key != null && v is Table rows)
                        z.Sections[key] = rows.Items.Select(i => i.value).OfType<Table>().Select(ToRow).ToList();
        }
        int dm = lua.IndexOf("DamageParameterTables", StringComparison.Ordinal);
        if (dm >= 0 && p.TableAfterReturn(dm) is { } d)
            z.Damage.AddRange(d.Items.Select(i => i.value).OfType<Table>().Select(ToRow));
        foreach (var (function, rows) in new[] { ("function this.EquipIdTable", z.EquipIds), ("function this.EquipDevelopConstSetting", z.DevelopConst) })
            if (lua.IndexOf(function, StringComparison.Ordinal) is var at and >= 0 && p.TableAfterReturn(at) is { } t)
                rows.AddRange(t.Items.Select(i => i.value).OfType<Table>().Select(ToRow));
        return z;
    }

    private static string Field(string lua, string name)
    {
        // HK 33 writes modName = "HK33", with spaces.
        var m = System.Text.RegularExpressions.Regex.Match(lua, name + @"\s*=\s*""([^""]*)""");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static ZetaRow ToRow(Table t)
    {
        var r = new ZetaRow();
        foreach (var (key, v) in t.Items)
        {
            if (v is not Atom a) continue;
            if (key != null) r.Named[key] = a.Text; else r.Values.Add(a.Text);
        }
        return r;
    }

    private abstract record Node;
    private sealed record Atom(string Text) : Node;
    private sealed record Table(List<(string? key, Node value)> Items) : Node;

    private sealed class Parser(string s)
    {
        private int _i;

        public Table? TableAfterReturn(int from)
        {
            int r = s.IndexOf("return", from, StringComparison.Ordinal);
            if (r < 0) return null;
            _i = r + "return".Length;
            Skip();
            return _i < s.Length && s[_i] == '{' ? ParseTable() : null;
        }

        private void Skip()
        {
            while (_i < s.Length)
            {
                if (char.IsWhiteSpace(s[_i])) _i++;
                else if (s[_i] == '-' && _i + 1 < s.Length && s[_i + 1] == '-')
                    while (_i < s.Length && s[_i] != '\n') _i++;
                else break;
            }
        }

        private Table ParseTable()
        {
            _i++; // {
            var items = new List<(string?, Node)>();
            while (true)
            {
                Skip();
                if (_i >= s.Length) throw new InvalidDataException("Zeta script ends inside a table.");
                if (s[_i] == '}') { _i++; return new Table(items); }
                if (s[_i] == ',' || s[_i] == ';') { _i++; continue; }
                if (s[_i] == '{') { items.Add((null, ParseTable())); continue; }
                string word = ReadWord();
                Skip();
                if (_i < s.Length && s[_i] == '=')
                {
                    _i++; Skip();
                    items.Add((word, s[_i] == '{' ? ParseTable() : new Atom(ReadWord())));
                }
                else items.Add((null, new Atom(word)));
            }
        }

        private string ReadWord()
        {
            if (s[_i] == '"')
            {
                int e = s.IndexOf('"', _i + 1);
                string str = s[(_i + 1)..e];
                _i = e + 1;
                return str;
            }
            var sb = new StringBuilder();
            while (_i < s.Length && !char.IsWhiteSpace(s[_i]) && s[_i] is not (',' or '}' or '{' or '=' or ';'))
                sb.Append(s[_i++]);
            if (sb.Length == 0) throw new InvalidDataException($"Unexpected '{s[_i]}' in Zeta script.");
            return sb.ToString();
        }
    }
}
