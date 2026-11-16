using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Archives;
using Svfm.Core.Mods;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>Who a copy of a body port is for.</summary>
public enum CopyKind { Ocelot, Quiet, Snake }

/// <summary>One copy tick box of an outfit: available when <see cref="Reason"/> is null, else greyed out with that reason.</summary>
public sealed record CopyOption(CopyKind Kind, string? Reason)
{
    public bool Available => Reason == null;
}

/// <summary>
/// Copies of a body port for other wearers (the owner, Nov 15, grill Q12–Q20; in-game tests in
/// .docs/Research_Playable_Ocelot.md). Only a full body replacement gets them:
/// a Snake-slot body with the head in its model gets an Ocelot copy and a Quiet copy (playable and buddy twins), the
/// the Quiet copy with the body's cloth and sim parts;
/// a Snake-slot body with a separate head gets the Ocelot copy only, and only when the mod ships a face model;
/// an Ocelot-slot body gets a Snake and Avatar copy with the head hidden (its skeleton does not fit Quiet).
/// The Ocelot and Snake copies are ordinary copies (Duplicate) with other wearers. The Quiet copy grafts the body's
/// model into Quiet's game costume and is merged as an extra mod (<see cref="QuietSource"/>), so that a project file
/// opens it again.
/// </summary>
public static class OutfitCopies
{
    /// <summary>The <see cref="ExtraMod.Path"/> of a Quiet copy; its <see cref="ExtraMod.Inner"/> is the source outfit's ID.</summary>
    public const string QuietSource = "copy:quiet";
    /// <summary>Quiet's default game costume: the costume the in-game test grafted into.</summary>
    public const string QuietCostume = QuietPlayableConverter.BuddyFolder + "buddy_quiet2_01";

    /// <summary>The copy tick boxes of an item: none when it is not a body port.</summary>
    public static IReadOnlyList<CopyOption> Options(IntakeResult res, ProjectItem item)
    {
        if (item is not OutfitItem || !res.BodySources.TryGetValue(item.Id, out var src)) return [];
        bool ocelotSlot = src.Bodies.All(b => b.Who == PlayerBranch.Ocelot);
        bool snakeSlot = src.Bodies.Any(b => b.Who is PlayerBranch.Snake or PlayerBranch.Avatar);
        string? slot = ocelotSlot || snakeSlot ? null : "Only for a body that replaces a suit of Snake or Ocelot.";
        string? quiet = slot ?? (ocelotSlot ? "Ocelot's skeleton does not fit Quiet (in-game test)."
            : !src.HeadInBody ? "The body uses a separate head; on Quiet the body did not show (in-game test)."
            : res.Game?.TryRead(QuietCostume + ".fpk") == null ? "Needs the game folder (Settings): the copy is made from Quiet's game costume."
            : null);
        // A Snake-slot body with a separate head: Ocelot loads no separate head or arm, so the mod must ship them (in-game test, Nov 15).
        string? ocelot = slot ?? (ocelotSlot ? "It is an Ocelot outfit already."
            : !src.HeadInBody && !ShipsFace(src) ? "The body uses Snake's separate head and arm; on Ocelot they did not show (in-game test)."
            : null);
        return
        [
            new(CopyKind.Ocelot, ocelot),
            new(CopyKind.Quiet, quiet),
            new(CopyKind.Snake, slot ?? (ocelotSlot ? null : "It is a Snake outfit already.")),
        ];
    }

    /// <summary>The name a copy starts with, for example "MGS1 SNAKE (Ocelot)".</summary>
    public static string Suffix(CopyKind kind) => kind switch { CopyKind.Ocelot => " (Ocelot)", CopyKind.Quiet => " (Quiet)", _ => " (Snake)" };

    /// <summary>The wearers of an Ocelot or Snake copy.</summary>
    public static PlayerBranch[] Wearers(CopyKind kind) => kind == CopyKind.Ocelot ? [PlayerBranch.Ocelot] : [PlayerBranch.Snake, PlayerBranch.Avatar];

    /// <summary>True when an outfit is worn by exactly the wearers of that copy kind (how a copy is found again).</summary>
    public static bool IsWornBy(ProjectItem i, CopyKind kind) =>
        i is OutfitItem o && o.Spec.Branches.Keys.Order().SequenceEqual(Wearers(kind).Order());

    /// <summary>The ExtraMod of the Quiet copy of an outfit.</summary>
    public static ExtraMod QuietExtra(ProjectItem source) => new(QuietSource, source.Id);

    /// <summary>
    /// Makes the Ocelot or Snake copy: a copy of the outfit (its own files) worn by the new wearers only. The copy takes
    /// the copy's own branch of the old wearer (Snake's, else the first); a Snake copy hides the head (Q18).
    /// </summary>
    public static OutfitItem MakeWearerCopy(IntakeResult res, OutfitItem source, CopyKind kind, string? wantedId = null)
    {
        if (kind == CopyKind.Quiet) throw new ArgumentException("The Quiet copy is merged: use OpenQuiet.");
        var copy = (OutfitItem)res.Duplicate(source, wantedId);
        var b = copy.Spec.Branches.TryGetValue(PlayerBranch.Snake, out var snake) ? snake : copy.Spec.Branches.Values.First();
        if (kind == CopyKind.Snake) b = b with { Base = b.Base with { EnableHead = false } };
        copy.Spec.Branches.Clear();
        foreach (var w in Wearers(kind)) copy.Spec.Branches[w] = b;
        Rename(copy, Strip(copy.Title) + Suffix(kind), EngName(copy) is { } eng ? Strip(eng) + Suffix(kind) : null);
        return copy;
    }

    /// <summary>
    /// The Quiet copy of a body port: the body's model in Quiet's game costume, as a buddy costume and its playable
    /// twin (Sync on). The result is merged into the host project.
    /// </summary>
    /// <param name="game">The files of the game.</param>
    /// <param name="host">The open project's intake, which holds the source outfit.</param>
    /// <param name="sourceId">The ID of the source outfit.</param>
    public static IntakeResult OpenQuiet(GameData game, IntakeResult host, string sourceId)
    {
        var item = host.Project?.Items.FirstOrDefault(i => i.Id == sourceId) ?? throw new ArgumentException($"The outfit {sourceId} is not in the project.");
        if (!host.BodySources.TryGetValue(sourceId, out var src)) throw new ArgumentException($"\"{item.Title}\" is not a body port.");
        var body = src.Bodies.First(b => b.Who is PlayerBranch.Snake or PlayerBranch.Avatar).Body;
        var bodyFpk = BodyConverter.Merge(game, src.Mod, body.VanillaPackage + ".fpk", isFpkd: false, []);
        var fmdl = bodyFpk.Files.FirstOrDefault(f => f.Extension == "fmdl" && f.Path.Contains("/chara/", StringComparison.Ordinal))
            ?? throw new InvalidDataException($"{body.VanillaPackage}.fpk holds no character model.");
        var pkg = FoxPackage.Read(game.TryRead(QuietCostume + ".fpk") ?? throw new FileNotFoundException("Quiet's game costume (buddy_quiet2_01) was not found: set the game folder."));
        var quiModel = pkg.Files.FirstOrDefault(f => f.Extension == "fmdl" && f.Path.Contains("/chara/qui/", StringComparison.Ordinal))
            ?? throw new InvalidDataException("Quiet's game costume holds no model.");
        quiModel.Data = fmdl.Data;
        // The model's helper-bone data goes with it: Quiet's own .frdv bent the legs of a Snake body (in-game test, Nov 15).
        if (bodyFpk.Find(Path.ChangeExtension(fmdl.Path, ".frdv")) is { } frdv && pkg.Find(Path.ChangeExtension(quiModel.Path, ".frdv")) is { } quiFrdv)
            quiFrdv.Data = frdv.Data;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (GraftClothAndSims(game, src, body.VanillaPackage, bodyFpk, fmdl.Path, pkg) is { } quiFpkd) files[QuietCostume + ".fpkd"] = quiFpkd.Write();
        files[QuietCostume + ".fpk"] = pkg.Write();
        // Textures: the body's texture pack, or else the mod's loose textures (both as the in-game test had them).
        if (src.Mod.ArchiveFiles.TryGetValue(body.VanillaPackage + ".pftxs", out var pack)) files[QuietCostume + ".pftxs"] = pack;
        else
            foreach (var (path, data) in src.Mod.ArchiveFiles)
                if (src.Mod.Ships(path) && (path.EndsWith(".ftex", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ftexs", StringComparison.OrdinalIgnoreCase)))
                    files[path] = data;
        // The ID comes from the ID of the source, not from its title: the user can change the title, and the project file
        // opens the copy again before it puts the title back. Unique IDs always: two sources of one mod start with the same letters.
        var res = Intake.Open(game, MgsvMod.Of("Quiet " + sourceId, src.Mod.Metadata.Author, files), uniqueIds: true);
        if (res.Project == null) return res;
        res.Project.Items.RemoveAll(i => i.TwinId == null);
        if (res.Project.Items.Count == 0) { res.Project = null; res.Findings.Add(new IntakeFinding("Quiet copy of " + item.Title, false, "The costume could not be made.")); return res; }
        string? eng = EngName(item);
        foreach (var i in res.Project.Items) Rename(i, item.Title + Suffix(CopyKind.Quiet), eng != null ? eng + Suffix(CopyKind.Quiet) : null);
        return res;
    }

    /// <summary>
    /// The cloth and sim parts of the body (Parasite Ninja's cape: a ClothDescription with its .fclo/.clo, and
    /// SimDescriptions) copied into Quiet's .parts, and the files they name into Quiet's packages (each into the kind of
    /// package the body has it in). Without them the cape did not move and the arms bent on Quiet (in-game test, Nov 15).
    /// Returns Quiet's changed .fpkd, or null when the body has none of these parts.
    /// </summary>
    private static FoxPackage? GraftClothAndSims(GameData game, BodySource src, string bodyPackage, FoxPackage bodyFpk, string modelPath, FoxPackage quiFpk)
    {
        var bodyFpkd = BodyConverter.Merge(game, src.Mod, bodyPackage + ".fpkd", isFpkd: true, []);
        var bodyParts = bodyFpkd.Files.Where(f => f.Extension == "parts").Select(f => FoxDataSet.Read(f.Data))
            .FirstOrDefault(p => p.ModelFiles.Contains(modelPath));
        var copy = bodyParts?.Entities.Where(e => bodyParts.Name(e.ClassHash) is "ClothDescription" or "SimDescription").ToList();
        if (bodyParts == null || copy is not { Count: > 0 }) return null;
        var fpkd = FoxPackage.Read(game.TryRead(QuietCostume + ".fpkd") ?? throw new FileNotFoundException("Quiet's game costume (buddy_quiet2_01.fpkd) was not found: set the game folder."));
        var partsFile = fpkd.Files.First(f => f.Extension == "parts");
        var parts = FoxDataSet.Read(partsFile.Data);
        int k = 0;
        foreach (var e in copy)
        {
            // Names from 0100 up: Quiet's own SimDescription0001/0002 stay as they are.
            parts = parts.WithEntityCopy(bodyParts, e, $"{bodyParts.Name(e.ClassHash)}{100 + k++:D4}", partsFile.Path);
            foreach (var p in e.Properties.Where(p => p.Type == 20))
                if (bodyParts.Name(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bodyParts.Data.AsSpan(p.Offset + p.ValueOffset))) is { Length: > 0 } path)
                    foreach (var (from, to) in new[] { (bodyFpk, quiFpk), (bodyFpkd, fpkd) })
                        if (from.Find(path) is { } f && to.Find(path) == null) to.Files.Add(f);
        }
        partsFile.Data = parts.Data;
        return fpkd;
    }

    /// <summary>True when the mod ships a body package of its own that holds a face model (MGS3 Major Ocelot: sna0_face0_cov).</summary>
    private static bool ShipsFace(BodySource src) => src.Bodies.Any(b =>
        src.Mod.ArchiveFiles.TryGetValue(b.Body.VanillaPackage + ".fpk", out var data)
        && FoxPackage.Read(data).Files.Any(f => f.Extension == "fmdl" && f.Path.Contains("/chara/", StringComparison.Ordinal) && f.Path.Contains("_face", StringComparison.Ordinal)));

    private static string Strip(string t) => t.EndsWith(" (copy)", StringComparison.Ordinal) ? t[..^" (copy)".Length] : t;
    private static string? EngName(ProjectItem i) => i.NameLangId is { } id && i.Text.TryGetValue(id, out var per) && per.TryGetValue("eng", out var t) ? t : null;
    private static void Rename(ProjectItem i, string title, string? eng)
    {
        i.Title = title;
        if (eng != null && i.NameLangId is { } id) i.SetText(id, "eng", eng);
    }
}
