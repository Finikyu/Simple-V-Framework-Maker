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
    public GameData? Game { get; private set; }
    public MgsvMod? Source { get; private set; }
    public IntakeResult? Intake { get; private set; }
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
    public string? SourceInner { get; private set; }

    /// <summary>Other mods opened together with the main one: its separate parts (textures, another character's body) or the model mod its textures are for.</summary>
    public IReadOnlyList<ExtraMod> Extras { get; private set; } = [];

    /// <summary>The items of the project file just opened whose edits could not be put back (the mod changed since the save).</summary>
    public IReadOnlyList<string> NotApplied { get; private set; } = [];

    public async Task<IntakeResult> OpenModAsync(string modPath, string? inner = null, IReadOnlyList<ExtraMod>? extras = null, IReadOnlySet<string>? savedIds = null)
    {
        DevLog.Info("open", "Opening " + modPath + (inner != null ? " (" + inner + ")" : ""));
        if (Settings.GameFolder.Length == 0) throw new InvalidOperationException("Choose the game folder first.");
        var folder = Settings.GameFolder;
        var (game, source, intake) = await Task.Run(() =>
        {
            var g = Game ?? GameFactory(folder);
            var m = MgsvMod.Open(modPath, inner);
            var whole = extras is not { Count: > 0 } ? m : MgsvMod.Combine([.. extras.Select(x => MgsvMod.Open(x.Path, x.Inner)), m], null, m);
            // A project file: the IDs it has decide the "Unique mod IDs" switch (Intake.OpenAsSaved).
            return (g, m, savedIds == null ? Svfm.Core.Project.Intake.Open(g, whole) : Svfm.Core.Project.Intake.OpenAsSaved(u => Svfm.Core.Project.Intake.Open(g, whole, u), savedIds));
        });
        // A mod with nothing to port does not replace the open project: the caller shows why, and the old session stays whole (#47).
        if (intake.Project == null) return intake;
        Game = game; Source = source; Intake = intake; SourceInner = inner; Extras = extras ?? []; NotApplied = [];
        // The R&D tab's "copy from a vanilla item" picker names rows from the game's own text, so it needs the game open
        // before it is built. Without this the picker listed raw lang IDs ("name_st_19001") instead of "Olive Drab",
        // which is exactly what the owner said made the feature useless: "we don't know the IDs and the user shouldn't be
        // assumed to do so".
        Svfm.Core.Knowledge.GameKnowledge.EnglishNamesGame = game;
        DevelopVm.RefreshVanillaRows();
        ProjectFile = null;
        Edits = intake.Project != null ? new ProjectEdits(intake) : null;
        NotifyChanged();
        return intake;
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

    public void ForgetGame() { Game = null; }

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
    public ProjectEdits? Edits { get; private set; }

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
        var edits = Edits ?? throw new InvalidOperationException("Nothing to save.");
        // Written beside it and then moved over it, so a failed write never leaves a cut-off project file.
        File.WriteAllText(file + ".tmp", edits.Save(Source!.SourcePath, SourceInner, Extras));
        File.Move(file + ".tmp", file, overwrite: true);
        ProjectFile = file; edits.MarkSaved();
        NotifyChanged();
    }

    /// <summary>Copies an item (the converter runs again with the copy's own names).</summary>
    /// <param name="item">The item to copy.</param>
    /// <returns>The copy.</returns>
    public async Task<ProjectItem> DuplicateAsync(ProjectItem item)
    {
        Checkpoint();
        var intake = Intake!;
        await IntakeWork.WaitAsync();
        ProjectItem copy;
        try { copy = await Task.Run(() => intake.Duplicate(item)); } finally { IntakeWork.Release(); }
        Edits!.AddCopy(item, copy);
        NotifyChanged();
        return copy;
    }

    public void Remove(ProjectItem item)
    {
        Checkpoint();
        Project!.Items.Remove(item);
        NotifyChanged();
    }

    /// <summary>Opens a saved project: the source mod again, then the user's changes.</summary>
    /// <param name="file">The .svfm file.</param>
    /// <returns>What was found.</returns>
    public async Task<IntakeResult> OpenProjectAsync(string file)
    {
        var sp = ProjectEdits.Read(File.ReadAllText(file));
        if (!File.Exists(sp.SourceMod)) throw new FileNotFoundException($"The mod this project was made from is missing: {sp.SourceMod}");
        // The open takes the session and the merges then change it, so a failure part-way puts the old session back (#47).
        var old = (Game, Source, Intake, SourceInner, Extras, NotApplied, ProjectFile, Edits);
        try
        {
            var savedIds = sp.Items.Select(i => i.Id).Concat(sp.Removed ?? []).ToHashSet(StringComparer.Ordinal);
            var res = await OpenModAsync(sp.SourceMod, sp.SourceInner, sp.Extras, savedIds);
            if (res.Project == null) throw new InvalidDataException("The mod can't be opened any more.");
            foreach (var m in sp.Merged) { if (!Svfm.Core.Project.Intake.IsMadeByApp(m) && !File.Exists(m.Path)) throw new FileNotFoundException($"A mod merged into this project is missing: {m.Path}"); await MergeModsAsync([m], savedIds); }
            NotApplied = Edits!.Apply(sp);
            ProjectFile = file;
            NotifyChanged();
            return res;
        }
        catch
        {
            (Game, Source, Intake, SourceInner, Extras, NotApplied, ProjectFile, Edits) = old;
            NotifyChanged();
            throw;
        }
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
        var edits = Edits ?? throw new InvalidOperationException("No project.");
        var folder = Settings.GameFolder;
        var host = Intake;
        var others = await Task.Run(() => { var game = Game ?? GameFactory(folder); return mods.Select(m => savedIds == null ? Svfm.Core.Project.Intake.OpenExtra(game, m, host) : Svfm.Core.Project.Intake.OpenAsSaved(u => Svfm.Core.Project.Intake.OpenExtra(game, m, host, u), savedIds)).ToList(); });
        Checkpoint();
        for (int i = 0; i < mods.Count; i++) edits.Merge(mods[i], others[i]);
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
        // The tick box shows the copy only when it is made: a second click in that time must not make a second copy.
        if (_copyInProgress) return;
        _copyInProgress = true;
        try { await SetCopyCoreAsync(source, kind, on); } finally { _copyInProgress = false; }
    }
    private bool _copyInProgress;

    private async Task SetCopyCoreAsync(ProjectItem source, CopyKind kind, bool on)
    {
        var edits = Edits ?? throw new InvalidOperationException("No project.");
        var have = edits.CopiesFor(source, kind);
        if (on == have.Count > 0) return;
        if (!on)
        {
            Checkpoint();
            foreach (var i in have) Project!.Items.Remove(i);
            NotifyChanged();
            return;
        }
        if (kind == CopyKind.Quiet) { await MergeModAsync(OutfitCopies.QuietExtra(source)); return; }
        Checkpoint();
        var intake = Intake!;
        await IntakeWork.WaitAsync();
        ProjectItem copy;
        try { copy = await Task.Run(() => OutfitCopies.MakeWearerCopy(intake, (OutfitItem)source, kind)); } finally { IntakeWork.Release(); }
        edits.AddCopy(source, copy);
        NotifyChanged();
    }
}
