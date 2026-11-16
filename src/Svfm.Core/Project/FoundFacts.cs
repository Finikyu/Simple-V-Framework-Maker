using Svfm.Core.Archives;
using Svfm.Core.Conversion;
using Svfm.Core.Formats;
using Svfm.Core.Knowledge;
using Svfm.Core.Mods;
using Svfm.Core.VFramework;

namespace Svfm.Core.Project;

/// <summary>How sure the tool is about a fact on the Found screen.</summary>
public enum FactStatus { Certain, Fixed, NeedsYou, LeftOut }

/// <summary>One row of the Found screen: a label, what was found, a file line and a status.</summary>
/// <summary>
/// One line of the Found screen. <paramref name="Chips"/> are the small labels beside it; each carries a
/// <see cref="ChipState"/> so the screen can show "can wear it", "can't" and "worth knowing" differently.
/// </summary>
public sealed record FoundFact(string Label, string Title, string Detail, string? Mono, FactStatus Status, IReadOnlyList<(string text, ChipState state)>? Chips = null);

/// <summary>How a chip reads: eligible, not eligible, or a note about an eligible one.</summary>
public enum ChipState { Ok, No, Note }

/// <summary>One model of the mod and the project item it becomes (null when this tool doesn't carry it).</summary>
public sealed record FoundModel(string Title, string File, string Detail, string? ItemId);

/// <summary>Builds the read-only summary the Found screen shows, from what the intake already knows.</summary>
public static class FoundFacts
{
    public static List<FoundFact> Build(GameData game, MgsvMod mod, IntakeResult res)
    {
        var facts = new List<FoundFact>();
        var cls = res.Classification;
        var supported = res.Findings.Where(f => f.Supported).ToList();
        string Stem(string p) => p[(p.LastIndexOf('/') + 1)..];

        // Kind
        if (res.Project?.Existing != null)
            facts.Add(new("Kind", "V Framework mod", "Opens for editing: every call it makes is kept as written.",
                string.Join(" · ", mod.GameFiles.Keys.Where(k => k.StartsWith("/mod/modules/", StringComparison.OrdinalIgnoreCase)).Select(Stem)), FactStatus.Certain));
        else if (supported.Count > 0)
        {
            var packs = cls.Files.Where(f => f.Kind == ModFileKind.Body && f.Path.LastIndexOf('.') > f.Path.LastIndexOf('/')).GroupBy(f => f.Path[..f.Path.LastIndexOf('.')]).Select(g => Stem(g.Key) + string.Concat(g.Select(f => f.Path[f.Path.LastIndexOf('.')..]).Order().Select((e, i) => i == 0 ? e : " · " + e))).ToList();
            facts.Add(new("Kind", supported[0].What, supported.Count > 1 ? "Also: " + string.Join("; ", supported.Skip(1).Select(s => s.What)) : "", packs.Count > 0 ? string.Join(", ", packs) : null, FactStatus.Certain));
        }
        else
            facts.Add(new("Kind", cls.Routes.FirstOrDefault() ?? "Nothing this tool can port", "", null, FactStatus.LeftOut));

        // Body details
        OutfitItem? outfit = res.Project?.Items.OfType<OutfitItem>().FirstOrDefault(i => !res.Project.Items.OfType<ExistingItem>().Any());
        var bodies = new List<BodyReplacement>();
        if (res.Project?.Existing == null)
            try { bodies = BodyConverter.Detect(game, mod); } catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or FileNotFoundException) { }
        if (bodies.Count > 0)
        {
            // Same rule as Intake: a package with no role is dropped. The icon body is the Base look (issue #59).
            var roled = bodies.Where(b => GameKnowledge.BodyRoleOf(b.VanillaPackage) != null).Select(b => (b, GameKnowledge.BodyRoleOf(b.VanillaPackage)!.Who)).ToList();
            var first = roled.Count > 0 ? BodyConverter.IconBody(roled).body : bodies[0];
            var pt = first.PartsType;
            string who = pt == null ? "" : string.Join(", ", pt.Packages.Keys.Select(Who).Distinct());
            facts.Add(new("Suit replaced", pt == null ? Stem(first.VanillaPackage) : $"{pt.Name}" + (pt.PartsType is { } n ? $" · parts type {n}" : ""),
                pt?.BuiltInHead == true ? "The game loads no separate face for this suit, so the head is part of the body: the character's head is hidden and there are no Heads worn."
                : "The character's own face is kept; the port can list heads in Heads worn.", null, FactStatus.Certain));
            if (BodyPlan.Build(roled).Values.Any(l => l.Count > 1))
                facts.Add(new("Looks", string.Join(" · ", roled.Select(x => BodyConverter.SuitName(game, x.b))), "Base: " + BodyConverter.SuitName(game, first), null, FactStatus.Certain));
            if (pt != null)
            {
                // Every character is listed, eligible or not, so the screen answers "who can wear it" at a glance
                // instead of only naming the ones that can. The owner asked for exactly this: "show off everyone, not
                // be select but show off who is eligible to wear it, and grey out who cannot."
                var chips = new List<(string, ChipState)>();
                var offered = outfit?.Spec.Branches.Keys.ToHashSet() ?? [];
                foreach (var (label, branches) in new[] { ("Snake / Avatar", new[] { PlayerBranch.Snake, PlayerBranch.Avatar }), ("DD male", [PlayerBranch.DdMale]), ("DD female", [PlayerBranch.DdFemale]), ("Ocelot", [PlayerBranch.Ocelot]), ("Quiet", [PlayerBranch.Quiet]) })
                {
                    // Issue #68: with a port, the wearers are the ones the port offers, not everyone who wears the vanilla suit.
                    bool can = offered.Count > 0 ? branches.Any(offered.Contains)
                        : branches.Any(b => pt.Packages.ContainsKey(b.ToString().ToUpperInvariant().Replace("DDMALE", "DD_MALE").Replace("DDFEMALE", "DD_FEMALE")));
                    // A shared package means the mod's one body is used by more than one character, which is worth saying
                    // but is not a problem, so it is a note rather than a warning. A port makes its own branch per wearer,
                    // so the shared vanilla package does not apply there (issue #68).
                    chips.Add((label, !can ? ChipState.No : offered.Count == 0 && label != "Snake / Avatar" && pt.Packages.Count > 1 ? ChipState.Note : ChipState.Ok));
                }
                int canWear = chips.Count(c => c.Item2 != ChipState.No);
                facts.Add(new("Wearers", "", canWear == 0 ? $"None of the characters use vanilla {pt.Name}."
                    : offered.Count > 0 ? "The port is made for: " + string.Join(", ", chips.Where(c => c.Item2 != ChipState.No).Select(c => c.Item1)) + "."
                    : pt.Packages.Count > 1
                        ? $"The mod changes one package that vanilla {pt.Name} uses for {who}, so each of them can wear it."
                        : $"Only {who} wear the vanilla suit, so only they can wear the port.", null, FactStatus.Certain, chips));
            }
            // Model
            var model = ModelFiles(mod, first.VanillaPackage);
            if (model.Count > 0)
                facts.Add(new("Model", "Body model" + (outfit != null && HasOwnHead(game, mod, first) ? " with its own face and hair" : ""), "", string.Join(" · ", model), FactStatus.Certain));
        }

        // Textures
        var loose = mod.ArchiveFiles.Keys.Where(k => k.EndsWith(".ftex", StringComparison.Ordinal) && !k.StartsWith("/Assets/tpp/ui/", StringComparison.Ordinal)).ToList();
        int blocks = mod.ArchiveFiles.Where(kv => kv.Key.EndsWith(".pftxs", StringComparison.Ordinal)).Sum(kv => { try { return TexturePack.Read(kv.Value).Textures.Count; } catch (InvalidDataException) { return 0; } });
        var notes = res.Project?.Items.SelectMany(i => i.Notes).ToList() ?? [];
        if (loose.Count > 0 || blocks > 0)
        {
            int newPaths = loose.Count(k => !InstalledTextures.IsVanillaPath(game, k));
            string title = (loose.Count > 0 ? $"{loose.Count} loose texture{(loose.Count == 1 ? "" : "s")} ({newPaths} at new paths)" : "") + (loose.Count > 0 && blocks > 0 ? " · " : "") + (blocks > 0 ? $"texture pack with {blocks} blocks" : "");
            var fix = notes.Where(n => n.Contains("Added back") || n.Contains("copied whole") || n.Contains("read from the package's own texture pack")).ToList();
            facts.Add(new("Textures", title, fix.Count > 0 ? string.Join(" ", fix) : "Copied as they are.", null, fix.Count > 0 ? FactStatus.Fixed : FactStatus.Certain));
        }
        // Face animation
        var face = notes.FirstOrDefault(n => n.Contains("face animation data"));
        if (face != null)
            facts.Add(new("Face animation", "No lip-sync data in the body", "V Framework outfits crash on deploy without it. " + face, null, FactStatus.Fixed));

        // Name, text, icon
        bool hasText = mod.ArchiveFiles.Keys.Any(k => k.Contains("/pack/ui/lang/", StringComparison.Ordinal));
        bool hasIcon = mod.ArchiveFiles.Keys.Any(k => k.Contains("/ui/texture/EquipIcon/", StringComparison.Ordinal));
        if (res.Project != null)
            facts.Add(hasText || hasIcon || res.Project.Existing != null
                ? new("Name, text, icon", (hasText ? "Text in the mod" : "No text in the mod") + (hasIcon ? ", icon in the mod" : ", no icon"), "Both can be changed on the next screen.", null, FactStatus.Certain)
                : new("Name, text, icon", "None in the mod", "Write a name and description; the icon is rendered from the model.", null, FactStatus.NeedsYou));

        // Left out
        foreach (var i in mod.Issues)
            facts.Add(new("Skipped", Stem(i.Path), i.Message, null, FactStatus.LeftOut));
        foreach (var f in res.Findings.Where(f => !f.Supported))
            facts.Add(new("Left out", f.What, f.Note ?? "", null, FactStatus.LeftOut));
        return facts;
    }

    /// <summary>The models of the project's items, with what each becomes; models this tool doesn't carry are listed too, unticked.</summary>
    public static List<FoundModel> Models(GameData game, MgsvMod mod, IntakeResult res)
    {
        var list = new List<FoundModel>();
        foreach (var item in res.Project?.Items ?? [])
        {
            string file = item.Files.Keys.FirstOrDefault(k => k.EndsWith(".fpk", StringComparison.Ordinal)) is { } p
                ? ModelFiles(item, p).FirstOrDefault() ?? p[(p.LastIndexOf('/') + 1)..] : "";
            list.Add(new FoundModel(item.Title, file, item.Kind switch
            {
                // A Quiet costume mod gives two rows, this one and "Buddy costume": the user ticks the ones to keep.
                ItemKind.Outfit when item is OutfitItem { Spec.Branches: { Count: 1 } qb } && qb.ContainsKey(PlayerBranch.Quiet) => "Playable Quiet outfit",
                ItemKind.Outfit => "Outfit body",
                ItemKind.HeadOption => "Head option",
                ItemKind.Weapon => "Weapon",
                ItemKind.BuddyOutfit => "Buddy costume",
                ItemKind.Item => "Thrown item",
                ItemKind.ExtraLooks => "Extra looks on a vanilla suit",
                _ => item.Kind.ToString(),
            }, item.Id));
        }
        foreach (var f in res.Findings.Where(f => !f.Supported))
            list.Add(new FoundModel(f.What, "", "Not a model this tool carries: left out", null));
        return list;
    }

    private static string Who(string key) => key switch
    {
        "SNAKE" => "Snake", "AVATAR" => "Avatar", "DD_MALE" => "DD male", "DD_FEMALE" => "DD female", "OCELOT" => "Ocelot", "QUIET" => "Quiet", _ => key,
    };

    private static List<string> ModelFiles(MgsvMod mod, string package) => ModelFiles(mod.ArchiveFiles, package);

    private static List<string> ModelFiles(ProjectItem item, string fpkPath) => ModelFiles(item.Files, fpkPath[..fpkPath.LastIndexOf('.')], true);

    private static List<string> ModelFiles(IReadOnlyDictionary<string, byte[]> files, string package, bool exact = false)
    {
        var names = new List<string>();
        foreach (var ext in new[] { ".fpk", ".fpkd" })
        {
            if (!files.TryGetValue(package + ext, out var data)) continue;
            try { names.AddRange(FoxPackage.Read(data).Files.Where(f => f.Extension is "fmdl" or "fclo" or "clo" or "sim").Select(f => f.Path[(f.Path.LastIndexOf('/') + 1)..])); }
            catch (InvalidDataException) { }
        }
        return names.Distinct().ToList();
    }

    private static bool HasOwnHead(GameData game, MgsvMod mod, BodyReplacement body)
    {
        try
        {
            if (!mod.ArchiveFiles.TryGetValue(body.VanillaPackage + ".fpk", out var d)) return false;
            var m = SuitRender.MainModel(FoxPackage.Read(d));
            return m != null && BodyConverter.HasOwnHead(m);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return false; }
    }
}
