using Svfm.Core.Formats;

namespace Svfm.Core.VFramework;

/// <summary>
/// A mod's own text in every game language. Each language goes into the vanilla language package
/// for it (lang_default_data_&lt;lang&gt;.fpk) as one new .lng2 file; SnakeBite and DMM merge the
/// package with vanilla, so no vanilla text is changed.
/// </summary>
public sealed class ModText
{
    /// <summary>Language package name → .lng2 suffix. The Chinese package holds Traditional Chinese ("cht").</summary>
    public static readonly IReadOnlyList<(string package, string suffix, string display)> Languages =
    [
        ("eng", "eng", "English"), ("fre", "fre", "French"), ("ger", "ger", "German"), ("ita", "ita", "Italian"),
        ("spa", "spa", "Spanish"), ("por", "por", "Portuguese"), ("rus", "rus", "Russian"), ("ara", "ara", "Arabic"),
        ("jpn", "jpn", "Japanese"), ("kor", "kor", "Korean"), ("chn", "cht", "Chinese (Traditional)"),
    ];

    /// <summary>File name of the .lng2 (without language and extension), e.g. "svfm_tbo".</summary>
    public required string FileBase { get; init; }

    /// <summary>Lang ID → language package name → text. English is the fallback for missing languages.</summary>
    public Dictionary<string, Dictionary<string, string>> Texts { get; } = new(StringComparer.Ordinal);

    public void Set(string langId, string english) => Set(langId, "eng", english);

    /// <summary>The package name of a language code: the app and the files call Traditional Chinese "cht", its package is "chn".</summary>
    /// <param name="language">A language code.</param>
    /// <returns>The package name.</returns>
    public static string Package(string language) => language == "cht" ? "chn" : language;

    public void Set(string langId, string language, string text)
    {
        language = Package(language);
        if (!Texts.TryGetValue(langId, out var per)) Texts[langId] = per = new(StringComparer.Ordinal);
        per[language] = text;
    }

    public string? Get(string langId, string language) => GetPackage(langId, Package(language));

    private string? GetPackage(string langId, string language) =>
        Texts.TryGetValue(langId, out var per) ? (per.TryGetValue(language, out var t) ? t : per.GetValueOrDefault("eng")) : null;

    /// <summary>Archive files: one language package per language.</summary>
    public Dictionary<string, byte[]> BuildPackages()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (package, suffix, _) in Languages)
        {
            var lang = new LangFile();
            foreach (var id in Texts.Keys) lang.Set(id, Get(id, package) ?? "");
            var pkg = new FoxPackage();
            pkg.Files.Add(new PackageFile($"/Assets/tpp/lang/ui/{FileBase}.{suffix}.lng2", lang.Write()));
            files[$"/Assets/tpp/pack/ui/lang/lang_default_data_{package}.fpk"] = pkg.Write();
        }
        return files;
    }
}
