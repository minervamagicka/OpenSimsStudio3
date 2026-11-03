using System.Buffers.Binary;
using OpenSimsStudio3.Formats.Compositing;

namespace OpenSimsStudio3.Formats.Textures;

/// <summary>The DDS pixel formats OSS3 writes: the ones TS3 uses for CAS textures.</summary>
public enum DdsFormat
{
    /// <summary>BC1: RGB with 1-bit (punch-through) alpha, 8 bytes per 4×4 block.</summary>
    Dxt1,
    /// <summary>BC3: RGB plus interpolated 8-bit alpha, 16 bytes per 4×4 block.</summary>
    Dxt5,
    /// <summary>Uncompressed 32-bit, stored B, G, R, A.</summary>
    A8R8G8B8,
}

/// <summary>
/// Encodes RGBA8 (straight alpha, rows top to bottom) to DDS with a full mip chain down to 1×1: DXT1 (BC1), DXT5 (BC3) or
/// uncompressed A8R8G8B8. Written from Microsoft's public DDS and BC1-3 format documentation.
/// <para>
/// Block compression is a deterministic range fit along each block's principal colour axis, refined by least squares,
/// with an exhaustive single-colour fit for flat blocks; candidates are scored with the palette exactly as
/// <see cref="DdsDecoder" /> rebuilds it. Mips are box-filtered per channel without alpha weighting, because TS3 masks
/// and control maps store data, not colour, in RGB and A.
/// </para>
/// </summary>
public static class DdsEncoder
{
    const int MaxExtent = 16384;

    /// <summary>The DDS file: header, then every mip from the top down.</summary>
    /// <exception cref="ArgumentException">Bad extents or a pixel buffer that doesn't match them.</exception>
    public static byte[] Encode(DdsImage image, DdsFormat format)
    {
        if (image.Width is < 1 or > MaxExtent || image.Height is < 1 or > MaxExtent)
            throw new ArgumentException($"image extents {image.Width}x{image.Height} out of range", nameof(image));
        if (image.Rgba.Length != image.Width * image.Height * 4) throw new ArgumentException("pixel buffer size does not match the extents", nameof(image));

        var chain = MipChain(image.Width, image.Height);
        var total = 128L;
        foreach (var (w, h) in chain) total += MipBytes(w, h, format);
        var file = new byte[total];
        WriteHeader(file, image.Width, image.Height, chain.Count, format);

        var offset = 128;
        var level = image;
        for (var i = 0; i < chain.Count; i++)
        {
            if (i > 0) level = Downsample(level, chain[i].Width, chain[i].Height);
            var size = MipBytes(level.Width, level.Height, format);
            var dst = file.AsSpan(offset, size);
            switch (format)
            {
                case DdsFormat.A8R8G8B8: WriteArgb(level, dst); break;
                default: WriteBlocks(level, dst, format); break;
            }
            offset += size;
        }
        return file;
    }

    /// <summary>Every mip's size, top first, halving (rounded down, never below 1) until both sides are 1.</summary>
    public static IReadOnlyList<(int Width, int Height)> MipChain(int width, int height)
    {
        var chain = new List<(int, int)> { (width, height) };
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            chain.Add((width, height));
        }
        return chain;
    }

    /// <summary>Bytes one mip takes in the file.</summary>
    public static int MipBytes(int width, int height, DdsFormat format) => format switch
    {
        DdsFormat.A8R8G8B8 => width * height * 4,
        DdsFormat.Dxt1 => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8,
        _ => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16,
    };

    /// <summary>
    /// Box filter to a smaller size: each target pixel averages (rounded) the source pixels whose index range maps to
    /// it, per channel. A 2:1 step averages 2×2; odd sides give the last target pixel the leftover row or column.
    /// </summary>
    public static DdsImage Downsample(DdsImage source, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var y0 = y * source.Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * source.Height / height);
            for (var x = 0; x < width; x++)
            {
                var x0 = x * source.Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * source.Width / width);
                long r = 0, g = 0, b = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var s = (sy * source.Width + sx) * 4;
                        r += source.Rgba[s];
                        g += source.Rgba[s + 1];
                        b += source.Rgba[s + 2];
                        a += source.Rgba[s + 3];
                    }
                long n = (y1 - y0) * (x1 - x0);
                var d = (y * width + x) * 4;
                rgba[d] = (byte)((r + n / 2) / n);
                rgba[d + 1] = (byte)((g + n / 2) / n);
                rgba[d + 2] = (byte)((b + n / 2) / n);
                rgba[d + 3] = (byte)((a + n / 2) / n);
            }
        }
        return new DdsImage(width, height, rgba);
    }

    static void WriteHeader(byte[] f, int width, int height, int mips, DdsFormat format)
    {
        const uint Caps = 0x1, Height = 0x2, Width = 0x4, Pitch = 0x8, PixelFormat = 0x1000, MipMapCount = 0x20000, LinearSize = 0x80000;
        const uint FlagAlphaPixels = 0x1, FlagFourCC = 0x4, FlagRgb = 0x40;
        const uint CapsComplex = 0x8, CapsTexture = 0x1000, CapsMipMap = 0x400000;
        var d = f.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(d, 0x20534444); // "DDS "
        BinaryPrimitives.WriteInt32LittleEndian(d[4..], 124);
        var compressed = format != DdsFormat.A8R8G8B8;
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], Caps | Height | Width | PixelFormat | MipMapCount | (compressed ? LinearSize : Pitch));
        BinaryPrimitives.WriteInt32LittleEndian(d[12..], height);
        BinaryPrimitives.WriteInt32LittleEndian(d[16..], width);
        BinaryPrimitives.WriteInt32LittleEndian(d[20..], compressed ? MipBytes(width, height, format) : width * 4);
        BinaryPrimitives.WriteInt32LittleEndian(d[28..], mips);
        BinaryPrimitives.WriteInt32LittleEndian(d[76..], 32);
        if (compressed)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d[80..], FlagFourCC);
            BinaryPrimitives.WriteUInt32LittleEndian(d[84..], format == DdsFormat.Dxt1 ? 0x31545844u : 0x35545844u); // "DXT1" / "DXT5"
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d[80..], FlagRgb | FlagAlphaPixels);
            BinaryPrimitives.WriteInt32LittleEndian(d[88..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(d[92..], 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(d[96..], 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(d[100..], 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(d[104..], 0xFF000000);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(d[108..], CapsTexture | (mips > 1 ? CapsComplex | CapsMipMap : 0));
    }

    static void WriteArgb(DdsImage image, Span<byte> dst)
    {
        var src = image.Rgba;
        for (var p = 0; p < image.Width * image.Height; p++)
        {
            dst[p * 4] = src[p * 4 + 2];
            dst[p * 4 + 1] = src[p * 4 + 1];
            dst[p * 4 + 2] = src[p * 4];
            dst[p * 4 + 3] = src[p * 4 + 3];
        }
    }

    static void WriteBlocks(DdsImage image, Span<byte> dst, DdsFormat format)
    {
        var bw = Math.Max(1, (image.Width + 3) / 4);
        var bh = Math.Max(1, (image.Height + 3) / 4);
        var blockBytes = format == DdsFormat.Dxt1 ? 8 : 16;
        Span<int> texels = stackalloc int[64];
        Span<bool> valid = stackalloc bool[16];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                for (var i = 0; i < 16; i++)
                {
                    int px = bx * 4 + (i & 3), py = by * 4 + (i >> 2);
                    valid[i] = px < image.Width && py < image.Height;
                    for (var c = 0; c < 4; c++) texels[i * 4 + c] = valid[i] ? image.Rgba[(py * image.Width + px) * 4 + c] : 0;
                }
                var block = dst.Slice((by * bw + bx) * blockBytes, blockBytes);
                if (format == DdsFormat.Dxt1) Bc1.Encode(texels, valid, block, punchThrough: true);
                else
                {
                    Bc3Alpha.Encode(texels, valid, block[..8]);
                    Bc1.Encode(texels, valid, block[8..], punchThrough: false);
                }
            }
    }

    /// <summary>BC1 colour block: two RGB565 end points, sixteen 2-bit indices.</summary>
    static class Bc1
    {
        /// <summary>For each 8-bit value, the 5- or 6-bit end-point pair whose 2/3 point (four-colour mode) or midpoint (three-colour mode) is closest.</summary>
        static readonly (byte A, byte B)[] Third5 = SingleTable(5, half: false), Third6 = SingleTable(6, half: false);
        static readonly (byte A, byte B)[] Half5 = SingleTable(5, half: true), Half6 = SingleTable(6, half: true);

        static (byte, byte)[] SingleTable(int bits, bool half)
        {
            var table = new (byte, byte)[256];
            var max = (1 << bits) - 1;
            for (var v = 0; v < 256; v++)
            {
                var best = int.MaxValue;
                for (var a = 0; a <= max; a++)
                    for (var b = 0; b <= max; b++)
                    {
                        int ea = Expand(a, bits), eb = Expand(b, bits);
                        var value = half ? (ea + eb) / 2 : (2 * ea + eb) / 3;
                        // Ties go to the closest pair, which keeps the other palette entries near the value.
                        var error = Math.Abs(value - v) * 1024 + Math.Abs(a - b);
                        if (error < best) { best = error; table[v] = ((byte)a, (byte)b); }
                    }
            }
            return table;
        }

        static int Expand(int v, int bits) => bits == 5 ? (v << 3) | (v >> 2) : (v << 2) | (v >> 4);

        static ushort Pack(int r5, int g6, int b5) => (ushort)((r5 << 11) | (g6 << 5) | b5);

        static ushort Quantize(double r, double g, double b) =>
            Pack(Q(r, 31), Q(g, 63), Q(b, 31));

        static int Q(double v, int max) => (int)Math.Clamp(Math.Round(Math.Clamp(v, 0, 255) * max / 255.0, MidpointRounding.AwayFromZero), 0, max);

        public static void Encode(ReadOnlySpan<int> texels, ReadOnlySpan<bool> valid, Span<byte> block, bool punchThrough)
        {
            Span<bool> transparent = stackalloc bool[16];
            Span<bool> opaque = stackalloc bool[16];
            var anyTransparent = false;
            var count = 0;
            for (var i = 0; i < 16; i++)
            {
                transparent[i] = punchThrough && valid[i] && texels[i * 4 + 3] < 128;
                opaque[i] = valid[i] && !transparent[i];
                anyTransparent |= transparent[i];
                if (opaque[i]) count++;
            }
            // Punch-through alpha needs three-colour mode (c0 <= c1), whose index 3 is transparent black.
            var threeColour = anyTransparent;

            if (count == 0)
            {
                Write(block, 0, 0, threeColour ? 0xFFFFFFFFu : 0u);
                return;
            }

            var bestError = long.MaxValue;
            ushort best0 = 0, best1 = 0;
            uint bestIndices = 0;
            void Try(ushort c0, ushort c1, Span<bool> o, Span<bool> t, ReadOnlySpan<int> px)
            {
                Order(ref c0, ref c1, threeColour);
                var error = Indices(px, o, t, c0, c1, out var indices);
                if (error < bestError) { bestError = error; best0 = c0; best1 = c1; bestIndices = indices; }
            }

            // 1. Flat block: exhaustive per-channel end points around the mean.
            double mr = 0, mg = 0, mb = 0;
            for (var i = 0; i < 16; i++)
                if (opaque[i]) { mr += texels[i * 4]; mg += texels[i * 4 + 1]; mb += texels[i * 4 + 2]; }
            mr /= count; mg /= count; mb /= count;
            int sr = (int)Math.Round(mr), sg = (int)Math.Round(mg), sb = (int)Math.Round(mb);
            var t5 = threeColour ? Half5 : Third5;
            var t6 = threeColour ? Half6 : Third6;
            Try(Pack(t5[sr].A, t6[sg].A, t5[sb].A), Pack(t5[sr].B, t6[sg].B, t5[sb].B), opaque, transparent, texels);
            Try(Quantize(mr, mg, mb), Quantize(mr, mg, mb), opaque, transparent, texels);

            // 2. Range fit along the principal axis of the opaque texels' colours.
            double crr = 0, cgg = 0, cbb = 0, crg = 0, crb = 0, cgb = 0;
            for (var i = 0; i < 16; i++)
            {
                if (!opaque[i]) continue;
                double r = texels[i * 4] - mr, g = texels[i * 4 + 1] - mg, b = texels[i * 4 + 2] - mb;
                crr += r * r; cgg += g * g; cbb += b * b; crg += r * g; crb += r * b; cgb += g * b;
            }
            // Power iteration, started from the covariance row with the largest norm: a fixed start such as (1, 1, 1)
            // can be orthogonal to the axis (a red/blue block) and collapse to zero.
            double ar = crr, ag = crg, ab = crb;
            if (crg * crg + cgg * cgg + cgb * cgb > ar * ar + ag * ag + ab * ab) (ar, ag, ab) = (crg, cgg, cgb);
            if (crb * crb + cgb * cgb + cbb * cbb > ar * ar + ag * ag + ab * ab) (ar, ag, ab) = (crb, cgb, cbb);
            for (var k = 0; k < 8; k++)
            {
                double nr = crr * ar + crg * ag + crb * ab, ng = crg * ar + cgg * ag + cgb * ab, nb = crb * ar + cgb * ag + cbb * ab;
                var len = Math.Max(Math.Abs(nr), Math.Max(Math.Abs(ng), Math.Abs(nb)));
                if (len < 1e-9) break;
                ar = nr / len; ag = ng / len; ab = nb / len;
            }
            var axisLen = ar * ar + ag * ag + ab * ab;
            if (axisLen > 1e-12)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                for (var i = 0; i < 16; i++)
                {
                    if (!opaque[i]) continue;
                    var t = ((texels[i * 4] - mr) * ar + (texels[i * 4 + 1] - mg) * ag + (texels[i * 4 + 2] - mb) * ab) / axisLen;
                    lo = Math.Min(lo, t);
                    hi = Math.Max(hi, t);
                }
                var c0 = Quantize(mr + hi * ar, mg + hi * ag, mb + hi * ab);
                var c1 = Quantize(mr + lo * ar, mg + lo * ag, mb + lo * ab);
                Try(c0, c1, opaque, transparent, texels);

                // 3. Least-squares refinement: solve for the end points that best fit the chosen indices.
                for (var iteration = 0; iteration < 2; iteration++)
                    if (Refit(texels, opaque, best0, best1, bestIndices, threeColour) is { } refit)
                        Try(refit.C0, refit.C1, opaque, transparent, texels);
            }

            Write(block, best0, best1, bestIndices);
        }

        /// <summary>Four-colour mode needs c0 &gt; c1, three-colour mode c0 &lt;= c1; a swap flips the indices' meaning, which <see cref="Indices" /> recomputes.</summary>
        static void Order(ref ushort c0, ref ushort c1, bool threeColour)
        {
            if (threeColour ? c0 > c1 : c0 < c1) (c0, c1) = (c1, c0);
        }

        /// <summary>The palette as <see cref="DdsDecoder" /> builds it (integer division as there).</summary>
        static void Palette(ushort c0, ushort c1, Span<int> pal)
        {
            Unpack(c0, pal[..3]);
            Unpack(c1, pal.Slice(3, 3));
            var four = c0 > c1;
            for (var ch = 0; ch < 3; ch++)
            {
                int a = pal[ch], b = pal[3 + ch];
                pal[6 + ch] = four ? (2 * a + b) / 3 : (a + b) / 2;
                pal[9 + ch] = four ? (a + 2 * b) / 3 : 0;
            }
        }

        static void Unpack(ushort c, Span<int> o)
        {
            o[0] = Expand(c >> 11, 5);
            o[1] = Expand((c >> 5) & 63, 6);
            o[2] = Expand(c & 31, 5);
        }

        /// <summary>Nearest palette entry per texel; transparent texels take index 3 (three-colour mode only). Returns the squared RGB error.</summary>
        static long Indices(ReadOnlySpan<int> px, ReadOnlySpan<bool> opaque, ReadOnlySpan<bool> transparent, ushort c0, ushort c1, out uint indices)
        {
            Span<int> pal = stackalloc int[12];
            Palette(c0, c1, pal);
            // In three-colour mode (c0 <= c1, which a DXT1 block with c0 == c1 also is) index 3 is transparent black.
            var usable = c0 > c1 ? 4 : 3;
            indices = 0;
            long total = 0;
            for (var i = 0; i < 16; i++)
            {
                if (transparent[i]) { indices |= 3u << (2 * i); continue; }
                if (!opaque[i]) continue;
                var best = int.MaxValue;
                var bestIndex = 0;
                for (var e = 0; e < usable; e++)
                {
                    int dr = px[i * 4] - pal[e * 3], dg = px[i * 4 + 1] - pal[e * 3 + 1], db = px[i * 4 + 2] - pal[e * 3 + 2];
                    var error = dr * dr + dg * dg + db * db;
                    if (error < best) { best = error; bestIndex = e; }
                }
                indices |= (uint)bestIndex << (2 * i);
                total += best;
            }
            return total;
        }

        /// <summary>End points minimising the squared error for fixed indices (normal equations of a two-unknown least squares).</summary>
        static (ushort C0, ushort C1)? Refit(ReadOnlySpan<int> px, ReadOnlySpan<bool> opaque, ushort c0, ushort c1, uint indices, bool threeColour)
        {
            var four = c0 > c1;
            double aa = 0, bb = 0, ab = 0;
            Span<double> ax = stackalloc double[3];
            Span<double> bx = stackalloc double[3];
            for (var i = 0; i < 16; i++)
            {
                if (!opaque[i]) continue;
                var e = (int)(indices >> (2 * i)) & 3;
                // Weight of c0 in the texel's palette entry.
                var w = e switch { 0 => 1.0, 1 => 0.0, 2 => four ? 2.0 / 3 : 0.5, _ => four ? 1.0 / 3 : -1 };
                if (w < 0) continue;
                var v = 1 - w;
                aa += w * w; bb += v * v; ab += w * v;
                for (var ch = 0; ch < 3; ch++) { ax[ch] += w * px[i * 4 + ch]; bx[ch] += v * px[i * 4 + ch]; }
            }
            var det = aa * bb - ab * ab;
            if (Math.Abs(det) < 1e-9) return null;
            Span<double> e0 = stackalloc double[3];
            Span<double> e1 = stackalloc double[3];
            for (var ch = 0; ch < 3; ch++)
            {
                e0[ch] = (bb * ax[ch] - ab * bx[ch]) / det;
                e1[ch] = (aa * bx[ch] - ab * ax[ch]) / det;
            }
            return (Quantize(e0[0], e0[1], e0[2]), Quantize(e1[0], e1[1], e1[2]));
        }

        static void Write(Span<byte> block, ushort c0, ushort c1, uint indices)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(block, c0);
            BinaryPrimitives.WriteUInt16LittleEndian(block[2..], c1);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], indices);
        }
    }

    /// <summary>BC3 alpha block: two 8-bit end points, sixteen 3-bit indices.</summary>
    static class Bc3Alpha
    {
        public static void Encode(ReadOnlySpan<int> texels, ReadOnlySpan<bool> valid, Span<byte> block)
        {
            int min = 255, max = 0, innerMin = 255, innerMax = 0;
            for (var i = 0; i < 16; i++)
            {
                if (!valid[i]) continue;
                var a = texels[i * 4 + 3];
                min = Math.Min(min, a);
                max = Math.Max(max, a);
                if (a is > 0 and < 255) { innerMin = Math.Min(innerMin, a); innerMax = Math.Max(innerMax, a); }
            }
            // Eight-value ramp between the extremes (a0 > a1), or six values plus exact 0 and 255 (a0 <= a1).
            var eight = (A0: max, A1: min);
            var six = innerMin <= innerMax ? (A0: innerMin, A1: innerMax) : (A0: min, A1: min);
            var e8 = Indices(texels, valid, eight.A0, eight.A1, out var i8);
            var e6 = Indices(texels, valid, six.A0, six.A1, out var i6);
            var (a0, a1, bits) = e8 <= e6 ? (eight.A0, eight.A1, i8) : (six.A0, six.A1, i6);
            block[0] = (byte)a0;
            block[1] = (byte)a1;
            for (var i = 0; i < 6; i++) block[2 + i] = (byte)(bits >> (8 * i));
        }

        /// <summary>Nearest ramp entry per texel, ramp built as <see cref="DdsDecoder" /> builds it. Returns the squared error.</summary>
        static long Indices(ReadOnlySpan<int> texels, ReadOnlySpan<bool> valid, int a0, int a1, out ulong bits)
        {
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
            bits = 0;
            long total = 0;
            for (var i = 0; i < 16; i++)
            {
                if (!valid[i]) continue;
                var a = texels[i * 4 + 3];
                var best = int.MaxValue;
                var bestIndex = 0;
                for (var e = 0; e < 8; e++)
                {
                    var d = (a - pal[e]) * (a - pal[e]);
                    if (d < best) { best = d; bestIndex = e; }
                }
                bits |= (ulong)bestIndex << (3 * i);
                total += best;
            }
            return total;
        }
    }
}
