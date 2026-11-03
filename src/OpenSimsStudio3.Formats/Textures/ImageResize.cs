using OpenSimsStudio3.Formats.Compositing;

namespace OpenSimsStudio3.Formats.Textures;

/// <summary>
/// Resizing straight-alpha RGBA8 images for thumbnails. Colour is weighted by alpha (premultiplied while filtering), so
/// transparent pixels don't darken edges. Shrinking averages the exact source area under each target pixel (fractional
/// coverage); enlarging is bilinear. Separable: rows first, then columns. Fully transparent results are 0,0,0,0.
/// Not for TS3 masks (which keep data under zero alpha): <see cref="DdsEncoder.Downsample" /> is for textures.
/// </summary>
public static class ImageResize
{
    public static DdsImage Resize(DdsImage source, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "size must be positive");
        if (source.Width <= 0 || source.Height <= 0 || source.Rgba.Length < source.Width * source.Height * 4)
            throw new ArgumentException("bad image size", nameof(source));

        // Premultiplied floats.
        var p = new float[source.Width * source.Height * 4];
        for (var i = 0; i < source.Width * source.Height; i++)
        {
            var a = source.Rgba[i * 4 + 3] / 255f;
            p[i * 4] = source.Rgba[i * 4] * a;
            p[i * 4 + 1] = source.Rgba[i * 4 + 1] * a;
            p[i * 4 + 2] = source.Rgba[i * 4 + 2] * a;
            p[i * 4 + 3] = a;
        }

        var columns = Weights(source.Width, width);
        var horizontal = new float[width * source.Height * 4];
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < width; x++)
                foreach (var (s, w) in columns[x])
                    for (var k = 0; k < 4; k++) horizontal[(y * width + x) * 4 + k] += p[(y * source.Width + s) * 4 + k] * w;

        var rows = Weights(source.Height, height);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                foreach (var (s, w) in rows[y])
                {
                    var i = (s * width + x) * 4;
                    r += horizontal[i] * w; g += horizontal[i + 1] * w; b += horizontal[i + 2] * w; a += horizontal[i + 3] * w;
                }
                var d = (y * width + x) * 4;
                var alpha = (byte)Math.Clamp(MathF.Round(a * 255f), 0, 255);
                rgba[d + 3] = alpha;
                if (alpha == 0) continue;
                rgba[d] = Unit(r / a);
                rgba[d + 1] = Unit(g / a);
                rgba[d + 2] = Unit(b / a);
            }
        return new DdsImage(width, height, rgba);
    }

    /// <summary>
    /// Scales the image to fit a <paramref name="side" /> square (keeping its aspect ratio, longer side = side) and
    /// centres it on a transparent square.
    /// </summary>
    public static DdsImage FitSquare(DdsImage source, int side)
    {
        if (source.Width == source.Height) return source.Width == side ? source : Resize(source, side, side);
        var (w, h) = source.Width > source.Height
            ? (side, Math.Max(1, (int)Math.Round((double)side * source.Height / source.Width)))
            : (Math.Max(1, (int)Math.Round((double)side * source.Width / source.Height)), side);
        var scaled = Resize(source, w, h);
        var square = new byte[side * side * 4];
        int left = (side - w) / 2, top = (side - h) / 2;
        for (var y = 0; y < h; y++)
            Array.Copy(scaled.Rgba, y * w * 4, square, ((top + y) * side + left) * 4, w * 4);
        return new DdsImage(side, side, square);
    }

    static byte Unit(float v) => (byte)Math.Clamp(MathF.Round(v), 0, 255);

    /// <summary>For each target index, the source indices and weights (summing to 1) that make it.</summary>
    static (int Source, float Weight)[][] Weights(int sourceLength, int targetLength)
    {
        var result = new (int, float)[targetLength][];
        var scale = (double)sourceLength / targetLength;
        for (var i = 0; i < targetLength; i++)
        {
            if (targetLength < sourceLength)
            {
                // Area: the source interval [i·scale, (i+1)·scale) and how much of each source pixel it covers.
                double from = i * scale, to = (i + 1) * scale;
                var list = new List<(int, float)>();
                for (var s = (int)Math.Floor(from); s < Math.Min(sourceLength, (int)Math.Ceiling(to)); s++)
                {
                    var cover = Math.Min(to, s + 1) - Math.Max(from, s);
                    if (cover > 1e-9) list.Add((s, (float)(cover / scale)));
                }
                result[i] = list.ToArray();
            }
            else
            {
                // Bilinear between pixel centres, clamped at the edges (identity when the lengths match).
                var x = (i + 0.5) * scale - 0.5;
                var s0 = (int)Math.Floor(x);
                var f = (float)(x - s0);
                int a = Math.Clamp(s0, 0, sourceLength - 1), b = Math.Clamp(s0 + 1, 0, sourceLength - 1);
                result[i] = f < 1e-6f ? [(a, 1f)] : [(a, 1 - f), (b, f)];
            }
        }
        return result;
    }
}
