using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace Svfm.Core.VFramework;

/// <summary>The "Own Lua" step of a thrown item: its text loaded from a .lua file, and its syntax check (issue #110).</summary>
public static class OwnLua
{
    /// <summary>The largest file that "Load from file…" copies into a step.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The text of a .lua file, or why it is refused. The text is read as UTF-8, without a byte order mark, with \n line ends.</summary>
    public static (string? Code, string? Refused) Load(string path)
    {
        long size = new FileInfo(path).Length;
        if (size > MaxBytes) return (null, $"{Path.GetFileName(path)} is {size / 1024.0:0.#} KB. A step takes a file of {MaxBytes / 1024} KB or less.");
        var bytes = File.ReadAllBytes(path);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return (null, $"{Path.GetFileName(path)} is not text: it holds NUL bytes.");
        string text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        return (text.Replace("\r\n", "\n").Replace('\r', '\n'), null);
    }

    /// <summary>The first syntax error of <paramref name="code"/> as "Line N: message", or null. The code is compiled, not run.</summary>
    public static string? SyntaxError(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        try { new Script(CoreModules.None).LoadString(code, null, "own Lua"); return null; }
        catch (SyntaxErrorException e)
        {
            string m = e.DecoratedMessage ?? e.Message;
            return Regex.Match(m, @"\((\d+),[^)]*\):\s*(.*)") is { Success: true } p ? $"Lua syntax error, line {p.Groups[1].Value}: {p.Groups[2].Value}" : "Lua syntax error: " + m;
        }
    }
}
