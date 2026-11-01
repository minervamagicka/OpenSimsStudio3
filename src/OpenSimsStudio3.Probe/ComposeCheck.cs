using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Install;

/// <summary>
/// compose &lt;casp-name&gt; &lt;outDir&gt; [maxSize] [--templates]: composes each preset's diffuse of a game CAS part with
/// OSS3's compositor and writes preset&lt;i&gt;.png. outDir must be under OSS3_LOCAL_DATA (a folder outside the repo):
/// these images are derived from EA textures and stay local. --templates also writes each preset's XML and the complate templates it names.
/// compose --survey &lt;perComplate&gt; [maxSize]: composes up to perComplate presets of every complate name in the install at
/// a small size and counts results and failure reasons; writes nothing.
/// </summary>
static class ComposeCheck
{
    const uint CaspType = 0x034AEECB;
    // EA-derived images may only go under this folder (outside the repo); the probe refuses to write without it.
    static readonly string? LocalRoot = Environment.GetEnvironmentVariable("OSS3_LOCAL_DATA");

    public static int Run(string[] args)
    {
        if (args[0] == "--survey") return Survey(int.Parse(args[1]), args.Length > 2 ? int.Parse(args[2]) : 64);
        var name = args[0];
        var outDir = Path.GetFullPath(args[1]);
        var maxSize = args.Length > 2 && int.TryParse(args[2], out var m) ? m : 1024;
        var dumpTemplates = args.Contains("--templates");
        if (string.IsNullOrEmpty(LocalRoot) || !outDir.StartsWith(Path.GetFullPath(LocalRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"compose: set OSS3_LOCAL_DATA to a folder outside the repo; outDir must be under it (EA-derived images stay local).");
            return 2;
        }

        var install = GameInstall.Find(null) ?? throw new InvalidOperationException("no TS3 install found");
        var timer = Stopwatch.StartNew();
        using var index = ResourceIndex.Build(install);
        var catalog = CasCatalog.Load(install);
        Console.WriteLine($"install indexed + catalog loaded in {timer.Elapsed.TotalSeconds:F1}s");
        var item = catalog.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item == null) { Console.Error.WriteLine($"compose: no CAS part named {name}"); return 1; }
        var bytes = index.Read(new ResourceKey(CaspType, item.Group, item.Instance)) ?? throw new InvalidDataException("CASP has no live copy");
        var casp = new CASPartResource.CASPartResource(0, new MemoryStream(bytes));
        Directory.CreateDirectory(outDir);
        var failed = 0;
        for (var i = 0; i < casp.Presets.Count; i++)
        {
            var xml = casp.Presets[i].XmlFile.ReadToEnd();
            timer.Restart();
            if (!CasCompositor.TryComposeDiffuse(index, xml, out var texture, out var reason, maxSize))
            {
                failed++;
                Console.WriteLine($"preset {i}: FAILED after {timer.Elapsed.TotalMilliseconds:F0} ms: {reason}");
                continue;
            }
            var path = Path.Combine(outDir, $"{name}_preset{i}.png");
            WritePng(path, texture!.Width, texture.Height, texture.Rgba);
            var opaque = 0;
            for (var p = 3; p < texture.Rgba.Length; p += 4) if (texture.Rgba[p] > 0) opaque++;
            Console.WriteLine($"preset {i}: complate={texture.Complate} part={texture.Part} size={texture.Width}x{texture.Height} " +
                              $"compose_ms={texture.Elapsed.TotalMilliseconds:F0} covered={100.0 * opaque / (texture.Width * texture.Height):F1}% " +
                              $"missing_textures={texture.MissingTextures.Count} out={path}");
            foreach (var key in texture.MissingTextures) Console.WriteLine($"    missing {key}");
            foreach (var c in OpenSimsStudio3.Formats.Cas.PresetModel.Parse(xml).Channels.Where(c => c.Enabled))
                Console.WriteLine($"    {c.Variable}: {c.PatternName} color={(c.Color == null ? "-" : string.Join(",", c.Color.Select(v => v.ToString("0.00"))))}");
        }

        if (dumpTemplates)
        {
            // The preset XML and every complate template it names (the part's complate and each pattern's).
            var reskey = new System.Text.RegularExpressions.Regex("reskey=\"key:0333406C:([0-9A-Fa-f]{8}):([0-9A-Fa-f]{16})\"");
            var written = new HashSet<ulong>();
            for (var i = 0; i < casp.Presets.Count; i++)
            {
                var xml = casp.Presets[i].XmlFile.ReadToEnd();
                File.WriteAllText(Path.Combine(outDir, $"{name}_preset{i}.xml"), xml);
                foreach (System.Text.RegularExpressions.Match hit in reskey.Matches(xml))
                {
                    var key = new ResourceKey(0x0333406C, Convert.ToUInt32(hit.Groups[1].Value, 16), Convert.ToUInt64(hit.Groups[2].Value, 16));
                    if (written.Add(key.Instance) && index.Read(key) is { } template)
                        File.WriteAllBytes(Path.Combine(outDir, $"template_{key.Instance:X16}.xml"), template);
                }
            }
        }
        Console.WriteLine($"COMPOSE_PROBE part={name} presets={casp.Presets.Count} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    static int Survey(int perComplate, int maxSize)
    {
        var install = GameInstall.Find(null) ?? throw new InvalidOperationException("no TS3 install found");
        using var index = ResourceIndex.Build(install);
        var catalog = CasCatalog.Load(install);
        var complateName = new System.Text.RegularExpressions.Regex("<complate name=\"([^\"]*)\"");
        var tried = new Dictionary<string, (int Ok, int Failed, double Ms)>();
        var reasons = new Dictionary<(string Complate, string Reason), (int Count, string Example)>();
        var digits = new System.Text.RegularExpressions.Regex(@"[0-9A-F]{8}:[0-9A-F]{8}:[0-9A-F]{16}|\d+(\.\d+)?");
        var timer = Stopwatch.StartNew();
        foreach (var item in catalog.Items)
        {
            if (index.Read(new ResourceKey(CaspType, item.Group, item.Instance)) is not { } bytes) continue;
            CASPartResource.CASPartResource casp;
            try { casp = new CASPartResource.CASPartResource(0, new MemoryStream(bytes)); }
            catch (Exception) { continue; }
            for (var i = 0; i < casp.Presets.Count; i++)
            {
                var xml = casp.Presets[i].XmlFile.ReadToEnd();
                var complate = complateName.Match(xml) is { Success: true } m ? m.Groups[1].Value : "(none)";
                var row = tried.GetValueOrDefault(complate);
                if (row.Ok + row.Failed >= perComplate) continue;
                var started = timer.Elapsed.TotalMilliseconds;
                var ok = CasCompositor.TryComposeDiffuse(index, xml, out _, out var reason, maxSize);
                row.Ms += timer.Elapsed.TotalMilliseconds - started;
                if (ok) row.Ok++;
                else
                {
                    row.Failed++;
                    var pattern = digits.Replace(reason ?? "", "#");
                    var r = reasons.GetValueOrDefault((complate, pattern));
                    reasons[(complate, pattern)] = (r.Count + 1, r.Example ?? $"{item.Name}#{i}: {reason}");
                }
                tried[complate] = row;
            }
        }
        foreach (var (complate, row) in tried.OrderByDescending(t => t.Value.Ok + t.Value.Failed))
            Console.WriteLine($"{complate,-28} ok={row.Ok,5} failed={row.Failed,5} avg_ms={row.Ms / Math.Max(1, row.Ok + row.Failed):F0}");
        foreach (var ((complate, _), (count, example)) in reasons.OrderByDescending(r => r.Value.Count))
            Console.WriteLine($"  {count,5} x {complate}: {example}");
        Console.WriteLine($"COMPOSE_SURVEY complates={tried.Count} ok={tried.Values.Sum(t => t.Ok)} failed={tried.Values.Sum(t => t.Failed)} max_size={maxSize}");
        return 0;
    }

    /// <summary>Minimal RGBA8 PNG: one IDAT, filter 0 on each row, zlib from System.IO.Compression.</summary>
    static void WritePng(string path, int width, int height, byte[] rgba)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgba, y * width * 4, width * 4);
            }
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", []);
    }

    static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(typed));
        output.Write(word);
    }

    static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
