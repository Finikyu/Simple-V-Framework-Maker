using Svfm.Core.VFramework;
using System.Text;
using System.Text.RegularExpressions;
using Svfm.Core.Rendering;

namespace Svfm.Core.Knowledge;

/// <summary>
/// The parts that the game lets a receiver take in its swappable slots (sights, flashlight or laser, under-barrel), from
/// the game's WeaponPartsCombinationSettings.lua (RegistPartsInclusionInfo and the _Receiver…Base calls).
/// </summary>
public static partial class WeaponPartChoices
{
    public const string TablePath = "/Assets/tpp/motherbase/script/WeaponPartsCombinationSettings.lua";

    /// <summary>The SetGunBasic slot of each partsType that a port can swap.</summary>
    private static readonly Dictionary<int, string> Slots = WeaponSlots.Columns.Where(s => s.PartsType != null).ToDictionary(s => s.PartsType!.Value, s => s.Key);

    /// <summary>The empty value of each slot, as the game's gunBasic table writes it.</summary>
    public static readonly IReadOnlyDictionary<string, string> None = WeaponSlots.Columns.Where(s => s.None != null).ToDictionary(s => s.Key, s => s.None!);

    [GeneratedRegex(@"TppMotherBaseManagement\.\w+\{receiverID=\{([^}]*)\},partsType=(\d+),partsIds=\{([^}]*)\}\}")]
    private static partial Regex Call();

    /// <summary>
    /// Slot → the parts the game allows for <paramref name="receiverId"/>, and for every receiver with the same model (the
    /// grades of one gun share their lists). Only parts with a package (a model) are listed. Empty when the table is missing.
    /// </summary>
    public static Dictionary<string, List<string>> For(Func<string, byte[]?> read, string receiverId)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (read(TablePath) is not { } data) return result;
        string? Model(string rc) => GameKnowledge.ChimeraParts.TryGetValue(rc, out var cp) ? WeaponAttachments.ModelKey(cp.Parts) : null;
        string? mine = Model(receiverId);
        static string Id(string s) => s.Trim().Replace("TppEquip.", "", StringComparison.Ordinal);
        foreach (Match m in Call().Matches(Encoding.UTF8.GetString(data)))
        {
            if (!Slots.TryGetValue(int.Parse(m.Groups[2].Value), out var slot)) continue;
            var receivers = m.Groups[1].Value.Split(',').Select(Id);
            if (!receivers.Any(rc => rc == receiverId || (mine != null && Model(rc) == mine))) continue;
            var list = result.TryGetValue(slot, out var l) ? l : result[slot] = [];
            foreach (var p in m.Groups[3].Value.Split(',').Select(Id))
                if (GameKnowledge.ChimeraParts.ContainsKey(p) && !list.Contains(p)) list.Add(p);
        }
        foreach (var k in result.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) result.Remove(k);
        return result;
    }
}
