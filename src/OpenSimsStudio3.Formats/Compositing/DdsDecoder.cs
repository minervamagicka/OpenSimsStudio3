using System.Buffers.Binary;

namespace OpenSimsStudio3.Formats.Compositing;

/// <summary>A decoded top mip: RGBA8, rows top to bottom, straight (not premultiplied) alpha.</summary>
public sealed record DdsImage(int Width, int Height, byte[] Rgba);

/// <summary>
/// Decodes the top mip of the DDS files TS3 uses for CAS textures: DXT1/3/5 (BC1-3), uncompressed RGB/RGBA described by
/// bit masks, and luminance (L8, A8L8). Written from Microsoft's public DDS and BC1-3 format documentation.
/// </summary>
public static class DdsDecoder
{
    const int MaxExtent = 16384;
    const uint FlagAlphaPixels = 0x1, FlagAlpha = 0x2, FlagFourCC = 0x4, FlagRgb = 0x40, FlagLuminance = 0x20000;

    /// <summary>The top mip, or null when the bytes are not a DDS this decoder supports.</summary>
    public static DdsImage? TryDecode(byte[] bytes)
    {
        try { return Decode(bytes); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return null; }
    }

    /// <exception cref="InvalidDataException">Not a DDS, bad extents or truncated data.</exception>
    /// <exception cref="NotSupportedException">A pixel format CAS textures don't use.</exception>
    public static DdsImage Decode(byte[] bytes)
    {
        if (bytes.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x20534444) throw new InvalidDataException("not a DDS");
        var d = bytes.AsSpan();
        var height = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(d[16..]);
        if (width is < 1 or > MaxExtent || height is < 1 or > MaxExtent) throw new InvalidDataException($"DDS extents {width}x{height} out of range");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(d[80..]);
        var fourCC = BinaryPrimitives.ReadUInt32LittleEndian(d[84..]);
        var data = d[128..];
        var rgba = new byte[width * height * 4];
        if ((flags & FlagFourCC) != 0)
        {
            var blockBytes = fourCC switch
            {
                0x31545844 => 8,  // "DXT1"
                0x33545844 => 16, // "DXT3"
                0x35545844 => 16, // "DXT5"
                _ => throw new NotSupportedException($"DDS FourCC 0x{fourCC:X8}"),
            };
            var bw = (width + 3) / 4;
            var bh = (height + 3) / 4;
            if ((long)bw * bh * blockBytes > data.Length) throw new InvalidDataException("DDS block data truncated");
            Span<byte> block = stackalloc byte[64];
            for (var by = 0; by < bh; by++)
                for (var bx = 0; bx < bw; bx++)
                {
                    var src = data.Slice((by * bw + bx) * blockBytes, blockBytes);
                    if (blockBytes == 8) ColourBlock(src, block, allowTransparent: true);
                    else
                    {
                        ColourBlock(src[8..], block, allowTransparent: false);
                        if (fourCC == 0x33545844) ExplicitAlpha(src, block);
                        else InterpolatedAlpha(src, block);
                    }
                    for (var py = 0; py < 4 && by * 4 + py < height; py++)
                        for (var px = 0; px < 4 && bx * 4 + px < width; px++)
                            block.Slice((py * 4 + px) * 4, 4).CopyTo(rgba.AsSpan(((by * 4 + py) * width + bx * 4 + px) * 4));
                }
            return new DdsImage(width, height, rgba);
        }

        if ((flags & (FlagRgb | FlagLuminance | FlagAlpha)) == 0) throw new NotSupportedException($"DDS pixel format flags 0x{flags:X}");
        var bits = BinaryPrimitives.ReadInt32LittleEndian(d[88..]);
        if (bits is not (8 or 16 or 24 or 32)) throw new NotSupportedException($"DDS {bits}-bit pixels");
        var masks = new uint[4];
        for (var i = 0; i < 4; i++) masks[i] = BinaryPrimitives.ReadUInt32LittleEndian(d[(92 + 4 * i)..]);
        if ((flags & FlagAlphaPixels) == 0 && (flags & FlagAlpha) == 0) masks[3] = 0;
        var luminance = (flags & FlagLuminance) != 0;
        var bpp = bits / 8;
        // Luminance files may carry extra bytes after the top mip (seen on L8 textures); only the top mip is read.
        if ((long)width * height * bpp > data.Length) throw new InvalidDataException("DDS pixel data truncated");
        for (var p = 0; p < width * height; p++)
        {
            uint v = 0;
            for (var b = 0; b < bpp; b++) v |= (uint)data[p * bpp + b] << (8 * b);
            var o = rgba.AsSpan(p * 4, 4);
            if (luminance) o[0] = o[1] = o[2] = Channel(v, masks[0]);
            else if ((flags & FlagRgb) == 0) o[0] = o[1] = o[2] = 0; // alpha-only (A8)
            else { o[0] = Channel(v, masks[0]); o[1] = Channel(v, masks[1]); o[2] = Channel(v, masks[2]); }
            o[3] = masks[3] == 0 ? (byte)255 : Channel(v, masks[3]);
        }
        return new DdsImage(width, height, rgba);
    }

    /// <summary>The masked field scaled to 0..255 (rounded).</summary>
    static byte Channel(uint value, uint mask)
    {
        if (mask == 0) return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        return (byte)(((ulong)((value & mask) >> shift) * 255 + max / 2) / max);
    }

    /// <summary>BC1 colour half: two RGB565 end points, 2-bit indices, 16 RGBA texels out.</summary>
    static void ColourBlock(ReadOnlySpan<byte> s, Span<byte> outBlock, bool allowTransparent)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(s);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        Span<int> pal = stackalloc int[16];
        Expand565(c0, pal[..4]);
        Expand565(c1, pal[4..8]);
        var four = c0 > c1 || !allowTransparent;
        for (var ch = 0; ch < 3; ch++)
        {
            int a = pal[ch], b = pal[4 + ch];
            pal[8 + ch] = four ? (2 * a + b) / 3 : (a + b) / 2;
            pal[12 + ch] = four ? (a + 2 * b) / 3 : 0;
        }
        pal[11] = 255;
        pal[15] = four ? 255 : 0;
        var indices = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        for (var i = 0; i < 16; i++)
        {
            var e = (int)(indices >> (2 * i)) & 3;
            for (var ch = 0; ch < 4; ch++) outBlock[i * 4 + ch] = (byte)pal[e * 4 + ch];
        }
    }

    static void Expand565(ushort c, Span<int> o)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        o[0] = (r << 3) | (r >> 2);
        o[1] = (g << 2) | (g >> 4);
        o[2] = (b << 3) | (b >> 2);
        o[3] = 255;
    }

    /// <summary>BC2 alpha: 4 bits per texel.</summary>
    static void ExplicitAlpha(ReadOnlySpan<byte> s, Span<byte> outBlock)
    {
        for (var i = 0; i < 16; i++)
        {
            var nibble = (s[i / 2] >> (4 * (i & 1))) & 15;
            outBlock[i * 4 + 3] = (byte)(nibble * 17);
        }
    }

    /// <summary>BC3 alpha: two 8-bit end points, 3-bit indices into an 8-entry ramp.</summary>
    static void InterpolatedAlpha(ReadOnlySpan<byte> s, Span<byte> outBlock)
    {
        int a0 = s[0], a1 = s[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = a0;
        pal[1] = a1;
        if (a0 > a1)
            for (var k = 1; k < 7; k++) pal[k + 1] = ((7 - k) * a0 + k * a1) / 7;
        else
        {
            for (var k = 1; k < 5; k++) pal[k + 1] = ((5 - k) * a0 + k * a1) / 5;
            pal[6] = 0;
            pal[7] = 255;
        }
        ulong bitsAll = 0;
        for (var i = 0; i < 6; i++) bitsAll |= (ulong)s[2 + i] << (8 * i);
        for (var i = 0; i < 16; i++) outBlock[i * 4 + 3] = (byte)pal[(int)(bitsAll >> (3 * i)) & 7];
    }
}
