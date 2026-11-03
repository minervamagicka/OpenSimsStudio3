using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OpenSimsStudio3.Formats.Sims3Pack;

/// <summary>What the manifest says about the whole .sims3pack.</summary>
public sealed record Sims3PackInfo
{
    /// <summary>The root's Type attribute ("CasPart" for CAS content).</summary>
    public string Type { get; init; } = "CasPart";
    /// <summary>The root's SubType attribute, written as 0x%08X. Its meaning is not documented (see README.md).</summary>
    public uint SubType { get; init; }
    /// <summary>The package's file name without extension.</summary>
    public string PackageId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>One file in the archive section, as the manifest lists it.</summary>
public sealed record Sims3PackEntry(string Name, long Offset, long Length, string Crc, string Guid, string ContentType, byte[] Data);

/// <summary>A .sims3pack read back: its header version, manifest and files.</summary>
public sealed record Sims3PackContents(ushort Version, XDocument Manifest, Sims3PackInfo Info, IReadOnlyList<Sims3PackEntry> Entries);

/// <summary>
/// Writes and reads the game launcher's .sims3pack container, written for OSS3 from public format notes (README.md,
/// 2026-11-03). Layout: a header (DWORD signature length, ASCII "TS3Pack", WORD version, DWORD manifest size), the
/// XML manifest, then the archive section with each packaged file at the manifest's Offset (from the start of the
/// archive section) and Length. Little-endian. <b>Unverified in game.</b>
/// </summary>
public static class Sims3PackFormat
{
    public const string Signature = "TS3Pack";
    /// <summary>The header's WORD after the signature; the notes give 0x0101 and call it a probable version.</summary>
    public const ushort HeaderVersion = 0x0101;
    public const string ArchiveVersion = "1.4";
    public const string MinReqVersion = "1.0.0.0";

    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Wraps one exported .package as a .sims3pack; the package is the only packaged file.</summary>
    public static void WriteFromPackage(string packagePath, string outPath, string displayName, string description = "")
    {
        var name = Path.GetFileName(packagePath);
        var info = new Sims3PackInfo { PackageId = Path.GetFileNameWithoutExtension(name), DisplayName = displayName, Description = description };
        Write(outPath, info, [(name, File.ReadAllBytes(packagePath))]);
    }

    public static void Write(string outPath, Sims3PackInfo info, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        if (files.Count == 0) throw new ArgumentException("A .sims3pack needs at least one file.", nameof(files));
        var manifest = Manifest(info, files);
        var xml = new MemoryStream();
        using (var writer = XmlWriter.Create(xml, new XmlWriterSettings { Encoding = Utf8, Indent = true }))
            manifest.Save(writer);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var temp = outPath + ".tmp";
        using (var stream = File.Create(temp))
        using (var w = new BinaryWriter(stream, Encoding.ASCII))
        {
            w.Write((uint)Signature.Length);
            w.Write(Encoding.ASCII.GetBytes(Signature));
            w.Write(HeaderVersion);
            w.Write((uint)xml.Length);
            w.Write(xml.GetBuffer(), 0, (int)xml.Length);
            foreach (var (_, data) in files) w.Write(data);
        }
        File.Move(temp, outPath, overwrite: true);
    }

    static XDocument Manifest(Sims3PackInfo info, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        var root = new XElement("Sims3Package",
            new XAttribute("Type", info.Type),
            new XAttribute("SubType", Hex(info.SubType)),
            new XElement("ArchiveVersion", ArchiveVersion),
            new XElement("DisplayName", info.DisplayName),
            new XElement("Description", info.Description),
            new XElement("PackageId", info.PackageId),
            new XElement("MinReqVersion", MinReqVersion));
        long offset = 0;
        foreach (var (name, data) in files)
        {
            root.Add(new XElement("PackagedFile",
                new XElement("Name", name),
                new XElement("Length", data.Length.ToString(CultureInfo.InvariantCulture)),
                new XElement("Offset", offset.ToString(CultureInfo.InvariantCulture)),
                new XElement("Crc", Crc(data)),
                new XElement("Guid", Path.GetFileNameWithoutExtension(name)),
                new XElement("ContentType", info.Type)));
            offset += data.Length;
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    /// <summary>s3pi's Sims3Pack CRC (third_party/s3pi/System.Custom/Sims3PackCRC.cs) of the file, as 0x%016X.</summary>
    public static string Crc(byte[] data) => "0x" + System.Security.Cryptography.Sims3PackCRC.CalculateCRC(data).ToString("X16", CultureInfo.InvariantCulture);

    static string Hex(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Reads a .sims3pack: checks the header, parses the manifest and cuts each packaged file out of the archive.</summary>
    public static Sims3PackContents Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var r = new BinaryReader(new MemoryStream(bytes, false), Encoding.ASCII);
        if (bytes.Length < 4) throw new InvalidDataException("Too short for a .sims3pack header.");
        var signatureLength = r.ReadUInt32();
        if (signatureLength != Signature.Length || bytes.Length < 4 + signatureLength + 6) throw new InvalidDataException("Not a .sims3pack (signature length).");
        if (Encoding.ASCII.GetString(r.ReadBytes((int)signatureLength)) != Signature) throw new InvalidDataException("Not a .sims3pack (signature).");
        var version = r.ReadUInt16();
        var xmlSize = r.ReadUInt32();
        var archive = r.BaseStream.Position + xmlSize;
        if (archive > bytes.Length) throw new InvalidDataException("The manifest runs past the end of the file.");
        XDocument manifest;
        using (var xml = new MemoryStream(bytes, (int)r.BaseStream.Position, (int)xmlSize, false))
            manifest = XDocument.Load(xml);
        var root = manifest.Root ?? throw new InvalidDataException("Empty manifest.");
        if (root.Name != "Sims3Package") throw new InvalidDataException($"Manifest root is <{root.Name}>, not <Sims3Package>.");

        var info = new Sims3PackInfo
        {
            Type = (string?)root.Attribute("Type") ?? "",
            SubType = ParseHex((string?)root.Attribute("SubType")),
            PackageId = (string?)root.Element("PackageId") ?? "",
            DisplayName = (string?)root.Element("DisplayName") ?? "",
            Description = (string?)root.Element("Description") ?? "",
        };
        var entries = new List<Sims3PackEntry>();
        foreach (var file in root.Elements("PackagedFile"))
        {
            var offset = long.Parse((string?)file.Element("Offset") ?? throw new InvalidDataException("PackagedFile without Offset."), CultureInfo.InvariantCulture);
            var length = long.Parse((string?)file.Element("Length") ?? throw new InvalidDataException("PackagedFile without Length."), CultureInfo.InvariantCulture);
            if (offset < 0 || length < 0 || archive + offset + length > bytes.Length) throw new InvalidDataException("A packaged file runs past the end of the archive.");
            entries.Add(new Sims3PackEntry((string?)file.Element("Name") ?? "", offset, length, (string?)file.Element("Crc") ?? "",
                (string?)file.Element("Guid") ?? "", (string?)file.Element("ContentType") ?? "", bytes.AsSpan((int)(archive + offset), (int)length).ToArray()));
        }
        return new Sims3PackContents(version, manifest, info, entries);
    }

    static uint ParseHex(string? text) =>
        text != null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
