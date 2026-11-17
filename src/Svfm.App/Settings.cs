using System.Text.Json;

namespace Svfm.App;

/// <summary>A project in the Recent projects list: its file, name, a line about it and a small picture (PNG, base64).</summary>
public sealed class RecentProject
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? IconPng { get; set; }
}

/// <summary>What the app remembers between runs (a small JSON file in the user's app-data folder).</summary>
public sealed class Settings
{
    public string GameFolder { get; set; } = "";
    public string LastFolder { get; set; } = "";
    /// <summary>
    /// The 3D preview: "Off" or "Stills" (pictures drawn by the software renderer, any machine). An old saved "Live"
    /// counts as Stills. Off is the default: the owner asked for it ("3D Preview should be off by default"), because
    /// drawing the model costs a second or two on every item and most edits don't need it.
    /// </summary>
    public string Preview { get; set; } = "Off";
    /// <summary>True once the user has picked a mode.</summary>
    public bool PreviewChosen { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public PreviewMode EffectivePreview => PreviewChosen && Enum.TryParse<PreviewMode>(Preview, out var m) && Enum.IsDefined(m) ? m : PreviewMode.Off;
    public bool CheckClashes { get; set; }
    /// <summary>
    /// Draws the 3D preview at half resolution. The owner asked for a way to make it faster: "The 3D Preview is very slow,
    /// it was faster in mock ups. Anyway to make it optionally GPU Bound? Put it by All 3D:on."
    ///
    /// This is not GPU acceleration, and the honest reason is worth writing down: the preview is drawn by a software
    /// rasteriser in Svfm.Core (IconRenderer), which is what makes it work identically on every machine and lets the same
    /// code produce the shipped icon. Moving it to the GPU means a second renderer written in shaders, which would then
    /// have to be kept in step with the software one or the preview would stop matching the icon it is previewing. The
    /// real cost was never the GPU: it was supersampling a 360×540 picture four times over on every mouse move. Drawing
    /// at half resolution and without supersampling is roughly eight times less work and is what this switch does.
    /// </summary>
    public bool FastPreview { get; set; }
    /// <summary>Developer mode (the switch on the Developer screen): the throw-script editor shows the Announce step and its
    /// diagnostics. Off for normal users (issue #97).</summary>
    public bool DeveloperMode { get; set; }
    /// <summary>Developer tab: a new port with a long name gets a coded ID (see <c>Intake.UniqueIds</c>). Off by default.</summary>
    public bool UniqueModIds { get; set; }
    /// <summary>Projects saved or opened lately, newest first (at most 6).</summary>
    public List<RecentProject> Recent { get; set; } = [];

    /// <summary>Puts a project at the top of the recent list.</summary>
    public void AddRecent(RecentProject r)
    {
        Recent.RemoveAll(x => string.Equals(x.Path, r.Path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, r);
        if (Recent.Count > 6) Recent.RemoveRange(6, Recent.Count - 6);
        Save();
    }
    public string ModsFolder { get; set; } = "";

    /// <summary>Where the file lives; tests point this elsewhere.</summary>
    public static string? PathOverride { get; set; }

    /// <summary>The folder of settings.json, error.log and the logs folder: "settings" next to the .exe (the owner: the settings
    /// must not leave the .exe folder).</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "settings");

    private static string FilePath => PathOverride ?? Path.Combine(Folder, "settings.json");

    /// <summary>The settings file's path (the error log sits next to it).</summary>
    public static string FilePathPublic => FilePath;

    private static readonly JsonSerializerOptions ReadJson = new() { RespectNullableAnnotations = true };

    public static Settings Load()
    {
        Settings s;
        // A new .exe folder starts fresh: the old %APPDATA% settings are not read (the owner: settings live only next to the .exe).
        string file = FilePath;
        // A null where a value must be (a hand-edited file) is a JsonException, so the app starts with defaults instead of crashing.
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), ReadJson) ?? new Settings(); s.Recent.RemoveAll(r => r == null); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { s = new Settings(); }
        // The owner: "It shouldn't assume the original location on first launch for the managers but it should take an
        // educated guess on TPP.exe". So the game folder is guessed when it is empty, and the mods folder never is.
        if (s.GameFolder.Length == 0 && GuessGameFolder() is { } guess) s.GameFolder = guess;
        // First start: write the file at once, so settings.json is next to the .exe from the first run (the tester looked for it).
        if (PathOverride == null && !File.Exists(FilePath)) s.Save();
        return s;
    }

    /// <summary>
    /// Looks for MGSV: The Phantom Pain in the usual Steam places. Returns the folder holding mgsvtpp.exe, or null.
    /// </summary>
    /// <remarks>
    /// Only a guess: the user can always change it in Settings. It checks the Steam library folders listed in
    /// libraryfolders.vdf as well as the default install, because the game is often on a second drive.
    /// </remarks>
    public static string? GuessGameFolder()
    {
        var roots = new List<string>();
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (pf.Length > 0) roots.Add(Path.Combine(pf, "Steam"));
        // Steam's own record of where it is, which covers a Steam installed anywhere. Windows only: the app is a Windows
        // tool, but the registry call is guarded so the headless tools still run elsewhere.
        if (OperatingSystem.IsWindows())
            foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
                try
                {
                    if (Microsoft.Win32.Registry.GetValue(key, "SteamPath", null) is string sp && sp.Length > 0) roots.Add(sp.Replace('/', '\\'));
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

        var libraries = new List<string>(roots);
        foreach (var root in roots)
        {
            // libraryfolders.vdf lists the other drives; "path" "E:\\Games\\Steam" is the line we want.
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(vdf)) continue;
                foreach (var line in File.ReadLines(vdf))
                {
                    int i = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    // TrimEntries: the tabs between "path" and the value are a part too, and would be parts[1].
                    var parts = line[i..].Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2 && parts[1].Length > 2) libraries.Add(parts[1].Replace("\\\\", "\\"));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(lib, "steamapps", "common", "MGS_TPP");
            if (File.Exists(Path.Combine(candidate, "mgsvtpp.exe"))) return candidate;
        }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* settings are a convenience */ }
    }
}
