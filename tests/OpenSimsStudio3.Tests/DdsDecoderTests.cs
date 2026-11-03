using OpenSimsStudio3.Formats.Compositing;

namespace OpenSimsStudio3.Tests;

public class DdsDecoderTests
{
    const ushort Red565 = 0xF800, Blue565 = 0x001F, White565 = 0xFFFF, Black565 = 0x0000;

    static byte[] Pixel(DdsImage image, int x, int y) => image.Rgba.AsSpan((y * image.Width + x) * 4, 4).ToArray();

    [Fact]
    public void Dxt1_end_points_decode_to_full_colours()
    {
        var image = DdsDecoder.Decode(TestDds.FourCC("DXT1", 4, 4, TestDds.Bc1(Red565, Blue565, 0)));
        Assert.Equal((4, 4), (image.Width, image.Height));
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(image, i % 4, i / 4)));
    }

    [Fact]
    public void Dxt1_four_colour_mode_interpolates_thirds()
    {
        var image = DdsDecoder.Decode(TestDds.FourCC("DXT1", 4, 4, TestDds.Bc1(White565, Black565, 2)));
        Assert.Equal(new byte[] { 170, 170, 170, 255 }, Pixel(image, 0, 0));
    }

    [Fact]
    public void Dxt1_three_colour_mode_index_3_is_transparent_black()
    {
        // c0 <= c1 selects the 3-colour mode with punch-through alpha.
        var image = DdsDecoder.Decode(TestDds.FourCC("DXT1", 4, 4, TestDds.Bc1(Black565, White565, 3)));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(image, 2, 2));
    }

    [Fact]
    public void Dxt1_texel_order_is_row_major_from_the_low_bits()
    {
        var indices = new int[16];
        indices[1] = 1; // texel (1,0) takes c1
        var image = DdsDecoder.Decode(TestDds.FourCC("DXT1", 4, 4, TestDds.Bc1(Red565, Blue565, indices)));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(image, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(image, 1, 0));
    }

    [Fact]
    public void Dxt5_alpha_ramp_has_eight_steps_when_a0_is_greater()
    {
        var alpha = TestDds.Bc3Alpha(255, 0, 2);
        var image = DdsDecoder.Decode(TestDds.FourCC("DXT5", 4, 4, [.. alpha, .. TestDds.Bc1(White565, Black565, 0)]));
        Assert.Equal((6 * 255) / 7, Pixel(image, 3, 3)[3]);
    }

    [Fact]
    public void Dxt5_six_step_ramp_has_explicit_0_and_255()
    {
        var indices = new int[16];
        indices[0] = 6;
        indices[1] = 7;
        var image = DdsDecoder.Decode(TestDds.FourCC("DXT5", 4, 4, [.. TestDds.Bc3Alpha(10, 200, indices), .. TestDds.Bc1(White565, Black565, 0)]));
        Assert.Equal(0, Pixel(image, 0, 0)[3]);
        Assert.Equal(255, Pixel(image, 1, 0)[3]);
    }

    [Fact]
    public void Dxt3_explicit_alpha_scales_nibbles()
    {
        var block = new byte[16];
        block[0] = 0xF0; // texel 0 alpha 0, texel 1 alpha 15
        TestDds.Bc1(White565, Black565, 0).CopyTo(block, 8);
        var image = DdsDecoder.Decode(TestDds.FourCC("DXT3", 4, 4, block));
        Assert.Equal(0, Pixel(image, 0, 0)[3]);
        Assert.Equal(255, Pixel(image, 1, 0)[3]);
    }

    [Fact]
    public void Non_multiple_of_four_extents_are_cropped()
    {
        var blocks = Enumerable.Range(0, 4).SelectMany(_ => TestDds.Bc1(Red565, Blue565, 0)).ToArray();
        var image = DdsDecoder.Decode(TestDds.FourCC("DXT1", 6, 5, blocks));
        Assert.Equal(6 * 5 * 4, image.Rgba.Length);
    }

    [Fact]
    public void A8R8G8B8_reads_channels_by_mask()
    {
        byte[] pixel = [0x30, 0x20, 0x10, 0x80]; // B, G, R, A in memory
        var image = DdsDecoder.Decode(TestDds.Masked(1, 1, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, pixel));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x80 }, image.Rgba);
    }

    [Fact]
    public void L8_fills_rgb_with_luminance_and_alpha_opaque()
    {
        var image = DdsDecoder.Decode(TestDds.Masked(2, 1, 8, 0xFF, 0, 0, 0, [7, 200], luminance: true));
        Assert.Equal(new byte[] { 7, 7, 7, 255, 200, 200, 200, 255 }, image.Rgba);
    }

    [Fact]
    public void A8_is_black_with_alpha()
    {
        var image = DdsDecoder.Decode(TestDds.Masked(1, 1, 8, 0, 0, 0, 0xFF, [99]));
        Assert.Equal(new byte[] { 0, 0, 0, 99 }, image.Rgba);
    }

    [Fact]
    public void Five_bit_channels_scale_to_full_range()
    {
        // R5G6B5 white: every field at its maximum.
        var image = DdsDecoder.Decode(TestDds.Masked(1, 1, 16, 0xF800, 0x07E0, 0x001F, 0, [0xFF, 0xFF]));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, image.Rgba);
    }

    [Fact]
    public void Not_a_dds_returns_null() => Assert.Null(DdsDecoder.TryDecode(new byte[200]));

    [Fact]
    public void Truncated_blocks_return_null() =>
        Assert.Null(DdsDecoder.TryDecode(TestDds.FourCC("DXT1", 8, 8, TestDds.Bc1(Red565, Blue565, 0))));

    [Fact]
    public void Unsupported_fourcc_returns_null() =>
        Assert.Null(DdsDecoder.TryDecode(TestDds.FourCC("ATI2", 4, 4, new byte[16])));

    [Fact]
    public void Extents_out_of_range_throw() =>
        Assert.Throws<InvalidDataException>(() => DdsDecoder.Decode(TestDds.FourCC("DXT1", 0, 4, new byte[8])));
}
