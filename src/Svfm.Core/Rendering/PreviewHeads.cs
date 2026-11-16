using Svfm.Core.Conversion;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.Core.Rendering;

/// <summary>Where the model of a Heads drop-down entry comes from.</summary>
public enum PreviewHeadKind { Mod, Vanilla, NoModel }

/// <summary>One entry of the Heads drop-down of the outfit 3D preview (issue #40).</summary>
/// <param name="Id">A mod head's item id, or a vanilla head's id.</param>
/// <param name="Title">The text in the drop-down.</param>
/// <param name="Kind">Where the model comes from.</param>
/// <param name="Mod">The mod's head option (Mod and mod NoModel entries).</param>
/// <param name="GamePackage">The game package of a vanilla head (Vanilla).</param>
/// <param name="Note">What the picture shows when there is no model; empty otherwise.</param>
/// <param name="Groups">The mesh groups of a vanilla head to draw, or null for the head rule.</param>
public sealed record PreviewHead(string Id, string Title, PreviewHeadKind Kind, HeadOptionItem? Mod, string? GamePackage, string Note, int[]? Groups = null);

/// <summary>Builds the Heads drop-down of the outfit 3D preview.</summary>
public static class PreviewHeads
{
    /// <summary>The game package that holds the model of a vanilla head, or null when the tool does not know one.</summary>
    public static string? VanillaPackage(string id, PlayerBranch who) => id switch
    {
        // The Avatar has its own faces (avm0/avm1 type 0-7) that depend on the face type the player chose;
        // the preview shows the first one, as it does for the DD soldiers (owner's decision).
        "bandana" or "infinitebandana" when who == PlayerBranch.Avatar => "/Assets/tpp/pack/player/avatar/face/plfova_avm0_type0_v00",
        // infinitebandana uses the bandana model (owner's statement, not yet checked in game).
        "bandana" or "infinitebandana" when who == PlayerBranch.Snake => "/Assets/tpp/pack/player/fova/plfova_sna0_face4_v00",
        // DD heads 552-557 all use the cm_dds5/6_head_z_eye0 face package (Soldier2FaceAndBodyData.lua, game exe).
        "balaclava" or "spheadgear" or "hpheadgear" when who is PlayerBranch.DdMale or PlayerBranch.DdFemale && SuitRender.SoldierFaces(who) is { Count: > 1 } f => f[1],
        _ => null,
    };

    /// <summary>
    /// The mesh groups to draw of a vanilla head package (issue #78), or null for the head rule (<c>WithHeadGroupsShown</c>).
    /// The three DD heads share one package (Research_Vanilla_Head_Packages.md); its groups are g0 balaclava, g1 grey cap,
    /// g2 dark grey cap and g3 green helmet (Probe --head). The Avatar face avm0_type0 has the headband in g2.
    /// </summary>
    /// <param name="id">The vanilla head id, or null for the face worn with no head option.</param>
    /// <param name="who">The wearer.</param>
    // shortcut: which groups the game shows per DD head is not confirmed in game (Research section 6), update after the owner's test.
    public static int[]? VanillaGroups(string? id, PlayerBranch who) => (id, who) switch
    {
        (null, PlayerBranch.Avatar) => [0, 1, 3, 4, 5, 6],
        ("balaclava", PlayerBranch.DdMale or PlayerBranch.DdFemale) => [0],
        ("spheadgear", PlayerBranch.DdMale or PlayerBranch.DdFemale) => [0, 1],
        ("hpheadgear", PlayerBranch.DdMale or PlayerBranch.DdFemale) => [0, 3],
        _ => null,
    };

    /// <summary>The entries for one wearer: each ticked head of the mod that has a branch for the wearer, then each ticked vanilla head.</summary>
    public static List<PreviewHead> For(IEnumerable<HeadOptionItem> modHeads, OutfitBranch branch, PlayerBranch who)
    {
        var list = new List<PreviewHead>();
        var ticked = branch.Base.HeadOptions.Concat(branch.HeadOptions).ToHashSet(StringComparer.Ordinal);
        var heads = modHeads.ToList();
        foreach (var h in heads)
        {
            if (!ticked.Contains(h.Spec.Key) || !h.Spec.Branches.TryGetValue(who, out var hb)) continue;
            string title = string.IsNullOrEmpty(h.Title) ? h.Spec.Key : h.Title;
            if (hb.Fpk is { Length: > 0 }) { list.Add(new(h.Id, title, PreviewHeadKind.Mod, h, null, "")); continue; }
            string note = hb.EnemyFaceId is { Length: > 0 } face
                ? $"This head is a soldier face the game already has ({face}), so the preview shows the default soldier face."
                : "This head has no model of its own, so the preview shows the outfit's own head.";
            list.Add(new(h.Id, title, PreviewHeadKind.NoModel, h, null, note));
        }
        var mods = heads.Select(h => h.Spec.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var id in branch.Base.HeadOptions.Concat(branch.HeadOptions).Distinct())
        {
            if (mods.Contains(id)) continue;
            string title = id switch
            {
                "bandana" => "Bandana", "infinitebandana" => "Infinity Bandana", "balaclava" => "Balaclava",
                "spheadgear" => "Soldier headgear (SP)", "hpheadgear" => "Soldier headgear (HP)", _ => id,
            };
            var package = VanillaPackage(id, who);
            list.Add(package != null
                ? new(id, title, PreviewHeadKind.Vanilla, null, package, "", VanillaGroups(id, who))
                : new(id, title, PreviewHeadKind.NoModel, null, null, "The preview has no model for this game head, so it shows the default face."));
        }
        return list;
    }
}
