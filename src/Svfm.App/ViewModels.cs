using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using System.Threading;
using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Mods;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>The window's file and folder pickers, so the screens can be run without a window.</summary>
public interface IDialogs
{
    Task<string?> PickFile(string title, params (string name, string[] patterns)[] types);
    Task<string?> SaveFile(string title, string suggestedName, string extension);
    Task<string?> PickFolder(string title);
    /// <summary>A yes/no question. True means the user chose <paramref name="yes"/>.</summary>
    Task<bool> Confirm(string title, string message, string yes, string no);
    /// <summary>The settings window: the game folder and the mod manager's mods folder.</summary>
    Task ShowSettings();
}

/// <summary>The window: header, current screen.</summary>
public sealed class MainVm : Observable
{
    public Session Session { get; }
    public IDialogs Dialogs { get; set; }
    public OpenVm OpenScreen { get; }
    private Observable _screen;

    public MainVm(Session session, IDialogs dialogs)
    {
        Session = session; Dialogs = dialogs;
        OpenScreen = new OpenVm(this);
        _screen = OpenScreen;
        DevLog.Start();
        DevLog.Info("app", $"Started; game folder {(session.Settings.GameFolder.Length > 0 ? session.Settings.GameFolder : "(not set)")}; preview {session.Settings.EffectivePreview}");
        UndoCommand = new Command(async () => { DevLog.Info("edit", "Undo"); await Session.UndoAsync(); }, () => Session.CanUndo);
        SaveCommand = new Command(async () => await SaveAsync(), () => Session.Project != null);
        OpenProjectCommand = new Command(async () => await OpenProjectAsync());
        NewFromModCommand = new Command(async () => await OpenModAsNewProjectAsync());
        OpenSettingsCommand = new Command(async () => await Dialogs.ShowSettings());
        BuildCommand = new Command(() => ShowBuild(), () => Session.Project != null);
        Session.Settings.Recent ??= [];
        Session.Changed += () => { UndoCommand.Raise(); SaveCommand.Raise(); BuildCommand.Raise(); Raise(nameof(HasProject)); Raise(nameof(SaveLabel)); };
    }

    private DeveloperVm? _dev;
    private bool _showDev;
    /// <summary>The Developer tab (log, self-check, report).</summary>
    public DeveloperVm Dev => _dev ??= new DeveloperVm(this);
    public bool ShowDev { get => _showDev; set { if (Set(ref _showDev, value)) { Raise(nameof(ShowApp)); Raise(nameof(ShowHeader)); Raise(nameof(CanSearch)); DevLog.Info("ui", value ? "Developer tab opened" : "Developer tab closed"); if (value) Dev.Refresh(); } } }
    public bool ShowApp => !_showDev;
    /// <summary>File > Developer screen and F12: open the developer log, or go back to the maker.</summary>
    public Command ToggleDev => new(() => ShowDev = !ShowDev);
    /// <summary>The header row shows on the editor, the build screen and the developer screen.</summary>
    public bool ShowHeader => OnOld || _showDev;
    /// <summary>Search (Ctrl+F) looks through the editor's fields, so it works only while the editor is on screen.</summary>
    public bool CanSearch => OnEditor && !_showDev;

    public bool OnFound => _screen is FoundVm;
    public bool OnOld => _screen is EditorVm or BuildVm;
    public Observable Screen { get => _screen; private set { Set(ref _screen, value); Raise(nameof(OnOpen)); Raise(nameof(OnFound)); Raise(nameof(OnOld)); Raise(nameof(ShowHeader)); Raise(nameof(OnEditor)); Raise(nameof(OnBuild)); Raise(nameof(CanSearch)); } }
    public bool OnOpen => _screen is OpenVm;
    public bool HasProject => Session.Project != null;
    public Command UndoCommand { get; }
    public Command SaveCommand { get; }
    public Command OpenProjectCommand { get; }
    /// <summary>Starts a new project from a .mgsv at any time (the owner's "Open..." note).</summary>
    public Command NewFromModCommand { get; }
    /// <summary>Opens the settings window (game folder, mod manager mods folder) from any screen.</summary>
    public Command OpenSettingsCommand { get; }
    public Command BuildCommand { get; }
    public string SaveLabel => Session.SaveLabel;

    public EditorVm? Editor { get; private set; }

    public PreviewMode PreviewMode
    {
        get => Session.Settings.EffectivePreview;
        set
        {
            if (Editor != null) Editor.Preview.Mode = value; else { Session.Settings.PreviewChosen = true; Session.Settings.Preview = value.ToString(); Session.Settings.Save(); }
            Raise(nameof(PreviewOff)); Raise(nameof(PreviewStills)); Raise(nameof(PreviewLabel));
        }
    }
    public string PreviewLabel => PreviewMode == PreviewMode.Off ? "All 3D: off" : "All 3D: on";
    public Command TogglePreview => new(() => PreviewMode = PreviewMode == PreviewMode.Off ? PreviewMode.Stills : PreviewMode.Off);
    public bool OnEditor => _screen is EditorVm;
    public bool OnBuild => _screen is BuildVm;
    public bool PreviewOff { get => PreviewMode == PreviewMode.Off; set { if (value) PreviewMode = PreviewMode.Off; } }
    public bool PreviewStills { get => PreviewMode == PreviewMode.Stills; set { if (value) PreviewMode = PreviewMode.Stills; } }

    public void ShowOpen() => Screen = OpenScreen;

    public void ShowFound(IntakeResult res, string file) => Screen = new FoundVm(this, res, Path.GetFileName(file));

    /// <summary>True while a mod or a project opens. A second open in that time would write the session at the same time (T4, 16 Nov 2026).</summary>
    internal bool Opening => _opening || OpenScreen.Busy;
    private bool _opening;

    /// <summary>Opens a project from the Recent list.</summary>
    public async Task OpenRecentAsync(RecentProject r)
    {
        if (Opening || !await MayReplaceProjectAsync()) return;
        _opening = true;
        try
        {
            // "Open a .mgsv as a new project" remembers the .mgsv itself, which is a zip, not a project file.
            if (r.Path.EndsWith(".mgsv", StringComparison.OrdinalIgnoreCase)) await Session.NewProjectFromModAsync(r.Path);
            else await Session.OpenProjectAsync(r.Path);
            ProjectOpened();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or NotSupportedException) { OpenFailed(e.Message); }
        catch (Exception e) when (e is NullReferenceException or ArgumentException) { OpenFailed($"This project file can't be opened: {e.Message}"); }
        finally { _opening = false; }
    }

    private void Remember(string file)
    {
        var p = Session.Project!;
        string kinds = string.Join(" + ", p.Items.GroupBy(i => i.Kind).Select(g => g.Count() == 1 ? g.Key.ToString().ToLowerInvariant() : $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}s"));
        string? icon = Session.Intake != null && p.Items.Count > 0 && Session.Intake.Icons.TryGetValue(p.Items[0].Id, out var img) ? System.Convert.ToBase64String(Png.Encode(img)) : null;
        Session.Settings.AddRecent(new RecentProject { Path = file, Name = p.Name, Version = p.Version, Summary = $"{kinds} · from \"{Session.Source?.Metadata.Name}\" by {Session.Source?.Metadata.Author}", IconPng = icon });
        Raise(nameof(Recent));
    }
    public IReadOnlyList<RecentProject> Recent => Session.Settings.Recent;

    private string _notice = "";
    /// <summary>A problem the app caught instead of closing (shown under the header until dismissed).</summary>
    public string Notice { get => _notice; set { NoticeHasLog = false; Set(ref _notice, value); Raise(nameof(HasNotice)); } }
    private bool _noticeHasLog;
    /// <summary>True when the notice comes from <see cref="ReportError"/>, so the banner offers "Open error.log".</summary>
    public bool NoticeHasLog { get => _noticeHasLog; private set => Set(ref _noticeHasLog, value); }
    /// <summary>Opens a file in its default program (a test replaces it).</summary>
    public Action<string> OpenFile { get; set; } = p => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
    public Command OpenErrorLog => _openErrorLog ??= new Command(() =>
    {
        string log = Path.Combine(Path.GetDirectoryName(Settings.FilePathPublic)!, "error.log");
        try { OpenFile(log); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { Notice = $"The app could not open error.log. The file is here: {log}"; }
    });
    private Command? _openErrorLog;
    public bool HasNotice => _notice.Length > 0;
    public Command DismissNotice => _dismiss ??= new Command(() => Notice = "");
    private Command? _dismiss;

    /// <summary>Opens Explorer with the file selected (a test replaces it).</summary>
    public Action<string> RevealFile { get; set; } = p => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\"");
    /// <summary>The folder of the Problem report: the exe folder, not the settings folder (a test replaces it).</summary>
    public string ProblemReportFolder { get; set; } = AppContext.BaseDirectory;
    /// <summary>Help > Save problem report, and the error banner button: writes the Problem report next to the exe and shows it in Explorer.</summary>
    public Command SaveProblemReport => _saveProblemReport ??= new Command(() =>
    {
        var now = DateTime.Now;
        string path = Path.Combine(ProblemReportFolder, ProblemReport.FileName(now));
        try
        {
            string errorLog = ErrorLogPath;
            // Issue #72: only the errors of this session; a log that was not open at the start is all new.
            path = ProblemReport.WriteForApp(typeof(MainVm).Assembly, ProblemReportFolder,
                ProblemReport.ErrorsSince(errorLog, errorLog == _errorLogAtStart.Path ? _errorLogAtStart.Length : 0), DevLog.Lines, now);
            DevLog.Info("report", "Problem report written: " + path);
            RevealFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notice = $"The app could not save the problem report: {e.Message}  The path is: {path}";
            return;
        }
        OpenWeb(ProblemReport.ProblemUrl, $"The problem report is saved. Send it with an issue on {ProblemReport.IssuesUrl}");
    });
    /// <summary>Help > Send feedback: opens the public feedback form in the browser.</summary>
    public Command SendFeedback => _sendFeedback ??= new Command(() =>
        OpenWeb(ProblemReport.FeedbackUrl, $"The app could not open the browser. Send feedback on {ProblemReport.IssuesUrl}"));
    private Command? _sendFeedback;
    private void OpenWeb(string url, string failNotice)
    {
        try { OpenFile(url); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Notice = failNotice; }
    }
    private Command? _saveProblemReport;

    private static string ErrorLogPath => Path.Combine(Path.GetDirectoryName(Settings.FilePathPublic)!, "error.log");
    /// <summary>The error log and its length when this session started (issue #72: the report leaves out older errors).</summary>
    private readonly (string Path, long Length) _errorLogAtStart = (ErrorLogPath, File.Exists(ErrorLogPath) ? new FileInfo(ErrorLogPath).Length : 0);

    /// <summary>Records an unexpected error in the log file and shows it, so the app carries on with the user's work intact.</summary>
    /// <param name="e">What was thrown.</param>
    public void ReportError(Exception e)
    {
        DevLog.Error("error", e);
        try
        {
            string dir = Path.GetDirectoryName(Settings.FilePathPublic)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {e}\n\n");
        }
        catch (Exception) { /* the log is a convenience */ }
        Notice = $"Something went wrong: {e.Message}  (details are in {Path.Combine(Path.GetDirectoryName(Settings.FilePathPublic)!, "error.log")}; your work is still open)";
        NoticeHasLog = true;
    }

    public void ShowEditor()
    {
        if (Editor == null) { Editor = new EditorVm(this); Raise(nameof(Editor)); Editor.Preview.ModeChanged += () => { Raise(nameof(PreviewOff)); Raise(nameof(PreviewStills)); Raise(nameof(PreviewLabel)); }; }
        Editor.Reload();
        Screen = Editor;
    }

    public void ShowBuild() { DevLog.Info("ui", "Review and build opened"); Screen = new BuildVm(this); }

    public void ProjectOpened()
    {
        DevLog.Info("project", $"Project open: {Session.Project?.Name} {Session.Project?.Version}, {Session.Project?.Items.Count} item(s): {string.Join("; ", Session.Project?.Items.Select(i => $"{i.Kind} {i.Title}") ?? [])}");
        Editor = null; Raise(nameof(Editor));
        ShowEditor();
        if (Session.NotApplied.Count > 0)
            Notice = $"Some edits could not be put back, because the mod changed since the project was saved: {string.Join(", ", Session.NotApplied)}. Saving the project now leaves them out.";
    }

    public async Task SaveAsync()
    {
        if (Session.Project == null) return;
        var file = Session.ProjectFile ?? await Dialogs.SaveFile("Save project", Session.Project.Id, ".svfm");
        if (file == null) return;
        try { Session.SaveProject(file); Remember(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ReportError(new IOException("Couldn't save the project: " + e.Message, e)); } // the wrap keeps the words in the banner and the cause in error.log
    }

    public async Task OpenProjectAsync()
    {
        if (Opening) return;
        if (Session.Dirty && !await Dialogs.Confirm("Open another project?",
                "This project has unsaved changes. Opening another one leaves them behind.", "Open another project", "Keep this one")) return;
        var file = await Dialogs.PickFile("Open a project", ("Simple V Framework Maker projects", ["*.svfm"]));
        if (file == null || Opening) return;
        _opening = true;
        try { await Session.OpenProjectAsync(file); Remember(file); ProjectOpened(); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException) { OpenFailed(e.Message); }
        // A hand-edited file can hold what the checks in ProjectEdits.Read do not see: say it is the file, not the app.
        catch (Exception e) when (e is NullReferenceException or ArgumentException) { OpenFailed($"This project file can't be opened: {e.Message}"); }
        finally { _opening = false; }
    }

    /// <summary>Shows why an open failed on the Open screen. The old project is still whole: "Start editing" goes back to it (#47).</summary>
    private void OpenFailed(string message) { OpenScreen.Message = message; ShowOpen(); }

    /// <summary>
    /// After a failed open the Open screen still has the old project behind it, so its recent list, file choice and
    /// drop must ask before they replace unsaved edits.
    /// </summary>
    public async Task<bool> MayReplaceProjectAsync() => !Session.Dirty || await Dialogs.Confirm("Open another mod or project?",
        "This project has unsaved changes. Opening another one leaves them behind.", "Open it", "Keep this one");

    /// <summary>
    /// Start a brand-new project from a .mgsv, at any time, without touching the project already open.
    /// The owner asked for this: "'Open...' at the top made me think it was referring to opening a new mod, a new .mgsv.
    /// We should have that as an option. Being able to open a new .mgsv at any time as a new project, not just to import
    /// another mod into the same project." The old header button only ever opened a saved .svfm, which is why it read as
    /// the wrong thing. This is the separate action, and it asks before throwing away unsaved work.
    /// </summary>
    public async Task OpenModAsNewProjectAsync()
    {
        if (Session.Dirty && !await Dialogs.Confirm("Start a new project?",
                "This project has unsaved changes. Starting a new one from a .mgsv leaves them behind.", "Start a new project", "Keep this one")) return;
        var file = await Dialogs.PickFile("Open a .mgsv as a new project", ("MGSV mods", ["*.mgsv"]));
        if (file == null || Opening) return;
        _opening = true;
        try
        {
            var res = await Session.NewProjectFromModAsync(file);
            if (res.Project == null)
            {
                // Tester feedback Nov 5, item 9: a mod with nothing to port goes back to the first screen with the reason.
                OpenFailed($"Could not open {Path.GetFileName(file)}: nothing in this mod can be ported yet. " + string.Join(" ", res.Classification.Routes.Take(3)));
                return;
            }
            Remember(file);
            ProjectOpened();
        }
        catch (AmbiguousModException)
        {
            OpenFailed($"{Path.GetFileName(file)} contains several mods. Open it here and pick the one to port.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or NotSupportedException or ArgumentException)
        {
            OpenFailed($"Could not open {Path.GetFileName(file)}: {e.Message}");
        }
        finally { _opening = false; }
    }
}

/// <summary>Open: choose the game folder and a mod; says what was found.</summary>
public sealed class OpenVm(MainVm main) : Observable
{
    private string _message = "", _modName = "";
    private bool _busy, _found;

    public MainVm Main { get; } = main;
    public string GameFolder
    {
        get => Main.Session.Settings.GameFolder;
        set { if (Main.Session.Settings.GameFolder == value) return; Main.Session.Settings.GameFolder = value; Main.Session.ForgetGame(); Main.Session.Settings.Save(); Raise(); Raise(nameof(GameStatus)); Raise(nameof(GameOk)); Raise(nameof(GameShown)); }
    }
    public string GameStatus => GameFolder.Length == 0 ? "Choose the folder that holds MGSV (the one with the master folder in it)."
        : Directory.Exists(Path.Combine(GameFolder, "master")) || IsGameDump(GameFolder)
          || Session.GameFactory != (Func<string, Svfm.Core.Archives.GameData>)Svfm.Core.Archives.GameData.OpenGameFolder
            ? "Game folder set." : "There's no master folder here. Pick the folder that holds mgsvtpp.exe, or a folder of unpacked game files (00_dat, chunk0_dat, ...).";

    /// <summary>
    /// True for a folder of unpacked game files (GzsTool: 00_dat/, chunk0_dat/, texture0_dat/, EquipIcon/) rather than an
    /// installed game. <see cref="Svfm.Core.Archives.GameData.OpenGameFolder"/> accepts both, so the Open screen must too,
    /// or it refuses a folder the tool can actually read.
    /// </summary>
    private static bool IsGameDump(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.GetDirectories(folder)
                .Any(d => Path.GetFileName(d).EndsWith("_dat", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public string Message { get => _message; set => Set(ref _message, value); }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;
    public bool Found { get => _found; private set => Set(ref _found, value); }
    public string ModName { get => _modName; private set => Set(ref _modName, value); }
    public ObservableCollection<FindingVm> Findings { get; } = [];
    public bool CanEdit => Main.Session.Project != null;

    public Command BrowseGame => new(async () =>
    {
        var f = await Main.Dialogs.PickFolder("Choose the game folder");
        if (f != null) GameFolder = f;
    });

    public Command ChooseMod => new(async () =>
    {
        var f = await Main.Dialogs.PickFile("Choose a mod", ("MGSV mods", ["*.mgsv", "*.zip"]));
        if (f != null) await LoadAsync(f);
    });

    /// <summary>Re-reads the settings after the settings window closes, so the Open screen shows the new folders.</summary>
    public void RefreshSettings()
    {
        Raise(nameof(GameFolder)); Raise(nameof(GameStatus)); Raise(nameof(GameOk)); Raise(nameof(GameShown));
    }

    public bool GameOk => GameStatus == "Game folder set.";
    public string GameShown => GameFolder.Length == 0 ? "Not chosen yet" : GameFolder;
    public IEnumerable<RecentVm> RecentItems => Main.Session.Settings.Recent.Select(r => new RecentVm(Main, r));
    public bool HasRecent => Main.Session.Settings.Recent.Count > 0;
    public void RefreshRecent() { Raise(nameof(RecentItems)); Raise(nameof(HasRecent)); }

    public Command StartEditing => new(() => Main.ProjectOpened(), () => CanEdit);

    public ObservableCollection<ModChoice> Choices { get; } = [];
    public bool HasChoices => Choices.Count > 0;

    private string _lastPath = "";
    private string? _lastInner;
    private List<ExtraMod> _extras = [];
    private bool _needsBase;
    /// <summary>The opened mod is only textures: say they need the mod with their model.</summary>
    public bool NeedsBase { get => _needsBase; private set => Set(ref _needsBase, value); }
    public ObservableCollection<string> ExtraNames { get; } = [];
    public bool HasExtras => ExtraNames.Count > 0;

    public Command AddPart => new(async () =>
    {
        var f = await Main.Dialogs.PickFile("Choose another part, or the mod the textures are for", ("MGSV mods", ["*.mgsv", "*.zip"]));
        if (f == null) return;
        if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> inside;
            try { inside = Svfm.Core.Mods.MgsvMod.ListMgsvInZip(f); } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { Message = e.Message; return; }
            if (inside.Count > 1)
            {
                Choices.Clear();
                Message = "This file contains several mods. Pick the one to add.";
                foreach (var c in inside) Choices.Add(new ModChoice(c.Split('/', '\\').Last(), new Command(async () => await LoadAsync(_lastPath, _lastInner, [.. _extras, new ExtraMod(f, c)]))));
                Raise(nameof(HasChoices));
                return;
            }
        }
        await LoadAsync(_lastPath, _lastInner, [.. _extras, new ExtraMod(f, null)]);
    });

    public Command ClearParts => new(async () => await LoadAsync(_lastPath, _lastInner));

    public async Task LoadAsync(string path, string? inner = null, IReadOnlyList<ExtraMod>? extras = null)
    {
        if (Busy || Main.Opening) return; // a drop or "Add another part" during a load would race it, and the last to end would win
        if (!await Main.MayReplaceProjectAsync()) return;
        _lastPath = path; _lastInner = inner; _extras = [.. extras ?? []]; NeedsBase = false; ExtraNames.Clear();
        foreach (var x in _extras) ExtraNames.Add(System.IO.Path.GetFileName(x.Path) + (x.Inner != null ? " › " + x.Inner.Split('/', '\\').Last() : ""));
        Raise(nameof(HasExtras));
        Message = ""; Findings.Clear(); Found = false; Busy = true; Choices.Clear(); Raise(nameof(HasChoices));
        try
        {
            var res = await Main.Session.OpenModAsync(path, inner, extras);
            ModName = $"{res.ModName} by {res.ModAuthor}";
            foreach (var f in res.Findings) Findings.Add(new FindingVm(f));
            if (res.Project == null) Message = "Nothing in this mod can be ported yet. " + string.Join(" ", res.Classification.Routes.Take(3));
            Found = true;
            NeedsBase = res.NeedsBaseMod;
            Raise(nameof(CanEdit));
            StartEditing.Raise();
            Main.ShowFound(res, path);
        }
        catch (AmbiguousModException e)
        {
            Message = "This file contains several mods. Pick the one to port.";
            foreach (var c in e.Choices) Choices.Add(new ModChoice(c.Split('/', '\\').Last(), new Command(async () => await LoadAsync(path, c))));
            Raise(nameof(HasChoices));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException or NotSupportedException)
        {
            Message = e.Message;
        }
        finally { Busy = false; }
    }
}

public sealed record ModChoice(string Label, Command Pick);

/// <summary>One line of the Recent projects list.</summary>
public sealed class RecentVm
{
    public RecentProject Recent { get; }
    public Command Open { get; }
    public Bitmap? Icon { get; }
    public RecentVm(MainVm main, RecentProject r)
    {
        Recent = r; Open = new Command(async () => await main.OpenRecentAsync(r));
        try { if (r.IconPng != null) Icon = new Bitmap(new MemoryStream(System.Convert.FromBase64String(r.IconPng))); } catch (Exception e) when (e is FormatException or ArgumentException or IOException) { }
    }
    public string Name => Recent.Name;
    public string Version => Recent.Version;
    public string Summary => Recent.Summary;
}

public sealed class FindingVm(IntakeFinding f)
{
    public string Text => f.What;
    public string Note => f.Supported ? f.Note ?? "Ported by this app" : f.Note ?? "";
    public string Brush => f.Supported ? "#58B07F" : "#FF8A3D";
}

/// <summary>One row of the per-language table.</summary>
public sealed class LangRow(ItemVm owner, string code, string display) : Observable
{
    public string Code { get; } = code;
    public string Display { get; } = display;
    public bool IsEnglish => Code == "eng";
    public string Name { get => owner.GetText(owner.NameId, Code, false); set { owner.SetText(owner.NameId, Code, value); Raise(); RaiseTag(); } }
    public string Description { get => owner.GetText(owner.InfoId, Code, false); set { owner.SetText(owner.InfoId, Code, value); Raise(); RaiseTag(); } }
    public string Tag => IsEnglish ? "" : (owner.Has(owner.NameId, Code) || owner.Has(owner.InfoId, Code)) ? "own text" : "uses English";
    public string TagBrush => Tag == "own text" ? "#58B07F" : "#7E848C";
    public void RaiseTag() { Raise(nameof(Tag)); Raise(nameof(TagBrush)); }
    public void RaiseAll() { Raise(nameof(Name)); Raise(nameof(Description)); RaiseTag(); }
}

/// <summary>One item of the mod: a row in its category's drop-down.</summary>
public sealed record BuddyChip(string Label, bool On);

public sealed class ItemVm : Observable
{
    private readonly Session _s;
    public ProjectItem Item { get; }
    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; private set => Set(ref _icon, value); }
    public ObservableCollection<LangRow> Rows { get; } = [];
    public OutfitVm? Outfit { get; private set; }
    public HeadVm? HeadInfo { get; private set; }
    public AlsoVm? Also { get; private set; }
    public bool IsOutfit => Outfit != null;
    /// <summary>The R&amp;D row of the item, when this tool made it.</summary>
    public DevelopVm? Develop { get; }
    public bool IsBuddy => Item is BuddyOutfitItem;
    public bool IsHead => Item is HeadOptionItem;
    public IReadOnlyList<BuddyChip> BuddyChips => Item is BuddyOutfitItem b ? [new("Quiet", b.Spec.Buddy == "quiet"), new("D-Horse", b.Spec.Buddy == "horse"), new("D-Dog", b.Spec.Buddy == "dog")] : [];
    public string BuddyNote => "The buddy the costume was made for. It is added as a new costume; the vanilla ones are untouched.";
    public string NotOfferedNote => "D-Walker mods are reported as not portable: V Framework 1.7 has no costume slot for it.";
    public bool HasSide => Item is SupportWeaponItem;
    public bool IsThrown => Item is SupportWeaponItem;
    public IReadOnlyList<string> SideWritten => Item is SupportWeaponItem t
        ? [$"An equip row like the {SupportWeaponFamilies.Name(t.Spec.ThrowAs)}'s: the same block, its own .parts, no package of its own.", "A new stats row, an R&D row and the icon. No vanilla row or file changes.", "The script, in the module's message handlers."]
        : [];
    public IReadOnlyList<string> SideKnow => Item is SupportWeaponItem
        ? ["Placed items (decoys, mines, C4) can't be ported: V Framework 1.7 only makes thrown ones.", "Chaff, noise and explosion steps need V Framework 1.7 installed."]
        : [];
    public StatsVm? Stats { get; }
    public List<StatsVm> MoreStats { get; } = [];
    public ScriptVm? Script { get; }
    public StatsVm? StatsFor(string tabId) => tabId == "stats" ? Stats : MoreStats.FirstOrDefault(x => x.TabId == tabId);

    public ItemVm(Session s, ProjectItem item)
    {
        _s = s; Item = item;
        LoadIcon();
        foreach (var (package, _, display) in ModText.Languages) Rows.Add(new LangRow(this, package, display));
        if (item is OutfitItem oi) { Outfit = new OutfitVm(s, this, oi); Also = new AlsoVm(s, this, oi); }
        // An existing outfit: the Abilities and Camo concealment tabs edit the outfit read from its script, and "Also under
        // other outfits" lists the suits the script already shows its looks under and adds new ones (not for looks on a vanilla suit).
        if (item is ExistingItem { Model: OutfitItem eo } eoi) { if (eoi.ItemType == ItemKind.Outfit) Also = new AlsoVm(s, this, eo, eoi.AlsoInScript); Outfit = new OutfitVm(s, this, eo); }
        if (item is HeadOptionItem hoi) HeadInfo = new HeadVm(s, hoi);
        // An existing head option: "Worn with" edits the head read from its script.
        if (item is ExistingItem { Model: HeadOptionItem eh }) HeadInfo = new HeadVm(s, eh, item);
        if (ProjectEdits.DevelopOf(item) != null) Develop = new DevelopVm(s, item);
        Stats = StatsVm.For(s, item);
        MoreStats.AddRange(StatsVm.More(s, item));
        if (item is SupportWeaponItem swi) Script = new ScriptVm(s, swi);
        if (item is ExistingItem { Script: not null } esc) Script = new ScriptVm(s, esc);
        if (item is ExistingItem ex)
        {
            if (ex.RealNameId != null) ExtraTexts.Add(new ExtraTextVm(this, "Real name (shown when the item is picked)", ex.RealNameId));
            foreach (var (label, id) in ex.OtherText) ExtraTexts.Add(new ExtraTextVm(this, label, id));
            // A weapon's gun and parts are on the Stats tab; Settings has the rest.
            foreach (var g in ex.Fields.Where(f => Stats == null || !ex.StatGroups.Contains(f.Group)).GroupBy(f => f.Group)) SettingGroups.Add(new FieldGroupVm(g.Key, g.Select(f => new FieldVm(s, ex, f))));
        }
    }

    public bool IsExisting => Item is ExistingItem;
    /// <summary>For an item one script call registers several times: what each time registers ("" otherwise).</summary>
    public string InstancesText => Item is ExistingItem { Instances.Count: > 0 } e
        ? $"The script makes this {e.Instances.Count} times with one call, for: {string.Join(", ", e.Instances)}. A setting you change here changes all of them."
        : "";
    public bool HasInstances => InstancesText.Length > 0;
    public ObservableCollection<ExtraTextVm> ExtraTexts { get; } = [];
    public ObservableCollection<FieldGroupVm> SettingGroups { get; } = [];
    public bool HasIconPicture => !HasIconControls && Icon != null;

    // ---- Twin (GLOSSARY.md): with Sync on, the name, the description, the icon and the R&D row stay equal to the twin's ----
    private ProjectItem? Twin => _s.Edits?.TwinOf(Item);
    public bool HasTwin => Twin != null;
    public bool TwinSync { get => HasTwin && Item.Synced; set => _s.SetTwinSync(Item, value); }
    public string TwinSyncText => Twin is { } t ? $"Sync with twin: {t.Title} (name, description, icon and R&D row)" : "";
    public string SyncedNote => Twin is { } t ? $"Synced with twin {t.Title}." : "";

    private void LoadIcon()
    {
        if (_s.Intake!.Icons.TryGetValue(Item.Id, out var img)) { using var ms = new MemoryStream(Png.Encode(img)); Icon = new Bitmap(ms); }
    }

    // ---- Icon controls (outfits made from a camo replacer) ----
    // A render that is asked for but not yet drawn counts as the current icon, so a second quick click reads the first.
    private IconSettings? Cur => _pendingIcon ?? _s.Intake!.IconOf(Item);
    private IconSettings? _pendingIcon;
    private int _iconTicket;
    private SemaphoreSlim IconLock => _s.IntakeWork;
    private bool _iconBusy;
    public bool HasIconControls => Item is OutfitItem && Cur != null;
    public Func<IDialogs>? DialogsOf { get; set; }
    /// <summary>Refreshes other items of the bar (the items of an existing mod that share the icon file just replaced).</summary>
    public Action<IReadOnlyCollection<ProjectItem>>? RefreshOthers { get; set; }
    public bool IsCustomIcon => Cur is { Style: IconStyle.Custom, FromView: false };
    public bool SourcePackaged => Cur == null && Icon != null && IsExisting;
    /// <summary>The icon the mod shipped for this item, as the item was when the mod was opened (issue #7, the packaged card).</summary>
    private IntakeResult.IconSnap? Packaged => IsExisting && _s.Edits?.OriginalOf(Item)?.IconSnap is { State: null, Image: not null } p ? p : null;
    public bool CanPickPackaged => Packaged != null;
    private Command? _pickPackaged;
    public Command PickPackaged => _pickPackaged ??= new Command(() =>
    {
        EndGenerate();
        if (Packaged is not { } p || SourcePackaged) return;
        _s.Checkpoint();
        _s.Intake!.RestoreIcon(Item, p);
        IconError = ""; PickerOpen = false; LoadIcon(); RaiseIcon(); _s.NotifyChanged();
        if (_s.Intake.IconSharersOf(Item) is { Count: > 0 } others) RefreshOthers?.Invoke(others);
    }, () => CanPickPackaged);
    // Why a card can't be used (issue #7: the cards always show, greyed with a reason).
    private string LockReason => Intake_Can ? "You cannot change the icon of this item. The note above tells why." : "This item has no icon that the tool can change.";
    private bool GenerateOn => CanGenerateCard && CanGenerate;
    public string PackagedReason => CanPickPackaged ? "" : "This mod has no packaged icon for this item.";
    public string GameReason => CanPickGameIcon ? "" : LockReason;
    public string GenerateReason => GenerateOn ? "" : !CanUpload ? LockReason
        : CanGenerateCard ? "The tool cannot make an icon for this kind of item. Upload an image." : "This item has no model in the mod. The tool cannot make an icon without a model. Upload an image.";
    public string UploadReason => CanUpload ? "" : LockReason;
    public bool SourceGenerate => Cur != null && (Cur.Style is not (IconStyle.Custom or IconStyle.Game) || Cur.FromView);
    public bool SourceGame => Cur?.Style == IconStyle.Game;
    /// <summary>
    /// Whether "An icon already in the game" is offered. Each item kind writes its icon path in its R&amp;D row, so a game
    /// icon works for every kind (issue #16: head options and buddy costumes too). The list itself is hidden unless the
    /// game card is open, so it can never sit open under a "generate from the model" choice. An existing item takes a
    /// game icon by a new path in its script, which needs no model.
    /// </summary>
    public bool CanPickGameIcon => CanUpload;
    /// <summary>An existing item has no maker, so its icon can't be generated; only an uploaded picture can replace it (gap G2).</summary>
    public bool CanGenerateCard => CanUpload && (!IsExisting || Item is ExistingItem { Model: not null });
    private bool _pickerOpen; private string _gameSearch = ""; private string? _selectedGame; private Bitmap? _gamePreview; private CancellationTokenSource? _gameWait;
    public bool PickerOpen { get => _pickerOpen; set { if (Set(ref _pickerOpen, value)) { Raise(nameof(PickerShown)); if (value) _ = SearchGameAsync(); } } }
    /// <summary>The game card opens the search and the list. A pick of another source closes them, so they cannot linger
    /// under a "generate from the model" choice.</summary>
    public bool PickerShown => _pickerOpen && CanPickGameIcon;
    public string GameSearch { get => _gameSearch; set { if (Set(ref _gameSearch, value)) _ = SearchGameAsync(); } }
    public ObservableCollection<GameIconRow> GameResults { get; } = [];
    private GameIconRow? _selectedRow;
    public GameIconRow? SelectedGameRow { get => _selectedRow; set { if (Set(ref _selectedRow, value)) SelectedGame = value?.Path; } }
    /// <summary>Issue #27: a click on a row (or an arrow key) sets the item's icon at once; each pick is one undo step.</summary>
    public string? SelectedGame
    {
        get => _selectedGame;
        set { if (Set(ref _selectedGame, value)) { LoadGamePreview(); if (value != null && !(SourceGame && Cur?.GamePath == value)) UseGameIcon(); } }
    }
    public Bitmap? GamePreview { get => _gamePreview; private set => Set(ref _gamePreview, value); }
    private Command? _openGame;
    public Command OpenGamePicker => _openGame ??= new Command(() => { EndGenerate(); PickerOpen = !PickerOpen; }, () => CanPickGameIcon);
    public string GameIconName => Cur?.GamePath is { } p ? GameIconRow.Label(_s.Intake!.GameIconName(p), p) : "";
    private async Task SearchGameAsync()
    {
        _gameWait?.Cancel(); var cts = _gameWait = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var text = _gameSearch;
            var found = await Task.Run(() => _s.Intake!.SearchGameIcons(text).Select(p => new GameIconRow(p, _s.Intake!.GameIconName(p))).ToList(), cts.Token);
            if (cts.IsCancellationRequested) return;
            GameResults.Clear(); foreach (var f in found) GameResults.Add(f);
        }
        catch (OperationCanceledException) { }
    }
    private void LoadGamePreview()
    {
        GamePreview = null;
        if (_selectedGame == null) return;
        var img = _s.Intake!.GameIconImage(_selectedGame);
        if (img != null) { using var ms = new MemoryStream(Png.Encode(img)); GamePreview = new Bitmap(ms); }
    }
    // After an undo the picked row is no longer the icon: unselect it, so a click on the same row applies it again.
    private void ClearStaleGamePick()
    {
        if (_applyingPick || _selectedGame == null || (SourceGame && Cur?.GamePath == _selectedGame)) return;
        _selectedRow = null; _selectedGame = null; GamePreview = null;
        Raise(nameof(SelectedGameRow)); Raise(nameof(SelectedGame));
    }
    // The picker stays open, so the user can click through the icons.
    private bool _applyingPick; // the checkpoint below calls Refresh, which must not clear this pick
    private void UseGameIcon()
    {
        _applyingPick = true;
        try
        {
            _s.Checkpoint();
            _s.Intake!.SetGameIcon(Item, _selectedGame!);
            IconError = ""; LoadIcon(); RaiseIcon(); _s.NotifyChanged();
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { IconError = "That icon can't be used: " + e.Message; RaiseIcon(); }
        finally { _applyingPick = false; }
    }
    public bool SourceUpload => IsCustomIcon;
    public bool CanUpload => Intake_Can && _s.Intake!.IconLockOf(Item) == null;
    /// <summary>
    /// Gap G2: what can be done with the icon of an existing item. An uploaded picture replaces the icon file the mod ships,
    /// at the same path and size; a game icon, or an icon the mod does not ship, changes the path in the script.
    /// </summary>
    public string IconLockedNote
    {
        get
        {
            if (!IsExisting) return "";
            if (_s.Intake!.IconLockOf(Item) is { } why) return why;
            var others = _s.Intake.IconSharersOf(Item);
            string note = IntakeResult.ShippedIconOf((ExistingItem)Item) != null
                ? "This item comes from a mod that is already a V Framework mod. An uploaded or generated picture replaces the icon file the mod ships, at its size. A game icon changes the icon path in the script."
                : "This item comes from a mod that is already a V Framework mod, and it uses an icon that its mod does not carry. A new picture goes to a new icon file, and the build points the script at it.";
            return others.Count == 0 ? note : note + $" {others.Count} other item{(others.Count == 1 ? "" : "s")} of this mod use the same icon file, so the picture becomes their icon too.";
        }
    }
    /// <summary>The size an upload is scaled to: an existing item keeps the size of the icon the mod ships.</summary>
    public string UploadHint => IsExisting && _s.Intake!.Icons.GetValueOrDefault(Item.Id) is { } shipped && (shipped.Width != 256 || shipped.Height != 128)
        ? $"PNG or JPG, scaled to cover the {shipped.Width} × {shipped.Height} picture the mod ships."
        : "PNG or JPG, scaled to cover the 256 × 128 icon window.";
    // An existing item with a game icon has no path of its own yet, and EnsureOwnIconPath gives it one: IconLockOf decides for those.
    private bool Intake_Can => Item is ExistingItem || Svfm.Core.Project.IntakeResult.IconPathOf(Item) != null;
    // Buddy costumes and head options are included: they have a maker now (see Intake), so their icon can be regenerated.
    // The owner could not attempt it before ("I could not attempt to generate an icon for Quiet", the same for D-Dog and
    // D-Horse, and "On head option ... I could not reselect 'generate from the model'").
    public bool CanGenerate => HasIconControls || Item is WeaponItem or BuddyOutfitItem or HeadOptionItem or ExistingItem { Model: not null };
    public string IconSourceName => SourceGame ? "An icon already in the game: " + GameIconName : SourceUpload ? "Uploaded image" : SourceGenerate ? (Cur?.Style == IconStyle.CamoCrop ? (_s.Intake!.IsCamoReplacer(Item) ? "Crop of the mod's camo texture" : "Crop of the outfit's main texture") : "Generated from the model") : SourcePackaged ? "Icon packaged with the mod" : "No icon";
    private Command? _upload;
    public Command UploadIcon => _upload ??= new Command(async () => { EndGenerate(); await UploadAsync(); }, () => CanUpload);
    private Command? _pickGenerate;
    public Command PickGenerate => _pickGenerate ??= new Command(() =>
    {
        if (!GenerateOn) return;
        PickerOpen = false;
        if (CanGenerateMode && OpenGenerate != null) OpenGenerate(this); else _ = GenerateAsync();
    }, () => GenerateOn);

    // ---- Generate mode (issues #9 and #17): the settings are a draft in the 3D panel. Each "Generate now" makes the icon
    // from it, and the mode stays open until "Done", another icon card or another item. ----
    private IconSettings? _draft;
    /// <summary>The settings being tried in Generate mode; null outside the mode.</summary>
    public IconSettings? Draft => _draft;
    public bool InGenerate => _draft != null;
    /// <summary>The draft changed, or the mode started or ended.</summary>
    public event Action? DraftChanged;
    /// <summary>Opens Generate mode in the 3D panel (set by the editor).</summary>
    public Action<ItemVm>? OpenGenerate { get; set; }
    /// <summary>A ported outfit and a buddy costume set their framing (a buddy's is the framing of the game's icons for that
    /// buddy, issue #33), and a head option uses the view of the 3D preview; weapons and existing outfits generate at once.</summary>
    public bool CanGenerateMode => GenerateOn && (Item is OutfitItem or BuddyOutfitItem or ExistingItem { Model: BuddyOutfitItem } || FollowsView);
    /// <summary>A head option: Generate now makes the icon from the view of the 3D preview, not from a framing (issue #17).</summary>
    public bool GeneratesFromView => Item is HeadOptionItem or ExistingItem { Model: HeadOptionItem };
    /// <summary>A weapon: its icon is an outline drawing from the 3D preview's view, with the preview's attachments (owner, Nov 7).</summary>
    public bool IsWeaponIcon => Item is WeaponItem or ExistingItem { Model: WeaponItem };
    /// <summary>Generate mode follows the 3D preview's view (a head option or a weapon), not a framing.</summary>
    public bool FollowsView => GeneratesFromView || IsWeaponIcon;
    /// <summary>Ends Generate mode (another icon card was picked). The icons already generated stay.</summary>
    private void EndGenerate() { if (InGenerate) SetDraft(null); }
    // The controls show the draft in Generate mode, and the icon's settings outside it.
    private IconSettings? Src => _draft ?? Cur;
    private void SetDraft(IconSettings? d) { _draft = d; _sameNote = ""; Raise(nameof(IconNote)); RaiseIcon(); DraftChanged?.Invoke(); }
    /// <summary>Starts the draft from the icon's settings (a crop stays a crop only where the item can be cropped).</summary>
    public void BeginGenerate()
    {
        if (IsWeaponIcon)
        {
            // The icon's own view and attachments; the automatic icon starts with every attachment on (owner, Nov 7).
            var w = Cur is { Style: IconStyle.Outline } o ? o : new IconSettings(IconStyle.Outline, true, 0, 0, Svfm.Core.Conversion.SuitIcon.DefaultContrast, 1f);
            if (Cur?.Style != IconStyle.Outline || Cur == _s.Intake!.DefaultIconOf(Item)) w = w with { Parts = IconSettings.PartSuppressor | IconSettings.PartLight | IconSettings.PartMagazine, HiddenModels = null };
            SetDraft(w);
            return;
        }
        var c = Cur ?? IconSettings.ChestDefault;
        _slot = 0; Raise(nameof(SlotChoices)); Raise(nameof(SlotIndex));
        SetDraft(c.Style == IconStyle.CamoCrop && CanCrop ? c : c with { Style = IconStyle.ChestRender });
    }
    private Command? _defaultFraming, _generateFromDraft, _cancelGenerate;
    /// <summary>The framing (or the crop) of the automatic icon.</summary>
    public Command DefaultFraming => _defaultFraming ??= new Command(() =>
    {
        if (_draft is not { } d) return;
        var a = _s.Intake!.DefaultIconOf(Item) ?? IconSettings.ChestDefault;
        SetDraft(d.Style == IconStyle.CamoCrop
            ? d with { CropX = a.CropX, CropY = a.CropY, Contrast = a.Contrast, Brightness = a.Brightness }
            : d with { OffsetX = a.OffsetX, OffsetY = a.OffsetY, Zoom = a.Zoom });
    }, () => InGenerate);
    /// <summary>Makes the icon from the draft. The mode stays open. One undo step puts the old icon back.</summary>
    public Command GenerateFromDraft => _generateFromDraft ??= new Command(async () =>
    {
        if (_draft is not { } d || IconBusy) return;
        _s.Checkpoint();
        SetSameNote("");
        if (_slot > 0 && Item is OutfitItem o) { await RenderSlotAsync(o, _slot - 1, d); return; }
        var before = _s.Intake!.Icons.GetValueOrDefault(Item.Id);
        await RenderAsync(d);
        // Issue #48: a Generate now that gives the same picture looked like "nothing happens", so say why.
        if (IconError.Length == 0 && before != null && _s.Intake.Icons.GetValueOrDefault(Item.Id) is { } after && after.Pixels.AsSpan().SequenceEqual(before.Pixels))
            SetSameNote("The new icon is the same as the old one. Change the framing or the slot to get a different icon.");
    }, () => InGenerate && !IconBusy);
    private string _sameNote = "";
    private void SetSameNote(string n) { _sameNote = n; Raise(nameof(IconNote)); }
    /// <summary>Makes a weapon's icon from the 3D preview's view and attachments. The mode stays open. One undo step puts the old icon back.</summary>
    /// <param name="s">The draft with the preview's turn, tilt and attachments.</param>
    public async Task GenerateWeaponAsync(IconSettings s)
    {
        if (IconBusy) return;
        _draft = s;
        _s.Checkpoint();
        await RenderAsync(s);
    }
    /// <summary>Makes the icon from a picture of the 3D preview's view (a head option). The mode stays open.</summary>
    /// <param name="picture">The 256x128 picture.</param>
    public Task UseViewIconAsync(Svfm.Core.Formats.RgbaImage picture) => SetPictureAsync(picture, true);
    /// <summary>"Done": ends the mode. The last generated icon stays; with no Generate now, the icon is as it was.</summary>
    public Command CancelGenerate => _cancelGenerate ??= new Command(() => SetDraft(null), () => InGenerate);
    private async Task UploadAsync()
    {
        var dlg = DialogsOf?.Invoke(); if (dlg == null) return;
        var file = await dlg.PickFile("Choose the icon picture", ("Pictures", ["*.png", "*.jpg", "*.jpeg", "*.dds"]));
        if (file == null) return;
        try
        {
            Svfm.Core.Formats.RgbaImage img;
            if (file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) img = Svfm.Core.Formats.DdsReader.Read(File.ReadAllBytes(file));
            else
            {
                using var sk = SkiaSharp.SKBitmap.Decode(file) ?? throw new InvalidDataException("That file isn't a picture the tool can read.");
                using var rgba = sk.Copy(SkiaSharp.SKColorType.Rgba8888);
                img = new Svfm.Core.Formats.RgbaImage(rgba.Width, rgba.Height, rgba.Bytes);
            }
            await SetPictureAsync(img, false);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or InvalidOperationException) { IconError = "The picture can't be used: " + e.Message; RaiseIcon(); }
    }
    private async Task SetPictureAsync(Svfm.Core.Formats.RgbaImage img, bool fromView)
    {
        try
        {
            _s.Checkpoint();
            // The upload is the newest request: a render that still waits is dropped, and one that runs finishes first.
            int ticket = ++_iconTicket;
            _pendingIcon = null;
            IconBusy = true;
            await IconLock.WaitAsync();
            try { await Task.Run(() => _s.Intake!.SetCustomIcon(Item, img, fromView)); }
            finally { IconLock.Release(); if (ticket == _iconTicket) IconBusy = false; }
            IconError = ""; PickerOpen = false;
            LoadIcon(); RaiseIcon(); _s.NotifyChanged();
            if (_s.Intake!.IconSharersOf(Item) is { Count: > 0 } others) RefreshOthers?.Invoke(others);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or InvalidOperationException) { IconError = "The picture can't be used: " + e.Message; RaiseIcon(); }
    }
    public bool IconBusy { get => _iconBusy; private set { Set(ref _iconBusy, value); GenerateFromDraft.Raise(); Raise(nameof(IconNote)); } }
    public string IconNote => IconBusy ? "Making the icon…" : InGenerate ? _sameNote : "";
    public bool CropOn
    {
        get => Src?.Style == IconStyle.CamoCrop;
        set
        {
            if (value == CropOn) return;
            if (_draft is { } d) { if (!value || CanCrop) SetDraft(d with { Style = value ? IconStyle.CamoCrop : IconStyle.ChestRender }); }
            else if (Cur != null) SetStyle(value ? IconStyle.CamoCrop : IconStyle.ChestRender);
        }
    }
    public bool RenderOn { get => Src?.Style == IconStyle.ChestRender; set { if (value) CropOn = false; } }
    public bool CanCrop => _s.Intake!.CanCrop(Item);
    /// <summary>Issue #88: every ported outfit can give one slot an icon of its own in Generate mode.</summary>
    public bool SlotOn => InGenerate && Item is OutfitItem;
    /// <summary>The SLOT list: the outfit icon (every slot with no slot icon), then each slot by its Sortie name (Naked included).</summary>
    public List<string> SlotChoices => Item is OutfitItem o
        ? ["Outfit icon (every slot)", .. IntakeResult.SlotLooks(o).Select((l, k) => (l.DisplayName == null ? "" : GetText(l.DisplayName, "eng", false)).Trim() is { Length: > 0 } t ? t : $"Look {k + 1}")]
        : [];
    private int _slot;
    private List<string>? _slotListed;
    /// <summary>A look can go away while Generate mode is open (Remove, undo): the list follows and the chosen entry stays in it.</summary>
    private void SyncSlots()
    {
        if (!SlotOn) return;
        var now = SlotChoices;
        if (_slotListed != null && now.SequenceEqual(_slotListed)) return;
        _slotListed = now;
        _slot = Math.Min(_slot, now.Count - 1);
        // The index first: a box whose index is past the end of its new list clears itself and stays empty.
        Raise(nameof(SlotIndex)); Raise(nameof(SlotChoices)); Raise(nameof(ScarfOn)); DraftChanged?.Invoke();
    }
    /// <summary>The chosen entry of <see cref="SlotChoices"/>: 0 is the outfit icon, n is slot n. The 3D panel shows that slot's look.</summary>
    public int SlotIndex { get => _slot; set { if (value < 0) return; value = Math.Min(value, Math.Max(0, SlotChoices.Count - 1)); if (value == _slot) return; _slot = value; Raise(nameof(SlotIndex)); Raise(nameof(ScarfOn)); DraftChanged?.Invoke(); } }
    /// <summary>Standard/Scarf of the outfit icon of a camo replacer. A slot entry draws its own look (the Scarf slot with the scarf), so it hides the choice.</summary>
    public bool ScarfOn => RenderOn && _slot == 0 && _s.Intake!.IsCamoReplacer(Item);
    public bool ScarfSlot
    {
        get => Src?.WithScarf ?? true;
        set
        {
            if (_draft is { } d) { if (d.WithScarf != value) SetDraft(d with { WithScarf = value }); }
            else if (Cur is { } c && c.WithScarf != value) { _s.Checkpoint(); _ = RenderAsync(c with { WithScarf = value }); }
        }
    }
    public bool StandardSlot { get => !ScarfSlot; set { if (value) ScarfSlot = false; else ScarfSlot = true; } }
    private CancellationTokenSource? _frameWait;
    private void Reframe(IconSettings n)
    {
        _frameWait?.Cancel(); var cts = _frameWait = new CancellationTokenSource();
        _s.Checkpoint($"{Item.Id}:frame");
        // A render still runs: it would write its older settings last, so the move is held as the newest request and drawn after it.
        if (IconBusy) { ++_iconTicket; _pendingIcon = n; }
        else _s.Intake!.SetIconState(Item, n);
        RaiseIcon();
        _ = Task.Delay(500, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(async () => await RenderAsync(Cur!)); }, TaskScheduler.Default);
    }
    // In Generate mode the framing is clamped to the slider ranges (±50%, 40-500%, the preview ZOOM range: owner, Nov 7), so a drag never goes past a slider end.
    public double FrameX { get => Src?.OffsetX * 100 ?? 0; set { if (_draft is { } d) SetDraft(d with { OffsetX = Math.Clamp((float)(value / 100), -0.5f, 0.5f) }); else if (Cur is { } c) Reframe(c with { OffsetX = (float)(value / 100) }); } }
    public double FrameY { get => Src?.OffsetY * 100 ?? 0; set { if (_draft is { } d) SetDraft(d with { OffsetY = Math.Clamp((float)(value / 100), -0.5f, 0.5f) }); else if (Cur is { } c) Reframe(c with { OffsetY = (float)(value / 100) }); } }
    /// <summary>Sets the outline icon size to 100 %.</summary>
    public Command FullSize => _fullSize ??= new Command(() => FrameZoom = 100);
    private Command? _fullSize;
    public double FrameZoom { get => (Src?.Zoom ?? 1f) * 100; set { if (_draft is { } d) SetDraft(d with { Zoom = Math.Clamp((float)(value / 100), 0.4f, 5f) }); else if (Cur is { } c) Reframe(c with { Zoom = (float)(value / 100) }); } }
    public double CropMaxX => CanCrop ? _s.Intake!.CropBounds(Item).x : 0;
    public double CropMaxY => CanCrop ? _s.Intake!.CropBounds(Item).y : 0;
    public double CropX { get => Src?.CropX ?? 0; set { if (Src is { } c && (int)value != c.CropX) Apply(c with { CropX = (int)value }, "icon:x"); } }
    public double CropY { get => Src?.CropY ?? 0; set { if (Src is { } c && (int)value != c.CropY) Apply(c with { CropY = (int)value }, "icon:y"); } }
    public double ContrastPct { get => (Src?.Contrast ?? 1.375f) / Svfm.Core.Conversion.SuitIcon.DefaultContrast * 100; set { if (Src is { } c) Apply(c with { Contrast = (float)(value / 100 * Svfm.Core.Conversion.SuitIcon.DefaultContrast) }, "icon:c"); } }
    public double BrightnessPct { get => (Src?.Brightness ?? 1f) * 100; set { if (Src is { } c) Apply(c with { Brightness = (float)(value / 100) }, "icon:b"); } }

    private void Apply(IconSettings n, string field = "icon")
    {
        if (_draft != null) { SetDraft(n); return; }
        _s.Checkpoint($"{Item.Id}:{field}");
        WriteIcon(n);
    }

    /// <summary>A quick icon change (crop, contrast) is written at once, unless a render still runs: that render would write its older settings last, so the change waits behind it.</summary>
    private void WriteIcon(IconSettings n)
    {
        if (IconBusy) { _ = RenderAsync(n); RaiseIcon(); return; }
        _s.Intake!.ApplyIcon(Item, n);
        LoadIcon(); RaiseIcon();
        _s.NotifyChanged();
    }

    private void SetStyle(IconStyle style)
    {
        var c = Cur!;
        _s.Checkpoint();
        if (style == IconStyle.CamoCrop) WriteIcon(c with { Style = style });
        else _ = RenderAsync(c with { Style = style });
    }

    /// <summary>Writes an error to error.log and shows the notice with the "Open error.log" button (MainVm.ReportError).</summary>
    public Action<Exception>? ReportError { get; set; }
    /// <summary>Draws the icon of one slot and gives it to that slot only (the caller takes the undo checkpoint).</summary>
    private async Task RenderSlotAsync(OutfitItem o, int slot, IconSettings d)
    {
        IconBusy = true;
        await IconLock.WaitAsync();
        try { await Task.Run(() => _s.Intake!.RenderSlotIcon(o, slot, d)); }
        catch (Exception e) when (e is not OperationCanceledException) { IconError = e.Message; ReportError?.Invoke(e); }
        finally { IconLock.Release(); IconBusy = false; RaiseIcon(); _s.NotifyChanged(); }
    }

    private async Task RenderAsync(IconSettings n)
    {
        int ticket = ++_iconTicket;
        _pendingIcon = n;
        IconBusy = true;
        await IconLock.WaitAsync();
        try
        {
            // A newer request for this item came while this one waited: only the newest is drawn, so it is the last to write.
            if (ticket != _iconTicket) return;
            await Task.Run(() => _s.Intake!.ApplyIcon(Item, n)); LoadIcon();
        }
        catch (Exception e) when (e is not OperationCanceledException) { IconError = e.Message; ReportError?.Invoke(e); } // issue #39: any failure is shown and written to error.log
        finally
        {
            IconLock.Release();
            if (ticket == _iconTicket) { _pendingIcon = null; IconBusy = false; RaiseIcon(); _s.NotifyChanged(); }
        }
    }

    private Task GenerateAsync()
    {
        _s.Checkpoint();
        if (IsWeaponIcon) return RenderAsync(new IconSettings(IconStyle.Outline, true, 0, 0, Svfm.Core.Conversion.SuitIcon.DefaultContrast, 1f, Parts: IconSettings.PartSuppressor | IconSettings.PartLight | IconSettings.PartMagazine));
        // Head options and buddy costumes have a maker but no camo recipe, so Cur can be null for them. Before this,
        // "Generate from the model" dereferenced the null and threw, so the button did nothing (the owner: "In Head
        // options, when I upload an image for an icon, I cannot generate an icon from the model again. These problems
        // don't seem to occur with the gun icons" - guns take the Outline branch above, which is why they were fine).
        return RenderAsync((Cur ?? Svfm.Core.Project.IconSettings.ChestDefault) with { Style = IconStyle.ChestRender });
    }
    public bool OutlineOn => Cur?.Style == IconStyle.Outline;

    public string IconError { get; private set; } = "";

    private void RaiseIcon()
    {
        foreach (var n in new[] { nameof(CropOn), nameof(RenderOn), nameof(ScarfOn), nameof(ScarfSlot), nameof(StandardSlot), nameof(CropX), nameof(CropY), nameof(ContrastPct), nameof(BrightnessPct), nameof(IconError), nameof(FrameX), nameof(FrameY), nameof(FrameZoom), nameof(CanCrop), nameof(SlotOn), nameof(InGenerate), nameof(Draft), nameof(CanGenerateMode), nameof(IsCustomIcon), nameof(SourcePackaged), nameof(SourceGenerate), nameof(SourceUpload), nameof(SourceGame), nameof(GameIconName), nameof(IconSourceName), nameof(HasIconControls), nameof(HasIconPicture), nameof(OutlineOn), nameof(CanGenerate), nameof(CanPickGameIcon), nameof(PickerShown), nameof(CanPickPackaged), nameof(PackagedReason), nameof(GameReason), nameof(GenerateReason), nameof(UploadReason) }) Raise(n);
        OpenGamePicker.Raise(); PickPackaged.Raise(); PickGenerate.Raise(); UploadIcon.Raise();
        DefaultFraming.Raise(); GenerateFromDraft.Raise(); CancelGenerate.Raise();
        SyncSlots();
    }

    public string? NameId => ProjectEdits.NameIdOf(Item);
    public string? InfoId => ProjectEdits.InfoIdOf(Item);
    public string Id => Item.Id;
    public string KeyText => Item.Key is { Length: > 0 } k ? k : Item.Id;
    public string DetectedText => Item is ExistingItem ? "Detected: V Framework item" : "Detected: ported from a replacer";
    public string KindText => EditorVm.GroupOf(Item) switch { "OUTFITS" => "Outfit", "LOOKS ON VANILLA SUITS" => "Looks on a vanilla suit", "HEADS" => "Head option", "WEAPONS" => "Weapon", "THROWABLES" => "Thrown item", _ => "Buddy costume" };
    public string Sub => Item switch
    {
        ExistingItem ex => ex.Kind switch { ItemKind.Outfit => "Outfit", ItemKind.HeadOption => "Head option", ItemKind.BuddyOutfit => "Buddy costume", ItemKind.ExtraLooks => "Looks on a vanilla suit", _ => ex.Support ? "Thrown item" : "Weapon or item" },
        OutfitItem o => $"Outfit · {o.LookSelectors} sortie slots",
        HeadOptionItem h => string.Join(" / ", h.Spec.Branches.Keys.OrderBy(k => k).Select(PlayerBranches.Label).Select(l => l.Replace(" soldier", "").Replace(" (male)", " M").Replace(" (female)", " F"))),
        SupportWeaponItem => "Thrown support weapon",
        WeaponItem w => System.Text.RegularExpressions.Regex.Replace(w.Spec.EquipType, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant() is var t ? char.ToUpperInvariant(t[0]) + t[1..] : "Weapon",
        BuddyOutfitItem b => b.Spec.Buddy switch { "quiet" => "Quiet", "horse" => "D-Horse", _ => "D-Dog" },
        _ => Item.Kind.ToString(),
    };
    public string Badge => Item switch { HeadOptionItem => "HEAD", SupportWeaponItem => "THRO", WeaponItem => "WEAP", BuddyOutfitItem => "BUDD", _ => "" };
    public bool HasBadge => Badge.Length > 0;
    // Gaps G1 and G11 of .docs/Parity_Existing_VFramework_Mods.md: an item read from an existing V Framework mod can be
    // removed when its calls are statements of their own (ExistingMod.RemoveLockOf), but not duplicated. Each drop-down
    // row shows the "!" for its own item; the Item menu shows it for the selected one.
    public string? DuplicateLock => Item is ExistingItem x ? ExistingMod.DuplicateLockOf(x) : null;
    public string? RemoveLock => Item is ExistingItem x && _s.Project?.Existing is { } data ? ExistingMod.RemoveLockOf(data, x) : null;
    public bool ActionsLocked => DuplicateLock != null || RemoveLock != null;
    /// <summary>The row's "i": the V Framework slots the item takes.</summary>
    public string SlotsNote => Item.LookSelectors > 0 ? $"This item takes {Item.LookSelectors} of the {VFrameworkLimits.LookSelectors} sortie slots and {Item.PartsTypes} of the {VFrameworkLimits.CustomPartsTypes} outfit slots. V Framework's slots are shared with every V Framework mod you have installed." : "";
    public bool HasSlotsNote => SlotsNote.Length > 0;
    public string ActionsNote => string.Join(" ", new[] { DuplicateLock is { } d ? $"This item can't be duplicated: {d}." : null, RemoveLock is { } r ? $"This item can't be taken out of the mod: {r}." : null }.OfType<string>());

    public bool Has(string? id, string lang) => id != null && Item.Text.TryGetValue(id, out var per) && per.TryGetValue(lang, out var t) && t.Length > 0;

    public string GetText(string? id, string lang, bool fallback)
    {
        // No lang ID at all: the item has no R&D row to name it (a head option with no develop row is the common case),
        // so its only name is the plain Title. Before this the name box read empty and an edit went nowhere.
        if (id == null) return lang == "eng" ? Item.Title : "";
        if (!Item.Text.TryGetValue(id, out var per)) return "";
        if (per.TryGetValue(lang, out var t)) return t;
        return fallback && per.TryGetValue("eng", out var e) ? e : "";
    }

    public void SetText(string? id, string lang, string value)
    {
        // No lang ID: there is no lang package entry to write, so the name lives on the item's Title alone. This used to
        // return silently, which is why renaming a head option that has no R&D row did nothing at all.
        if (id == null)
        {
            if (lang != "eng") return;
            _s.Checkpoint($"{Item.Id}:title");
            Item.Title = value;
            _s.NotifyChanged();
            return;
        }
        _s.Checkpoint($"{Item.Id}:{id}:{lang}");
        if (!Item.Text.TryGetValue(id, out var per)) Item.Text[id] = per = new(StringComparer.Ordinal);
        if (lang != "eng" && value.Length == 0) per.Remove(lang); else per[lang] = value;
        if (lang == "eng" && id == NameId) Item.Title = value;
        _s.NotifyChanged();
    }

    public string Name { get => GetText(NameId, "eng", false); set { SetText(NameId, "eng", value); Raise(); RowsChanged(); } }
    public string Description { get => GetText(InfoId, "eng", false); set { SetText(InfoId, "eng", value); Raise(); RowsChanged(); } }
    public string Title => Item.Title.Length > 0 ? Item.Title : Item.Id;

    private void RowsChanged() { foreach (var r in Rows) r.RaiseAll(); Raise(nameof(Title)); }

    /// <summary>Re-read everything after an undo.</summary>
    public void Refresh() { ClearStaleGamePick(); foreach (var t in ExtraTexts) t.RaiseAll(); foreach (var g in SettingGroups) foreach (var f in g.Fields) f.RaiseAll(); Outfit?.Reload(); Also?.Refresh(); HeadInfo?.Reload(); Develop?.Reload(); Stats?.Reload(); foreach (var m in MoreStats) m.Reload(); Script?.Reload(); LoadIcon(); RaiseIcon(); Raise(nameof(Name)); Raise(nameof(Description)); Raise(nameof(Title)); foreach (var n in new[] { nameof(HasTwin), nameof(TwinSync), nameof(TwinSyncText), nameof(SyncedNote) }) Raise(n); foreach (var r in Rows) r.RaiseAll(); foreach (var n in new[] { nameof(DuplicateLock), nameof(RemoveLock), nameof(ActionsLocked), nameof(ActionsNote) }) Raise(n); }

    public bool IsEmptyName => Name.Trim().Length == 0;

    // ---- Compare with the mod's original ----

    /// <summary>The section of the edits that a tab shows (null for a tab id that has none, e.g. the old "alsounder").</summary>
    public static EditSection? SectionOf(string tabId) => tabId switch
    {
        "name" => EditSection.Name, "texts" => EditSection.Texts, "slots" => EditSection.Slots,
        "abilities" => EditSection.Abilities, "camo" => EditSection.Camo,
        "heads" => EditSection.Heads, "stats" => EditSection.Stats, "sortie" => EditSection.Sortie, "attack" => EditSection.Attack,
        "script" => EditSection.Script, "settings" => EditSection.Settings, "rd" => EditSection.Rd, "deploy" => EditSection.Deploy,
        _ => null,
    };

    /// <summary>What differs from the original on a tab.</summary>
    public List<DiffRow> DiffFor(string tabId) => SectionOf(tabId) is { } sec && _s.Edits is { } e ? [.. ProjectEdits.TabSections(sec).SelectMany(x => e.Diff(Item, x))] : [];
}

/// <summary>One tab in the editor's left rail.</summary>
/// <summary>The R&amp;D row of any item the tool made: grade, costs and whether it starts developed.</summary>
public sealed class DevelopVm(Session s, ProjectItem item) : Observable
{
    // Issue #25: a head option has no grade row.
    public bool GradeShown => item.Kind != ItemKind.HeadOption;
    private DevelopRow Dev => ProjectEdits.DevelopOf(item)!;
    private void Mutate(string field, Func<DevelopRow, DevelopRow> f) { s.Checkpoint($"{item.Id}:{field}"); ProjectEdits.SetDevelop(item, f(Dev)); s.NotifyChanged(); }
    public bool GradeOn { get => Dev.Grade != null; set { if (value != GradeOn) { Mutate("d:grade", d => d with { Grade = value ? 1 : null }); Raise(); Raise(nameof(Grade)); } } }
    public decimal Grade { get => Dev.Grade ?? 1; set => Mutate("d:g", d => d with { Grade = (int)Math.Clamp(value, 1, 15) }); }
    public decimal DevelopCost { get => Dev.GmpCost; set => Mutate("d:c", d => d with { GmpCost = (int)Math.Max(0, value) }); }
    public decimal UsageCost { get => Dev.UsageGmpCost; set => Mutate("d:u", d => d with { UsageGmpCost = (int)Math.Max(0, value) }); }
    public bool AlreadyDeveloped { get => Dev.InitiallyAvailable; set { Mutate("d:i", d => d with { InitiallyAvailable = value }); Raise(nameof(DevelopNote)); } }
    public string DevelopNote => Dev.InitiallyAvailable ? "Available from the start. The development cost is not charged." : "Must be developed in R&D first, for the development cost.";
    public static IReadOnlyList<string> Sections { get; } = ["None", "Combat", "R&D", "Base Development", "Support", "Intel", "Medical"];
    /// <summary>
    /// The materials the game charges, named the way the game names them (the owner's request: "Material names should be
    /// based on their ingame name"). The Lua takes the internal name, so the two are kept side by side.
    /// </summary>
    public static IReadOnlyList<MaterialChoice> ResourceChoices { get; } =
    [
        new("None", "None"),
        new("FuelResource", "Fuel Resources"),
        new("BioticResource", "Biological Material"),
        new("CommonMetal", "Common Metals"),
        new("MinorMetal", "Minor Metals"),
        new("PreciousMetal", "Precious Metals"),
        // The eight medicinal plants. The ids are the game's own (TppTerminal.RESOURCE_INFORMATION_TABLE
        // maps TYPE_HERB_* to Plant2000-2007); the labels are the names the iDroid shows.
        new("Plant2000", "Golden Crescent"),
        new("Plant2001", "African Peach"),
        new("Plant2002", "Digitalis (Purpurea)"),
        new("Plant2003", "Digitalis (Lutea)"),
        new("Plant2004", "Black Carrot"),
        new("Plant2005", "Wormwood"),
        new("Plant2006", "Tarragon"),
        new("Plant2007", "Haoma"),
        // The Parasite Suit cartridges cost parasites (fultoned Skulls). Labels: the game's English lang file and the
        // R&D lists of gamerguides.com ("Mist Parasites"). Curing is the armor parasite (TppTerminal: Metal -> CURING).
        new("ParasiteFog", "Mist Parasites"),
        new("ParasiteCamofla", "Camouflage Parasites"),
        new("ParasiteCuring", "Armor Parasites"),
    ];
    /// <summary>The internal names, in the same order as <see cref="ResourceChoices"/> (for the drop-down's index).</summary>
    public static IReadOnlyList<string> Resources { get; } = ResourceChoices.Select(c => c.Id).ToList();
    public static IReadOnlyList<string> Unlocks { get; } = ["Always shown in R&D", "Hidden until its blueprint is obtained or a matching specialist joins", "Hidden until a story mission is cleared"];
    /// <summary>
    /// The story missions an R&amp;D row can wait for, by the name the game shows (the owner's item 15: the unlock needs
    /// reference points the user knows). Codes from the wiki's MissionCodes page.
    /// </summary>
    public static IReadOnlyList<MissionChoice> Missions { get; } =
    [
        new(10010, "Prologue: Awakening"), new(10020, "Episode 1: Phantom Limbs"), new(10030, "Episode 2: Diamond Dogs"),
        new(10036, "Episode 3: A Hero's Way"), new(10043, "Episode 4: C2W"), new(10033, "Episode 5: Over the Fence"),
        new(10040, "Episode 6: Where Do the Bees Sleep?"), new(10041, "Episode 7: Red Brass"), new(10044, "Episode 8: Occupation Forces"),
        new(10054, "Episode 9: Backup, Back Down"), new(10052, "Episode 10: Angel with Broken Wings"), new(10050, "Episode 11: Cloaked in Silence"),
        new(10070, "Episode 12: Hellbound"), new(10080, "Episode 13: Pitch Dark"), new(10086, "Episode 14: Lingua Franca"),
        new(10082, "Episode 15: Footprints of Phantoms"), new(10090, "Episode 16: Traitors' Caravan"), new(10091, "Episode 17: Rescue the Intel Agents"),
        new(10100, "Episode 18: Blood Runs Deep"), new(10195, "Episode 19: On the Trail"), new(10110, "Episode 20: Voices"),
        new(10121, "Episode 21: The War Economy"), new(10115, "Episode 22: Retake the Platform"), new(10120, "Episode 23: The White Mamba"),
        new(10085, "Episode 24: Close Contact"), new(10200, "Episode 25: Aim True, Ye Vengeful"), new(10211, "Episode 26: Hunting Down"),
        new(10081, "Episode 27: Root Cause"), new(10130, "Episode 28: Code Talker"), new(10140, "Episode 29: Metallic Archaea"),
        new(10150, "Episode 30: Skull Face"), new(10151, "Episode 31: Sahelanthropus"), new(10045, "Episode 32: To Know Too Much"),
        new(10093, "Episode 35: Cursed Legacy"), new(10156, "Episode 38: Extraordinary"), new(10171, "Episode 41: Proxy War Without End"),
        new(10240, "Episode 43: Shining Lights, Even in Death"), new(10260, "Episode 45: A Quiet Exit"), new(10280, "Episode 46: Truth: The Man Who Sold the World"),
    ];
    private static string? R(string? v) => v is null or "None" ? null : v;
    public decimal TimeMinutes { get => Dev.TimeMinutes; set => Mutate("d:t", d => d with { TimeMinutes = (int)Math.Max(0, value) }); }
    public int Section { get => Dev.SectionId; set { Mutate("d:s", d => d with { SectionId = value, SectionLevel = value == 0 ? 0 : d.SectionLevel }); Raise(); } }
    public decimal SectionLevel { get => Dev.SectionLevel; set => Mutate("d:sl", d => d with { SectionLevel = (int)Math.Clamp(value, 0, 99) }); }
    public int Section2 { get => Dev.Section2Id; set { Mutate("d:s2", d => d with { Section2Id = value, Section2Level = value == 0 ? 0 : d.Section2Level }); Raise(); } }
    public decimal Section2Level { get => Dev.Section2Level; set => Mutate("d:s2l", d => d with { Section2Level = (int)Math.Clamp(value, 0, 99) }); }
    public string Resource1 { get => Dev.Resource1 ?? "None"; set => Mutate("d:r1", d => d with { Resource1 = R(value) }); }
    public MaterialChoice Resource1Choice { get => Pick(Dev.Resource1); set => Resource1 = value?.Id ?? "None"; }
    public decimal Resource1Count { get => Dev.Resource1Count; set => Mutate("d:r1c", d => d with { Resource1Count = (int)Math.Max(0, value) }); }
    public string Resource2 { get => Dev.Resource2 ?? "None"; set => Mutate("d:r2", d => d with { Resource2 = R(value) }); }
    public MaterialChoice Resource2Choice { get => Pick(Dev.Resource2); set => Resource2 = value?.Id ?? "None"; }
    public decimal Resource2Count { get => Dev.Resource2Count; set => Mutate("d:r2c", d => d with { Resource2Count = (int)Math.Max(0, value) }); }
    public string Usage1 { get => Dev.Usage1 ?? "None"; set => Mutate("d:u1", d => d with { Usage1 = R(value) }); }
    public MaterialChoice Usage1Choice { get => Pick(Dev.Usage1); set => Usage1 = value?.Id ?? "None"; }
    public decimal Usage1Count { get => Dev.Usage1Count; set => Mutate("d:u1c", d => d with { Usage1Count = (int)Math.Max(0, value) }); }
    public string Usage2 { get => Dev.Usage2 ?? "None"; set => Mutate("d:u2", d => d with { Usage2 = R(value) }); }
    public MaterialChoice Usage2Choice { get => Pick(Dev.Usage2); set => Usage2 = value?.Id ?? "None"; }
    private static MaterialChoice Pick(string? id) => ResourceChoices.FirstOrDefault(c => c.Id == (id ?? "None")) ?? ResourceChoices[0];
    public decimal Usage2Count { get => Dev.Usage2Count; set => Mutate("d:u2c", d => d with { Usage2Count = (int)Math.Max(0, value) }); }
    /// <summary>The 26 staff skills that vanilla R&amp;D rows need (p04), in plain words. The game's spelling errors stay in the names.</summary>
    public static IReadOnlyList<MaterialChoice> SkillChoices { get; } =
    [
        new("GunsmithHandGun", "Gunsmith (handguns)"), new("GunsmithAssultRifle", "Gunsmith (assault rifles)"),
        new("GunsmithGrenadeLauncher", "Gunsmith (grenade launchers)"), new("GunsmithShotGun", "Gunsmith (shotguns)"),
        new("GunsmithSubmachineGun", "Gunsmith (submachine guns)"), new("GunsmithMachineGun", "Gunsmith (machine guns)"),
        new("GunsmithMissile", "Gunsmith (missiles)"), new("GunsmithSniperRifle", "Gunsmith (sniper rifles)"),
        new("TranqEngineer", "Tranquilizer engineer"), new("SleepingGasEngineer", "Sleeping gas engineer"),
        new("SuppressorEngineer", "Suppressor engineer"), new("MaterialEngineer", "Material engineer"),
        new("MissileHomingEngineer", "Missile homing engineer"), new("DrugEngineer", "Drug engineer"),
        new("ElectricSpinningEngineer", "Electric spinning engineer"), new("MechatronicsEngineer", "Mechatronics engineer"),
        new("MetamaterialEngineer", "Metamaterial engineer"), new("MonitorEngineer", "Monitor engineer"),
        new("RadarEngineer", "Radar engineer"), new("RocketControlEngineer", "Rocket control engineer"),
        new("CyberneticsEngineer", "Cybernetics engineer"), new("ElectricEngineer", "Electrical engineer"),
        new("ElectromagneticNetEngineer", "Electromagnetic net engineer"), new("HaulageEngineer", "Haulage engineer"),
        new("Zoologist", "Zoologist"), new("ParasiteReseacher", "Parasite researcher (Code Talker only)"),
    ];
    public bool SkillOn { get => Dev.Skill != null; set { if (value != SkillOn) { Mutate("d:sk", d => Shown(d with { Skill = value ? SkillChoices[0].Id : null })); Raise(); Raise(nameof(SkillList)); Raise(nameof(SkillChoice)); Raise(nameof(Unlock)); } } }
    /// <summary>A row hidden until obtained with nothing left to obtain is shown again: else it stays hidden for ever.</summary>
    private static DevelopRow Shown(DevelopRow d) => d.HiddenUntilObtained && !d.CanBeHidden() ? d with { HiddenUntilObtained = false } : d;
    /// <summary>The skill drop-down: a value that is not one of the 26 (a number from an old project) is kept and shown as unknown.</summary>
    public IReadOnlyList<MaterialChoice> SkillList => Dev.Skill is { } sk && !SkillChoices.Any(c => c.Id == sk) ? [.. SkillChoices, new(sk, $"Unknown skill ({sk})")] : SkillChoices;
    public MaterialChoice? SkillChoice
    {
        get => SkillList.FirstOrDefault(c => c.Id == Dev.Skill);
        set { if (value != null && value.Id != Dev.Skill) { Mutate("d:skv", d => d with { Skill = value.Id }); Raise(); } }
    }
    public static string SkillInfo => "A staff member with this skill must be at Mother Base before this can be developed.";
    public int Unlock
    {
        get => Dev.AfterMission != null ? 2 : Dev.HiddenUntilObtained ? 1 : 0;
        set
        {
            if (value < 0 || value == Unlock) return;
            // The middle choice needs a blueprint or a specialist (issue #37).
            if (value == 1 && !Dev.CanBeHidden()) { Avalonia.Threading.Dispatcher.UIThread.Post(() => Raise(nameof(Unlock))); return; }
            // A story mission is the bluePrintId condition, so it takes the place of a game blueprint.
            Mutate("d:un", d => d with { HiddenUntilObtained = value == 1, AfterMission = value == 2 ? d.AfterMission ?? Missions[1].Code : null, BluePrint = value == 2 ? null : d.BluePrint });
            Raise(); Raise(nameof(MissionOn)); Raise(nameof(AfterMission)); Raise(nameof(BluePrintChoice)); Raise(nameof(BluePrintPickOn));
        }
    }
    public static string UnlockInfo => "\"Hidden until its blueprint is obtained or a matching specialist joins\": the item does not show in R&D until the player gets its blueprint, or until a staff member with its specialist skill is at Mother Base. Set a blueprint or a specialist first. Without one, the item stays hidden.";
    /// <summary>"None" and the game's blueprints that vanilla rows use, each with the name of an item that uses it.</summary>
    public static IReadOnlyList<MaterialChoice> BluePrints { get; private set; } = BuildBluePrints();
    private static List<MaterialChoice> BuildBluePrints()
    {
        var game = Svfm.Core.Knowledge.GameKnowledge.EnglishNamesGame;
        return [new("None", "None"), .. DevelopRow.VanillaBluePrints.Select(b => new MaterialChoice(b.Id,
            Svfm.Core.Knowledge.GameKnowledge.VanillaDevelop(b.DevelopId) is { } r ? $"{b.Id} ({Svfm.Core.Knowledge.GameKnowledge.EnglishName(game, r.Name)})" : b.Id))];
    }
    public IReadOnlyList<MaterialChoice> BluePrintList => BluePrints;
    public MaterialChoice BluePrintChoice
    {
        get => BluePrints.FirstOrDefault(c => c.Id == (Dev.BluePrint ?? "None")) ?? BluePrints[0];
        set { if (value != null && R(value.Id) != Dev.BluePrint) { Mutate("d:bp", d => Shown(d with { BluePrint = R(value.Id) })); Raise(); Raise(nameof(Unlock)); } }
    }
    /// <summary>The drop-down shows when the script gives no blueprint of its own and no story mission is set.</summary>
    public bool BluePrintPickOn => Dev.OwnBluePrint == null && Dev.AfterMission == null;
    public bool OwnBluePrintOn => Dev.OwnBluePrint != null;
    public string OwnBluePrint => $"{Dev.OwnBluePrint} (the mod's own blueprint, kept as it is)";
    public bool MissionOn => Dev.AfterMission != null;
    public MissionChoice? AfterMission
    {
        get => Dev.AfterMission is { } m ? Missions.FirstOrDefault(c => c.Code == m) : null;
        set { if (value != null) { Mutate("d:um", d => d with { AfterMission = value.Code }); Raise(); } }
    }
    /// <summary>
    /// The line the results screen shows when the rank is capped. Kept as the item's own text under "&lt;prefix&gt;_rank";
    /// empty means the V Framework default (the item's name), so no lang id is written.
    /// </summary>
    public string ResultLine
    {
        get => Dev.ResultRankLang is { } id && item.Text.TryGetValue(id, out var per) && per.TryGetValue("eng", out var t) ? t : "";
        set
        {
            if (value == ResultLine) return;
            string id = item.Prefix + "_rank";
            s.Checkpoint($"{item.Id}:d:rline");
            if (value.Length == 0) { item.Text.Remove(id); ProjectEdits.SetDevelop(item, Dev with { ResultRankLang = null }); }
            else
            {
                if (!item.Text.TryGetValue(id, out var per)) item.Text[id] = per = new(StringComparer.Ordinal);
                per["eng"] = value;
                ProjectEdits.SetDevelop(item, Dev with { ResultRankLang = id });
            }
            s.NotifyChanged();
            Raise();
        }
    }
    public bool UpgradeOn { get => Dev.UpgradeOfDevelopId != null; set { if (value != UpgradeOn) { Mutate("d:up", d => d with { UpgradeOfDevelopId = value ? 0 : null }); Raise(); Raise(nameof(UpgradeOf)); } } }
    public decimal UpgradeOf { get => Dev.UpgradeOfDevelopId ?? 0; set => Mutate("d:upv", d => d with { UpgradeOfDevelopId = (int)Math.Max(0, value) }); }
    /// <summary>The vanilla row this one upgrades, picked by name rather than by develop ID.</summary>
    public VanillaRowChoice? UpgradeOfChoice
    {
        get => Dev.UpgradeOfDevelopId is { } id ? VanillaRows.FirstOrDefault(r => r.Id == id) : null;
        set { if (value != null) { Mutate("d:upv", d => d with { UpgradeOfDevelopId = value.Id }); Raise(); } }
    }
    public bool RankLimited { get => Dev.RankLimited; set { Mutate("d:rl", d => d with { RankLimited = value }); Raise(nameof(RankLimited)); } }
    private decimal _vanillaId;
    public decimal VanillaId { get => _vanillaId; set { Set(ref _vanillaId, value); Raise(nameof(VanillaInfo)); } }
    public string VanillaInfo => Svfm.Core.Knowledge.GameKnowledge.VanillaDevelop((int)_vanillaId) is { } v ? $"Grade {v.Grade}, develop {v.DevelopGmp:N0} GMP, deploy {v.UsageGmp:N0} GMP" : "No vanilla row with that ID";
    public Command FillVanilla => new(() =>
    {
        if (Svfm.Core.Knowledge.GameKnowledge.VanillaDevelop((int)_vanillaId) is not { } v) return;
        Mutate("d:fill", d => d with { Grade = v.Grade, GmpCost = v.DevelopGmp, UsageGmpCost = v.UsageGmp });
        Reload();
    });

    // ---- Copy the R&D row of a vanilla item the user picks by name (no IDs to look up) ----
    /// <summary>Vanilla R&amp;D rows the user can copy from, by the name the game shows.</summary>
    public static IReadOnlyList<VanillaRowChoice> VanillaRows { get; private set; } = BuildVanillaRows();
    private static List<VanillaRowChoice> BuildVanillaRows()
    {
        var game = Svfm.Core.Knowledge.GameKnowledge.EnglishNamesGame;
        return Svfm.Core.Knowledge.GameKnowledge.VanillaDevelopRows
            .Select(r => new VanillaRowChoice(int.Parse(r.Key), r.Value, Svfm.Core.Knowledge.GameKnowledge.EnglishName(game, r.Value.Name)))
            .OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }
    /// <summary>Rebuilds the list once the game folder is known (the names come from the game's own text).</summary>
    public static void RefreshVanillaRows() { VanillaRows = BuildVanillaRows(); BluePrints = BuildBluePrints(); }
    private VanillaRowChoice? _copyFrom;
    public VanillaRowChoice? CopyFrom
    {
        get => _copyFrom;
        set { if (Set(ref _copyFrom, value)) { Raise(nameof(CopyFromInfo)); CopyFromRow.Raise(); } }
    }
    public string CopyFromInfo => _copyFrom == null ? "Pick an item to see its row." : $"Grade {_copyFrom.Grade}, develop {_copyFrom.DevelopGmp:N0} GMP, deploy {_copyFrom.UsageGmp:N0} GMP ; also its time, staff, materials, specialist and blueprint (the deploy cost is on the Deployment tab)";
    // Issue #63: one instance, so the Raise in CopyFrom reaches the button that is bound to it (a new one per get left it disabled).
    private Command? _copyFromRow;
    public Command CopyFromRow => _copyFromRow ??= new(() =>
    {
        if (_copyFrom == null) return;
        Mutate("d:copy", d => _copyFrom.Row.Apply(d));
        Reload();
    }, () => _copyFrom != null);
    public void Reload() { foreach (var n in new[] { nameof(GradeOn), nameof(Grade), nameof(DevelopCost), nameof(UsageCost), nameof(AlreadyDeveloped), nameof(DevelopNote), nameof(TimeMinutes), nameof(Section), nameof(SectionLevel), nameof(Section2), nameof(Section2Level), nameof(Resource1), nameof(Resource1Choice), nameof(Resource1Count), nameof(Resource2), nameof(Resource2Choice), nameof(Resource2Count), nameof(Usage1), nameof(Usage1Choice), nameof(Usage1Count), nameof(Usage2), nameof(Usage2Choice), nameof(Usage2Count), nameof(SkillOn), nameof(SkillList), nameof(SkillChoice), nameof(Unlock), nameof(UpgradeOn), nameof(UpgradeOf), nameof(UpgradeOfChoice), nameof(RankLimited), nameof(ResultLine), nameof(MissionOn), nameof(AfterMission), nameof(BluePrintList), nameof(BluePrintChoice), nameof(BluePrintPickOn), nameof(OwnBluePrintOn), nameof(OwnBluePrint), nameof(CopyFromInfo) }) Raise(n); }
}

/// <summary>A material the game charges for development or deployment: the Lua's internal name and the game's own name.</summary>
/// <summary>A story mission an R&amp;D row can wait for: its code and the name the game shows.</summary>
public sealed record MissionChoice(int Code, string Label) { public override string ToString() => Label; }

public sealed record MaterialChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A row of the game icon picker (issue #16): the in-game name first, the file name after it in muted text.</summary>
public sealed record GameIconRow(string Path, string? Name)
{
    public string File => Path.Split('/').Last();
    public bool HasName => Name != null;
    /// <summary>The name, or the file name when no game item uses the icon.</summary>
    public string Title => Name ?? File;
    public static string Label(string? name, string path) => name == null ? path.Split('/').Last() : $"{name} ({path.Split('/').Last()})";
}

/// <summary>
/// A vanilla R&amp;D row the user can copy from, named the way the game names it ("Bandana", "Battle Dress grade 3") so
/// nobody has to know develop IDs. The owner asked for this: "Fill from vanilla item makes no sense, especially as we
/// don't know the IDs... have other items as a reference point".
/// </summary>
public sealed class VanillaRowChoice(int id, Svfm.Core.Knowledge.VanillaDevelopRow row, string? englishName)
{
    public int Id { get; } = id;
    public Svfm.Core.Knowledge.VanillaDevelopRow Row { get; } = row;
    public int Grade { get; } = row.Grade;
    public int DevelopGmp { get; } = row.DevelopGmp;
    public int UsageGmp { get; } = row.UsageGmp;
    /// <summary>The game's own name for the row, with its grade.</summary>
    public string Label { get; } = $"{(englishName ?? row.Name)}  ·  grade {row.Grade}";
    public override string ToString() => Label;
}

public sealed record ContentRow(string Title, string Kind, string Detail);
/// <summary>One line of the Checks list; Level is ok, warn or bad.</summary>
public sealed record CheckRow(string Title, string Detail, string Level)
{
    public Avalonia.Media.IBrush Brush => Level switch { "bad" => Avalonia.Media.Brushes.IndianRed, "warn" => Avalonia.Media.Brush.Parse("#FF8A3D"), _ => Avalonia.Media.Brush.Parse("#58B07F") };
}

/// <summary>A line of the Copy these settings list: a mod item to copy to, or (issue #64, R&amp;D tab) a vanilla item to copy from.</summary>
public sealed class CopyTarget : Observable
{
    public CopyTarget(ItemVm item) { Item = item; Label = item.Title; }
    public CopyTarget(VanillaRowChoice vanilla) { Vanilla = vanilla; Label = $"{vanilla.Label}  (vanilla)"; }
    /// <summary>The mod item, or null for a vanilla item.</summary>
    public ItemVm? Item { get; }
    /// <summary>The vanilla item, or null for a mod item.</summary>
    public VanillaRowChoice? Vanilla { get; }
    public string Label { get; }
    private bool _on;
    public bool On { get => _on; set => Set(ref _on, value); }
}

public sealed class TabVm(string id, string title, int number, string group) : Observable
{
    private bool _active, _changed;
    public bool IsActive { get => _active; set => Set(ref _active, value); }
    /// <summary>The tab holds changes from the original (shown as an amber dot and an amber border).</summary>
    public bool Changed { get => _changed; set { if (Set(ref _changed, value)) Raise(nameof(ChangedClass)); } }
    /// <summary>False for a tab that doesn't apply to the selected item: it is greyed out and can't be opened.</summary>
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Raise(nameof(Disabled)); Raise(nameof(OffClass)); Note = value ? "" : "This tab does not apply to the selected item."; } } }
    private bool _enabled = true;
    public bool Disabled => !_enabled;
    /// <summary>Style classes for the tab button: "changed" (amber border) and "off" (greyed out).</summary>
    public string ChangedClass => _changed ? "changed" : "";
    public string OffClass => _enabled ? "" : "off";
    private string _status = "ok";
    /// <summary>ok, warn or bad: the dot beside the tab (green, orange, red).</summary>
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(StatusBrush)); } }
    /// <summary>The "i" badge: what the tab holds (owner, Nov 3: the "i" and "!" badges stay on tabs).</summary>
    public string Info => EditorVm.TabInfo(Id) is { Length: > 0 } t ? t : Title + ".";
    /// <summary>The "!" badge: why the tab is greyed, or what its orange or red dot warns about.</summary>
    public string Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    private string _note = "";
    public bool HasNote => _note.Length > 0;
    public Avalonia.Media.IBrush StatusBrush => Avalonia.Media.Brush.Parse(_status switch { "bad" => "#E5484D", "warn" => "#F0A73A", _ => "#4CB782" });
    public Command Select { get; set; } = new(() => { });
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Number { get; } = number.ToString();
    /// <summary>The row the tab sits in: IDENTITY, BEHAVIOUR, R&amp;D or DEPLOYMENT.</summary>
    public string Group { get; } = group;
}

/// <summary>
/// One item drop-down above the editor (issue #5), in place of the left rail and the items bar. The box shows the selected
/// item of its category; the open list has a search box. A category the mod has nothing in is greyed (the owner:
/// "Unselectable options such as 'head' 'Weapon' should be greyed out").
/// </summary>
public sealed class CategoryVm(EditorVm editor, string name, string dropName, params string[] groups) : Observable
{
    private string _search = "";
    private ItemVm? _last;
    public string Name { get; } = name;
    /// <summary>The name of the box in the view (AppShots finds it by this name).</summary>
    public string DropName { get; } = dropName;
    public IReadOnlyList<string> Groups { get; } = groups;
    private List<ItemVm> Mine => editor.Items.Where(i => groups.Contains(EditorVm.GroupOf(i.Item))).ToList();
    /// <summary>The rows of the open list: the items that match the search.</summary>
    public ObservableCollection<ItemVm> Rows { get; } = [];
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }
    /// <summary>
    /// The category and its count, then its slot counter (Session_Nov03_ui_mgsv.md, decision 5): the sortie slots its items
    /// take of V Framework's 138. Only outfits and looks take sortie slots, so the other boxes show 0. An empty box shows the
    /// count only.
    /// </summary>
    public string Label => Mine is var m && m.Count > 0 ? $"{Name} · {m.Count} · {m.Sum(i => i.Item.LookSelectors)}/{VFrameworkLimits.LookSelectors} slots" : $"{Name} · 0";
    public string SlotsTip => Mine is var m && m.Count > 0
        ? $"Sortie slots {m.Sum(i => i.Item.LookSelectors)} / {VFrameworkLimits.LookSelectors} · Outfit slots {m.Sum(i => i.Item.PartsTypes)} / {VFrameworkLimits.CustomPartsTypes}. {editor.SlotInfo}"
        : OffNote;
    public bool IsActive => editor.InGroup(groups);
    public bool Enabled => Mine.Count > 0;
    public bool Disabled => !Enabled;
    public string OffNote => Enabled ? "" : $"This mod has no {Name.ToLowerInvariant()}.";
    /// <summary>The item the box shows: the selected item when it is in this category, else the last one picked here.</summary>
    public ItemVm? Current => IsActive ? editor.Selected : _last;
    public bool HasCurrent => Current != null;
    public bool NoCurrent => Enabled && Current == null;
    /// <summary>What the rows bind to: the list emptying itself during a search must not clear the selection.</summary>
    /// <remarks>A box that is not open selects no row, so a click on its last pick still selects it.</remarks>
    public ItemVm? RowSelection { get => IsActive ? editor.Selected : null; set { if (value != null) editor.Selected = value; } }
    public void Refresh()
    {
        if (IsActive) _last = editor.Selected;
        if (_last != null && !editor.Items.Contains(_last)) _last = null;
        var list = Mine.Where(i => EditorVm.Matches(i, _search)).ToList();
        if (!Rows.SequenceEqual(list)) { Rows.Clear(); foreach (var i in list) Rows.Add(i); }
        foreach (var n in new[] { nameof(Label), nameof(SlotsTip), nameof(IsActive), nameof(Enabled), nameof(Disabled), nameof(OffNote), nameof(Current), nameof(HasCurrent), nameof(NoCurrent), nameof(RowSelection) }) Raise(n);
    }
}

/// <summary>The editor: item drop-downs, tabs and the selected item's settings.</summary>
public sealed class EditorVm : Observable
{
    private readonly MainVm _main;
    private ItemVm? _selected;
    private bool _confirm, _showLangs;
    private string _status = "";

    public EditorVm(MainVm main)
    {
        _main = main;
        Categories = [new(this, "Outfits", "OutfitsDrop", "OUTFITS", "LOOKS ON VANILLA SUITS"), new(this, "Heads", "HeadsDrop", "HEADS"), new(this, "Weapons", "WeaponsDrop", "WEAPONS"), new(this, "Throwables", "ThrowablesDrop", "THROWABLES"), new(this, "Buddies", "BuddiesDrop", "BUDDY COSTUMES")];
        Preview = new PreviewVm(main.Session);
        _main.Session.Changed += OnChanged;
        DuplicateCommand = new Command(async () => await DuplicateAsync(), () => _selected != null && _main.Session.Intake!.CanDuplicate(_selected.Item) && !Busy);
        AskRemove = new Command(() => { Confirming = true; }, () => _selected != null && Items.Count > 1 && RemoveLock == null);
        DoRemove = new Command(() => RemoveSelected());
        KeepIt = new Command(() => { Confirming = false; });
        FillLanguages = new Command(() => FillEmptyFromEnglish(), () => _selected != null);
        AddFromMod = new Command(async () => await AddFromModAsync(), () => !Busy);
        ToggleCompare = new Command(() => { ShowCompare = !ShowCompare; });
        ResetTab = new Command(() => { if (_selected != null && _tab != null) { if (ItemVm.SectionOf(_tab.Id) is { } sec) { _main.Session.ResetPart(_selected.Item, ProjectEdits.TabParts(sec)); } _selected.Refresh(); RefreshChanges(); } }, () => TabHasChanges);
    }

    public string SlotInfo => "V Framework has a fixed number of slots, shared with every V Framework mod you have installed.";
    // The blue "i" badges explain themselves on hover, not on click (the owner: "Blue ! should give information on
    // hover, not click"). They are plain Borders with a ToolTip.Tip bound to the strings below, styled by Border.info
    // in App.axaml, which also shortens the tooltip delay so the text appears as soon as the pointer rests on them.
    // ---- Copy this tab's settings to other items ----
    public ObservableCollection<CopyTarget> CopyTargets { get; } = [];
    public bool CanCopy => CopyTargets.Count > 0 && _tab?.Id is "abilities" or "camo" or "rd" or "deploy";
    public string CopyInfo => "Copies what is on this tab (abilities, camo concealment, R&D row or deployment) to the items you tick, so the Black Ocelot variants do not have to be typed three times. On the R&D tab, tick one vanilla item to copy its R&D row to this item and the ticked items. The abilities do not change.";
    public Command CopySettings => new(() =>
    {
        if (_selected == null || _tab == null) return;
        var ticked = CopyTargets.Where(t => t.On).ToList();
        var vanilla = ticked.Where(t => t.Vanilla != null).ToList();
        var targets = ticked.Where(t => t.Item != null).Select(t => t.Item!).ToList();
        if (vanilla.Count > 1) { Status = "Tick only one vanilla item to copy from."; return; }
        if (vanilla.Count == 0 && targets.Count == 0) { Status = "Tick the items to copy to first."; return; }
        var s = _main.Session; s.Checkpoint();
        if (vanilla is [{ Vanilla: { } v }])
        {
            // Issue #64: a vanilla item is the source; its R&D row only (the vanilla abilities are not in the app data).
            targets.Insert(0, _selected);
            foreach (var i in targets) { ProjectEdits.CopyVanillaRow(i.Item, v.Row); i.Refresh(); }
            s.NotifyChanged();
            Status = $"Copied the R&D row of {v.Label} to {targets.Count} item{(targets.Count == 1 ? "" : "s")}.";
            foreach (var t in CopyTargets) t.On = false;
            return;
        }
        foreach (var t in targets)
        {
            switch (_tab.Id)
            {
                case "abilities" or "camo" when ProjectEdits.OutfitOf(_selected.Item) is { } src && ProjectEdits.OutfitOf(t.Item) is { } dst:
                    var from = src.Spec.Branches.Values.First();
                    foreach (var k in dst.Spec.Branches.Keys.ToList())
                        dst.Spec.Branches[k] = _tab.Id == "abilities" ? dst.Spec.Branches[k] with { Abilities = from.Abilities } : dst.Spec.Branches[k] with { CamoBonusType = from.CamoBonusType, CamoBonusValues = from.CamoBonusValues };
                    break;
                case "rd" or "deploy" when ProjectEdits.DevelopOf(_selected.Item) is { } d0 && ProjectEdits.DevelopOf(t.Item) is { } d1:
                    ProjectEdits.SetDevelop(t.Item, _tab.Id == "rd" ? d0 with { FollowsVanillaDevelopId = d1.FollowsVanillaDevelopId, UsageGmpCost = d1.UsageGmpCost, Usage1 = d1.Usage1, Usage1Count = d1.Usage1Count, Usage2 = d1.Usage2, Usage2Count = d1.Usage2Count } : d1 with { UsageGmpCost = d0.UsageGmpCost, Usage1 = d0.Usage1, Usage1Count = d0.Usage1Count, Usage2 = d0.Usage2, Usage2Count = d0.Usage2Count });
                    break;
            }
            t.Refresh();
        }
        s.NotifyChanged();
        Status = $"Copied to {targets.Count} item{(targets.Count == 1 ? "" : "s")}.";
        foreach (var t in CopyTargets) t.On = false;
    });
    private void RebuildCopyTargets()
    {
        CopyTargets.Clear();
        if (_selected != null && _tab != null)
            foreach (var i in Items.Where(i => i != _selected && (_tab.Id is "abilities" or "camo" ? ProjectEdits.OutfitOf(i.Item) != null && ProjectEdits.OutfitOf(_selected.Item) != null : _tab.Id is "rd" or "deploy" && ProjectEdits.DevelopOf(i.Item) != null && ProjectEdits.DevelopOf(_selected.Item) != null)))
                CopyTargets.Add(new CopyTarget(i));
        // Issue #64: on the R&D tab the vanilla items follow the mod items, as sources.
        if (_tab?.Id == "rd" && _selected != null && ProjectEdits.DevelopOf(_selected.Item) != null)
            foreach (var v in DevelopVm.VanillaRows) CopyTargets.Add(new CopyTarget(v));
        Raise(nameof(CanCopy));
    }

    public Command AddFromMod { get; }
    public string AddFromModInfo => "Add the contents of another .mgsv (outfit, head, weapon, item or buddy costume) to this mod. Every outfit, weapon or costume in the mod is listed here so you can switch between them. Ctrl+Z takes them out again.";
    /// <summary>Issue #80: a zip with several .mgsv shows the same pick list as the first open; only the pick merges.</summary>
    public ObservableCollection<ModChoice> AddChoices { get; } = [];
    public bool HasAddChoices => AddChoices.Count > 0;
    public Command CancelAddChoices => new(() => { AddChoices.Clear(); Raise(nameof(HasAddChoices)); });
    /// <summary>The line above the pick list: the mods of a zip, or the Quiet costumes of the game.</summary>
    public string AddChoicesTitle { get; private set; } = "This file contains several mods. Pick the one to add.";
    public string AddQuietCostumeInfo => "Add one of the six Quiet costumes of the game as a playable Quiet outfit. The outfit is a copy of the costume files of the game. Ctrl+Z takes it out again.";
    /// <summary>The owner, Nov 14: the Quiet buddy costumes of the game as playable Quiet outfits, with no mod.</summary>
    public Command AddQuietCostume => new(() =>
    {
        if (Busy) return;
        AddChoices.Clear();
        AddChoicesTitle = "Pick the Quiet costume of the game to add as a playable Quiet outfit."; Raise(nameof(AddChoicesTitle));
        foreach (var (package, name) in Svfm.Core.Conversion.QuietPlayableConverter.GameCostumes(_main.Session.Game))
            AddChoices.Add(new ModChoice(name, new Command(async () => { AddChoices.Clear(); Raise(nameof(HasAddChoices)); await MergeExtraAsync(new ExtraMod(Svfm.Core.Conversion.QuietPlayableConverter.GameSource, package)); })));
        Raise(nameof(HasAddChoices));
    });
    public string AddAllQuietCostumesInfo => "Add each Quiet costume of the game that is not in this mod as a playable Quiet outfit. Ctrl+Z takes them all out again.";
    /// <summary>The owner, Nov 15: each costume of the game that is not in the project, as one undo step.</summary>
    public Command AddAllQuietCostumes => new(async () =>
    {
        if (Busy) return;
        AddChoices.Clear(); Raise(nameof(HasAddChoices));
        var src = Svfm.Core.Conversion.QuietPlayableConverter.GameSource;
        var missing = Svfm.Core.Conversion.QuietPlayableConverter.GameCostumes(_main.Session.Game)
            .Where(c => !_main.Session.Merged.Any(m => m.Path == src && m.Inner == c.Package)).Select(c => new ExtraMod(src, c.Package)).ToList();
        if (missing.Count == 0) { Status = "Each Quiet costume of the game is already in this mod."; return; }
        await MergeExtraAsync(missing);
    });
    private async Task AddFromModAsync()
    {
        var f = await _main.Dialogs.PickFile("Choose a mod to add items from", ("MGSV mods", ["*.mgsv", "*.zip"]));
        if (f == null || Busy) return;
        AddChoices.Clear(); Raise(nameof(HasAddChoices));
        try
        {
            if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var inside = Svfm.Core.Mods.MgsvMod.ListMgsvInZip(f);
                if (inside.Count > 1)
                {
                    Status = AddChoicesTitle = "This file contains several mods. Pick the one to add."; Raise(nameof(AddChoicesTitle));
                    foreach (var c in inside) AddChoices.Add(new ModChoice(c.Split('/', '\\').Last(), new Command(async () => { AddChoices.Clear(); Raise(nameof(HasAddChoices)); await MergeExtraAsync(new ExtraMod(f, c)); })));
                    Raise(nameof(HasAddChoices));
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { Status = "Couldn't add it: " + e.Message; return; }
        await MergeExtraAsync(new ExtraMod(f, null));
    }
    private Task MergeExtraAsync(ExtraMod x) => MergeExtraAsync([x]);
    private async Task MergeExtraAsync(IReadOnlyList<ExtraMod> x)
    {
        if (Busy) return;
        try
        {
            Busy = true; Status = "Adding…";
            await _main.Session.MergeModsAsync(x);
            Status = "Added.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException) { Status = "Couldn't add it: " + e.Message; }
        finally { Busy = false; }
    }

    private string _search = "";
    /// <summary>The search box in the header: Enter goes to the tab that holds the field.</summary>
    public string FieldSearch { get => _search; set { if (Set(ref _search, value)) Raise(nameof(SearchHint)); } }
    public string SearchHint { get; private set; } = "";
    private static readonly Dictionary<string, string> Keywords = new()
    {
        ["name"] = "name description text language languages translate english icon picture upload generate render crop who wear worn with snake avatar dd male female soldier character branch face faces",
        ["texts"] = "other text real name",
        ["slots"] = "sortie slot slots look looks worn first label cycle button order also under other outfits vanilla suit variation no new slot",
        ["alsounder"] = "also under other outfits vanilla suit variation no new slot",
        ["abilities"] = "abilities behaves like preset silent footsteps quiet movement sprint speed sound dash attack raiden run damage reduction defense health regeneration life recovery cloth rattle hit sound",
        ["camo"] = "camo concealment ground material hide rock grass sand snow",
        ["heads"] = "head heads bandana balaclava headgear infinity",
        ["rd"] = "r&d research development grade cost gmp developed available start",
        ["deploy"] = "deployment deploy cost gmp sortie usage",
        ["stats"] = "stats fire rate range recoil reload magazine ammo bullet damage fuse effect time infinite ammo horse gear dog",
        ["settings"] = "settings script",
    };
    /// <summary>Goes to the tab whose fields match the words typed (the tab with the most matching words).</summary>
    public void RunFieldSearch()
    {
        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).ToList();
        if (words.Count == 0) { SearchHint = ""; Raise(nameof(SearchHint)); return; }
        var best = Tabs.Select(t => (t, n: words.Count(w => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || (Keywords.TryGetValue(t.Id, out var k) && k.Contains(w, StringComparison.Ordinal))))).OrderByDescending(x => x.n).First();
        if (best.n == 0) { SearchHint = "Nothing matches on this item."; Raise(nameof(SearchHint)); return; }
        SelectedTab = best.t; SearchHint = ""; Raise(nameof(SearchHint));
    }

    public Command ToggleCompare { get; }
    public Command ResetTab { get; }
    private bool _showCompare;
    public bool ShowCompare { get => _showCompare; set { if (Set(ref _showCompare, value)) { Raise(nameof(CompareLabel)); Raise(nameof(CompareVisible)); } } }
    public string CompareLabel => _showCompare ? "Hide the comparison" : "Compare with the mod's original";
    public ObservableCollection<DiffRow> CompareRows { get; } = [];
    public bool TabHasChanges => CompareRows.Count > 0;
    public bool CompareVisible => _showCompare;
    public bool NoDifferences => _showCompare && CompareRows.Count == 0;
    public string TabSubtitle => TabInfo(_tab?.Id);
    public static string TabInfo(string? id) => id switch
    {
        "name" => "What the item is called in game, its description, who can wear it and its icon.",
        "texts" => "Other texts the mod's script uses.",
        "slots" => "The looks on the sortie cycle button.",
        "deploy" => "What taking the item on a sortie costs.",
        "abilities" => "How the outfit plays.",
        "camo" => "How well the outfit hides the player on each ground.",
        "heads" => "Heads that can be worn with this outfit.",
        "sortie" => "How the item shows in the details panel of Sortie Prep.",
        "attack" => "The damage and explosion the item uses.",
        "script" => "What the item does when it is thrown, step by step.",
        "rd" => "The item's row in R&D: when it is available and what it costs.",
        "settings" => "Settings read from the mod's script.",
        _ => "",
    };

    /// <summary>Marks the tabs that differ from the original and fills the comparison of the current one.</summary>
    private static string StatusOf(ItemVm it, string tab) => tab switch
    {
        "name" => it.Name.Trim().Length == 0 || it.Outfit is { } o && !o.Wearers.Any(w => w.Wears) && o.Wearers.Count > 0 ? "bad" : it.Description.Trim().Length == 0 ? "warn" : "ok",
        "rd" => it.Develop != null && ProjectEdits.DevelopOf(it.Item) is { InitiallyAvailable: false, GmpCost: 0 } ? "warn" : "ok",
        // The slots tab now also holds "also under other outfits", so it carries both warnings: a slot with no label,
        // and an outfit added under another with no slots ticked (which would add nothing).
        "slots" => it.Outfit is { } ov && ov.LookRows.Any(r => r.Look.DisplayName != null && ov.LookLabel(r.Look).Trim().Length == 0) ? "warn"
            : it.Item is OutfitItem oo && oo.Spec.AlsoUnder.Any(a => a.Slots.Count == 0) ? "warn" : "ok",
        _ => "ok",
    };

    /// <summary>The text of the tab's "!" badge for the dot that <see cref="StatusOf"/> sets; empty when the dot is green.</summary>
    private static string NoteOf(string tab, string status) => (tab, status) switch
    {
        ("name", "bad") => "The item has no name, or nobody can wear it.",
        ("name", "warn") => "The item has no description.",
        ("rd", "warn") => "The item is not available at the start, and it costs nothing to develop.",
        ("slots", "warn") => "A sortie slot has no label, or an outfit added under another outfit has no slots ticked.",
        _ => "",
    };

    public void RefreshChanges()
    {
        CompareRows.Clear();
        if (_selected == null) return;
        foreach (var t in Tabs) { t.Changed = _selected.DiffFor(t.Id).Count > 0; t.Status = StatusOf(_selected, t.Id); if (t.Enabled) t.Note = NoteOf(t.Id, t.Status); }
        if (_tab != null) foreach (var r in _selected.DiffFor(_tab.Id)) CompareRows.Add(r);
        Raise(nameof(TabHasChanges)); Raise(nameof(NoDifferences)); ResetTab.Raise();
    }

    public Session Session => _main.Session;
    public ObservableCollection<ItemVm> Items { get; } = [];
    /// <summary>The Sortie Prep mockup on the Sortie slots tab; built only while that tab is open.</summary>
    public SortieVm Sortie => _sortie ??= new SortieVm(this);
    private SortieVm? _sortie;
    private void RefreshSortie(bool followItem = false) { if (TabSlots) Sortie.Rebuild(followItem); }
    public static string GroupOf(ProjectItem i) => i is SupportWeaponItem or ExistingItem { Support: true } ? "THROWABLES" : i.Kind switch { ItemKind.Outfit => "OUTFITS", ItemKind.ExtraLooks => "LOOKS ON VANILLA SUITS", ItemKind.HeadOption => "HEADS", ItemKind.Weapon => "WEAPONS", ItemKind.Item => "THROWABLES", _ => "BUDDY COSTUMES" };
    /// <summary>Whether an item matches a search: every word is in its name or its key.</summary>
    public static bool Matches(ItemVm i, string search) => search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || (i.Item.Key?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));
    public bool InGroup(params string[] g) => _selected != null && g.Contains(GroupOf(_selected.Item));
    /// <summary>The five item drop-downs: Outfits, Heads, Weapons, Throwables, Buddies.</summary>
    public IReadOnlyList<CategoryVm> Categories { get; }
    public ObservableCollection<TabVm> Tabs { get; } = [];
    private TabVm? _tab;
    public TabVm? SelectedTab { get => _tab; set { if (Set(ref _tab, value)) { foreach (var t in Tabs) t.IsActive = t == value; RefreshChanges(); Raise(nameof(TabSubtitle)); RebuildCopyTargets(); } if (true) foreach (var n in new[] { nameof(TabTexts), nameof(TabSettings), nameof(TabName), nameof(TabStats), nameof(TabDeploy), nameof(TabAbilities), nameof(TabRd), nameof(TabCamo), nameof(TabSlots), nameof(TabHeads), nameof(TabAlso), nameof(TabScript), nameof(CurrentStats) }) Raise(n); RefreshSortie(true); } }
    public bool TabName => _tab?.Id == "name";
    public bool TabStats => _tab != null && _selected?.StatsFor(_tab.Id) != null;
    public StatsVm? CurrentStats => _tab == null ? null : _selected?.StatsFor(_tab.Id);
    public bool TabScript => _tab?.Id == "script";
    public bool TabDeploy => _tab?.Id == "deploy";
    public bool TabTexts => _tab?.Id == "texts";
    public bool TabSettings => _tab?.Id == "settings";
    public bool TabAbilities => _tab?.Id == "abilities";
    public bool TabRd => _tab?.Id == "rd";
    public bool TabCamo => _tab?.Id == "camo";
    public bool TabSlots => _tab?.Id == "slots";
    public bool TabHeads => _tab?.Id == "heads";
    /// <summary>Kept for the saved-tab id: "also under other outfits" now lives inside the Sortie slots tab.</summary>
    public bool TabAlso => _tab?.Id == "alsounder";

    /// <summary>Shows tab number n (the number keys 1-9).</summary>
    /// <param name="n">1-based.</param>
    public void GoTab(int n) { if (n >= 1 && n <= Tabs.Count) SelectedTab = Tabs[n - 1]; }
    /// <summary>The four tab rows (issues #6 and #19).</summary>
    public List<TabVm> IdentityTabs => [.. Tabs.Where(t => t.Group == "IDENTITY")];
    public List<TabVm> BehaviourTabs => [.. Tabs.Where(t => t.Group == "BEHAVIOUR")];
    public List<TabVm> DevelopmentTabs => [.. Tabs.Where(t => t.Group == "DEVELOPMENT")];

    private void RebuildTabs()
    {
        var keep = _tab?.Id;
        Tabs.Clear();
        // Issue #35: a tab that never applies to the item's kind is hidden, so the tabs after it move up (a weapon's
        // Stats is tab 4). A tab that applies to the kind but is not available yet is greyed out and can't be opened
        // (the owner: "make it obvious which options are available and which aren't").
        // Four rows (issues #6 and #19): Identity, Behaviour, R&D and Deployment. The wearer cards
        // (Wearers, Worn with, Faces) are on Name, text & icon.
        var list = new List<(string Id, string Title, string Group, bool On)>
        {
            ("name", "Name, text & icon", "IDENTITY", _selected != null),
            ("texts", "Other in-game text", "IDENTITY", _selected?.IsExisting == true && _selected.ExtraTexts.Count > 0),
            // "Also under other outfits" is no longer its own tab: it is part of the Sortie slots tab (item 4 of the
            // Windows test feedback). The "alsounder" id is kept so saved projects and the change list still resolve.
            ("slots", "Sortie slots", "IDENTITY", _selected is { IsOutfit: true } || _selected?.Item.Kind == ItemKind.ExtraLooks),
            ("abilities", "Abilities", "BEHAVIOUR", _selected?.IsOutfit == true),
            ("camo", "Camo Values", "BEHAVIOUR", _selected?.IsOutfit == true),
            ("heads", "Heads worn", "BEHAVIOUR", _selected is { IsOutfit: true }),
        };
        if (_selected?.Stats is { } st) list.Add(("stats", st.TabTitle, "BEHAVIOUR", true));
        else list.Add(("stats", "Stats", "BEHAVIOUR", false));
        if (_selected != null) foreach (var m in _selected.MoreStats) list.Add((m.TabId, m.TabTitle, "BEHAVIOUR", true));
        list.Add(("script", "Effects", "BEHAVIOUR", _selected?.Script != null));
        list.Add(("settings", "Settings", "BEHAVIOUR", _selected?.IsExisting == true && _selected.SettingGroups.Count > 0));
        list.Add(("rd", "R&D row", "DEVELOPMENT", _selected?.Develop != null));
        list.Add(("deploy", "Deployment", "DEVELOPMENT", _selected?.Develop != null));
        if (_selected is { IsOutfit: false }) list.RemoveAll(t => t.Id is "abilities" or "camo" or "heads");
        for (int i = 0; i < list.Count; i++)
            Tabs.Add(new TabVm(list[i].Id, list[i].Title, i + 1, list[i].Group) { Enabled = list[i].On });
        foreach (var t in Tabs) { var tab = t; tab.Select = new Command(() => { if (tab.Enabled) SelectedTab = tab; }); }
        Raise(nameof(IdentityTabs)); Raise(nameof(BehaviourTabs)); Raise(nameof(DevelopmentTabs));
        SelectedTab = Tabs.FirstOrDefault(t => t.Id == keep && t.Enabled) ?? Tabs.FirstOrDefault(t => t.Enabled) ?? Tabs[0];
        RefreshChanges();
    }
    private bool _busy;
    /// <summary>A copy or a merge runs. Both commands read it, so a change turns them on or off.</summary>
    public bool Busy { get => _busy; private set { _busy = value; DuplicateCommand.Raise(); AddFromMod.Raise(); } }
    public Command DuplicateCommand { get; }
    public Command AskRemove { get; }
    public Command DoRemove { get; }
    public Command KeepIt { get; }
    public Command FillLanguages { get; }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string GameLine => _main.Session.Settings.GameFolder.Length == 0 ? "Game folder not set" : "Game folder: " + _main.Session.Settings.GameFolder;
    public bool Confirming { get => _confirm; set { Set(ref _confirm, value); Raise(nameof(NotConfirming)); } }
    public bool NotConfirming => !_confirm;
    public bool ShowLanguages { get => _showLangs; set => Set(ref _showLangs, value); }
    // A disabled Duplicate or Remove shows the reason on hover, and the "!" beside the Item menu shows it too.
    private string? DuplicateLock => _selected?.DuplicateLock;
    private string? RemoveLock => _selected?.RemoveLock;
    public bool ItemActionsLocked => _selected?.ActionsLocked ?? false;
    public string ItemActionsNote => _selected?.ActionsNote ?? "";
    public string DuplicateTip => DuplicateLock is { } d ? $"This item can't be duplicated: {d}." : "Make a copy of the selected item";
    public string RemoveTip => RemoveLock is { } r ? $"This item can't be taken out of the mod: {r}." : "Remove the selected item from this mod";
    public string RemoveText => _selected == null ? "" : _selected.IsExisting
        ? $"Remove {_selected.Title} from this mod? The build takes its calls and its R&D row out of the script. Its files and text stay in the mod. The original mod's files are not touched."
        : $"Remove {_selected.Title} from this mod? Its name and text, icon, R&D row and every setting are deleted. The original mod's files are not touched.";
    public string UsesEnglishNote => "A language left empty uses the English text.";

    public PreviewVm Preview { get; }
    public ItemVm? Selected
    {
        get => _selected;
        set => _main.Session.NotAnEdit(() => { if (Set(ref _selected, value)) { foreach (var c in Categories) c.Refresh(); Preview.Fire(Preview.LoadAsync(value)); RebuildTabs(); Confirming = false; Raise(nameof(HasSelected)); Raise(nameof(RemoveText)); foreach (var n in new[] { nameof(ItemActionsLocked), nameof(ItemActionsNote), nameof(DuplicateTip), nameof(RemoveTip) }) Raise(n); DuplicateCommand.Raise(); AskRemove.Raise(); FillLanguages.Raise(); RefreshSortie(true); } });
    }
    public bool HasSelected => _selected != null;

    /// <summary>Rebuilds the item drop-downs from the project (after opening, duplicating, removing or undoing).</summary>
    public void Reload()
    {
        var keep = _selected?.Item;
        var p = _main.Session.Project;
        var existing = Items.ToDictionary(i => i.Item);
        Items.Clear();
        if (p != null)
            foreach (var it in p.Items.Where(ShownInBar)) { var vm = existing.TryGetValue(it, out var e) ? e : new ItemVm(_main.Session, it) { ReportError = _main.ReportError, DialogsOf = () => _main.Dialogs, OpenGenerate = Preview.StartGenerate, RefreshOthers = others => { foreach (var v in Items.Where(v => others.Contains(v.Item))) v.Refresh(); } }; vm.Refresh(); Items.Add(vm); }
        Selected = Items.FirstOrDefault(i => i.Item == keep) ?? Items.FirstOrDefault();
        foreach (var c in Categories) c.Refresh();
        DuplicateCommand.Raise(); AskRemove.Raise();
    }

    /// <summary>
    /// Looks added to a vanilla suit are hidden (a port makes thirteen of them for one head); in an existing mod they show
    /// when they have text or settings of their own, or when one script call adds them to several suits.
    /// </summary>
    private static bool ShownInBar(ProjectItem i) =>
        i.Kind != ItemKind.ExtraLooks || (i is ExistingItem e && (e.Text.Count > 0 || e.Instances.Count > 0 || e.Fields.Concat(e.SpecFields).Any(f => f.Present && (f.Group == "Outfit" || f.Group.StartsWith("Abilities", StringComparison.Ordinal)))));

    private void OnChanged()
    {
        var p = _main.Session.Project;
        if (p == null) return;
        // Typing doesn't change the item list; only rebuild when the list changed.
        if (!Items.Select(i => i.Item).SequenceEqual(p.Items.Where(ShownInBar))) { Reload(); RebuildCopyTargets(); }
        else { foreach (var i in Items) i.Refresh(); Raise(nameof(RemoveText)); foreach (var c in Categories) c.Refresh(); RefreshChanges(); }
        RefreshSortie();
    }

    private async Task DuplicateAsync()
    {
        if (_selected == null) return;
        Busy = true; Status = "Copying…";
        try
        {
            var src = _selected.Item;
            var copy = await _main.Session.DuplicateAsync(src);
            Reload();
            Selected = Items.FirstOrDefault(i => i.Item == copy);
            Status = $"Copied as “{copy.Title}”.";
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { Status = "Couldn't copy: " + e.Message; }
        finally { Busy = false; }
    }

    private void RemoveSelected()
    {
        if (_selected == null || Items.Count < 2) return;
        var item = _selected.Item;
        int at = Items.IndexOf(_selected);
        _main.Session.Remove(item);
        Reload();
        Selected = Items[Math.Min(at, Items.Count - 1)];
        // The owner asked us not to promise Ctrl+Z here: "Do not say Ctrl+Z brings it back."
        Status = $"Removed “{item.Title}”.";
    }

    private void FillEmptyFromEnglish()
    {
        if (_selected == null) return;
        foreach (var id in new[] { _selected.NameId, _selected.InfoId })
        {
            string en = _selected.GetText(id, "eng", false);
            foreach (var r in _selected.Rows.Where(r => !r.IsEnglish))
                if (!_selected.Has(id, r.Code) && en.Length > 0) _selected.SetText(id, r.Code, en);
        }
    }
}

/// <summary>Review &amp; build: mod details, checks, dry run, log and the build itself.</summary>
public sealed class BuildVm : Observable
{
    private readonly MainVm _main;
    private bool _override, _clash, _dry;
    private string _status = "";
    private ProjectBuildResult? _result;

    public BuildVm(MainVm main)
    {
        _main = main;
        _clash = main.Session.Settings.CheckClashes;
        BuildNow = new Command(async () => await BuildAsync(), () => CanBuild);
        BackToEdit = new Command(() => _main.ShowEditor());
        Recheck();
    }

    public Session Session => _main.Session;
    public ModProject Project => _main.Session.Project!;
    public Command BuildNow { get; }
    public Command BackToEdit { get; }
    public ObservableCollection<string> Errors { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> Files { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<string> Contents { get; } = [];
    public ObservableCollection<ContentRow> ContentRows { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public Command DryRunNow => new(() => Recheck());
    public string FileName => string.Concat(Project.Name.Replace(' ', '_').Where(c => !Path.GetInvalidFileNameChars().Contains(c))) + "_" + Project.Version + ".mgsv";
    public string Headline => $"{Project.Items.Count} item{(Project.Items.Count == 1 ? "" : "s")} · {Files.Count} files";
    public string AttentionTitle => $"{Errors.Count} thing{(Errors.Count == 1 ? "" : "s")} need{(Errors.Count == 1 ? "s" : "")} attention";

    public string ModName { get => Project.Name; set { Session.Checkpoint("mod:name"); Project.Name = value; Session.NotifyChanged(); Raise(); Raise(nameof(FileName)); } }
    public string ModVersion { get => Project.Version; set { Session.Checkpoint("mod:version"); Project.Version = value; Session.NotifyChanged(); Raise(); Raise(nameof(FileName)); } }
    public string ModAuthor { get => Project.Author; set { Session.Checkpoint("mod:author"); Project.Author = value; Session.NotifyChanged(); Raise(); } }
    public string ModDescription { get => Project.Description; set { Session.Checkpoint("mod:desc"); Project.Description = value; Session.NotifyChanged(); Raise(); } }

    public bool BuildAnyway { get => _override; set { Set(ref _override, value); Raise(nameof(CanBuild)); Raise(nameof(BuildLabel)); BuildNow.Raise(); } }
    public bool CheckClashes { get => _clash; set { if (Set(ref _clash, value)) { Session.Settings.CheckClashes = value; Session.Settings.Save(); Recheck(); } } }
    public bool HasErrors => Errors.Count > 0;
    public bool NoErrors => Errors.Count == 0;
    public bool HasWarnings => Warnings.Count > 0;
    public bool CanBuild => !_building && Ready;
    private bool Ready => _result != null && (_result.Ok || _override);
    private bool _building;
    public string BuildLabel => HasErrors ? (_override ? "Build anyway (not recommended)" : $"Build (fix the {Errors.Count} problem{(Errors.Count == 1 ? "" : "s")} first)") : "Build";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool DryRun { get => _dry; set => Set(ref _dry, value); }
    public string Summary => _result == null ? "" : $"{_result.LookSelectors} of 138 look selectors · {_result.PartsTypes} of 223 parts types";

    public void Recheck()
    {
        Errors.Clear(); Warnings.Clear(); Files.Clear(); Log.Clear(); Contents.Clear(); ContentRows.Clear(); Checks.Clear();
        var t = DateTime.Now.ToString("HH:mm:ss");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _result = ProjectBuilder.Build(Project, Session.Game);
        DevLog.Info("build", $"Dry run: {_result.Errors.Count} error(s), {_result.Warnings.Count} warning(s), {_result.Mod.ArchiveFiles.Count} files ({sw.ElapsedMilliseconds} ms)");
        foreach (var e in _result.Errors) DevLog.Error("build", e);
        foreach (var w in _result.Warnings) DevLog.Warn("build", w);
        foreach (var e in _result.Errors) { Errors.Add(e); Log.Add($"{t}  ERROR  {e}"); }
        foreach (var w in _result.Warnings) { Warnings.Add(w); Log.Add($"{t}  WARN   {w}"); }
        long total = 0;
        foreach (var (path, data) in _result.Mod.ArchiveFiles.Concat(_result.Mod.GameFiles)) { Files.Add($"{path}   ({FormatSize(data.Length)})"); total += data.Length; }
        Log.Insert(0, $"{t}  project: {Project.Name} {Project.Version}, {Project.Items.Count} item(s)");
        Log.Add($"{t}  dry run: would write {Files.Count} files, {FormatSize(total)}; wrote nothing");
        foreach (var i in Project.Items) { Contents.Add($"{(i.Title.Length > 0 ? i.Title : i.Id)} · {i.Kind} · {i.Files.Count} files"); ContentRows.Add(new ContentRow(i.Title.Length > 0 ? i.Title : i.Id, EditorVm.GroupOf(i) switch { "OUTFITS" => "Outfit", "LOOKS ON VANILLA SUITS" => "Looks on a vanilla suit", "HEADS" => "Head option", "WEAPONS" => "Weapon", "THROWABLES" => "Thrown item", _ => "Buddy costume" }, $"{i.Files.Count} files")); }
        if (CheckClashes)
        {
            var found = ClashCheck();
            Log.Add($"{t}  clash check: {(found.Count == 0 ? "no clashes with installed mods" : found.Count + " possible clash(es)")}");
            foreach (var c in found) { Warnings.Add(c); }
        }
        foreach (var e in Errors) Checks.Add(new CheckRow("Needs attention before building", e, "bad"));
        foreach (var w in Warnings) Checks.Add(new CheckRow(w.StartsWith("Clash", StringComparison.Ordinal) ? "Clash check" : "Worth knowing", w, "warn"));
        if (Errors.Count == 0) Checks.Add(new CheckRow("No vanilla file is replaced", "Everything the mod adds uses new paths; nothing in the game's own files is touched.", "ok"));
        if (CheckClashes && !Warnings.Any(w => w.Contains("lash"))) Checks.Add(new CheckRow("No clash with installed mods", "Checked against the mods folder set in Settings.", "ok"));
        DevLog.LastBuildReport = string.Join('\n', new[] { $"Build report (dry run, {t})" }.Concat(Log).Concat(Files.Select(f => "  file: " + f)));
        Raise(nameof(FileName)); Raise(nameof(Headline)); Raise(nameof(AttentionTitle));
        Raise(nameof(HasErrors)); Raise(nameof(NoErrors)); Raise(nameof(HasWarnings)); Raise(nameof(CanBuild)); Raise(nameof(BuildLabel)); Raise(nameof(Summary));
        BuildNow.Raise();
    }

    private List<string> ClashCheck()
    {
        var found = new List<string>();
        string folder = Session.Settings.ModsFolder;
        if (folder.Length == 0 || !Directory.Exists(folder)) { found.Add("Clash check skipped: the Diamond Mod Manager mods folder isn't set or doesn't exist."); return found; }
        var others = ModConflicts.ReadModsFolder(folder);
        found.AddRange(ModConflicts.Find(ModFootprint.From(_result!.Mod), others).Select(c => c.ToString()));
        return found;
    }

    private static string FormatSize(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} B";

    private async Task BuildAsync()
    {
        if (_building) return;
        // The save runs off the UI thread, so a second click would start a second save of the same file.
        _building = true; BuildNow.Raise(); Raise(nameof(CanBuild));
        try { await BuildOnce(); }
        finally { _building = false; BuildNow.Raise(); Raise(nameof(CanBuild)); }
    }

    private async Task BuildOnce()
    {
        string suggested = string.Concat(Project.Name.Replace(' ', '_').Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (suggested.Length == 0) suggested = Project.Id;
        var file = await _main.Dialogs.SaveFile("Build the mod", suggested, ".mgsv");
        if (file == null) return;
        // The suggested name is often the source mod's own file name. Writing over it would lose the mod the project reads.
        var s = _main.Session;
        var sources = new[] { s.Source?.SourcePath }.Concat(s.Extras.Select(x => x.Path)).Concat(s.Merged.Select(x => x.Path));
        if (sources.Any(p => p != null && string.Equals(Path.GetFullPath(p), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"{Path.GetFileName(file)} is a mod this project is made from. Choose another name, so the original stays.";
            return;
        }
        try
        {
            Recheck();
            if (!Ready) return;
            var mod = _result!.Mod;
            Status = "Building...";
            // A big mod takes seconds to pack; off the UI thread the window keeps drawing.
            await Task.Run(() => mod.Save(file));
            DevLog.Info("build", "Built " + file + " (" + new FileInfo(file).Length + " bytes)");
            Status = $"Built {Path.GetFileName(file)} ({FormatSize(new FileInfo(file).Length)}).";
            Log.Add($"{DateTime.Now:HH:mm:ss}  build ok: {file}");
            DevLog.LastBuildReport += $"\n{DateTime.Now:HH:mm:ss}  build ok: {file}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _main.ReportError(e); // puts it in the Problem report and offers the report button
            Status = "Couldn't write the file: " + e.Message;
        }
    }
}
