using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Formats.Compositing;

/// <summary>A composed texture: RGBA8 rows top to bottom, with the colour already multiplied by alpha (see CasCompositor).</summary>
public sealed record CompositedTexture(int Width, int Height, byte[] Rgba, string Complate, string Part, TimeSpan Elapsed, IReadOnlyList<ResourceKey> MissingTextures);

/// <summary>
/// Composes a CAS recolour's diffuse from its preset XML by running the steps of its complate template (the
/// texturePart's "diffuse" destination) on the CPU. A preview of what the game builds, not the game's own compositor.
/// Sources and inferred rules: README.md in this folder.
/// </summary>
public static class CasCompositor
{
    public const uint ImageType = 0x00B2D882;

    public static CompositedTexture? ComposeDiffuse(IResourceSource source, string presetXml, int maxSize = 1024) =>
        TryComposeDiffuse(source, presetXml, out var t, out _, maxSize) ? t : null;

    /// <param name="part">The texturePart to compose by its part name (for example "Scalp" or "Face"); null for the first, the part's own.</param>
    public static bool TryComposeDiffuse(IResourceSource source, string presetXml, out CompositedTexture? texture, out string? reason, int maxSize = 1024, string? part = null)
    {
        texture = null;
        reason = null;
        var timer = Stopwatch.StartNew();
        try
        {
            var preset = XElement.Parse(presetXml);
            var root = preset.Name.LocalName == "complate" ? preset : preset.Element("complate") ?? throw new InvalidDataException("preset has no <complate>");
            var run = new Run(source, root.Elements("pattern"));
            var complate = run.Bind(root, root.Elements("value"));
            var (texturePart, partName) = complate.Template.Elements("texturePart")
                .Select(tp => (tp, Name: complate.Attr(tp, "part") ?? ""))
                .FirstOrDefault(tp => part == null || string.Equals(tp.Name, part, StringComparison.OrdinalIgnoreCase));
            if (texturePart == null) { reason = $"{complate.Name} has no texturePart {part}."; return false; }
            var destination = Destination(texturePart, "diffuse");
            if (destination == null) { reason = $"{complate.Name} has no diffuse destination for {partName}."; return false; }
            var steps = destination.Elements("step").ToList();
            var (w, h) = run.TargetSize(complate, steps, maxSize);
            var pixels = run.Execute(complate, steps, w, h);
            var rgba = new byte[w * h * 4];
            for (var i = 0; i < rgba.Length; i++) rgba[i] = ToByte(pixels[i]);
            texture = new CompositedTexture(w, h, rgba, complate.Name, partName, timer.Elapsed, run.Missing);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or System.Xml.XmlException or FormatException or OverflowException)
        {
            reason = e.Message;
            return false;
        }
    }

    static XElement? Destination(XElement texturePart, string name) =>
        texturePart.Elements("destination").FirstOrDefault(d => string.Equals((string?)d.Attribute("textureName"), name, StringComparison.OrdinalIgnoreCase))
        ?? texturePart.Elements("destination").FirstOrDefault(d => d.Attribute("textureName") == null);

    static byte ToByte(float v) => (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);

    /// <summary>A decoded texture as straight-alpha floats 0..1.</summary>
    sealed record Image(int Width, int Height, float[] Px);

    /// <summary>One composition: the template and image caches, the preset's patterns and the textures not found.</summary>
    sealed class Run(IResourceSource source, IEnumerable<XElement> patterns)
    {
        readonly Dictionary<ResourceKey, XElement> _templates = new();
        readonly Dictionary<ResourceKey, Image?> _images = new();
        readonly Dictionary<string, XElement> _patterns = patterns
            .Where(p => p.Attribute("variable") != null)
            .GroupBy(p => (string)p.Attribute("variable")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Image> _fabrics = new(StringComparer.OrdinalIgnoreCase);

        public List<ResourceKey> Missing { get; } = [];

        /// <summary>The template a preset &lt;complate&gt; or &lt;pattern&gt; names by reskey, bound to its values.</summary>
        public Complate Bind(XElement node, IEnumerable<XElement> values)
        {
            var reskey = (string?)node.Attribute("reskey") ?? throw new InvalidDataException($"<{node.Name}> has no reskey");
            var key = ParseKey(reskey) ?? throw new InvalidDataException($"bad reskey {reskey}");
            if (!_templates.TryGetValue(key, out var template))
            {
                var bytes = source.Read(key) ?? throw new InvalidDataException($"complate template {key} not found");
                _templates[key] = template = Complate.ParseTemplate(bytes);
            }
            return new Complate(template, values);
        }

        /// <summary>
        /// The largest texture the enabled steps draw, halved until it fits maxSize; 256 square when they draw none.
        /// </summary>
        public (int W, int H) TargetSize(Complate c, IEnumerable<XElement> steps, int maxSize)
        {
            int w = 0, h = 0;
            foreach (var step in steps.Where(s => c.Bool(s, "enabled", true)))
                if (c.Attr(step, "texture") is { } t && !t.StartsWith("RenderTexture_", StringComparison.Ordinal) && Texture(t) is { } img && img.Width * img.Height > w * h) (w, h) = (img.Width, img.Height);
            if (w == 0) (w, h) = (256, 256);
            while (w > maxSize || h > maxSize) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
            return (w, h);
        }

        public float[] Execute(Complate c, IReadOnlyList<XElement> steps, int w, int h)
        {
            var a = new float[w * h * 4];
            float[]? b = null;
            var target = a;
            foreach (var step in steps)
            {
                if (!c.Bool(step, "enabled", true)) continue;
                var type = (string?)step.Attribute("type") ?? "";
                if (type == "SetTarget")
                {
                    target = c.Attr(step, "renderTarget") == "RenderTarget_B" ? b ??= new float[w * h * 4] : a;
                    continue;
                }
                Source src;
                switch (type)
                {
                    case "ColorFill":
                    {
                        var col = Color(c.Attr(step, "color"));
                        src = (_, _, o) => col.CopyTo(o);
                        break;
                    }
                    case "ChannelSelect":
                    {
                        if (Input(c, step, w, h, a, b) is not { } img) continue;
                        var sel = Vec4(c.Attr(step, "select"), 0);
                        var sample = Sampler(c, step, img, w, h);
                        src = (x, y, o) =>
                        {
                            sample(x, y, o);
                            var v = o[0] * sel[0] + o[1] * sel[1] + o[2] * sel[2] + o[3] * sel[3];
                            o[0] = o[1] = o[2] = o[3] = v;
                        };
                        break;
                    }
                    case "DrawImage":
                    {
                        if (Input(c, step, w, h, a, b) is not { } img) continue;
                        src = Sampler(c, step, img, w, h);
                        break;
                    }
                    case "HSVShift":
                    {
                        if (Input(c, step, w, h, a, b) is not { } img) continue;
                        var shift = Vec4(c.Attr(step, "hsvShift"), 0);
                        var sample = Sampler(c, step, img, w, h);
                        src = (x, y, o) => { sample(x, y, o); HsvShift(o, shift); };
                        break;
                    }
                    case "DrawFabric":
                    {
                        var name = c.Attr(step, "pattern") ?? "";
                        if (Fabric(name, w, h) is not { } img) continue;
                        src = Sampler(c, step, img, w, h);
                        break;
                    }
                    case "CASPickData":
                        continue; // writes pick IDs for the specular map; never part of what the diffuse shows
                    default:
                        throw new NotSupportedException($"Unsupported diffuse operation {type} in {c.Name}.");
                }
                Draw(c, step, target, w, h, src);
            }
            return a;
        }

        delegate void Source(int x, int y, Span<float> rgba);

        /// <summary>
        /// The step's texture: an image by key, or a render target's contents (RenderTexture_A/B, copied so a step may
        /// read the target it draws to); null (skip the step) when the key isn't in the install.
        /// </summary>
        Image? Input(Complate c, XElement step, int w, int h, float[] a, float[]? b)
        {
            var name = c.Attr(step, "texture") ?? throw new InvalidDataException($"{step.Attribute("type")} step has no texture in {c.Name}");
            if (name == "RenderTexture_A") return new Image(w, h, (float[])a.Clone());
            if (name == "RenderTexture_B") return new Image(w, h, b == null ? new float[w * h * 4] : (float[])b.Clone());
            return Texture(name);
        }

        Image? Texture(string value)
        {
            if (ParseKey(value) is not { } key) throw new NotSupportedException($"texture {value} is not a resource key");
            if (_images.TryGetValue(key, out var img)) return img;
            var bytes = source.Read(key);
            if (bytes == null) Missing.Add(key);
            else
            {
                var dds = DdsDecoder.Decode(bytes);
                var px = new float[dds.Rgba.Length];
                for (var i = 0; i < px.Length; i++) px[i] = dds.Rgba[i] / 255f;
                img = new Image(dds.Width, dds.Height, px);
            }
            return _images[key] = img;
        }

        /// <summary>
        /// The preset's pattern for a DrawFabric step, composed once at the target's size (not its own textures' size;
        /// inferred from the oracle's blur, see README.md) and stored as RGBA8 like a texture.
        /// </summary>
        Image? Fabric(string variable, int w, int h)
        {
            if (_fabrics.TryGetValue(variable, out var done)) return done;
            if (!_patterns.TryGetValue(variable, out var node)) return null;
            var pattern = Bind(node, node.Elements("value"));
            var destination = pattern.Template.Elements("texturePart").Select(tp => Destination(tp, "diffuse")).FirstOrDefault(d => d != null)
                              ?? throw new InvalidDataException($"pattern {pattern.Name} has no destination");
            var px = Execute(pattern, destination.Elements("step").ToList(), w, h);
            for (var i = 0; i < px.Length; i++) px[i] = ToByte(px[i]) / 255f;
            return _fabrics[variable] = new Image(w, h, px);
        }

        /// <summary>Samples an image across the target: destRect places it, sourceRect tiles it (wrapping), rotation turns it.</summary>
        static Source Sampler(Complate c, XElement step, Image img, int w, int h)
        {
            var filter = c.Bool(step, "enableFiltering", true);
            var srcRect = Vec4(c.Attr(step, "sourceRect"), 0) is var sr && c.Attr(step, "sourceRect") != null ? sr : [0, 0, 1, 1];
            var dstRect = c.Attr(step, "destRect") is { } dr ? Vec4(dr, 0) : null;
            var angle = c.Float(step, "rotation", 0);
            var (sin, cos) = MathF.SinCos(angle);
            return (x, y, o) =>
            {
                var s = (x + 0.5f) / w;
                var t = (y + 0.5f) / h;
                if (dstRect != null)
                {
                    s = (s - dstRect[0]) / (dstRect[2] - dstRect[0]);
                    t = (t - dstRect[1]) / (dstRect[3] - dstRect[1]);
                }
                if (angle != 0)
                {
                    float ds = s - 0.5f, dt = t - 0.5f;
                    (s, t) = (cos * ds - sin * dt + 0.5f, sin * ds + cos * dt + 0.5f);
                }
                var u = srcRect[0] + s * (srcRect[2] - srcRect[0]);
                var v = srcRect[1] + t * (srcRect[3] - srcRect[1]);
                Sample(img, u, v, filter, o);
            };
        }

        static void Sample(Image img, float u, float v, bool filter, Span<float> o)
        {
            if (!filter)
            {
                var px = Wrap((int)MathF.Floor(u * img.Width), img.Width);
                var py = Wrap((int)MathF.Floor(v * img.Height), img.Height);
                img.Px.AsSpan((py * img.Width + px) * 4, 4).CopyTo(o);
                return;
            }
            var fx = u * img.Width - 0.5f;
            var fy = v * img.Height - 0.5f;
            var x0 = (int)MathF.Floor(fx);
            var y0 = (int)MathF.Floor(fy);
            float ax = fx - x0, ay = fy - y0;
            int xa = Wrap(x0, img.Width), xb = Wrap(x0 + 1, img.Width), ya = Wrap(y0, img.Height), yb = Wrap(y0 + 1, img.Height);
            var p = img.Px;
            for (var ch = 0; ch < 4; ch++)
            {
                var top = p[(ya * img.Width + xa) * 4 + ch] * (1 - ax) + p[(ya * img.Width + xb) * 4 + ch] * ax;
                var bottom = p[(yb * img.Width + xa) * 4 + ch] * (1 - ax) + p[(yb * img.Width + xb) * 4 + ch] * ax;
                o[ch] = top * (1 - ay) + bottom * ay;
            }
        }

        static int Wrap(int i, int n) => ((i % n) + n) % n;

        /// <summary>Writes the step's colour into the target through its mask, blend and colour-write state.</summary>
        void Draw(Complate c, XElement step, float[] target, int w, int h, Source src)
        {
            var blend = c.Bool(step, "enableBlending", false);
            var srcBlend = c.Attr(step, "srcBlend") ?? "One";
            var dstBlend = c.Attr(step, "dstBlend") ?? "Zero";
            var write = ColorWrite(c.Attr(step, "colorWrite"));
            Source? mask = null;
            float[] maskSelect = [0, 0, 0, 1];
            var maskBias = 0f;
            if (c.Attr(step, "mask") is { } maskName && Texture(maskName) is { } maskImg)
            {
                mask = Sampler(c, new XElement("step"), maskImg, w, h);
                maskSelect = Vec4(c.Attr(step, "maskSelect"), 0) is var ms && c.Attr(step, "maskSelect") != null ? ms : maskSelect;
                maskBias = c.Float(step, "maskBias", 0);
            }
            var dstRect = c.Attr(step, "destRect") is { } dr ? Vec4(dr, 0) : null;
            Span<float> s = stackalloc float[4];
            Span<float> m = stackalloc float[4];
            Span<float> fs = stackalloc float[4];
            Span<float> fd = stackalloc float[4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    if (dstRect != null)
                    {
                        float px = (x + 0.5f) / w, py = (y + 0.5f) / h;
                        if (px < dstRect[0] || px >= dstRect[2] || py < dstRect[1] || py >= dstRect[3]) continue;
                    }
                    if (mask != null)
                    {
                        mask(x, y, m);
                        if (m[0] * maskSelect[0] + m[1] * maskSelect[1] + m[2] * maskSelect[2] + m[3] * maskSelect[3] + maskBias < 0) continue;
                    }
                    src(x, y, s);
                    var d = target.AsSpan((y * w + x) * 4, 4);
                    if (blend)
                    {
                        Factor(srcBlend, s, d, fs);
                        Factor(dstBlend, s, d, fd);
                        for (var ch = 0; ch < 4; ch++) s[ch] = Math.Clamp(s[ch] * fs[ch] + d[ch] * fd[ch], 0f, 1f);
                    }
                    for (var ch = 0; ch < 4; ch++)
                        if ((write & (1 << ch)) != 0) d[ch] = s[ch];
                }
        }

        static void Factor(string blend, ReadOnlySpan<float> s, ReadOnlySpan<float> d, Span<float> f)
        {
            for (var ch = 0; ch < 4; ch++)
                f[ch] = blend switch
                {
                    "Zero" => 0,
                    "One" => 1,
                    "SrcColor" => s[ch],
                    "InvSrcColor" => 1 - s[ch],
                    "SrcAlpha" => s[3],
                    "InvSrcAlpha" => 1 - s[3],
                    "DestAlpha" => d[3],
                    "InvDestAlpha" => 1 - d[3],
                    "DestColor" => d[ch],
                    "InvDestColor" => 1 - d[ch],
                    "SrcAlphaSat" => ch == 3 ? 1 : Math.Min(s[3], 1 - d[3]),
                    _ => throw new NotSupportedException($"blend {blend}"),
                };
        }

        /// <summary>Channel bits R=1 G=2 B=4 A=8 from names like "Color", "Alpha", "All", "Red".</summary>
        static int ColorWrite(string? text)
        {
            if (text == null) return 15;
            var bits = 0;
            foreach (var part in text.Split([',', '|', ' '], StringSplitOptions.RemoveEmptyEntries))
                bits |= part.ToLowerInvariant() switch
                {
                    "red" => 1, "green" => 2, "blue" => 4, "alpha" => 8, "color" => 7, "all" => 15, "none" => 0,
                    _ => throw new NotSupportedException($"colorWrite {part}"),
                };
            return bits;
        }

        static float[] Color(string? text)
        {
            var v = Complate.Floats(text ?? "");
            return [v.ElementAtOrDefault(0), v.ElementAtOrDefault(1), v.ElementAtOrDefault(2), v.Length > 3 ? v[3] : 1];
        }

        static float[] Vec4(string? text, float fill)
        {
            var v = Complate.Floats(text ?? "");
            return [.. Enumerable.Range(0, 4).Select(i => i < v.Length ? v[i] : fill)];
        }

        /// <summary>
        /// Black-box result (README.md): before the shift each channel moves 8/255 away from mid-grey (up above 0.5,
        /// down below), even for a zero shift.
        /// </summary>
        const float HsvBias = 8f / 255f;

        /// <summary>
        /// HSVShift: hue added (wrapping), saturation scaled by (1 + shift) and value added (both clamped), after
        /// the channel bias above.
        /// </summary>
        static void HsvShift(Span<float> o, float[] shift)
        {
            for (var ch = 0; ch < 3; ch++) o[ch] = Math.Clamp(o[ch] + (o[ch] > 0.5f ? HsvBias : o[ch] < 0.5f ? -HsvBias : 0), 0, 1);
            float r = o[0], g = o[1], b = o[2];
            var max = MathF.Max(r, MathF.Max(g, b));
            var min = MathF.Min(r, MathF.Min(g, b));
            var delta = max - min;
            var hue = 0f;
            if (delta > 0)
            {
                hue = max == r ? (g - b) / delta : max == g ? 2 + (b - r) / delta : 4 + (r - g) / delta;
                hue /= 6;
                if (hue < 0) hue += 1;
            }
            var sat = max > 0 ? delta / max : 0;
            hue += shift[0];
            hue -= MathF.Floor(hue);
            sat = Math.Clamp(sat * (1 + shift[1]), 0, 1);
            var val = Math.Clamp(max + shift[2], 0, 1);
            var hh = hue * 6;
            var sector = (int)MathF.Floor(hh) % 6;
            var f = hh - MathF.Floor(hh);
            float p = val * (1 - sat), q = val * (1 - sat * f), t = val * (1 - sat * (1 - f));
            (o[0], o[1], o[2]) = sector switch
            {
                0 => (val, t, p), 1 => (q, val, p), 2 => (p, val, t), 3 => (p, q, val), 4 => (t, p, val), _ => (val, p, q),
            };
        }

        static ResourceKey? ParseKey(string text)
        {
            var p = text.Trim().Split(':');
            if (p.Length != 4 || p[0] != "key") return null;
            return uint.TryParse(p[1], NumberStyles.HexNumber, null, out var t) && uint.TryParse(p[2], NumberStyles.HexNumber, null, out var g)
                   && ulong.TryParse(p[3], NumberStyles.HexNumber, null, out var i) ? new ResourceKey(t, g, i) : null;
        }
    }
}
