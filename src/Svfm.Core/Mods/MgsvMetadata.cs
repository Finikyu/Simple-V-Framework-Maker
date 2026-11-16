using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Svfm.Core.Mods;

public sealed record QarEntryInfo(ulong Hash, string FilePath, bool Compressed, string ContentHash);
public sealed record FpkEntryInfo(string FpkFile, string FilePath, string ContentHash);
public sealed record GameFileInfo(string FilePath, string ContentHash);

/// <summary>
/// metadata.xml of a SnakeBite/DMM mod (.mgsv). Only files listed here are installed.
/// QarEntries are files for the archives (/Assets/...), FpkEntries list every file inside the
/// mod's packages, FileEntries are game-folder files (zip folder GameDir/).
/// </summary>
public sealed class MgsvMetadata
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Website { get; set; } = "";
    public string MgsVersion { get; set; } = "1.0.15.4";
    public string SbVersion { get; set; } = "0.9.2.5";
    public string Description { get; set; } = "";
    public List<QarEntryInfo> QarEntries { get; } = [];
    public List<FpkEntryInfo> FpkEntries { get; } = [];
    public List<GameFileInfo> FileEntries { get; } = [];

    public static MgsvMetadata Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException e) { throw new InvalidDataException("metadata.xml is not valid XML: " + e.Message, e); } // the open-mod catch lists hold InvalidData, not XmlException
        var root = doc.Root ?? throw new InvalidDataException("metadata.xml is empty.");
        string A(XElement? e, string name) => (string?)e?.Attribute(name) ?? "";
        var m = new MgsvMetadata
        {
            Name = A(root, "Name"),
            Version = A(root, "Version"),
            Author = A(root, "Author"),
            Website = A(root, "Website"),
            MgsVersion = A(root.Element("MGSVersion"), "Version"),
            SbVersion = A(root.Element("SBVersion"), "Version"),
            Description = (string?)root.Element("Description") ?? "",
        };
        foreach (var e in root.Element("QarEntries")?.Elements("QarEntry") ?? [])
            m.QarEntries.Add(new QarEntryInfo(
                // A hand-made file may leave Hash out; nothing reads it (the builder hashes the path again).
                ulong.TryParse(A(e, "Hash"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong h) ? h : 0,
                A(e, "FilePath"),
                string.Equals(A(e, "Compressed"), "true", StringComparison.OrdinalIgnoreCase),
                A(e, "ContentHash")));
        foreach (var e in root.Element("FpkEntries")?.Elements("FpkEntry") ?? [])
            m.FpkEntries.Add(new FpkEntryInfo(A(e, "FpkFile"), A(e, "FilePath"), A(e, "ContentHash")));
        foreach (var e in root.Element("FileEntries")?.Elements("FileEntry") ?? [])
            m.FileEntries.Add(new GameFileInfo(A(e, "FilePath"), A(e, "ContentHash")));
        return m;
    }
}
