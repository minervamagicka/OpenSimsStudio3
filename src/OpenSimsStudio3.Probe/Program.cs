using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenSimsStudio3.Formats.Meshes;
using s3pi.Interfaces;
using OpenSimsStudio3.Formats.Validation;
using OpenSimsStudio3.Rendering;

// Command-line checks against a real install. Reads packages only; writes only the output file it is given.
//   geoms <package> [show]                 load every GEOM and report what parsed
//   render <package> <instance|first> <out.bmp>  render one GEOM offscreen with Vulkan
//   roundtrip <casp|geom> <package>...    parse and rewrite every resource of a type, compare bytes
//   uvs <package>... [--all]              run the UV checks on every GEOM; print problems and a tally per kind
return args.Length > 0 ? args[0] switch
{
    "geoms" when args.Length >= 2 => Geoms(args[1], args.Length > 2 ? int.Parse(args[2]) : 10),
    "roundtrip" when args.Length >= 3 => RoundTripCommand(args[1], args[2..]),
    "caspparts" when args.Length >= 3 => Program.CaspParts(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 12),
    "caspcats" when args.Length >= 2 => Program.CaspCats(args[1..]),
    "unknowns" when args.Length >= 2 => UnknownFields.Run(args[1..]),
    "investigate" when args.Length >= 3 => Investigate.Run(args[1], args[2..]),
    "scriptenums" when args.Length >= 3 => ScriptEnums.Run(args[1], args[2]),
    "layouts" when args.Length >= 4 => Layouts.Run(args[1], args[2], args[3..]),
    "install" => InstallReport.Run(args.Length > 1 ? args[1] : null),
    "cascatalog" => CatalogReport.Run(args.Length > 1 ? args[1] : null),
    "clone" when args.Length >= 3 => CloneCheck.Run(args[1], args[2]),
    "newproject" when args.Length >= 3 => ProjectCheck.Run(args[1], args[2]),
    "thumbs" when args.Length >= 3 => ThumbCheck.Run(args[1], args[2..]),
    "vpxy" when args.Length >= 2 => VpxyDump.Run(args[1]),
    "vpxyrt" => VpxyRoundTrip.Run(),
    "haircolor" => HairColor.Run(args[1..]),
    "texrender" when args.Length >= 3 => TextureRenderCheck.Run(args[1], args[2]),
    "presetrt" => PresetRoundTrip.Run(),
    "presets" when args.Length >= 3 => PresetDump.Run(args[1], args[2]),
    "compose" when args.Length >= 3 => ComposeCheck.Run(args[1..]),
    "tangents" when args.Length >= 3 => TangentCheck.Run(args[1..]),
    "mesh" when args.Length >= 6 => MeshCheck.Run(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), args[5]),
    "session" when args.Length >= 2 => SessionCheck.Run(args[1], args.Length > 2 && args[2] == "--show"),
    "export" when args.Length >= 3 => ExportCheck.Run(args[1], args[2..]),
    "sims3pack" when args.Length >= 2 => Sims3PackDump.Run(args[1]),
    "keys" when args.Length >= 2 => PackageKeys.Run(args[1], args.Length > 2 ? args[2] : null),
    "bgeo" when args.Length >= 2 => BgeoCheck.Run(args[1..]),
    "nudes" => NudeParts.Run(),
    "caspdeps" when args.Length >= 2 => CaspDeps.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 3),
    "uvs" when args.Length >= 2 => UvCheck.Run(args[1..]),
    "geomdiff" when args.Length >= 3 => Program.GeomDiff(args[1], ulong.Parse(args[2], NumberStyles.HexNumber)),
    "render" when args.Length >= 4 => Render(args[1], args[2], args[3]),
    _ => Usage(),
} : Usage();

static int Usage()
{
    Console.Error.WriteLine("usage: geoms <package> [show] | render <package> <instance-hex|first> <out.bmp> | roundtrip <casp|geom> <package>...");
    return 2;
}

static int Geoms(string packagePath, int show)
{
    var (package, geoms) = GeomLoader.Open(packagePath);
    int ok = 0, failed = 0;
    foreach (var entry in geoms)
    {
        try
        {
            var mesh = GeomLoader.Load(package, entry);
            if (++ok <= show)
            {
                var (min, max) = mesh.Bounds();
                Console.WriteLine($"{entry.Key} verts={mesh.Positions.Length} tris={mesh.TriangleCount} bones={mesh.BoneCount} shader=0x{mesh.Shader:X8} min={min} max={max}");
            }
        }
        catch (Exception e)
        {
            if (++failed <= 5) Console.WriteLine($"FAILED {entry.Key}: {e.GetType().Name}: {e.Message}");
        }
    }
    Console.WriteLine($"GEOM_PROBE geoms={geoms.Count} parsed={ok} failed={failed}");
    return failed == 0 ? 0 : 1;
}

static int Render(string packagePath, string which, string outPath)
{
    var (package, geoms) = GeomLoader.Open(packagePath);
    GeomMesh? mesh = null;
    if (which == "first")
    {
        // First drawable (shaded) GEOM with a reasonable size; shader 0 GEOMs are morph deltas.
        foreach (var entry in geoms)
        {
            var candidate = GeomLoader.Load(package, entry);
            if (candidate.Shader != 0 && candidate.Positions.Length > 1000) { mesh = candidate; break; }
        }
    }
    else
    {
        var instance = ulong.Parse(which, NumberStyles.HexNumber);
        var entry = geoms.FirstOrDefault(g => g.Instance == instance);
        if (entry != null) mesh = GeomLoader.Load(package, entry);
    }
    if (mesh == null) { Console.Error.WriteLine("GEOM not found"); return 1; }

    const int width = 640, height = 800;
    using var renderer = new OffscreenMeshRenderer();
    renderer.SetMesh(mesh);
    var camera = new OrbitCamera();
    var (min, max) = mesh.Bounds();
    camera.Frame(min, max);
    var pixels = new byte[width * height * 4];
    var timer = Stopwatch.StartNew();
    renderer.Render(width, height, camera.ViewProjection((float)width / height), new Vector3(0.4f, 0.8f, 0.6f), pixels);
    var first = timer.Elapsed.TotalMilliseconds;
    timer.Restart();
    const int frames = 30;
    for (var i = 0; i < frames; i++)
    {
        camera.Orbit(0.05f, 0);
        renderer.Render(width, height, camera.ViewProjection((float)width / height), new Vector3(0.4f, 0.8f, 0.6f), pixels);
    }
    var perFrame = timer.Elapsed.TotalMilliseconds / frames;
    WriteBmp(outPath, width, height, pixels);
    var covered = 0;
    for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2]) covered++;
    Console.WriteLine($"RENDER_PROBE mesh={mesh.Name} verts={mesh.Positions.Length} tris={mesh.TriangleCount} gpu=\"{renderer.DeviceName}\" first_ms={first:F1} frame_ms={perFrame:F2} covered_pixels={covered} out={outPath}");
    return covered > 0 ? 0 : 1;
}

static int RoundTripCommand(string kind, string[] packages)
{
    (uint Type, Func<byte[], RoundTrip.Result> Check) spec = kind switch
    {
        "casp" => (0x034AEECBu, bytes => RoundTrip.Resource(bytes, s => new CASPartResource.CASPartResource(0, s))),
        "geom" => (GeomLoader.GeomType, RoundTrip.Rcol),
        _ => throw new ArgumentException($"unknown kind {kind}; use casp or geom"),
    };
    var failedTotal = 0;
    foreach (var path in packages)
    {
        var package = s3pi.Package.Package.OpenPackage(0, path, false);
        var entries = package.GetResourceList.Where(e => e.ResourceType == spec.Type && !e.IsDeleted).ToList();
        int passed = 0, failed = 0, deletions = 0;
        var failures = new Dictionary<string, int>();
        foreach (var entry in entries)
        {
            byte[] bytes;
            using (var stream = ((s3pi.Package.Package)package).GetResource(entry))
            {
                // Patch packages remove base resources with a deletion marker (no data); nothing to round-trip.
                if (stream == null) { deletions++; continue; }
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }
            var result = spec.Check(bytes);
            if (result.Passed) { passed++; continue; }
            failed++;
            // Group failures by their message without the numbers, so one bug shows up once.
            var pattern = NumberPattern.Replace(result.Detail, "#");
            if (failures.TryGetValue(pattern, out var n)) failures[pattern] = n + 1;
            else
            {
                failures[pattern] = 1;
                Console.WriteLine($"  FAIL {entry.ResourceType:X8}:{entry.ResourceGroup:X8}:{entry.Instance:X16} {result.Detail}");
            }
        }
        foreach (var (pattern, count) in failures.OrderByDescending(f => f.Value).Take(8))
            Console.WriteLine($"  {count,6} x {pattern}");
        Console.WriteLine($"ROUNDTRIP kind={kind} package={Path.GetFileName(path)} resources={entries.Count} passed={passed} failed={failed} deletion_markers={deletions}");
        failedTotal += failed;
    }
    return failedTotal == 0 ? 0 : 1;
}

static void WriteBmp(string path, int width, int height, byte[] bgraTopDown)
{
    using var file = File.Create(path);
    using var w = new BinaryWriter(file);
    var size = width * height * 4;
    w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + size); w.Write(0); w.Write(54);
    w.Write(40); w.Write(width); w.Write(-height); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(size);
    w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
    w.Write(bgraTopDown, 0, size);
}

static partial class Program
{
    // Hex (0x1F0), two-digit byte values (B2) and decimal numbers in failure messages.
    static readonly System.Text.RegularExpressions.Regex NumberPattern = new(@"0x[0-9A-F]+|\b[0-9A-F]{2}\b|\d+");
}

static partial class Program
{
    /// <summary>geomdiff: show where one GEOM's rewritten chunk differs, with the vertex layout for context.</summary>
    internal static int GeomDiff(string packagePath, ulong instance)
    {
        var (package, geoms) = GeomLoader.Open(packagePath);
        var entry = geoms.First(g => g.Instance == instance);
        byte[] bytes;
        using (var stream = ((s3pi.Package.Package)package).GetResource(entry.IndexEntry))
        using (var copy = new MemoryStream()) { stream.CopyTo(copy); bytes = copy.ToArray(); }
        var rcol = new s3pi.GenericRCOLResource.GenericRCOLResource(0, new MemoryStream(bytes));
        var geom = (meshExpImp.ModelBlocks.GEOM)rcol.ChunkEntries[0].RCOLBlock;
        Console.WriteLine($"shader=0x{(uint)geom.Shader:X8} verts={geom.VertexData.Count} formats=" +
            string.Join(",", geom.VertexFormats.Select(f => f.Usage.ToString())));
        var stored = bytes.AsSpan(0x2C).ToArray();
        byte[] written;
        using (var s = geom.UnParse()) written = ((MemoryStream)s).ToArray();
        var diffs = Enumerable.Range(0, Math.Min(stored.Length, written.Length)).Where(i => stored[i] != written[i]).ToList();
        Console.WriteLine($"differing bytes: {diffs.Count}; first at {diffs.FirstOrDefault()}, last at {diffs.LastOrDefault()}");
        foreach (var at in diffs.Take(3))
        {
            var from = Math.Max(0, at - 8);
            Console.WriteLine($"@{at}: stored  {Convert.ToHexString(stored, from, 24)}");
            Console.WriteLine($"@{at}: written {Convert.ToHexString(written, from, 24)}");
        }
        return 0;
    }
}

static partial class Program
{
    /// <summary>caspparts: for CASPs whose name contains a filter, show where the two CAS-part reference slots point.</summary>
    internal static int CaspParts(string packagePath, string filter, int show)
    {
        var package = s3pi.Package.Package.OpenPackage(0, packagePath, false);
        var entries = package.GetResourceList.Where(e => e.ResourceType == 0x034AEECB && !e.IsDeleted).ToList();
        // Name lookup for referenced CASPs, so a key can be shown with the item it points to.
        var names = new Dictionary<ulong, string>();
        var parts = new List<(IResourceIndexEntry Entry, CASPartResource.CASPartResource Casp)>();
        foreach (var e in entries)
        {
            using var s = ((s3pi.Package.Package)package).GetResource(e);
            if (s == null) continue;
            var casp = new CASPartResource.CASPartResource(0, s);
            names[e.Instance] = casp.Unknown1;
            parts.Add((e, casp));
        }
        int shown = 0, withRefs = 0;
        foreach (var (e, casp) in parts)
        {
            string Ref(byte i)
            {
                if (i >= casp.TGIBlocks.Count) return $"index {i} out of range";
                var t = casp.TGIBlocks[i];
                if (t.ResourceType == 0 && t.Instance == 0) return "null";
                var name = t.ResourceType == 0x034AEECB && names.TryGetValue(t.Instance, out var n) ? $" ({n})" : "";
                return $"{t.ResourceType:X8}:{t.ResourceGroup:X8}:{t.Instance:X16}{name}";
            }
            var (r1, r2) = (Ref(casp.CasPart1Index), Ref(casp.CasPart2Index));
            if (r1 != "null" || r2 != "null") withRefs++;
            if (!casp.Unknown1.Contains(filter, StringComparison.OrdinalIgnoreCase) || shown >= show) continue;
            shown++;
            Console.WriteLine($"{casp.Unknown1} [{casp.Clothing}] part1={r1} part2={r2} shoeMaterial={casp.Unknown4}");
        }
        Console.WriteLine($"CASP_PARTS casps={parts.Count} with_any_reference={withRefs}");
        return 0;
    }
}

static partial class Program
{
    /// <summary>caspcats: how many CASPs per species carry each outfit-category bit (bits 0-19), across packages.</summary>
    internal static int CaspCats(string[] packages)
    {
        var counts = new SortedDictionary<string, int[]>();
        var totals = new SortedDictionary<string, int>();
        foreach (var path in packages)
        {
            var package = s3pi.Package.Package.OpenPackage(0, path, false);
            foreach (var e in package.GetResourceList.Where(e => e.ResourceType == 0x034AEECB && !e.IsDeleted))
            {
                using var s = ((s3pi.Package.Package)package).GetResource(e);
                if (s == null) continue;
                var casp = new CASPartResource.CASPartResource(0, s);
                var species = casp.AgeGender.Species.ToString();
                if (!counts.TryGetValue(species, out var row)) counts[species] = row = new int[20];
                totals[species] = totals.GetValueOrDefault(species) + 1;
                var bits = (uint)casp.ClothingCategory;
                for (var b = 0; b < 20; b++) if ((bits & (1u << b)) != 0) row[b]++;
            }
        }
        var names = Enumerable.Range(0, 20).Select(b => Enum.IsDefined(typeof(CASPartResource.ClothingCategoryFlags), 1u << b)
            ? ((CASPartResource.ClothingCategoryFlags)(1u << b)).ToString() : $"bit{b}").ToArray();
        foreach (var (species, row) in counts)
        {
            Console.WriteLine($"{species} ({totals[species]} CASPs):");
            for (var b = 0; b < 20; b++) if (row[b] > 0) Console.WriteLine($"  0x{1u << b:X5} {names[b],-20} {row[b]}");
        }
        return 0;
    }
}
