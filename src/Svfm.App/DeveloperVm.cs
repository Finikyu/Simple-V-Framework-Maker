using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;

namespace Svfm.App;

/// <summary>The Developer tab: the session log as it is written, a self-check, a note box and a report file for sending back.</summary>
public sealed class DeveloperVm : Observable
{
    private readonly MainVm _main;
    private readonly DispatcherTimer _tick;
    private bool _dirty;
    private string _text = "";
    private string _note = "";
    private int _level;
    private string _status = "";
    private string? _other;
    private object? _otherFor;

    public DeveloperVm(MainVm main)
    {
        _main = main;
        DevLog.Added += _ => _dirty = true;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _tick.Tick += (_, _) => { if (_dirty) { _dirty = false; Rebuild(); } };
        _tick.Start();
        AddNote = new Command(() => { if (_note.Trim().Length > 0) { DevLog.Info("note", _note.Trim()); Note = ""; } });
        SelfCheck = new Command(RunSelfCheck);
        SaveReport = new Command(WriteReport);
        OpenFolder = new Command(() => Open(DevLog.Folder));
        OpenSettings = new Command(() => Open(Path.GetDirectoryName(Settings.FilePathPublic)!));
        ShowLog = new Command(() => { _other = null; Rebuild(); });
        ShowBuildReport = new Command(() => { _otherFor = _main.Session.Project; _other = DevLog.LastBuildReport.Length > 0 ? DevLog.LastBuildReport : "No build report yet. Open a mod and open the Review and build screen first."; Rebuild(); });
        ShowDump = new Command(DumpMod);
        Rebuild();
    }

    private void Open(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
        catch (Exception e) { Status = "Can't open the folder: " + e.Message; }
    }

    private void DumpMod()
    {
        var p = _main.Session.Project;
        _otherFor = p;
        _other = p == null ? "No mod is open. Open a mod first." : ProjectDump.ToJson(p);
        if (p != null)
        {
            try { Directory.CreateDirectory(DevLog.Folder); string f = Path.Combine(DevLog.Folder, $"svfm-dump-{DateTime.Now:yyyyMMdd-HHmmss}.json"); File.WriteAllText(f, _other); Status = "Dump saved: " + f; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "Can't save the dump: " + e.Message; }
        }
        Rebuild();
    }

    /// <summary>Rebuilds the text now. Called when the tab is opened, so it never shows a stale log while the 300 ms
    /// timer is still waiting (and so a headless run, where timers do not tick, still sees the current log).</summary>
    public void Refresh() { _dirty = false; Rebuild(); }

    public static IReadOnlyList<string> Levels { get; } = ["Everything", "Warnings and errors", "Errors and checks only"];
    public int Level { get => _level; set { if (Set(ref _level, value)) Rebuild(); } }
    /// <summary>Developer mode: the throw-script editor shows the Announce step and its diagnostics (issue #97). Kept in the settings.</summary>
    public bool DeveloperMode
    {
        get => _main.Session.Settings.DeveloperMode;
        set
        {
            if (value == DeveloperMode) return;
            _main.Session.Settings.DeveloperMode = value; _main.Session.Settings.Save();
            DevLog.Info("ui", value ? "Developer mode on" : "Developer mode off");
            foreach (var i in _main.Editor?.Items ?? []) i.Script?.Reload();
            Raise();
        }
    }
    public string Text { get => _text; private set => Set(ref _text, value); }
    public string Note { get => _note; set => Set(ref _note, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string FileLine => DevLog.FilePath == null ? "The log file could not be created; the log is kept in memory only." : "Log file: " + DevLog.FilePath;
    /// <summary>"Unique mod IDs": saved in the settings; acts on the next mod opened, not on an open project.</summary>
    public bool UniqueModIds
    {
        get => _main.Session.Settings.UniqueModIds;
        set
        {
            if (value == UniqueModIds) return;
            _main.Session.Settings.UniqueModIds = Svfm.Core.Project.Intake.UniqueIds = value;
            _main.Session.Settings.Save();
            Raise(nameof(UniqueModIds));
        }
    }
    public Command AddNote { get; }
    public Command SelfCheck { get; }
    public Command SaveReport { get; }
    public Command OpenFolder { get; }
    public Command OpenSettings { get; }
    public Command ShowLog { get; }
    public Command ShowBuildReport { get; }
    public Command ShowDump { get; }
    /// <summary>Issue #122 (N3): the Close button of the developer screen; F12 does the same.</summary>
    public Command Close => _close ??= new Command(() => _main.ShowDev = false);
    private Command? _close;
    /// <summary>The whole log as text, for the clipboard.</summary>
    public string AllText => string.Join('\n', DevLog.Lines);

    private void Rebuild()
    {
        if (_other != null && _otherFor != _main.Session.Project) _other = null; // another mod was opened since
        if (_other != null) { Text = _other; Raise(nameof(FileLine)); return; }
        var lines = DevLog.Lines.Where(l => l.StartsWith('#') || l.StartsWith("    |") || Keep(l));
        var sb = new StringBuilder();
        foreach (var l in lines.TakeLast(1500)) sb.Append(l).Append('\n');
        Text = sb.ToString();
        Raise(nameof(FileLine));
    }

    private bool Keep(string l)
    {
        if (_level == 0) return true;
        string lvl = l.Length > 25 ? l.Substring(25, 5).Trim() : "";
        return _level == 1 ? lvl is "WARN" or "ERROR" or "CHECK" : lvl is "ERROR" or "CHECK";
    }

    // ---- Self-check ----

    private void RunSelfCheck()
    {
        var sw = Stopwatch.StartNew();
        int pass = 0, total = 0;
        void C(string name, Func<(bool ok, string detail)> f)
        {
            total++;
            try { var (ok, detail) = f(); if (ok) pass++; DevLog.Check(name, ok, detail); }
            catch (Exception e) { DevLog.Check(name, false, e.GetType().Name + ": " + e.Message); DevLog.Error("selfcheck", e); }
        }
        DevLog.Info("selfcheck", "Self-check started");
        var s = _main.Session;
        C("log file is being written", () => (DevLog.FilePath != null && File.Exists(DevLog.FilePath), DevLog.FilePath ?? "none"));
        C("settings folder is writable", () => { string p = Path.Combine(Path.GetDirectoryName(Settings.FilePathPublic)!, "write-test.tmp"); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); File.Delete(p); return (true, Path.GetDirectoryName(p)!); });
        C("bundled fonts load", () =>
        {
            bool a = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(new Avalonia.Media.FontFamily("fonts:Plex#IBM Plex Sans")), out var g1);
            bool b = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(new Avalonia.Media.FontFamily("fonts:Plex#IBM Plex Mono")), out var g2);
            bool c = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(new Avalonia.Media.FontFamily("fonts:Plex#Rajdhani")), out var g3);
            return (a && b && c, $"{(a ? g1?.FamilyName ?? "loaded" : "missing")}, {(b ? g2?.FamilyName ?? "loaded" : "missing")}, {(c ? g3?.FamilyName ?? "loaded" : "missing")}");
        });
        C("path dictionary", () => (PathDictionary.Bundled.Count > 100000, PathDictionary.Bundled.Count.ToString("N0") + " paths"));
        C("picture codec (PNG)", () => { var img = new RgbaImage(8, 4); img.Pixels[0] = 200; var back = Png.Decode(Png.Encode(img)); return (back.Width == 8 && back.Pixels[0] == 200, "8x4 round trip"); });
        C("icon format (ftex) round trip", () =>
        {
            var img = new RgbaImage(256, 128); for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = (byte)(i / 512); img.Pixels[i + 1] = 90; img.Pixels[i + 2] = 40; img.Pixels[i + 3] = 255; }
            var ft = Svfm.Core.Conversion.SuitIcon.ToGameFiles(img);
            var parts = ft.Ftexs.ToDictionary(k => int.Parse(k.Key.ToString()), k => k.Value);
            var back = FtexTexture.Read(ft.Ftex).DecodeLargestAvailable(parts);
            return (back.Width == 256 && back.Height == 128, $"{back.Width}x{back.Height}, {ft.Ftexs.Count} mip file(s)");
        });
        C("game folder", () => (s.Game != null, s.Game == null ? "not set: opening mods and the 3D preview need it" : s.Settings.GameFolder));
        if (s.Game != null)
        {
            C("game data: equipment tables", () => (s.Game.Contains(SupportTables.Path), SupportTables.Path));
            DevLog.Info("selfcheck", "game data: damage tables " + (s.Game.Contains(EquipTables.DamageParametersPath) ? "present" : "absent (thrown items can't get their own damage rows)"));
        }
        C("preview mode", () => (true, "mode " + s.Settings.EffectivePreview));
        if (s.Project != null)
        {
            C("project builds (dry run)", () =>
            {
                var r = ProjectBuilder.Build(s.Project, s.Game);
                foreach (var e in r.Errors) DevLog.Error("selfcheck", "build: " + e);
                foreach (var w in r.Warnings) DevLog.Warn("selfcheck", "build: " + w);
                return (r.Ok, $"{s.Project.Items.Count} item(s), {r.Mod.ArchiveFiles.Count} files, {r.Errors.Count} error(s), {r.Warnings.Count} warning(s)");
            });
            foreach (var i in s.Project.Items)
            {
                var item = i;
                C($"Lua for {item.Kind} \"{item.Title}\"", () => { var lua = item.LuaBlock(); return (lua.Length > 0, lua.Length + " chars"); });
            }
        }
        else DevLog.Info("selfcheck", "no project open: open a mod and run the self-check again to include the project checks");
        DevLog.Write("CHECK", "selfcheck", $"SUMMARY  {pass}/{total} passed in {sw.ElapsedMilliseconds} ms");
        Status = $"Self-check: {pass} of {total} passed.";
    }

    // ---- Report ----

    private void WriteReport()
    {
        try
        {
            Directory.CreateDirectory(DevLog.Folder);
            string file = Path.Combine(DevLog.Folder, $"svfm-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Simple V Framework Maker: test report");
            sb.AppendLine("=====================================");
            var s = _main.Session;
            sb.AppendLine($"Game folder: {(s.Settings.GameFolder.Length > 0 ? s.Settings.GameFolder : "(not set)")}");
            sb.AppendLine($"Preview: {s.Settings.EffectivePreview}");
            if (s.Project != null)
            {
                sb.AppendLine($"Project: {s.Project.Name} {s.Project.Version}; source: {s.Source?.SourcePath}");
                foreach (var i in s.Project.Items) sb.AppendLine($"  - {i.Kind}: {i.Title} ({i.Id}), {i.Files.Count} files");
            }
            else sb.AppendLine("Project: none open");
            sb.AppendLine();
            sb.AppendLine("Log");
            sb.AppendLine("---");
            foreach (var l in DevLog.Lines) sb.AppendLine(l);
            File.WriteAllText(file, sb.ToString());
            DevLog.Info("report", "Report written: " + file);
            Status = "Report saved: " + file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "Can't save the report: " + e.Message; }
    }
}
