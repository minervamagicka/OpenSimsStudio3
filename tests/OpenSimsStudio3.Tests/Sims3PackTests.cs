using System.Text;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Sims3Pack;

namespace OpenSimsStudio3.Tests;

public class Sims3PackTests
{
    [Fact]
    public void Header_is_signature_version_and_manifest_size()
    {
        using var temp = new TempFolder();
        Sims3PackFormat.Write(temp["a.sims3pack"], new Sims3PackInfo { PackageId = "a", DisplayName = "A" }, [("a.package", [1, 2, 3])]);
        var bytes = File.ReadAllBytes(temp["a.sims3pack"]);
        Assert.Equal(7u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal("TS3Pack", Encoding.ASCII.GetString(bytes, 4, 7));
        Assert.Equal((ushort)0x0101, BitConverter.ToUInt16(bytes, 11));
        var xmlSize = (int)BitConverter.ToUInt32(bytes, 13);
        var xml = Encoding.UTF8.GetString(bytes, 17, xmlSize);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.EndsWith("</Sims3Package>", xml);
        Assert.NotEqual(0xEF, bytes[17]); // no BOM
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes[(17 + xmlSize)..]);
    }

    [Fact]
    public void Every_manifest_field_reads_back()
    {
        using var temp = new TempFolder();
        byte[] first = [1, 2, 3, 4], second = [9, 8];
        var info = new Sims3PackInfo { Type = "CasPart", SubType = 0x4, PackageId = "skirt", DisplayName = "Long skirt & <co>", Description = "Test" };
        Sims3PackFormat.Write(temp["s.sims3pack"], info, [("skirt.package", first), ("extra.package", second)]);

        var pack = Sims3PackFormat.Read(temp["s.sims3pack"]);
        Assert.Equal(Sims3PackFormat.HeaderVersion, pack.Version);
        Assert.Equal(info, pack.Info);
        var root = pack.Manifest.Root!;
        Assert.Equal("0x00000004", (string?)root.Attribute("SubType"));
        Assert.Equal("1.4", (string?)root.Element("ArchiveVersion"));
        Assert.Equal("1.0.0.0", (string?)root.Element("MinReqVersion"));

        Assert.Equal(2, pack.Entries.Count);
        var (a, b) = (pack.Entries[0], pack.Entries[1]);
        Assert.Equal(("skirt.package", 0L, 4L, "skirt", "CasPart"), (a.Name, a.Offset, a.Length, a.Guid, a.ContentType));
        Assert.Equal(("extra.package", 4L, 2L, "extra", "CasPart"), (b.Name, b.Offset, b.Length, b.Guid, b.ContentType));
        Assert.Equal(first, a.Data);
        Assert.Equal(second, b.Data);
        Assert.Equal(Sims3PackFormat.Crc(first), a.Crc);
        Assert.Equal(Sims3PackFormat.Crc(second), b.Crc);
        Assert.Matches("^0x[0-9A-F]{16}$", a.Crc);
        Assert.NotEqual(a.Crc, b.Crc);
    }

    [Fact]
    public void Wraps_an_exported_package_unchanged()
    {
        using var temp = new TempFolder();
        TestPackages.Write(temp["Long skirt.package"], (new ResourceKey(0x034AEECB, 0, 1), [5, 6, 7]));
        Sims3PackFormat.WriteFromPackage(temp["Long skirt.package"], temp["out/Long skirt.sims3pack"], "Long skirt");

        var pack = Sims3PackFormat.Read(temp["out/Long skirt.sims3pack"]);
        Assert.Equal(("Long skirt", "Long skirt", "CasPart"), (pack.Info.PackageId, pack.Info.DisplayName, pack.Info.Type));
        var entry = Assert.Single(pack.Entries);
        Assert.Equal("Long skirt.package", entry.Name);
        Assert.Equal(File.ReadAllBytes(temp["Long skirt.package"]), entry.Data);
        Assert.False(File.Exists(temp["out/Long skirt.sims3pack.tmp"]));
    }

    [Fact]
    public void Crc_is_s3pis_sims3pack_crc()
    {
        byte[] data = [0x10, 0x20, 0x30];
        Assert.Equal("0x" + System.Security.Cryptography.Sims3PackCRC.CalculateCRC(data).ToString("X16"), Sims3PackFormat.Crc(data));
    }

    [Fact]
    public void Rejects_other_files()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(temp["x.sims3pack"], Encoding.ASCII.GetBytes("DBPF\x02\0\0\0 not a sims3pack"));
        Assert.Throws<InvalidDataException>(() => Sims3PackFormat.Read(temp["x.sims3pack"]));
    }

    [Fact]
    public void Rejects_a_file_past_the_archive_end()
    {
        using var temp = new TempFolder();
        Sims3PackFormat.Write(temp["t.sims3pack"], new Sims3PackInfo { PackageId = "t" }, [("t.package", [1, 2, 3])]);
        var bytes = File.ReadAllBytes(temp["t.sims3pack"]);
        File.WriteAllBytes(temp["t.sims3pack"], bytes[..^1]);
        Assert.Throws<InvalidDataException>(() => Sims3PackFormat.Read(temp["t.sims3pack"]));
    }

    [Fact]
    public void Needs_a_file()
    {
        using var temp = new TempFolder();
        Assert.Throws<ArgumentException>(() => Sims3PackFormat.Write(temp["e.sims3pack"], new Sims3PackInfo(), []));
    }
}
