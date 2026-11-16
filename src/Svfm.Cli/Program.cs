using System.Security.Cryptography;
using Svfm.Core.Archives;
using Svfm.Core.Formats;
using Svfm.Core.Hashing;
using Svfm.Core.Mods;

namespace Svfm.Cli;

public static class Program
{
    private const string Usage = """
        svfm - Simple V Framework Maker command line

          hash <text | /Assets/path.ext> ...           StrCode32 for names / lang IDs, path code for paths
          qar-ls <archive.dat> [--dict file]...         list an archive (path code, size, name)
          qar-x  <archive.dat> <out_dir> [--dict file]... [--only text]
                                                        extract files, named as GzsTool 0.5.3 names them
          qar-cat <archive.dat> <game path> <out_file>  extract one file by its game path
          fpk-ls <file.fpk|.fpkd>                       list a package (and the text in any .lng2 inside)
          fpk-x  <file.fpk|.fpkd> <out_dir>             extract a package
          pftxs-ls <pack.pftxs> [--png out_dir]         list a texture pack; --png saves each texture's largest mip
          ftex-png <texture.ftex> <out.png>             decode a texture (uses the .N.ftexs files next to it)
          mod-ls <mod.mgsv|.zip>                        show a mod's metadata, files and install problems
          mod-sort <mod.mgsv|.zip> [game_folder ...]    sort a mod's files by kind, say which replace vanilla files, and name the port routes
          icon-camo <camo texture .ftex|.pftxs> <vanilla icon .ftex> <out_base> [--x X --y Y --contrast C --brightness B]
                                                        fatigues icon from a camo texture (writes .ftex/.ftexs and a PNG preview)
          convert-camo <mod.mgsv|.zip> <out.mgsv> --game <MGS_TPP folder> --id ID --name NAME --desc TEXT
                 [--key KEY] [--preset ID] [--looks normal,scarf,naked] [--icon render|crop|crop:X,Y|vanilla|recolour:H0-H1] [--dict extra_paths.txt]
                                                        turn a Snake camo replacer into its own V Framework 1.7 outfit
          presets                                       list the "behaves like" presets
          fmdl-info <model.fmdl> [--dict file]          bones, mesh groups, materials and their textures
          mgsv-pack <folder> <out.mgsv> --name N [--version V --author A --website W --desc-file F]
                                                        build a mod from a folder with Assets/ and GameDir/
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine(Usage); return 0; }
        try
        {
            return args[0] switch
            {
                "hash" => Hash(args[1..]),
                "qar-ls" => QarList(args[1..]),
                "qar-x" => QarExtract(args[1..]),
                "qar-cat" => QarCat(args[1..]),
                "fpk-ls" => FpkList(args[1..]),
                "fpk-x" => FpkExtract(args[1..]),
                "pftxs-ls" => PackList(args[1..]),
                "ftex-png" => FtexPng(args[1..]),
                "mod-ls" => ModList(args[1..]),
                "mod-sort" => ModSort(args[1..]),
                "mgsv-pack" => MgsvPack(args[1..]),
                "icon-camo" => IconCamo(args[1..]),
                "convert-camo" => ConvertCamo(args[1..]),
                "presets" => ListPresets(),
                "fmdl-info" => FmdlInfo(args[1..]),
                _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
        // A missing or malformed argument (args[i] past the end, int.Parse on text) gets a clear message, not a stack trace.
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException or OverflowException or ArgumentException)
        {
            return Fail($"bad or missing argument for '{args[0]}': {ex.Message}\n\n{Usage}");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Hash(string[] a)
    {
        foreach (var t in a)
            Console.WriteLine(t.Contains('/') ? $"{t}\t{FoxHash.PathCode64(t)}\t0x{FoxHash.PathCode64(t):x}" : $"{t}\t{FoxHash.StrCode32(t)}");
        return 0;
    }

    private static (List<string> positional, PathDictionary dict, string? only) ParseArchiveArgs(string[] a)
    {
        var pos = new List<string>();
        var dict = PathDictionary.Bundled.Copy();
        string? only = null;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--dict") dict.LoadFile(a[++i]);
            else if (a[i] == "--only") only = a[++i];
            else pos.Add(a[i]);
        }
        return (pos, dict, only);
    }

    private static int QarList(string[] a)
    {
        var (pos, dict, _) = ParseArchiveArgs(a);
        using var qar = QarArchive.Open(pos[0]);
        Console.WriteLine($"# flags 0x{qar.Flags:x} version {qar.Version} entries {qar.Entries.Count}");
        foreach (var e in qar.Entries)
            Console.WriteLine($"{e.Hash:x16}\t{e.UncompressedSize}\t{(e.Compressed ? "z" : "-")}\t{dict.DisplayName(e.Hash)}");
        return 0;
    }

    private static int QarExtract(string[] a)
    {
        var (pos, dict, only) = ParseArchiveArgs(a);
        using var qar = QarArchive.Open(pos[0]);
        string outDir = pos[1];
        int n = 0;
        foreach (var e in qar.Entries)
        {
            string name = dict.DisplayName(e.Hash).TrimStart('/');
            if (only != null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] data = qar.Read(e);
            File.WriteAllBytes(target, data);
            Console.WriteLine($"{Convert.ToHexString(MD5.HashData(data))}\t{data.Length}\t{name}");
            n++;
        }
        Console.Error.WriteLine($"{n} files");
        return 0;
    }

    private static int QarCat(string[] a)
    {
        using var qar = QarArchive.Open(a[0]);
        File.WriteAllBytes(a[2], qar.Read(a[1]));
        return 0;
    }

    private static int FpkList(string[] a)
    {
        var pkg = FoxPackage.Read(a[0]);
        foreach (var r in pkg.References) Console.WriteLine($"ref\t{System.Text.Encoding.Latin1.GetString(r)}");
        foreach (var f in pkg.Files)
        {
            Console.WriteLine($"{f.Data.Length}\t{f.Path}{(f.NameObfuscated ? "\t(name obfuscated)" : "")}");
            if (f.Extension == "lng2")
                foreach (var e in LangFile.Read(f.Data).Entries) Console.WriteLine($"    {e.Key,10}  {e.Text}");
        }
        return 0;
    }

    private static int FpkExtract(string[] a)
    {
        var pkg = FoxPackage.Read(a[0]);
        string root = Path.GetFullPath(a[1]) + Path.DirectorySeparatorChar;
        foreach (var f in pkg.Files)
        {
            string target = Path.GetFullPath(Path.Combine(a[1], f.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            // File names come from the package: one with ".." would land outside the output folder.
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine($"skipped (outside the folder): {f.Path}"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, f.Data);
            Console.WriteLine(target);
        }
        return 0;
    }

    private static int PackList(string[] a)
    {
        var pack = TexturePack.Read(File.ReadAllBytes(a[0]));
        string? png = a.Length > 2 && a[1] == "--png" ? a[2] : null;
        for (int i = 0; i < pack.Textures.Count; i++)
        {
            var t = pack.Textures[i];
            var h = t.Header;
            Console.WriteLine($"{i}\t{t.PathCode:x16}\t{h.Width}x{h.Height}\tformat {h.PixelFormat}\tparts {string.Join(",", t.Parts.Select(p => p.FileNumber?.ToString() ?? "?"))}");
            if (png != null)
            {
                Directory.CreateDirectory(png);
                var img = t.Decode();
                File.WriteAllBytes(Path.Combine(png, $"{i}_{t.PathCode:x16}.png"), Png.Encode(img));
            }
        }
        return 0;
    }

    private static int FtexPng(string[] a)
    {
        var tex = FtexTexture.Read(File.ReadAllBytes(a[0]));
        var files = new Dictionary<int, byte[]>();
        for (int k = 1; k <= 6; k++)
        {
            string p = a[0][..^5] + $".{k}.ftexs";
            if (File.Exists(p)) files[k] = File.ReadAllBytes(p);
        }
        var img = tex.DecodeLargestAvailable(files);
        File.WriteAllBytes(a[1], Png.Encode(img));
        Console.WriteLine($"{img.Width}x{img.Height} format {tex.PixelFormat}");
        return 0;
    }

    private static int ModList(string[] a)
    {
        MgsvMod mod;
        try { mod = MgsvMod.Open(a[0], a.Length > 1 ? a[1] : null); }
        catch (AmbiguousModException ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }
        var m = mod.Metadata;
        Console.WriteLine($"{m.Name} {m.Version} by {m.Author}{(mod.InnerName != null ? $"  [{mod.InnerName}]" : "")}");
        Console.WriteLine($"MGSV {m.MgsVersion}, SnakeBite {m.SbVersion}");
        foreach (var f in mod.ArchiveFiles) Console.WriteLine($"archive\t{f.Value.Length}\t{f.Key}");
        foreach (var f in mod.GameFiles) Console.WriteLine($"game\t{f.Value.Length}\t{f.Key}");
        foreach (var i in mod.Issues) Console.WriteLine($"NOTE\t{i.Path}: {i.Message}");
        return 0;
    }

    private static int ModSort(string[] a)
    {
        var mod = MgsvMod.Open(a[0]);
        using var game = new Svfm.Core.Archives.GameData();
        foreach (var folder in a.Skip(1)) game.AddFolder(folder);
        var c = Svfm.Core.Conversion.ModClassifier.Classify(a.Length > 1 ? game : null, mod);
        Console.WriteLine($"{mod.Metadata.Name} {mod.Metadata.Version} by {mod.Metadata.Author}");
        foreach (var g in c.Files.GroupBy(f => f.Kind))
        {
            Console.WriteLine($"{g.Key}:");
            foreach (var f in g) Console.WriteLine($"  {(a.Length > 1 ? (f.ReplacesVanilla ? "replaces" : "new     ") : "")}  {f.Path}");
        }
        foreach (var r in c.Routes) Console.WriteLine($"ROUTE\t{r}");
        return 0;
    }

    private static int MgsvPack(string[] a)
    {
        string folder = a[0], output = a[1];
        var b = new MgsvBuilder();
        for (int i = 2; i + 1 < a.Length; i += 2)
        {
            switch (a[i])
            {
                case "--name": b.Name = a[i + 1]; break;
                case "--version": b.Version = a[i + 1]; break;
                case "--author": b.Author = a[i + 1]; break;
                case "--website": b.Website = a[i + 1]; break;
                case "--desc-file": b.Description = File.ReadAllText(a[i + 1]); break;
                default: return Fail($"unknown option {a[i]}");
            }
        }
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string rel = "/" + Path.GetRelativePath(folder, f).Replace('\\', '/');
            if (rel.StartsWith("/Assets/", StringComparison.Ordinal)) b.AddArchiveFile(rel, File.ReadAllBytes(f));
            else if (rel.StartsWith("/GameDir/", StringComparison.Ordinal)) b.AddGameFile(rel["/GameDir".Length..], File.ReadAllBytes(f));
            else if (rel is not ("/metadata.xml" or "/readme.txt")) Console.Error.WriteLine($"skipped (not under Assets/ or GameDir/): {rel}");
        }
        foreach (var issue in b.Check()) Console.Error.WriteLine($"WARNING {issue.Path}: {issue.Message}");
        b.Save(output);
        Console.WriteLine($"{output}: {b.ArchiveFiles.Count} archive files, {b.GameFiles.Count} game files");
        return 0;
    }

    private static RgbaImage LoadTexture(string path)
    {
        if (path.EndsWith(".pftxs", StringComparison.OrdinalIgnoreCase))
            return TexturePack.Read(File.ReadAllBytes(path)).Textures[0].Decode();
        var files = new Dictionary<int, byte[]>();
        for (int k = 1; k <= 6; k++)
        {
            string p = path[..^5] + $".{k}.ftexs";
            if (File.Exists(p)) files[k] = File.ReadAllBytes(p);
        }
        return FtexTexture.Read(File.ReadAllBytes(path)).DecodeLargestAvailable(files);
    }

    private static int IconCamo(string[] a)
    {
        var texture = LoadTexture(a[0]);
        var mask = Svfm.Core.Conversion.SuitIcon.MaskFrom(LoadTexture(a[1]));
        string outBase = a[2];
        var o = Svfm.Core.Conversion.SuitIcon.DefaultCropOrigin;
        float contrast = Svfm.Core.Conversion.SuitIcon.DefaultContrast, brightness = 1f;
        for (int i = 3; i + 1 < a.Length; i += 2)
        {
            var v = a[i + 1];
            switch (a[i])
            {
                case "--x": o.x = int.Parse(v); break;
                case "--y": o.y = int.Parse(v); break;
                case "--contrast": contrast = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                case "--brightness": brightness = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                default: return Fail($"unknown option {a[i]}");
            }
        }
        var icon = Svfm.Core.Conversion.SuitIcon.FromCamoTexture(texture, mask, o, contrast, brightness);
        var files = Svfm.Core.Conversion.SuitIcon.ToGameFiles(icon);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outBase))!);
        File.WriteAllBytes(outBase + ".ftex", files.Ftex);
        foreach (var (n, d) in files.Ftexs) File.WriteAllBytes($"{outBase}.{n}.ftexs", d);
        File.WriteAllBytes(outBase + "_preview.png", Png.Encode(icon));
        Console.WriteLine($"{outBase}.ftex (+{files.Ftexs.Count} .ftexs), preview {outBase}_preview.png");
        return 0;
    }

    private static int ListPresets()
    {
        foreach (var p in Svfm.Core.Knowledge.GameKnowledge.Presets) Console.WriteLine($"{p.Id,-22} {p.Label}");
        return 0;
    }

    private static int ConvertCamo(string[] a)
    {
        string input = a[0], output = a[1];
        var opt = new Dictionary<string, string>();
        for (int i = 2; i + 1 < a.Length; i += 2) opt[a[i].TrimStart('-')] = a[i + 1];
        foreach (var need in new[] { "game", "id", "name", "desc" })
            if (!opt.ContainsKey(need)) return Fail($"--{need} is required");

        using var game = Svfm.Core.Archives.GameData.OpenGameFolder(opt["game"]);
        if (game.Sources.Count == 0) return Fail($"no game archives found under {opt["game"]}\\master");
        var mod = MgsvMod.Open(input);
        var found = Svfm.Core.Conversion.CamoConverter.Detect(game, mod);
        if (found.Count == 0) return Fail("this mod does not replace a Snake fatigues camo");
        if (found.Count > 1) Console.Error.WriteLine($"replaces several camos ({string.Join(", ", found.Select(f => f.camo.Name))}); converting the first");
        var (camo, fatigues, naked) = found[0];
        Console.WriteLine($"replaces camo {camo.FovaCamoId} {camo.Name} ({camo.Description}): fatigues {(fatigues ? "yes" : "no")}, naked {(naked ? "yes" : "no")}");

        var looks = Svfm.Core.Conversion.FatiguesLooks.All;
        if (opt.TryGetValue("looks", out var ls))
        {
            looks = 0;
            foreach (var l in ls.Split(','))
                looks |= l.Trim().ToLowerInvariant() switch
                {
                    "normal" => Svfm.Core.Conversion.FatiguesLooks.Normal,
                    "scarf" => Svfm.Core.Conversion.FatiguesLooks.Scarf,
                    "naked" => Svfm.Core.Conversion.FatiguesLooks.Naked,
                    _ => throw new InvalidDataException($"unknown look {l}"),
                };
        }
        Svfm.Core.Conversion.IconChoice? icon = null; // default: made from the mod's own assets
        if (opt.TryGetValue("icon", out var ic))
        {
            if (ic == "vanilla") icon = new Svfm.Core.Conversion.IconChoice.Vanilla();
            else if (ic == "render") icon = new Svfm.Core.Conversion.IconChoice.Render();
            else if (ic == "render-noscarf") icon = new Svfm.Core.Conversion.IconChoice.Render(WithScarf: false);
            else if (ic.StartsWith("recolour:"))
            {
                var h = ic["recolour:".Length..].Split('-');
                icon = new Svfm.Core.Conversion.IconChoice.RecolourVanilla(float.Parse(h[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(h[1], System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (ic.StartsWith("crop:"))
            {
                var xy = ic["crop:".Length..].Split(',');
                icon = new Svfm.Core.Conversion.IconChoice.CamoCrop(int.Parse(xy[0]), int.Parse(xy[1]));
            }
            else if (ic == "crop")
            {
                var o = camo.IconTextureOrigin;
                var d = Svfm.Core.Conversion.SuitIcon.DefaultCropOrigin;
                icon = new Svfm.Core.Conversion.IconChoice.CamoCrop(o?[0] ?? d.x, o?[1] ?? d.y);
            }
        }
        var options = new Svfm.Core.Conversion.CamoConversionOptions
        {
            Id = opt["id"],
            Key = opt.GetValueOrDefault("key") ?? $"SVFM:{opt["id"]}",
            Name = opt["name"],
            RealName = opt.GetValueOrDefault("real"),
            Description = opt["desc"].Replace("\\n", "\n"),
            Preset = opt.GetValueOrDefault("preset") ?? "fatigues",
            Looks = looks,
            Icon = icon,
            Dictionary = opt.TryGetValue("dict", out var dictFile) ? WithFile(dictFile) : null,
            ModVersion = opt.GetValueOrDefault("version") ?? "1.0",
            ModAuthor = opt.GetValueOrDefault("author") ?? "",
        };
        var r = Svfm.Core.Conversion.CamoConverter.Convert(game, mod, camo, options);
        r.Mod.Save(output);
        string baseOut = Path.ChangeExtension(output, null);
        File.WriteAllText(baseOut + ".lua.txt", r.Lua);
        if (r.Icon != null) File.WriteAllBytes(baseOut + "_icon.png", Png.Encode(r.Icon));
        if (r.OldIcon != null) File.WriteAllBytes(baseOut + "_icon_old_backup.png", Png.Encode(r.OldIcon));
        foreach (var n in r.Notes) Console.WriteLine("NOTE " + n);
        Console.WriteLine($"{output}: {r.Mod.ArchiveFiles.Count} archive files, module {r.Mod.GameFiles.Keys.Single()}");
        return 0;
    }

    private static int FmdlInfo(string[] a)
    {
        var (pos, dict, _) = ParseArchiveArgs(a);
        var m = FmdlModel.Read(File.ReadAllBytes(pos[0]));
        string N(string? name, ulong hash) => name ?? Svfm.Core.Rendering.FoxNames.Lookup(hash) ?? $"#{hash:x12}";
        Console.WriteLine($"bones {m.Bones.Count}");
        foreach (var b in m.Bones) Console.WriteLine($"  {b.Index,3} {N(b.Name, b.NameHash),-28} parent {b.Parent,3}  {b.World.X,7:0.000} {b.World.Y,7:0.000} {b.World.Z,7:0.000}");
        Console.WriteLine($"mesh groups {m.MeshGroups.Count}");
        for (int i = 0; i < m.MeshGroups.Count; i++) Console.WriteLine($"  {i,3} {N(m.MeshGroups[i].name, m.MeshGroups[i].hash)}{(m.MeshGroups[i].hidden ? " (hidden)" : "")}");
        Console.WriteLine($"materials {m.Materials.Count}");
        foreach (var mat in m.Materials)
        {
            Console.WriteLine($"  {mat.Index,3} {N(mat.Name, mat.NameHash)}");
            foreach (var t in mat.Textures) Console.WriteLine($"        {N(t.Role, t.RoleHash),-24} {dict.TryGetPath(t.PathCode) ?? t.FileName ?? $"{t.PathCode:x16}"}");
        }
        Console.WriteLine($"meshes {m.Meshes.Count}");
        foreach (var me in m.Meshes) Console.WriteLine($"  {me.Index,3} material {me.MaterialIndex,3} group {me.MeshGroup,3} verts {me.Positions.Length,6} tris {me.Indices.Length / 3,6} uv {(me.Uv != null ? "y" : "n")} nrm {(me.Normals != null ? "y" : "n")}");
        return 0;
    }

    /// <summary>The bundled dictionary plus the paths in an extra file.</summary>
    private static Svfm.Core.Archives.PathDictionary WithFile(string file)
    {
        var d = Svfm.Core.Archives.PathDictionary.Bundled.Copy();
        d.LoadFile(file);
        return d;
    }
}
