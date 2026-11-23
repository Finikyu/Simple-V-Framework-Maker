using System.IO.Compression;
using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace Svfm.Core.Text;

/// <summary>The spelling of the spell check (issue #135): UK or US English.</summary>
public enum SpellLanguage { UK, US }

/// <summary>
/// Finds the misspelled words of a text with a Hunspell dictionary (embedded, so the release stays one .exe).
/// One instance per language, loaded on first use (about a quarter of a second).
/// </summary>
public sealed partial class SpellChecker
{
    private static readonly Dictionary<SpellLanguage, SpellChecker> Loaded = [];
    private readonly WordList _words;

    private SpellChecker(WordList words) => _words = words;

    /// <summary>The checker of a language; the first call loads its dictionary.</summary>
    public static SpellChecker For(SpellLanguage language)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(language, out var c)) return c;
            string name = language == SpellLanguage.US ? "en_US" : "en_GB";
            using var dic = Open(name + ".dic");
            using var aff = Open(name + ".aff");
            return Loaded[language] = new SpellChecker(WordList.CreateFromStreams(dic, aff));
        }
    }

    private static Stream Open(string file)
    {
        var s = typeof(SpellChecker).Assembly.GetManifestResourceStream("Svfm.Spell." + file + ".gz")
            ?? throw new InvalidOperationException("The dictionary " + file + " is not in the build.");
        return new GZipStream(s, CompressionMode.Decompress);
    }

    // Letters, digits, '_' and apostrophes; a word with a digit or '_' is an ID (WP_10603) and is not checked.
    [GeneratedRegex(@"[\p{L}\p{N}_'’]+")]
    private static partial Regex WordRegex();

    /// <summary>The misspelled words of <paramref name="text"/>, as start and length. Words in <paramref name="extra"/> are correct.</summary>
    public List<(int Start, int Length)> Misspelled(string? text, ICollection<string>? extra = null)
    {
        var bad = new List<(int, int)>();
        if (string.IsNullOrEmpty(text)) return bad;
        foreach (Match m in WordRegex().Matches(text))
        {
            // Quotes round a word ('word') are not part of it.
            int start = m.Index, end = m.Index + m.Length;
            while (start < end && text[start] is '\'' or '’') start++;
            while (end > start && text[end - 1] is '\'' or '’') end--;
            if (end - start < 2) continue;
            string word = text[start..end];
            if (word.Any(c => char.IsDigit(c) || c == '_')) continue;
            if (word.All(c => !char.IsLower(c))) continue; // TPP, RPG: acronyms
            if (!IsCorrect(word, extra)) bad.Add((start, end - start));
        }
        return bad;
    }

    /// <summary>True when the word is in the dictionary or in <paramref name="extra"/>.</summary>
    public bool IsCorrect(string word, ICollection<string>? extra = null)
    {
        word = word.Replace('’', '\'');
        return (extra?.Contains(word) ?? false) || _words.Check(word);
    }

    /// <summary>At most <paramref name="max"/> corrections of a word.</summary>
    public List<string> Suggest(string word, int max = 6) => _words.Suggest(word.Replace('’', '\'')).Take(max).ToList();
}
