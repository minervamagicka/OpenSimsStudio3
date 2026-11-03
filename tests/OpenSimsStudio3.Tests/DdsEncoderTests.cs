using System.Buffers.Binary;
using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Textures;

namespace OpenSimsStudio3.Tests;

/// <summary>Encodes images the tests make and decodes them back with <see cref="DdsDecoder" />.</summary>
public class DdsEncoderTests
{
    static DdsImage Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a; }
        return new DdsImage(w, h, rgba);
    }

    static DdsImage Make(int w, int h, Func<int, int, (int R, int G, int B, int A)> pixel)
    {
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var o = (y * w + x) * 4;
                rgba[o] = (byte)r; rgba[o + 1] = (byte)g; rgba[o + 2] = (byte)b; rgba[o + 3] = (byte)a;
            }
        return new DdsImage(w, h, rgba);
    }

    /// <summary>Per-channel worst and mean absolute difference.</summary>
    static (int Max, double Mean) Error(DdsImage a, DdsImage b, int channel)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int max = 0;
        long sum = 0;
        for (var i = channel; i < a.Rgba.Length; i += 4)
        {
            var d = Math.Abs(a.Rgba[i] - b.Rgba[i]);
            max = Math.Max(max, d);
            sum += d;
        }
        return (max, (double)sum / (a.Width * a.Height));
    }

    /// <summary>Decodes one mip by giving its data a header of its own.</summary>
    static DdsImage DecodeMip(byte[] dds, DdsFormat format, int level)
    {
        var chain = DdsEncoder.MipChain(BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(16)), BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(12)));
        var offset = 128;
        for (var i = 0; i < level; i++) offset += DdsEncoder.MipBytes(chain[i].Width, chain[i].Height, format);
        var (w, h) = chain[level];
        var header = dds[..128];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), h);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), w);
        return DdsDecoder.Decode([.. header, .. dds.AsSpan(offset, DdsEncoder.MipBytes(w, h, format))]);
    }

    // Values whose RGB565 form expands back to themselves: c = (q << 3 | q >> 2) for red/blue, (q << 2 | q >> 4) for green.
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(132, 130, 66)]
    public void Dxt1_solid_end_point_colours_are_exact(int r, int g, int b)
    {
        var image = Solid(8, 8, (byte)r, (byte)g, (byte)b);
        var decoded = DdsDecoder.Decode(DdsEncoder.Encode(image, DdsFormat.Dxt1));
        Assert.Equal(image.Rgba, decoded.Rgba);
    }

    [Fact]
    public void Dxt1_any_solid_colour_is_within_two_levels()
    {
        for (var v = 0; v < 256; v += 7)
        {
            var image = Solid(4, 4, (byte)v, (byte)(255 - v), (byte)(v / 2 + 40));
            var decoded = DdsDecoder.Decode(DdsEncoder.Encode(image, DdsFormat.Dxt1));
            for (var c = 0; c < 3; c++) Assert.True(Error(image, decoded, c).Max <= 2, $"value {v}, channel {c}");
            Assert.Equal(255, decoded.Rgba[3]);
        }
    }

    [Theory]
    [InlineData(DdsFormat.Dxt1)]
    [InlineData(DdsFormat.Dxt5)]
    public void Gradients_stay_within_error_bounds(DdsFormat format)
    {
        var image = Make(64, 64, (x, y) => (x * 4, y * 4, (x + y) * 2, 255));
        var decoded = DdsDecoder.Decode(DdsEncoder.Encode(image, format));
        for (var c = 0; c < 3; c++)
        {
            var (max, mean) = Error(image, decoded, c);
            Assert.True(max <= 12, $"channel {c} max error {max}");
            Assert.True(mean <= 4, $"channel {c} mean error {mean:F2}");
        }
    }

    [Fact]
    public void Dxt5_alpha_is_exact_for_flat_and_two_level_blocks_and_close_on_ramps()
    {
        var flat = Solid(4, 4, 10, 20, 30, 77);
        Assert.Equal(flat.Rgba.Where((_, i) => i % 4 == 3), DdsDecoder.Decode(DdsEncoder.Encode(flat, DdsFormat.Dxt5)).Rgba.Where((_, i) => i % 4 == 3));

        var cutout = Make(8, 8, (x, y) => (200, 100, 50, x < 4 ? 0 : 255));
        Assert.Equal(0, Error(cutout, DdsDecoder.Decode(DdsEncoder.Encode(cutout, DdsFormat.Dxt5)), 3).Max);

        var ramp = Make(16, 16, (x, y) => (128, 128, 128, x * 16 + y));
        var (max, _) = Error(ramp, DdsDecoder.Decode(DdsEncoder.Encode(ramp, DdsFormat.Dxt5)), 3);
        Assert.True(max <= 4, $"alpha max error {max}");
    }

    [Fact]
    public void Dxt1_keeps_punch_through_alpha()
    {
        var image = Make(8, 8, (x, y) => (x * 30, 90, 200, (x + y) % 3 == 0 ? 0 : 255));
        var decoded = DdsDecoder.Decode(DdsEncoder.Encode(image, DdsFormat.Dxt1));
        for (var p = 0; p < 64; p++) Assert.Equal(image.Rgba[p * 4 + 3], decoded.Rgba[p * 4 + 3]);
    }

    [Fact]
    public void A8r8g8b8_is_exact_with_its_masks()
    {
        var image = Make(5, 3, (x, y) => (x * 50, y * 100, 7 * x + y, 255 - x * 40));
        var dds = DdsEncoder.Encode(image, DdsFormat.A8R8G8B8);
        Assert.Equal(image.Rgba, DdsDecoder.Decode(dds).Rgba);
        Assert.Equal(new byte[] { (byte)image.Rgba[2], image.Rgba[1], image.Rgba[0], image.Rgba[3] }, dds[128..132]);
    }

    [Theory]
    [InlineData(256, 128, 9)]
    [InlineData(1, 1, 1)]
    [InlineData(6, 5, 3)]
    [InlineData(1, 8, 4)]
    public void Mip_chain_goes_to_one_by_one(int w, int h, int count)
    {
        var chain = DdsEncoder.MipChain(w, h);
        Assert.Equal(count, chain.Count);
        Assert.Equal((w, h), chain[0]);
        Assert.Equal((1, 1), chain[^1]);
    }

    [Theory]
    [InlineData(DdsFormat.Dxt1, "DXT1")]
    [InlineData(DdsFormat.Dxt5, "DXT5")]
    [InlineData(DdsFormat.A8R8G8B8, "32-bit")]
    public void Header_reads_back_and_file_holds_every_mip(DdsFormat format, string name)
    {
        var dds = DdsEncoder.Encode(Solid(64, 32, 1, 2, 3), format);
        Assert.Equal(new TextureInfo(64, 32, name, 7), TextureInfo.Read(dds));
        Assert.Equal(128 + DdsEncoder.MipChain(64, 32).Sum(m => DdsEncoder.MipBytes(m.Width, m.Height, format)), dds.Length);
        Assert.Equal(124, BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(4)));
        Assert.Equal(0x401008u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(108))); // texture | complex | mipmap
    }

    [Theory]
    [InlineData(DdsFormat.Dxt1)]
    [InlineData(DdsFormat.Dxt5)]
    [InlineData(DdsFormat.A8R8G8B8)]
    public void Every_mip_of_a_solid_image_decodes_to_the_colour(DdsFormat format)
    {
        var image = Solid(16, 4, 255, 0, 255, 255);
        var dds = DdsEncoder.Encode(image, format);
        var chain = DdsEncoder.MipChain(16, 4);
        for (var level = 0; level < chain.Count; level++)
        {
            var mip = DecodeMip(dds, format, level);
            Assert.Equal(chain[level], (mip.Width, mip.Height));
            Assert.All(mip.Rgba.Chunk(4), p => Assert.Equal(new byte[] { 255, 0, 255, 255 }, p));
        }
    }

    [Theory]
    [InlineData(DdsFormat.Dxt1)]
    [InlineData(DdsFormat.Dxt5)]
    [InlineData(DdsFormat.A8R8G8B8)]
    public void Sizes_that_are_not_multiples_of_four_round_trip(DdsFormat format)
    {
        var image = Make(7, 5, (x, y) => (x + y) % 2 == 0 ? (255, 0, 0, 255) : (0, 0, 255, 255));
        var dds = DdsEncoder.Encode(image, format);
        var decoded = DdsDecoder.Decode(dds);
        Assert.Equal((7, 5), (decoded.Width, decoded.Height));
        // Two colours per block at most: BC1 end points hold them exactly.
        Assert.Equal(image.Rgba, decoded.Rgba);
        var chain = DdsEncoder.MipChain(7, 5);
        Assert.Equal([(7, 5), (3, 2), (1, 1)], chain);
        Assert.Equal((1, 1), (DecodeMip(dds, format, 2).Width, DecodeMip(dds, format, 2).Height));
    }

    [Fact]
    public void Dxt1_of_an_opaque_image_never_decodes_transparent()
    {
        var random = new Random(1234);
        var image = Make(64, 64, (_, _) => (random.Next(256), random.Next(256), random.Next(256), 255));
        Assert.All(DdsDecoder.Decode(DdsEncoder.Encode(image, DdsFormat.Dxt1)).Rgba.Where((_, i) => i % 4 == 3), a => Assert.Equal(255, a));
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        var image = Make(32, 32, (x, y) => ((x * 37 + y * 11) & 255, (x * y) & 255, (x ^ y) * 8 & 255, (x * 9) & 255));
        Assert.Equal(DdsEncoder.Encode(image, DdsFormat.Dxt5), DdsEncoder.Encode(image, DdsFormat.Dxt5));
    }

    [Fact]
    public void Downsample_averages_two_by_two()
    {
        var image = Make(2, 2, (x, y) => (x * 100 + y * 50, 0, 0, x == 0 ? 0 : 255));
        var small = DdsEncoder.Downsample(image, 1, 1);
        Assert.Equal(new byte[] { 75, 0, 0, 128 }, small.Rgba);
    }

    [Fact]
    public void Bad_buffers_are_refused()
    {
        Assert.Throws<ArgumentException>(() => DdsEncoder.Encode(new DdsImage(4, 4, new byte[10]), DdsFormat.Dxt1));
        Assert.Throws<ArgumentException>(() => DdsEncoder.Encode(new DdsImage(0, 4, []), DdsFormat.Dxt1));
    }

    [Fact]
    public void Import_keeps_the_slot_format_and_takes_the_image_size()
    {
        var opaque = Solid(8, 8, 9, 9, 9);
        var seeThrough = Solid(8, 8, 9, 9, 9, 100);
        Assert.Equal(DdsFormat.Dxt1, TextureImport.FormatFor(TextureInfo.Read(DdsEncoder.Encode(opaque, DdsFormat.Dxt1)), seeThrough));
        Assert.Equal(DdsFormat.Dxt5, TextureImport.FormatFor(TextureInfo.Read(TestDds.FourCC("DXT3", 4, 4, new byte[16])), opaque));
        Assert.Equal(DdsFormat.A8R8G8B8, TextureImport.FormatFor(TextureInfo.Read(DdsEncoder.Encode(opaque, DdsFormat.A8R8G8B8)), opaque));
        Assert.Equal(DdsFormat.A8R8G8B8, TextureImport.FormatFor(TextureInfo.Read(TestDds.Masked(1, 1, 8, 0xFF, 0, 0, 0, [1], luminance: true)), opaque));
        Assert.Equal(DdsFormat.Dxt1, TextureImport.FormatFor(null, opaque));
        Assert.Equal(DdsFormat.Dxt5, TextureImport.FormatFor(null, seeThrough));

        var existing = DdsEncoder.Encode(Solid(256, 256, 0, 0, 0), DdsFormat.Dxt5);
        var result = TextureImport.Encode(Solid(12, 8, 1, 1, 1), existing);
        Assert.Equal((DdsFormat.Dxt5, 12, 8), (result.Format, result.Width, result.Height));
        Assert.Equal(new TextureInfo(12, 8, "DXT5", 4), TextureInfo.Read(result.Dds));
        Assert.Single(result.Warnings);
        Assert.Empty(TextureImport.Encode(Solid(16, 8, 1, 1, 1), existing).Warnings);
    }
}
