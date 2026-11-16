using Svfm.Core.Formats;
using Svfm.Core.Mods;

namespace Svfm.Core.Conversion;

/// <summary>A voice package a mod ships: the .fpk V Framework's voiceFpk names, the .sdf it loads and the banks it names.</summary>
public sealed record VoicePackage(string Fpk, string SoundDataFile, IReadOnlyList<string> Banks);

/// <summary>
/// Voice packages (Raiden Voice, Yazed0071, Oct 2026): a player package whose .fpkd holds a .fox2 with a SoundPackage
/// entity (soundDataFile → a .sdf in /Assets/tpp/sound/package/), the .sdf naming the sound banks to load
/// (SoundDataFileInfo loadBanks, e.g. "vox_player_raiden" → /Assets/tpp/sound/asset/vox_player_raiden.sbp). V Framework
/// gives one to an outfit or a vanilla suit per voice type (voiceFpk, voiceType), so a voice is added without replacing
/// a vanilla sound bank.
/// </summary>
public static class VoicePackages
{
    public static List<VoicePackage> Find(MgsvMod mod)
    {
        var list = new List<VoicePackage>();
        foreach (var (path, data) in mod.ArchiveFiles.Where(kv => kv.Key.EndsWith(".fpkd", StringComparison.Ordinal)))
        {
            FoxPackage pk;
            try { pk = FoxPackage.Read(data); } catch (InvalidDataException) { continue; }
            foreach (var f in pk.Files.Where(f => f.Extension == "fox2"))
            {
                FoxDataSet ds;
                try { ds = FoxDataSet.Read(f.Data); } catch (InvalidDataException) { continue; } // a damaged .fox2 must not stop the whole mod's intake
                foreach (var (_, sdf) in ds.StringValues("SoundPackage", "soundDataFile"))
                {
                    if (string.IsNullOrEmpty(sdf)) continue;
                    var sdfFile = pk.Find(sdf)?.Data ?? mod.ArchiveFiles.GetValueOrDefault(sdf);
                    var banks = sdfFile == null ? [] : BankNames(sdfFile, mod);
                    list.Add(new VoicePackage(path[..^1], sdf, banks));
                }
            }
        }
        return list;
    }

    /// <summary>The banks a .sdf names that the mod ships as /Assets/tpp/sound/asset/&lt;name&gt;.sbp (the .sdf is a box5 data file; its strings are not delimited, so each shipped bank's name is looked for).</summary>
    private static List<string> BankNames(byte[] sdf, MgsvMod mod)
    {
        string text = System.Text.Encoding.ASCII.GetString(sdf);
        const string dir = "/Assets/tpp/sound/asset/";
        return mod.ArchiveFiles.Keys.Where(k => k.StartsWith(dir, StringComparison.Ordinal) && k.EndsWith(".sbp", StringComparison.Ordinal) && k.IndexOf('/', dir.Length) < 0)
            .Select(k => k[dir.Length..^4]).Where(n => text.Contains(n, StringComparison.Ordinal)).Distinct().ToList();
    }
}
