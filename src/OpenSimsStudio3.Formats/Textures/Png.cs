using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OpenSimsStudio3.Formats.Compositing;

namespace OpenSimsStudio3.Formats.Textures;

/// <summary>
/// A small PNG codec for thumbnails, written from the PNG specification (W3C PNG, 2nd/3rd edition): Formats has no
/// image library. <see cref="Encode" /> writes 8-bit straight-alpha RGBA (colour type 6) with per-row adaptive filters.
/// <see cref="Decode" /> reads non-interlaced 8-bit greyscale, grey+alpha, RGB, RGBA and palette images (with tRNS) and
/// 16-bit greyscale/RGB/RGBA (high byte kept); anything else throws <see cref="InvalidDataException" />. The app's
/// importer uses Skia for arbitrary files; this reads back what OSS3 wrote and what most tools write.
/// </summary>
public static class Png
{
    static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[..8].SequenceEqual(Signature);

    public static byte[] Encode(DdsImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Rgba.Length < image.Width * image.Height * 4)
            throw new ArgumentException("bad image size", nameof(image));
        var stride = image.Width * 4;
        var filtered = new byte[(stride + 1) * image.Height];
        var candidate = new byte[stride];
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Rgba.AsSpan(y * stride, stride);
            var above = y == 0 ? ReadOnlySpan<byte>.Empty : image.Rgba.AsSpan((y - 1) * stride, stride);
            // Heuristic from the specification (section 12.8): the filter with the smallest sum of signed differences.
            var best = long.MaxValue;
            for (byte f = 0; f <= 4; f++)
            {
                long sum = 0;
                for (var i = 0; i < stride; i++)
                {
                    var a = i >= 4 ? row[i - 4] : 0;
                    var b = above.IsEmpty ? 0 : above[i];
                    var c = i >= 4 && !above.IsEmpty ? above[i - 4] : 0;
                    candidate[i] = unchecked((byte)(row[i] - Predict(f, a, b, c)));
                    sum += Math.Abs((int)(sbyte)candidate[i]);
                }
                if (sum >= best) continue;
                best = sum;
                filtered[y * (stride + 1)] = f;
                candidate.CopyTo(filtered, y * (stride + 1) + 1);
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        WriteChunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) z.Write(filtered);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <exception cref="InvalidDataException">Not a PNG, or a kind this decoder doesn't read.</exception>
    public static DdsImage Decode(byte[] bytes)
    {
        if (!IsPng(bytes)) throw new InvalidDataException("not a PNG");
        int width = 0, height = 0, depth = 0, colourType = -1;
        byte[]? palette = null, transparency = null;
        using var idat = new MemoryStream();
        var at = 8;
        while (at + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
            var type = Encoding.ASCII.GetString(bytes, at + 4, 4);
            if (length < 0 || at + 12 + length > bytes.Length) throw new InvalidDataException("truncated PNG chunk");
            var data = bytes.AsSpan(at + 8, length);
            switch (type)
            {
                case "IHDR":
                    if (length < 13) throw new InvalidDataException("short IHDR");
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8];
                    colourType = data[9];
                    if (data[12] != 0) throw new InvalidDataException("interlaced PNGs are not supported");
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": transparency = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            at += 12 + length;
            if (type == "IEND") break;
        }
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new InvalidDataException("bad PNG size");
        var channels = colourType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {colourType}") };
        if (!(depth == 8 || (depth == 16 && colourType != 3))) throw new InvalidDataException($"{depth}-bit PNGs of colour type {colourType} are not supported");
        if (colourType == 3 && palette == null) throw new InvalidDataException("palette PNG without PLTE");
        var bpp = channels * depth / 8;
        var stride = width * bpp;

        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = z.Read(raw, read, raw.Length - read);
                if (n == 0) throw new InvalidDataException("PNG image data is short");
                read += n;
            }
        }

        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            if (filter > 4) throw new InvalidDataException($"PNG filter {filter}");
            var source = raw.AsSpan(y * (stride + 1) + 1, stride);
            var row = pixels.AsSpan(y * stride, stride);
            var above = y == 0 ? Span<byte>.Empty : pixels.AsSpan((y - 1) * stride, stride);
            for (var i = 0; i < stride; i++)
            {
                var a = i >= bpp ? row[i - bpp] : 0;
                var b = above.IsEmpty ? 0 : above[i];
                var c = i >= bpp && !above.IsEmpty ? above[i - bpp] : 0;
                row[i] = unchecked((byte)(source[i] + Predict(filter, a, b, c)));
            }
        }

        var rgba = new byte[width * height * 4];
        var step = depth / 8;  // 16-bit samples keep their high (first) byte
        for (var p = 0; p < width * height; p++)
        {
            var s = p * bpp;
            byte Sample(int k) => pixels[s + k * step];
            var d = p * 4;
            switch (colourType)
            {
                case 0: rgba[d] = rgba[d + 1] = rgba[d + 2] = Sample(0); rgba[d + 3] = 255; break;
                case 4: rgba[d] = rgba[d + 1] = rgba[d + 2] = Sample(0); rgba[d + 3] = Sample(1); break;
                case 2: rgba[d] = Sample(0); rgba[d + 1] = Sample(1); rgba[d + 2] = Sample(2); rgba[d + 3] = 255; break;
                case 6: rgba[d] = Sample(0); rgba[d + 1] = Sample(1); rgba[d + 2] = Sample(2); rgba[d + 3] = Sample(3); break;
                case 3:
                    var index = pixels[s];
                    if (index * 3 + 2 >= palette!.Length) throw new InvalidDataException("PNG palette index out of range");
                    rgba[d] = palette[index * 3];
                    rgba[d + 1] = palette[index * 3 + 1];
                    rgba[d + 2] = palette[index * 3 + 2];
                    rgba[d + 3] = transparency != null && index < transparency.Length ? transparency[index] : (byte)255;
                    break;
            }
        }
        return new DdsImage(width, height, rgba);
    }

    static int Predict(int filter, int a, int b, int c) => filter switch
    {
        0 => 0,
        1 => a,
        2 => b,
        3 => (a + b) / 2,
        _ => Paeth(a, b, c),
    };

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(typeBytes, data));
        output.Write(word);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>The PNG chunk CRC (ISO 3309 / ITU-T V.42) over type and data.</summary>
    public static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
