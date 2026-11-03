using OpenSimsStudio3.Formats.Cas;

namespace OpenSimsStudio3.Tests;

public class TextureInfoTests
{
    [Fact]
    public void Reads_size_fourcc_and_mips()
    {
        var info = TextureInfo.Read(TestDds.FourCC("DXT5", 256, 128, new byte[16], mips: 9));
        Assert.Equal(new TextureInfo(256, 128, "DXT5", 9), info);
    }

    [Fact]
    public void Uncompressed_reports_bit_count() =>
        Assert.Equal("32-bit", TextureInfo.Read(TestDds.Masked(1, 1, 32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000, new byte[4]))!.Format);

    [Fact]
    public void Zero_mip_count_means_one() =>
        Assert.Equal(1, TextureInfo.Read(TestDds.FourCC("DXT1", 4, 4, new byte[8], mips: 0))!.Mipmaps);

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Non_dds_is_null(byte[]? bytes) => Assert.Null(TextureInfo.Read(bytes));
}
