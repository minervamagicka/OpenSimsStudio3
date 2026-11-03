using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Compositing;

namespace OpenSimsStudio3.Formats.Textures;

/// <summary>An encoded texture ready for a slot, the format chosen for it, and anything the artist should know.</summary>
public sealed record EncodedTexture(byte[] Dds, DdsFormat Format, int Width, int Height, IReadOnlyList<string> Warnings);

/// <summary>Turns an artist's image into the DDS a texture slot gets.</summary>
public static class TextureImport
{
    /// <summary>
    /// Encodes <paramref name="image" /> for a slot that currently holds <paramref name="existingDds" /> (null or not a
    /// DDS for an empty or missing slot). The slot keeps its format; the size is the image's (the slot's own size when
    /// they match), with a warning when a side is not a power of two.
    /// </summary>
    public static EncodedTexture Encode(DdsImage image, byte[]? existingDds)
    {
        var format = FormatFor(TextureInfo.Read(existingDds), image);
        var warnings = new List<string>();
        if (!IsPowerOfTwo(image.Width) || !IsPowerOfTwo(image.Height)) warnings.Add($"{image.Width} × {image.Height} is not a power of two");
        return new EncodedTexture(DdsEncoder.Encode(image, format), format, image.Width, image.Height, warnings);
    }

    /// <summary>
    /// The slot's format: DXT1 stays DXT1, DXT3 and DXT5 become DXT5 (OSS3 writes no DXT3), uncompressed and luminance
    /// textures become A8R8G8B8. An empty slot gets DXT1 when the image is opaque, else DXT5.
    /// </summary>
    public static DdsFormat FormatFor(TextureInfo? existing, DdsImage image) => existing?.Format switch
    {
        "DXT1" => DdsFormat.Dxt1,
        "DXT3" or "DXT5" => DdsFormat.Dxt5,
        null => IsOpaque(image) ? DdsFormat.Dxt1 : DdsFormat.Dxt5,
        _ => DdsFormat.A8R8G8B8,
    };

    static bool IsOpaque(DdsImage image)
    {
        for (var i = 3; i < image.Rgba.Length; i += 4)
            if (image.Rgba[i] != 255) return false;
        return true;
    }

    static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;
}
