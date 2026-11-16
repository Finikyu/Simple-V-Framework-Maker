using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Svfm.Core.Project;

/// <summary>
/// A small, readable JSON dump of the parsed data of a loaded mod, for the Developer screen. It walks public
/// properties, so it follows the model without a second list to keep. Binary data is never written: a byte array
/// becomes "(N bytes)". Long text and long lists are cut, a repeated object (a cycle) becomes "(cycle)", and deep
/// objects stop at <see cref="MaxDepth"/>.
/// </summary>
public static class ProjectDump
{
    public const int MaxDepth = 8, MaxItems = 200, MaxText = 400;

    public static string ToJson(object? root)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            Write(w, root, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Write(Utf8JsonWriter w, object? v, int depth, HashSet<object> path)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); return;
            case string s: w.WriteStringValue(s.Length > MaxText ? s[..MaxText] + $"... ({s.Length} chars)" : s); return;
            case bool b: w.WriteBooleanValue(b); return;
            case byte[] bytes: w.WriteStringValue($"({bytes.Length} bytes)"); return;
            case float f when !float.IsFinite(f): w.WriteStringValue(f.ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
            case double d when !double.IsFinite(d): w.WriteStringValue(d.ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
            case float or double or decimal or sbyte or byte or short or ushort or int or uint or long or ulong:
                w.WriteRawValue(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!); return;
            case Enum e: w.WriteStringValue(e.ToString()); return;
            case char or Guid or DateTime or DateTimeOffset or TimeSpan: w.WriteStringValue(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); return;
            case Delegate or Type or Stream or MemberInfo: w.WriteStringValue("(" + v.GetType().Name + ")"); return;
        }
        if (depth >= MaxDepth) { w.WriteStringValue("(too deep: " + v.GetType().Name + ")"); return; }
        if (!path.Add(v)) { w.WriteStringValue("(cycle)"); return; }
        try
        {
            if (v is IDictionary dict)
            {
                w.WriteStartObject();
                int n = 0;
                foreach (DictionaryEntry kv in dict)
                {
                    if (n++ >= MaxItems) { w.WriteString("...", $"({dict.Count - MaxItems} more)"); break; }
                    w.WritePropertyName(Convert.ToString(kv.Key, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                    Write(w, kv.Value, depth + 1, path);
                }
                w.WriteEndObject();
            }
            else if (v is IEnumerable list)
            {
                w.WriteStartArray();
                int n = 0;
                foreach (var x in list)
                {
                    if (n++ >= MaxItems) { w.WriteStringValue("... (more items not shown)"); break; }
                    Write(w, x, depth + 1, path);
                }
                w.WriteEndArray();
            }
            else
            {
                w.WriteStartObject();
                foreach (var p in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                    object? val;
                    try { val = p.GetValue(v); }
                    catch (Exception ex) { w.WriteString(p.Name, "(unreadable: " + (ex.InnerException ?? ex).GetType().Name + ")"); continue; }
                    w.WritePropertyName(p.Name);
                    Write(w, val, depth + 1, path);
                }
                w.WriteEndObject();
            }
        }
        finally { path.Remove(v); }
    }
}
