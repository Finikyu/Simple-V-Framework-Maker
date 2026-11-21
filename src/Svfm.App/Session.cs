using Svfm.Core.Formats;
using Svfm.Core.Archives;
using Svfm.Core.Mods;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>The open mod: the game, the source mod and the project being edited, with its edits.</summary>
public sealed class Session : Observable
{
    /// <summary>Opens the game the user chose. Tests replace it with a small game.</summary>
    public static Func<string, GameData> GameFactory { get; set; } = GameData.OpenGameFolder;

    public Settings Settings { get; }
    /// <summary>The file pickers and questions of the window (set by <see cref="MainVm"/>).</summary>
    public IDialogs? Dialogs { get; set; }
    private GameData? _game;
    private long _gameChecked;
    private readonly object _gameLock = new();
    /// <summary>
    /// The open game. SnakeBite, DMM or a Steam verify can change its archives while the app runs
    /// (<see cref="GameData.Changed"/>); the game is then opened again. One check every 2 s at most.
    /// </summary>
    public GameData? Game
    {
        get
        {
            lock (_gameLock)
            {
                var g = _game;
                if (g == null || Environment.TickCount64 - _gameChecked < 2000) return g;
                _gameChecked = Environment.TickCount64;
                if (!g.Changed()) return g;
                try
                {
                    var fresh = GameFactory(Settings.GameFolder);
                    DevLog.Info("game", "The game files changed (a mod manager or Steam). The game is opened again.");
                    if (Svfm.Core.Knowledge.GameKnowledge.EnglishNamesGame == g) Svfm.Core.Knowledge.GameKnowledge.EnglishNamesGame = fresh;
                    // The old one is not disposed: a background job can still read it. Its files close when it is collected.
                    return _game = fresh;
                }
                catch (Exception e) // a manager in the middle of a write: keep the old one and try again at the next check
                {
                    DevLog.Warn("game", "The game files changed but could not be opened again: " + e.Message);
                    return g;
                }
            }
        }
        private set { lock (_gameLock) { _game = value; _gameChecked = Environment.TickCount64; } }
    }
    /// <summary>The open project and its flows (null when no project is open). This class adds the log and the change notice.</summary>
    private OpenProject? _open;
    public MgsvMod? Source => _open?.Source;
    public IntakeResult? Intake => _open?.Intake;
    public ModProject? Project => Intake?.Project;
    public string? ProjectFile { get; private set; }
    /// <summary>One background job on the Intake at a time: an icon render and a copy both run the converter, which changes the project.</summary>
    public SemaphoreSlim IntakeWork { get; } = new(1, 1);

    public Session(Settings settings) { Settings = settings; Svfm.Core.Project.Intake.UniqueIds = settings.UniqueModIds; }

    public bool CanUndo => Edits?.CanUndo == true;
    public bool Dirty => Edits?.Dirty == true;
    public string SaveLabel => Project == null ? "" : Dirty ? "Unsaved changes" : "Saved";

    /// <summary>Raised after anything the screens show has changed (an edit, undo, a duplicate, a removal).</summary>
    public event Action? Changed;

    public void NotifyChanged()
    {
        Edits?.SyncTwins();
        Raise(nameof(CanUndo)); Raise(nameof(Dirty)); Raise(nameof(SaveLabel));
        Changed?.Invoke();
    }

    /// <summary>Turns Sync of an item and its twin on or off (GLOSSARY.md, Twin).</summary>
    public void SetTwinSync(ProjectItem item, bool on)
    {
        if (Edits?.SetSync(item, on) != true) return;
        DevLog.Info("edit", "change sync");
        NotifyChanged();
    }

    /// <summary>Opens the game (checks the folder) and a mod, and ports what the app can. The slow part runs off the UI thread.</summary>
    /// <param name="modPath">The .mgsv or .zip.</param>
    /// <returns>What was found.</returns>
    public string? SourceInner => _open?.SourceInner;

    /// <summary>Other mods opened together with the main one: its separate parts (textures, another character's body) or the model mod its textures are for.</summary>
    public IReadOnlyList<ExtraMod> Extras => _open?.Extras ?? [];

    /// <summary>The items of the project file just opened whose edits could not be put back (the mod changed since the save).</summary>
    public IReadOnlyList<string> NotApplied => _open?.NotApplied ?? [];

    public async Task<IntakeResult> OpenModAsync(string modPath, string? inner = null, IReadOnlyList<ExtraMod>? extras = null, IReadOnlySet<string>? savedIds = null)
    {
        DevLog.Info("open", "Opening " + modPath + (inner != null ? " (" + inner + ")" : ""));
        if (Settings.GameFolder.Length == 0) throw new InvalidOperationException("Choose the game folder first.");
        var (intake, open) = await OpenProject.OpenModAsync(GameNow, modPath, inner, extras, savedIds, IntakeWork);
        // A mod with nothing to port does not replace the open project: the caller shows why, and the old session stays whole (#47).
        if (open == null) return intake;
        Take(open, null);
        return intake;
    }

    private GameData GameNow() => Game ?? GameFactory(Settings.GameFolder);

    /// <summary>Makes a project the open one.</summary>
    private void Take(OpenProject open, string? file)
    {
        Game = open.Game; _open = open; ProjectFile = file;
        // The R&D tab's "copy from a vanilla item" picker names rows from the game's own text, so it needs the game open
        // before it is built. Without this the picker listed raw lang IDs ("name_st_19001") instead of "Olive Drab",
        // which is exactly what the owner said made the feature useless: "we don't know the IDs and the user shouldn't be
        // assumed to do so". OpenProject sets the game of the names (GameKnowledge.EnglishNamesGame) when it opens the mod.
        DevelopVm.RefreshVanillaRows();
        NotifyChanged();
    }

    /// <summary>
    /// Starts a brand-new project from a .mgsv, discarding whatever was open. This is the "open a new .mgsv at any time"
    /// the owner asked for, as opposed to <see cref="MergeModAsync"/>, which adds a mod's items to the project already
    /// open. It is deliberately just <see cref="OpenModAsync"/> with nothing carried over: that method already clears the
    /// undo history, the saved file name, the merged mods and the remembered originals, so the result is a clean project.
    /// </summary>
    /// <param name="modPath">The .mgsv to start from.</param>
    /// <returns>What was found in it.</returns>
    public Task<IntakeResult> NewProjectFromModAsync(string modPath) => OpenModAsync(modPath);

    /// <summary>
    /// Lets go of the game after a change of the game folder. When nothing open uses it, it is closed, so that its files are
    /// not locked and its memory is released (T11). The open project still reads the game it was opened with (icon jobs,
    /// issue #99): that one is not disposed, and its files close when it is collected.
    /// </summary>
    /// <returns>True when the old game was disposed.</returns>
    public bool ForgetGame()
    {
        GameData? old;
        lock (_gameLock) { old = _game; _game = null; _gameChecked = Environment.TickCount64; }
        if (old == null || _open?.Game == old) return false;
        if (Svfm.Core.Knowledge.GameKnowledge.EnglishNamesGame == old) Svfm.Core.Knowledge.GameKnowledge.EnglishNamesGame = null;
        old.Dispose();
        return true;
    }

    private (GameData Game, Svfm.Core.Knowledge.EquipTables? Tables)? _equip;
    /// <summary>The game's weapon part tables, read once for each game. Null with no game open.</summary>
    public Svfm.Core.Knowledge.EquipTables? VanillaEquip
    {
        get
        {
            if (Game == null) return null;
            if (_equip?.Game != Game) _equip = (Game, Svfm.Core.Knowledge.EquipTables.Load(Game.TryRead));
            return _equip!.Value.Tables;
        }
    }

    // ---- Edits: undo, Reset, Compare and the project file are in ProjectEdits ----

    /// <summary>The edits of the open project (null when no project is open).</summary>
    public ProjectEdits? Edits => _open?.Edits;

    /// <summary>Puts parts of an item back as the original had them (one undo step).</summary>
    public void ResetPart(ProjectItem i, params EditPart[] parts)
    {
        if (Edits?.Reset(i, parts) != true) return;
        DevLog.Info("edit", "change");
        NotifyChanged();
    }

    /// <summary>Call before a change. Typing in one field is one undo step (a pause of 1.5 seconds starts another).</summary>
    /// <param name="field">Which field is being edited, or "" for a one-off action.</param>
    public void Checkpoint(string field = "")
    {
        if (Edits == null) return;
        if (field.Length == 0 || field != Edits.LastField) DevLog.Info("edit", field.Length == 0 ? "change" : "change " + field);
        if (Edits.Checkpoint(field)) NotifyChanged();
    }

    /// <summary>
    /// Runs a change of what the screens show (another item selected) that is not an edit. Values that the controls write
    /// while it runs are undone: a combo box keeps its index when its list changes to the new item's, and writes that
    /// choice into the new item (seen Nov 3: CLOTH SOUND of an outfit, after the item before it was removed).
    /// </summary>
    /// <param name="change">The change.</param>
    public void NotAnEdit(Action change)
    {
        int depth = Edits?.Mark() ?? 0;
        try { change(); }
        finally { if (Edits?.UndoTo(depth) == true) NotifyChanged(); }
    }

    public async Task UndoAsync()
    {
        // An icon render or a copy still running on the Intake would write after the undo, or change the item list while the undo fills it.
        await IntakeWork.WaitAsync();
        try { if (Edits?.Undo() == true) NotifyChanged(); } finally { IntakeWork.Release(); }
    }

    public void SaveProject(string file)
    {
        DevLog.Info("project", "Saving " + file);
        var open = _open ?? throw new InvalidOperationException("Nothing to save.");
        // Written beside it and then moved over it, so a failed write never leaves a cut-off project file.
        File.WriteAllText(file + ".tmp", open.Save());
        File.Move(file + ".tmp", file, overwrite: true);
        ProjectFile = file; open.Edits.MarkSaved();
        NotifyChanged();
    }

    /// <summary>Copies an item (the converter runs again with the copy's own names).</summary>
    /// <param name="item">The item to copy.</param>
    /// <returns>The copy.</returns>
    public async Task<ProjectItem> DuplicateAsync(ProjectItem item)
    {
        DevLog.Info("edit", "change");
        var copy = await _open!.DuplicateAsync(item);
        NotifyChanged();
        return copy;
    }

    /// <summary>"Make grades..." (issue #121): copies of an item at the given grades, each linked to the one before.</summary>
    public async Task<List<ProjectItem>> MakeGradesAsync(ProjectItem item, IReadOnlyList<int> grades)
    {
        DevLog.Info("edit", "change");
        var made = await _open!.MakeGradesAsync(item, grades);
        NotifyChanged();
        return made;
    }

    public void Remove(ProjectItem item)
    {
        DevLog.Info("edit", "change");
        _open!.Remove(item);
        NotifyChanged();
    }

    /// <summary>Opens a saved project: the source mod again, then the user's changes.</summary>
    /// <param name="file">The .svfm file.</param>
    /// <returns>What was found.</returns>
    public async Task<IntakeResult> OpenProjectAsync(string file)
    {
        DevLog.Info("open", "Opening " + file);
        if (Settings.GameFolder.Length == 0) throw new InvalidOperationException("Choose the game folder first.");
        // A failure part-way throws before the session takes the project, so the old session stays whole (#47).
        var open = await OpenProject.OpenFileAsync(GameNow, file, IntakeWork);
        Take(open, file);
        return open.Intake;
    }

    /// <summary>Mods whose items were merged into the open project (listed in the project file).</summary>
    public IReadOnlyList<ExtraMod> Merged => Edits?.Merged ?? [];

    /// <summary>Adds another mod's items to the open project; every edit so far stays, and Ctrl+Z takes the items out again.</summary>
    /// <param name="mod">The mod to add.</param>
    public Task MergeModAsync(ExtraMod mod) => MergeModsAsync([mod]);

    /// <summary>Adds the items of several mods as one undo step; each is opened before any is added.</summary>
    /// <param name="mods">The mods to add.</param>
    /// <param name="savedIds">The item IDs of the project file that is opening, else null.</param>
    public async Task MergeModsAsync(IReadOnlyList<ExtraMod> mods, IReadOnlySet<string>? savedIds = null)
    {
        var open = _open ?? throw new InvalidOperationException("No project.");
        await open.MergeAsync(mods, savedIds);
        DevLog.Info("edit", "change");
        NotifyChanged();
    }

    /// <summary>
    /// Makes or removes the copy of a body port for other wearers (<see cref="OutfitCopies"/>): one undo step. The Quiet
    /// copy (two twin items) is merged; the Ocelot and Snake copies are copies of the outfit.
    /// </summary>
    /// <param name="source">The body port.</param>
    /// <param name="kind">Which copy.</param>
    /// <param name="on">True to make it, false to remove it.</param>
    public async Task SetCopyAsync(ProjectItem source, CopyKind kind, bool on)
    {
        // The tick box shows the copy only when it is made: a second click in that time must not make a second copy (OpenProject.SetCopyAsync).
        var open = _open ?? throw new InvalidOperationException("No project.");
        if (!await open.SetCopyAsync(source, kind, on)) return;
        DevLog.Info("edit", "change");
        NotifyChanged();
    }
}
