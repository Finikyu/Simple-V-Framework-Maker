using Svfm.Core.Archives;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;
using Port = Svfm.Core.Project.Intake;

namespace Svfm.Core.Project;

/// <summary>
/// The open project: the source mod, its intake and its edits, and the flows that change them. Each flow holds its
/// order here (open a mod, open a project file, merge, duplicate, remove, a copy for other wearers on or off). A slow
/// step runs off the caller's thread. A change of the edits runs on the caller's thread.
/// </summary>
public sealed class OpenProject
{
    private readonly Func<GameData> _game;
    private bool _copyInProgress;

    /// <summary>The game the source mod was opened with.</summary>
    public GameData Game { get; }
    public MgsvMod Source { get; }
    public string? SourceInner { get; }
    /// <summary>Other mods opened together with the source mod.</summary>
    public IReadOnlyList<ExtraMod> Extras { get; }
    public IntakeResult Intake { get; }
    public ModProject Project => Intake.Project!;
    public ProjectEdits Edits { get; }
    /// <summary>The items of the project file whose edits could not be put back (the mod changed since the save).</summary>
    public IReadOnlyList<string> NotApplied { get; private set; } = [];
    /// <summary>One background job on the intake at a time: the converter changes the project.</summary>
    public SemaphoreSlim Work { get; }

    private OpenProject(Func<GameData> game, GameData opened, MgsvMod source, string? inner, IReadOnlyList<ExtraMod> extras, IntakeResult intake, SemaphoreSlim? work)
    {
        _game = game; Game = opened; Source = source; SourceInner = inner; Extras = extras; Intake = intake;
        Edits = new ProjectEdits(intake);
        Edits.SyncTwins();
        Work = work ?? new(1, 1);
    }

    /// <summary>Opens a mod and ports what the app can.</summary>
    /// <param name="game">Gives the open game. It is called off the caller's thread, and again for each merge.</param>
    /// <param name="modPath">The .mgsv or .zip.</param>
    /// <param name="inner">The mod in the .zip, when the .zip holds more than one.</param>
    /// <param name="extras">Other mods to open together with this one.</param>
    /// <param name="savedIds">The item IDs of the project file that is opening, else null.</param>
    /// <param name="work">The lock for background jobs on the intake, when the caller has its own.</param>
    /// <returns>What was found, and the open project. The project is null when the mod has nothing to port.</returns>
    public static async Task<(IntakeResult Found, OpenProject? Open)> OpenModAsync(Func<GameData> game, string modPath, string? inner = null,
        IReadOnlyList<ExtraMod>? extras = null, IReadOnlySet<string>? savedIds = null, SemaphoreSlim? work = null)
    {
        var (g, source, intake) = await Task.Run(() =>
        {
            var g = game();
            var m = MgsvMod.Open(modPath, inner);
            var whole = extras is not { Count: > 0 } ? m : MgsvMod.Combine([.. extras.Select(x => MgsvMod.Open(x.Path, x.Inner)), m], null, m);
            // A project file: the IDs it has decide the "Unique mod IDs" switch (Intake.OpenAsSaved).
            return (g, m, savedIds == null ? Port.Open(g, whole) : Port.OpenAsSaved(u => Port.Open(g, whole, u), savedIds));
        });
        if (intake.Project == null) return (intake, null);
        // The names of vanilla rows come from the game's own text, so the game must be set before a screen lists them.
        GameKnowledge.EnglishNamesGame = g;
        return (intake, new OpenProject(game, g, source, inner, extras ?? [], intake, work));
    }

    /// <summary>
    /// Opens a saved project: the source mod with the saved IDs, then each merged mod, then the saved edits. A failure
    /// throws and gives no project, so the caller keeps the project it has (#47).
    /// </summary>
    /// <param name="game">Gives the open game.</param>
    /// <param name="file">The .svfm file.</param>
    /// <param name="work">The lock for background jobs on the intake, when the caller has its own.</param>
    public static async Task<OpenProject> OpenFileAsync(Func<GameData> game, string file, SemaphoreSlim? work = null)
    {
        var sp = ProjectEdits.Read(File.ReadAllText(file));
        if (!File.Exists(sp.SourceMod)) throw new FileNotFoundException($"The mod this project was made from is missing: {sp.SourceMod}");
        var savedIds = sp.Items.Select(i => i.Id).Concat(sp.Removed ?? []).ToHashSet(StringComparer.Ordinal);
        var (_, open) = await OpenModAsync(game, sp.SourceMod, sp.SourceInner, sp.Extras, savedIds, work);
        if (open == null) throw new InvalidDataException("The mod can't be opened any more.");
        foreach (var m in sp.Merged)
        {
            if (!Port.IsMadeByApp(m) && !File.Exists(m.Path)) throw new FileNotFoundException($"A mod merged into this project is missing: {m.Path}");
            await open.MergeAsync([m], savedIds);
        }
        open.NotApplied = open.Edits.Apply(sp);
        open.Edits.SyncTwins();
        return open;
    }

    /// <summary>Adds the items of several mods as one undo step; each is opened before any is added.</summary>
    /// <param name="mods">The mods to add.</param>
    /// <param name="savedIds">The item IDs of the project file that is opening, else null.</param>
    public async Task MergeAsync(IReadOnlyList<ExtraMod> mods, IReadOnlySet<string>? savedIds = null)
    {
        var host = Intake;
        var others = await Task.Run(() =>
        {
            var game = _game();
            return mods.Select(m => savedIds == null ? Port.OpenExtra(game, m, host) : Port.OpenAsSaved(u => Port.OpenExtra(game, m, host, u), savedIds)).ToList();
        });
        Checkpoint();
        for (int i = 0; i < mods.Count; i++) Edits.Merge(mods[i], others[i]);
        Edits.SyncTwins();
    }

    /// <summary>Copies an item as one undo step (the converter runs again with the copy's own names).</summary>
    /// <returns>The copy.</returns>
    public async Task<ProjectItem> DuplicateAsync(ProjectItem item)
    {
        Checkpoint();
        await Work.WaitAsync();
        ProjectItem copy;
        try { copy = await Task.Run(() => Intake.Duplicate(item)); } finally { Work.Release(); }
        Edits.AddCopy(item, copy);
        Edits.SyncTwins();
        return copy;
    }

    /// <summary>Removes an item as one undo step.</summary>
    public void Remove(ProjectItem item)
    {
        Checkpoint();
        Project.Items.Remove(item);
        Edits.SyncTwins();
    }

    /// <summary>
    /// Makes or removes the copy of a body port for other wearers (<see cref="OutfitCopies"/>): one undo step. The Quiet
    /// copy (two twin items) is merged; the Ocelot and Snake copies are copies of the outfit.
    /// </summary>
    /// <returns>False when nothing changed: the copy is already as asked, or another call is in progress.</returns>
    public async Task<bool> SetCopyAsync(ProjectItem source, CopyKind kind, bool on)
    {
        // The copy shows only when it is made: a second call in that time must not make a second copy.
        if (_copyInProgress) return false;
        _copyInProgress = true;
        try
        {
            var have = Edits.CopiesFor(source, kind);
            if (on == have.Count > 0) return false;
            if (!on)
            {
                Checkpoint();
                foreach (var i in have) Project.Items.Remove(i);
                Edits.SyncTwins();
                return true;
            }
            if (kind == CopyKind.Quiet) { await MergeAsync([OutfitCopies.QuietExtra(source)]); return true; }
            Checkpoint();
            await Work.WaitAsync();
            ProjectItem copy;
            try { copy = await Task.Run(() => OutfitCopies.MakeWearerCopy(Intake, (OutfitItem)source, kind)); } finally { Work.Release(); }
            Edits.AddCopy(source, copy);
            Edits.SyncTwins();
            return true;
        }
        finally { _copyInProgress = false; }
    }

    /// <summary>The text of the project file (.svfm) for the project as it is now.</summary>
    public string Save() => Edits.Save(Source.SourcePath, SourceInner, Extras);

    // The twins get their values after each undo step, as they do after each change on a screen.
    private void Checkpoint() { if (Edits.Checkpoint()) Edits.SyncTwins(); }
}
