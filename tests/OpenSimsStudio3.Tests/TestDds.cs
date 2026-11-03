using System.Buffers.Binary;

namespace OpenSimsStudio3.Tests;

/// <summary>Builds small DDS files for tests, from the public DDS header layout. No game textures are used.</summary>
static class TestDds
{
    const uint FlagAlphaPixels = 0x1, FlagAlpha = 0x2, FlagFourCC = 0x4, FlagRgb = 0x40, FlagLuminance = 0x20000;

    public static byte[] FourCC(string fourCC, int width, int height, byte[] blocks, int mips = 1)
    {
        var header = Header(width, height, mips, FlagFourCC);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84), BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(fourCC)));
        return [.. header, .. blocks];
    }

    public static byte[] Masked(int width, int height, int bits, uint r, uint g, uint b, uint a, byte[] pixels, bool luminance = false)
    {
        var flags = luminance ? FlagLuminance : r == 0 && g == 0 && b == 0 ? FlagAlpha : FlagRgb;
        if (a != 0 && !luminance && flags == FlagRgb) flags |= FlagAlphaPixels;
        if (a != 0 && luminance) flags |= FlagAlphaPixels;
        var header = Header(width, height, 1, flags);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(88), bits);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(92), r);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(96), g);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100), b);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(104), a);
        return [.. header, .. pixels];
    }

    static byte[] Header(int width, int height, int mips, uint pixelFlags)
    {
        var h = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x20534444); // "DDS "
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 124);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), mips);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), pixelFlags);
        return h;
    }

    /// <summary>A BC1 block: two RGB565 end points and sixteen 2-bit indices (texel 0 in the lowest bits).</summary>
    public static byte[] Bc1(ushort c0, ushort c1, params int[] indices)
    {
        uint bits = 0;
        for (var i = 0; i < 16; i++) bits |= (uint)(indices.Length == 16 ? indices[i] : indices[0]) << (2 * i);
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, c0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), c1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), bits);
        return b;
    }

    /// <summary>A BC3 alpha half: two end points and sixteen 3-bit indices.</summary>
    public static byte[] Bc3Alpha(byte a0, byte a1, params int[] indices)
    {
        ulong bits = 0;
        for (var i = 0; i < 16; i++) bits |= (ulong)(indices.Length == 16 ? indices[i] : indices[0]) << (3 * i);
        var b = new byte[8];
        b[0] = a0;
        b[1] = a1;
        for (var i = 0; i < 6; i++) b[2 + i] = (byte)(bits >> (8 * i));
        return b;
    }
}
