using System.Globalization;
using System.Text;

namespace Svfm.Core.Formats;

/// <summary>
/// A value of a Lua table constructor as the game's parameter files write them (EquipParameters.lua,
/// DamageParameterTables.lua, EquipParameterTables.lua): a number, a string literal, a name (a constant such as
/// <c>TppEquip.RC_10001</c>, kept as text), or a table with array items and named fields.
/// </summary>
public abstract record LuaValue
{
    public sealed record Number(double Value) : LuaValue
    {
        public override string ToString() => LuaLiteral.FormatNumber(Value);
    }

    public sealed record Text(string Value) : LuaValue
    {
        public override string ToString() => LuaLiteral.Quote(Value);
    }

    /// <summary>A name or dotted name (TppEquip.RC_10001, true, false, nil).</summary>
    public sealed record Name(string Value) : LuaValue
    {
        public override string ToString() => Value;
    }

    public sealed record Table(IReadOnlyList<LuaValue> Items, IReadOnlyList<KeyValuePair<string, LuaValue>> Fields) : LuaValue
    {
        public LuaValue? this[string field] => Fields.FirstOrDefault(f => f.Key == field).Value
            // "[n]" is the n-th item, the name a path gives a look in a list.
            ?? (field.Length > 2 && field[0] == '[' && field[^1] == ']' && int.TryParse(field[1..^1], out int n) && n >= 1 && n <= Items.Count ? Items[n - 1] : null);

        /// <summary>
        /// A parameter of an R&amp;D row, whichever way the script spells it (<c>p06</c> or <c>langEquipName</c>), with the
        /// key as written so an edit can target that exact field. Null when the table does not set it. When a table sets
        /// both spellings the first one in the text is returned; which one V Framework uses then is not known.
        /// </summary>
        public (string Key, LuaValue Value)? Field(VFramework.ParamSchema schema, string name)
        {
            if (schema.Find(name) is not { } p) return null;
            foreach (var f in Fields)
                if (f.Key == p.Positional || (p.Alias != null && f.Key == p.Alias)) return (f.Key, f.Value);
            return null;
        }

        public override string ToString() => LuaLiteral.Write(this);
    }

    /// <summary>The number, or null.</summary>
    public double? AsNumber => this is Number n ? n.Value : null;
}

/// <summary>Reads and writes the table literals of the game's Lua parameter files (no expressions, no functions).</summary>
public static class LuaLiteral
{
    /// <summary>
    /// The table passed to <paramref name="call"/> in a file such as
    /// <c>this={}TppEquip.ReloadEquipParameterTables2{gunBasic={…},…}</c>; null when the call isn't there.
    /// </summary>
    public static LuaValue.Table? CallArgument(string source, string call)
    {
        source = BlankComments(source);
        int i = source.IndexOf(call, StringComparison.Ordinal);
        if (i < 0) return null;
        // AR-15 pack, 4PR: TppEquip.ReloadEquipParameterTables2(this.equipParameterTables) after this.equipParameterTables={…}
        var named = System.Text.RegularExpressions.Regex.Match(source[(i + call.Length)..], @"^\s*\(\s*([A-Za-z_][\w.]*)\s*\)");
        if (named.Success)
        {
            var def = System.Text.RegularExpressions.Regex.Match(source[..i], @"(?<![\w.])" + System.Text.RegularExpressions.Regex.Escape(named.Groups[1].Value) + @"\s*=\s*\{");
            return def.Success ? new Parser(source, def.Index + def.Length - 1).ReadValue() as LuaValue.Table : null;
        }
        int open = source.IndexOf('{', i + call.Length);
        if (open < 0) return null;
        var p = new Parser(source, open);
        return p.ReadValue() as LuaValue.Table;
    }

    /// <summary>The table passed to every <paramref name="call"/> in the file, in order (EquipDevelopConstSetting.lua makes 920 RegCstDev calls).</summary>
    public static List<LuaValue.Table> CallArguments(string source, string call)
    {
        var all = new List<LuaValue.Table>();
        source = BlankComments(source);
        for (int i = source.IndexOf(call, StringComparison.Ordinal); i >= 0; i = source.IndexOf(call, i + call.Length, StringComparison.Ordinal))
        {
            int open = source.IndexOf('{', i + call.Length);
            if (open < 0) break;
            if (new Parser(source, open).ReadValue() is LuaValue.Table t) all.Add(t);
        }
        return all;
    }

    /// <summary>The source with its comments blanked out (same length), so that a commented-out call is not found (Guns By Glecas
    /// 2.3: <c>--[[TppDamage.ReloadDamageParameter{…--]]</c> on line 2 of its DamageParameterTables.lua).</summary>
    internal static string BlankComments(string s)
    {
        if (!s.Contains("--", StringComparison.Ordinal)) return s;
        var b = s.ToCharArray();
        for (int i = 0; i < b.Length; i++)
        {
            char c = b[i];
            if (c == '"' || c == '\'')
            {
                for (i++; i < b.Length && b[i] != c && b[i] != '\n'; i++) if (b[i] == '\\') i++;
                continue;
            }
            if (c != '-' || i + 1 >= b.Length || b[i + 1] != '-') continue;
            int end = i + 3 < b.Length && b[i + 2] == '[' && b[i + 3] == '['
                ? (s.IndexOf("]]", i + 4, StringComparison.Ordinal) is var e and >= 0 ? e + 2 : b.Length)
                : (s.IndexOf('\n', i) is var n and >= 0 ? n : b.Length);
            for (int k = i; k < end; k++) if (b[k] != '\n') b[k] = ' ';
            i = end - 1;
        }
        return new string(b);
    }

    public static LuaValue Parse(string source) => new Parser(source, 0).ReadValue();

    /// <summary>The value, or null when the text is not a Lua literal (an expression, or a number out of range).</summary>
    public static LuaValue? TryParse(string source)
    {
        try { return Parse(source); } catch (Exception e) when (e is FormatException or OverflowException) { return null; }
    }

    private sealed class Parser(string s, int pos)
    {
        private int _i = pos;

        private void Skip()
        {
            while (_i < s.Length)
            {
                if (char.IsWhiteSpace(s[_i])) { _i++; continue; }
                if (s[_i] == '-' && _i + 1 < s.Length && s[_i + 1] == '-')
                {
                    // A comment: --[[ … ]] or to the end of the line.
                    if (_i + 3 < s.Length && s[_i + 2] == '[' && s[_i + 3] == '[')
                    {
                        int end = s.IndexOf("]]", _i + 4, StringComparison.Ordinal);
                        _i = end < 0 ? s.Length : end + 2;
                    }
                    else
                    {
                        int end = s.IndexOf('\n', _i);
                        _i = end < 0 ? s.Length : end + 1;
                    }
                    continue;
                }
                break;
            }
        }

        public LuaValue ReadValue()
        {
            Skip();
            if (_i >= s.Length) throw new FormatException("Unexpected end of Lua source.");
            char c = s[_i];
            if (c == '{') return ReadTable();
            if (c is '"' or '\'') return new LuaValue.Text(ReadString());
            if (char.IsDigit(c) || c == '.' || c == '-' || c == '+') return new LuaValue.Number(ReadNumber());
            if (char.IsLetter(c) || c == '_') return new LuaValue.Name(ReadName());
            throw new FormatException($"Unexpected '{c}' at {_i}.");
        }

        private LuaValue.Table ReadTable()
        {
            _i++; // {
            var items = new List<LuaValue>();
            var fields = new List<KeyValuePair<string, LuaValue>>();
            while (true)
            {
                Skip();
                if (_i >= s.Length) throw new FormatException("Unclosed table.");
                if (s[_i] == '}') { _i++; break; }
                // name = value, or a value
                int save = _i;
                if (char.IsLetter(s[_i]) || s[_i] == '_')
                {
                    string name = ReadName();
                    Skip();
                    if (_i < s.Length && s[_i] == '=' && (_i + 1 >= s.Length || s[_i + 1] != '='))
                    {
                        _i++;
                        fields.Add(new(name, ReadValue()));
                        goto separator;
                    }
                    _i = save;
                }
                else if (s[_i] == '[')
                {
                    // [key] = value: kept with the key's text.
                    _i++;
                    var key = ReadValue();
                    Skip();
                    if (_i < s.Length && s[_i] == ']') _i++;
                    Skip();
                    if (_i < s.Length && s[_i] == '=') _i++;
                    fields.Add(new(key is LuaValue.Text t ? t.Value : key.ToString()!, ReadValue()));
                    goto separator;
                }
                items.Add(ReadValue());
            separator:
                Skip();
                if (_i < s.Length && (s[_i] == ',' || s[_i] == ';')) _i++;
            }
            return new LuaValue.Table(items, fields);
        }

        private string ReadName()
        {
            int start = _i;
            while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_' || s[_i] == '.')) _i++;
            return s[start.._i];
        }

        private double ReadNumber()
        {
            int start = _i;
            if (s[_i] is '-' or '+') _i++;
            if (_i + 1 < s.Length && s[_i] == '0' && (s[_i + 1] == 'x' || s[_i + 1] == 'X'))
            {
                _i += 2;
                int hs = _i;
                while (_i < s.Length && Uri.IsHexDigit(s[_i])) _i++;
                double hv = ulong.Parse(s[hs.._i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return s[start] == '-' ? -hv : hv;
            }
            while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.' || s[_i] == 'e' || s[_i] == 'E'
                                     || ((s[_i] == '-' || s[_i] == '+') && (s[_i - 1] == 'e' || s[_i - 1] == 'E')))) _i++;
            return double.Parse(s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private string ReadString()
        {
            char q = s[_i++];
            var sb = new StringBuilder();
            var bytes = new List<byte>(); // a run of \ddd escapes: bytes of UTF-8 text, decoded together
            void Flush() { if (bytes.Count > 0) { sb.Append(Encoding.UTF8.GetString(bytes.ToArray())); bytes.Clear(); } }
            while (_i < s.Length && s[_i] != q)
            {
                if (s[_i] == '\\' && _i + 1 < s.Length)
                {
                    char e = s[++_i];
                    if (char.IsAsciiDigit(e))
                    {
                        int v = 0, n = 0;
                        while (n < 3 && _i < s.Length && char.IsAsciiDigit(s[_i])) { v = v * 10 + (s[_i++] - '0'); n++; }
                        if (v <= 255) bytes.Add((byte)v); else { Flush(); sb.Append((char)v); }
                        continue;
                    }
                    Flush();
                    _i++;
                    // A backslash before a line end is a line end (\r\n counts as one).
                    if (e is '\r' or '\n') { sb.Append('\n'); if (_i < s.Length && s[_i] is '\r' or '\n' && s[_i] != e) _i++; continue; }
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => e });
                    continue;
                }
                Flush();
                sb.Append(s[_i++]);
            }
            Flush();
            _i++;
            return sb.ToString();
        }
    }

    /// <summary>A number for a number box. A script can hold a number a decimal cannot (1e29, or 1e400, which reads as infinity).</summary>
    public static decimal ForNumberBox(double v) => (decimal)Math.Clamp(double.IsFinite(v) ? v : 0, -7.9e28, 7.9e28);

    public static string FormatNumber(double v)
    {
        // Lua has no literal for these: the .NET name would be read as a nil global, and the field would be lost.
        if (!double.IsFinite(v)) return "0";
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15) return ((long)v).ToString(CultureInfo.InvariantCulture);
        if (Math.Abs(v) >= 1e15) return v.ToString("R", CultureInfo.InvariantCulture);
        string s = v.ToString("0.######", CultureInfo.InvariantCulture);
        // A float (0.1f is 0.100000001...) keeps the short form; a double from a mod that it would change is written exactly.
        return double.Parse(s, CultureInfo.InvariantCulture) == v || (double)(float)v == v ? s : v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A Lua string literal; control characters are escaped, so a pasted line end can't cut the string.</summary>
    public static string Quote(string s) => Svfm.Core.VFramework.LuaWriter.Str(s);

    public static string Write(LuaValue v) => v switch
    {
        LuaValue.Table t => "{ " + string.Join(", ", t.Items.Select(Write).Concat(t.Fields.Select(f => $"{Key(f.Key)} = {Write(f.Value)}"))) + " }",
        _ => v.ToString()!,
    };

    /// <summary>A field key as Lua: a name as it is; a number or a dotted name (a <c>[key]</c> the parser kept as text) in brackets; other text quoted in brackets.</summary>
    private static string Key(string k) =>
        System.Text.RegularExpressions.Regex.IsMatch(k, @"^[A-Za-z_][A-Za-z0-9_]*$") && !Keywords.Contains(k) ? k
        : double.TryParse(k, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || System.Text.RegularExpressions.Regex.IsMatch(k, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$") ? $"[{k}]"
        : $"[{Quote(k)}]";

    private static readonly HashSet<string> Keywords = ["and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"];
}
