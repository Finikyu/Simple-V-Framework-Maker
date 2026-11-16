using System.Text;
using System.Text.RegularExpressions;

namespace Svfm.Core.Formats;

/// <summary>What the text of a value in a Lua table constructor is.</summary>
public enum LuaLiteralKind { Number, String, Bool, Nil, Table, Other }

/// <summary>One entry of a Lua table constructor, with where it sits in the source text.</summary>
public sealed class LuaEntry
{
    /// <summary>The field name, or null for a positional entry or a key that is not a plain name or string.</summary>
    public string? Key { get; init; }
    /// <summary>Start of the entry (its key, or its value when positional).</summary>
    public int Start { get; init; }
    /// <summary>Where the value's text starts and ends (end exclusive).</summary>
    public int ValueStart { get; init; }
    public int ValueEnd { get; init; }
    public LuaLiteralKind Kind { get; init; }
    /// <summary>The nested table, when <see cref="Kind"/> is Table.</summary>
    public LuaTable? Table { get; init; }
    /// <summary>Whether a comma or semicolon follows the entry, and where that character is (-1 when none).</summary>
    public int SeparatorAt { get; init; } = -1;
}

/// <summary>A table constructor <c>{ … }</c> found in Lua source: where it opens and closes, and its entries.</summary>
public sealed class LuaTable
{
    /// <summary>Index of the opening brace.</summary>
    public int Open { get; init; }
    /// <summary>Index of the closing brace.</summary>
    public int Close { get; init; }
    public List<LuaEntry> Entries { get; } = [];
    /// <summary>The entry with this field name (the first), or null.</summary>
    /// <param name="key">The field name.</param>
    /// <returns>The entry or null.</returns>
    public LuaEntry? Find(string key) =>
        // "[n]" is the n-th positional entry (a look in a variants list), as LuaValue.Table names it.
        key.Length > 2 && key[0] == '[' && key[^1] == ']' && int.TryParse(key[1..^1], out int n)
            ? Entries.Where(e => e.Key == null).ElementAtOrDefault(n - 1)
            : Entries.FirstOrDefault(e => e.Key == key);
}

/// <summary>One argument of a call.</summary>
public sealed class LuaArgument
{
    public int Start { get; init; }
    public int End { get; init; }
    /// <summary>The table, when the argument is a table constructor on its own.</summary>
    public LuaTable? Table { get; init; }
}

/// <summary>A call such as <c>V_Player.RegisterOutfit({ … })</c> found in source text.</summary>
public sealed class LuaCallSite
{
    public required string Path { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public List<LuaArgument> Arguments { get; } = [];
}

/// <summary>
/// A small reader for the shape of Lua source: it finds calls by their dotted name and the table constructors passed to
/// them, with the position of every field's value, so one value can be replaced and the rest of the file left exactly as
/// the author wrote it. It does not run or understand Lua: a value that is not a plain number, string or true/false is
/// reported as "other" and never changed.
/// </summary>
public static class LuaSource
{
    private static readonly Regex NumberRx = new(@"^-?(0[xX][0-9a-fA-F]+|(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?)$", RegexOptions.Compiled);

    private static bool IdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IdentPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>The index of <c>[</c>…<c>[</c> long-bracket level at <paramref name="i"/>, or -1.</summary>
    private static int LongLevel(string s, int i)
    {
        if (i >= s.Length || s[i] != '[') return -1;
        int j = i + 1, n = 0;
        while (j < s.Length && s[j] == '=') { n++; j++; }
        return j < s.Length && s[j] == '[' ? n : -1;
    }

    private static int SkipLong(string s, int i, int level)
    {
        string close = "]" + new string('=', level) + "]";
        int end = s.IndexOf(close, i + level + 2, StringComparison.Ordinal);
        return end < 0 ? s.Length : end + close.Length;
    }

    private static int SkipString(string s, int i)
    {
        char q = s[i++];
        while (i < s.Length && s[i] != q) i += s[i] == '\\' ? 2 : 1;
        return Math.Min(i + 1, s.Length);
    }

    /// <summary>Skips white space and comments.</summary>
    private static int Trivia(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                int lv = LongLevel(s, i + 2);
                if (lv >= 0) { i = SkipLong(s, i + 2, lv); continue; }
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            break;
        }
        return i;
    }

    private static bool OpensBlock(string w) => w is "function" or "if" or "do" or "repeat";
    private static bool ClosesBlock(string w) => w is "end" or "until";

    /// <summary>
    /// Where the block that a keyword just before <paramref name="i"/> opens (function, if, do, repeat) ends: the index
    /// after its <c>end</c> (or <c>until</c>), or -1 when the source ends first. Strings and comments are skipped.
    /// </summary>
    public static int BlockEnd(string s, int i)
    {
        int blocks = 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
            if (c == '[' && LongLevel(s, i) is var lv and >= 0) { i = SkipLong(s, i, lv); continue; }
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') { i = Trivia(s, i); continue; }
            if (IdentStart(c))
            {
                int j = i; while (j < s.Length && IdentPart(s[j])) j++;
                string w = s[i..j];
                if (OpensBlock(w)) blocks++; else if (ClosesBlock(w) && --blocks == 0) return j;
                i = j; continue;
            }
            i++;
        }
        return -1;
    }

    /// <summary>Where an expression that starts at <paramref name="i"/> ends: before the first top-level comma, semicolon or closing bracket.</summary>
    private static int ExprEnd(string s, int i)
    {
        int depth = 0, blocks = 0, last = i;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"' || c == '\'') { i = SkipString(s, i); last = i; continue; }
            if (c == '[' && LongLevel(s, i) is var lv and >= 0) { i = SkipLong(s, i, lv); last = i; continue; }
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') { i = Trivia(s, i); continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (IdentStart(c))
            {
                int j = i; while (j < s.Length && IdentPart(s[j])) j++;
                string w = s[i..j];
                if (OpensBlock(w)) blocks++; else if (ClosesBlock(w)) blocks--;
                i = j; last = i; continue;
            }
            if (blocks > 0) { i++; last = i; continue; }
            if (c is '(' or '{' or '[') { depth++; i++; last = i; continue; }
            if (c is ')' or '}' or ']') { if (depth == 0) return last; depth--; i++; last = i; continue; }
            if ((c == ',' || c == ';') && depth == 0) return last;
            i++; last = i;
        }
        return last;
    }

    private static LuaLiteralKind Classify(string v)
    {
        if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0] && SkipString(v, 0) == v.Length) return LuaLiteralKind.String;
        if (NumberRx.IsMatch(v)) return LuaLiteralKind.Number;
        if (v is "true" or "false") return LuaLiteralKind.Bool;
        if (v == "nil") return LuaLiteralKind.Nil;
        return LuaLiteralKind.Other;
    }

    /// <summary>Reads the table constructor that opens at <paramref name="open"/>.</summary>
    /// <param name="s">The source.</param>
    /// <param name="open">Index of the opening brace.</param>
    /// <returns>The table, or null when the text is not a well-formed table.</returns>
    public static LuaTable? ParseTable(string s, int open)
    {
        if (open >= s.Length || s[open] != '{') return null;
        var entries = new List<LuaEntry>();
        int i = open + 1;
        while (true)
        {
            i = Trivia(s, i);
            if (i >= s.Length) return null;
            if (s[i] == '}') break;
            int start = i;
            string? key = null;
            int vs = i;
            if (IdentStart(s[i]))
            {
                int j = i; while (j < s.Length && IdentPart(s[j])) j++;
                int k = Trivia(s, j);
                if (k < s.Length && s[k] == '=' && (k + 1 >= s.Length || s[k + 1] != '=')) { key = s[i..j]; vs = Trivia(s, k + 1); }
            }
            else if (s[i] == '[' && LongLevel(s, i) < 0)
            {
                int k = Trivia(s, i + 1);
                string? lit = null;
                if (k < s.Length && (s[k] == '"' || s[k] == '\'')) { int e = SkipString(s, k); if (e <= k + 1) return null; lit = s[(k + 1)..(e - 1)]; k = e; }
                else { while (k < s.Length && s[k] != ']') k++; }
                k = Trivia(s, k);
                if (k >= s.Length || s[k] != ']') return null;
                k = Trivia(s, k + 1);
                if (k >= s.Length || s[k] != '=') return null;
                key = lit; vs = Trivia(s, k + 1);
            }
            LuaTable? sub = null;
            int ve;
            if (vs < s.Length && s[vs] == '{' && ParseTable(s, vs) is { } t && Trivia(s, t.Close + 1) is var after && after < s.Length && (s[after] is ',' or ';' or '}')) { sub = t; ve = t.Close + 1; }
            else ve = ExprEnd(s, vs);
            if (ve <= vs && sub == null) return null;
            int sep = Trivia(s, ve);
            int sepAt = -1;
            if (sep < s.Length && (s[sep] == ',' || s[sep] == ';')) { sepAt = sep; sep++; }
            else if (sep >= s.Length || s[sep] != '}') return null;
            string text = s[vs..ve];
            entries.Add(new LuaEntry { Key = key, Start = start, ValueStart = vs, ValueEnd = ve, Kind = sub != null ? LuaLiteralKind.Table : Classify(text), Table = sub, SeparatorAt = sepAt });
            i = sep;
        }
        var table = new LuaTable { Open = open, Close = i };
        table.Entries.AddRange(entries);
        return table;
    }

    /// <summary>Every call of <paramref name="path"/> (e.g. <c>V_Player.RegisterOutfit</c>) in the source, in text order. Calls inside comments and strings are not counted.</summary>
    /// <param name="s">The source.</param>
    /// <param name="path">The dotted name.</param>
    /// <returns>The call sites.</returns>
    public static List<LuaCallSite> FindCalls(string s, string path)
    {
        var found = new List<LuaCallSite>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') { i = Trivia(s, i); continue; }
            if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
            if (c == '[' && LongLevel(s, i) is var lv and >= 0) { i = SkipLong(s, i, lv); continue; }
            if (!IdentStart(c)) { i++; continue; }
            int start = i;
            // A dotted name; one that follows a '.' or ':' is the middle of another chain.
            bool inner = start > 0 && (s[start - 1] == '.' || s[start - 1] == ':');
            int j = i;
            while (true)
            {
                while (j < s.Length && IdentPart(s[j])) j++;
                if (j + 1 < s.Length && s[j] == '.' && IdentStart(s[j + 1])) { j++; continue; }
                break;
            }
            i = j;
            if (inner || s[start..j] != path) continue;
            int p = Trivia(s, j);
            if (p >= s.Length) continue;
            var call = new LuaCallSite { Path = path, Start = start, End = j };
            if (s[p] == '{')
            {
                var t = ParseTable(s, p);
                if (t == null) continue;
                call.Arguments.Add(new LuaArgument { Start = p, End = t.Close + 1, Table = t });
                found.Add(call); i = t.Close + 1;
            }
            else if (s[p] == '(')
            {
                int a = p + 1;
                bool ok = true;
                while (true)
                {
                    a = Trivia(s, a);
                    if (a >= s.Length) { ok = false; break; }
                    if (s[a] == ')') { a++; break; }
                    int astart = a, aend;
                    LuaTable? tbl = null;
                    if (s[a] == '{' && ParseTable(s, a) is { } t && Trivia(s, t.Close + 1) is var after && after < s.Length && (s[after] is ',' or ')')) { tbl = t; aend = t.Close + 1; }
                    else aend = ExprEnd(s, a);
                    call.Arguments.Add(new LuaArgument { Start = astart, End = aend, Table = tbl });
                    a = Trivia(s, aend);
                    if (a < s.Length && s[a] == ',') { a++; continue; }
                    if (a < s.Length && s[a] == ')') { a++; break; }
                    ok = false; break;
                }
                if (!ok) continue;
                found.Add(new LuaCallSite { Path = path, Start = start, End = a }.WithArguments(call.Arguments));
                i = a;
            }
        }
        return found;
    }

    private static LuaCallSite WithArguments(this LuaCallSite c, List<LuaArgument> args) { c.Arguments.AddRange(args); return c; }

    /// <summary>A value written as this Lua text as it is (a name such as TppMotherBaseManagementConst.DESIGN_2001).</summary>
    public sealed record Code(string Lua);

    /// <summary>How a value is written back into Lua source.</summary>
    /// <param name="value">A bool, a number, a string or a <see cref="Code"/>.</param>
    /// <returns>The literal text.</returns>
    public static string Literal(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string t => LuaLiteral.Quote(t),
        int n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f => LuaLiteral.FormatNumber(f),
        double d => LuaLiteral.FormatNumber(d),
        Code c => c.Lua,
        _ => throw new ArgumentException("Only a bool, a number or a string can be written as a Lua value."),
    };

    /// <summary>
    /// Sets one field of a table passed to a call, keeping everything else in the source byte for byte. An existing plain
    /// number, string or true/false is replaced; a field that is not there is added to its table (the tables on the way
    /// are added too). A field written as an expression or a variable is never changed.
    /// </summary>
    /// <param name="source">The Lua source.</param>
    /// <param name="table">The table the field path starts in (found in <paramref name="source"/>).</param>
    /// <param name="path">The field path, e.g. <c>["ddMale", "abilities", "silentFootsteps"]</c>.</param>
    /// <param name="value">A bool, a number or a string.</param>
    /// <param name="error">Why it could not be changed.</param>
    /// <param name="overName">Also replace a value written as a name or an expression (an icon path given by a local such as
    /// <c>ICON</c>): the new literal goes in at this one place, and the name and its other uses are left as they are.</param>
    /// <returns>The new source, or null.</returns>
    public static string? SetField(string source, LuaTable table, IReadOnlyList<string> path, object value, out string? error, bool overName = false)
    {
        error = null;
        string literal = Literal(value);
        for (int level = 0; ; level++)
        {
            var entry = table.Find(path[level]);
            bool last = level == path.Count - 1;
            if (entry == null)
            {
                if (path[level].StartsWith('[')) { error = $"the list has no entry {path[level]}"; return null; }
                string text = last ? $"{path[level]} = {literal}" : NestedTable(path.Skip(level).ToList(), literal);
                return Insert(source, table, text);
            }
            if (last)
            {
                if (entry.Kind is LuaLiteralKind.Number or LuaLiteralKind.String or LuaLiteralKind.Bool || (overName && entry.Kind == LuaLiteralKind.Other))
                    return source[..entry.ValueStart] + literal + source[entry.ValueEnd..];
                error = $"\"{path[level]}\" is not a plain value in the script (it is " + (entry.Kind == LuaLiteralKind.Table ? "a table" : "set by a name or an expression") + ")";
                return null;
            }
            if (entry.Kind != LuaLiteralKind.Table || entry.Table == null) { error = $"\"{path[level]}\" is not a table written out in the script"; return null; }
            table = entry.Table;
        }
    }

    /// <summary>
    /// Sets one field of a table passed to a call to a table written as Lua text, keeping everything else in the source
    /// byte for byte. An existing table or nil value is replaced; a field that is not there is added (the tables on the
    /// way are added too). A field written as a plain value is never changed, and one written as an expression or a
    /// variable only with <paramref name="overName"/>.
    /// </summary>
    /// <param name="source">The Lua source.</param>
    /// <param name="table">The table the field path starts in (found in <paramref name="source"/>).</param>
    /// <param name="path">The field path, e.g. <c>["ddMale", "variants"]</c>.</param>
    /// <param name="luaText">The new value: a table constructor as Lua text.</param>
    /// <param name="error">Why it could not be changed.</param>
    /// <param name="overName">Also replace a table given by a name or an expression (a local such as <c>LOOKS</c>), when the
    /// caller has checked that the new text gives back what the name stands for in every call: the new table goes in at
    /// this one place, and the name and its other uses are left as they are.</param>
    /// <returns>The new source, or null.</returns>
    public static string? ReplaceTable(string source, LuaTable table, IReadOnlyList<string> path, string luaText, out string? error, bool overName = false)
    {
        error = null;
        for (int level = 0; ; level++)
        {
            var entry = table.Find(path[level]);
            bool last = level == path.Count - 1;
            if (entry == null) return Insert(source, table, NestedTable(path.Skip(level).ToList(), luaText));
            if (last)
            {
                if (entry.Kind is LuaLiteralKind.Table or LuaLiteralKind.Nil || (overName && entry.Kind == LuaLiteralKind.Other))
                    return source[..entry.ValueStart] + luaText + source[entry.ValueEnd..];
                error = $"\"{path[level]}\" is not a table in the script (it is " + (entry.Kind == LuaLiteralKind.Other ? "set by a name or an expression" : "a plain value") + ")";
                return null;
            }
            if (entry.Kind != LuaLiteralKind.Table || entry.Table == null) { error = $"\"{path[level]}\" is not a table written out in the script"; return null; }
            table = entry.Table;
        }
    }

    /// <summary>
    /// Removes one field of a table passed to a call, with its separator. When the field is alone on its line, the whole
    /// line goes. A field that is not there is no error.
    /// </summary>
    /// <param name="source">The Lua source.</param>
    /// <param name="table">The table the field path starts in (found in <paramref name="source"/>).</param>
    /// <param name="path">The field path, e.g. <c>["ddFemale"]</c>.</param>
    /// <param name="error">Why it could not be removed.</param>
    /// <returns>The new source, or null.</returns>
    public static string? RemoveField(string source, LuaTable table, IReadOnlyList<string> path, out string? error)
    {
        error = null;
        for (int level = 0; ; level++)
        {
            var entry = table.Find(path[level]);
            if (entry == null) return source;
            if (level < path.Count - 1)
            {
                if (entry.Kind != LuaLiteralKind.Table || entry.Table == null) { error = $"\"{path[level]}\" is not a table written out in the script"; return null; }
                table = entry.Table;
                continue;
            }
            int from = entry.Start, to = entry.SeparatorAt >= 0 ? entry.SeparatorAt + 1 : entry.ValueEnd;
            // The last entry without a separator: take the comma before it instead, so no dangling comma is left.
            if (entry.SeparatorAt < 0 && table.Entries.Count > 1 && table.Entries[^2] is { SeparatorAt: >= 0 } prev && ReferenceEquals(entry, table.Entries[^1]))
                from = prev.SeparatorAt;
            int ls = source.LastIndexOf('\n', Math.Max(0, from - 1)) + 1;
            int eol = source.IndexOf('\n', to);
            if (source[ls..from].Trim().Length == 0 && eol >= 0 && source[to..eol].Trim().Length == 0)
                return source[..ls] + source[(eol + 1)..];
            int end = to;
            while (end < source.Length && source[end] == ' ') end++;
            return source[..from] + source[end..];
        }
    }

    private static string NestedTable(IReadOnlyList<string> path, string literal) =>
        path.Count == 1 ? $"{path[0]} = {literal}" : $"{path[0]} = {{ {NestedTable(path.Skip(1).ToList(), literal)} }}";

    private static string Insert(string s, LuaTable t, string text)
    {
        bool multi = s.AsSpan(t.Open, t.Close - t.Open).Contains('\n');
        if (t.Entries.Count == 0)
        {
            if (!multi) return s[..(t.Open + 1)] + " " + text + " " + s[(t.Open + 1)..];
            string outer = LineIndent(s, t.Open);
            return s[..(t.Open + 1)] + "\n" + outer + "  " + text + ",\n" + outer + s[t.Close..];
        }
        var last = t.Entries[^1];
        int vEnd = last.ValueEnd, sepEnd = last.SeparatorAt >= 0 ? last.SeparatorAt + 1 : vEnd;
        string head = s[..vEnd] + (last.SeparatorAt < 0 ? "," : "") + s[vEnd..sepEnd];
        int ins = sepEnd;
        if (multi && s.IndexOf('\n', sepEnd) is var eol and >= 0)
        {
            string tail = s[sepEnd..eol].Trim();
            if (tail.Length == 0 || tail.StartsWith("--", StringComparison.Ordinal)) ins = eol;
        }
        string add = multi ? "\n" + LineIndent(s, last.Start) + text + "," : " " + text + ",";
        return head + s[sepEnd..ins] + add + s[ins..];
    }

    private static string LineIndent(string s, int at)
    {
        int ls = s.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        int e = ls; while (e < s.Length && (s[e] == ' ' || s[e] == '\t')) e++;
        return s[ls..e];
    }
}
