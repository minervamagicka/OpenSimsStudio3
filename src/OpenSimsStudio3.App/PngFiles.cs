using System.Runtime.InteropServices;
using OpenSimsStudio3.Formats.Compositing;
using SkiaSharp;

namespace OpenSimsStudio3.App;

/// <summary>
/// PNG in and out through SkiaSharp (the Skia that Avalonia already ships). Pixels are straight RGBA8: TS3 masks keep
/// data in RGB where alpha is 0, which premultiplied decoding would lose.
/// </summary>
public static class PngFiles
{
    static SKImageInfo Info(int width, int height) => new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

    /// <exception cref="InvalidDataException">Not an image Skia can read.</exception>
    public static DdsImage Load(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("not a readable image");
        var info = Info(codec.Info.Width, codec.Info.Height);
        using var bitmap = SKBitmap.Decode(codec, info) ?? throw new InvalidDataException("the image could not be decoded");
        var rgba = new byte[info.Width * info.Height * 4];
        for (var y = 0; y < info.Height; y++)
            Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, rgba, y * info.Width * 4, info.Width * 4);
        return new DdsImage(info.Width, info.Height, rgba);
    }

    public static void Save(DdsImage image, string path)
    {
        using var bitmap = new SKBitmap(Info(image.Width, image.Height));
        for (var y = 0; y < image.Height; y++)
            Marshal.Copy(image.Rgba, y * image.Width * 4, bitmap.GetPixels() + y * bitmap.RowBytes, image.Width * 4);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("PNG encoding failed");
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
