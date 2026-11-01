using System.Diagnostics;
using System.Numerics;
using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Rendering;

/// <summary>
/// thumbs &lt;outDir&gt; &lt;name…&gt;: renders the browser thumbnail of each named CAS part (LOD 0, untextured) to BMP files in
/// outDir (keep it under _local: EA-derived images), with timings. "sample" renders 40 parts across categories.
/// </summary>
static class ThumbCheck
{
    public const int Size = 280;

    public static int Run(string outDir, string[] names)
    {
        var install = GameInstall.Find(null)!;
        var clock = Stopwatch.StartNew();
        using var index = ResourceIndex.Build(install);
        var catalog = CasCatalog.Load(install);
        Console.WriteLine($"index + catalog {clock.ElapsedMilliseconds} ms");
        var items = names is ["sample"]
            ? catalog.Items.GroupBy(i => i.Category).SelectMany(g => g.Where((_, k) => k % Math.Max(1, g.Count() / 5) == 0).Take(5)).ToList()
            : names.Select(n => catalog.Items.First(i => i.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        Directory.CreateDirectory(outDir);
        using var renderer = new OffscreenMeshRenderer { ClearColor = new Vector4(0.047f, 0.063f, 0.086f, 1f) };
        var pixels = new byte[Size * Size * 4];
        int made = 0, none = 0;
        double load = 0, draw = 0;
        foreach (var item in items)
        {
            clock.Restart();
            var mesh = CasMeshes.Lod0(index, new ResourceKey(0x034AEECB, item.Group, item.Instance));
            load += clock.Elapsed.TotalMilliseconds;
            if (mesh == null) { none++; Console.WriteLine($"  {item.Category,-18} {item.Name}: no mesh"); continue; }
            clock.Restart();
            Thumbnails.Draw(renderer, mesh, Size, pixels);
            draw += clock.Elapsed.TotalMilliseconds;
            WriteBmp(Path.Combine(outDir, item.Name + ".bmp"), Size, Size, pixels);
            made++;
            Console.WriteLine($"  {item.Category,-18} {item.Name}: {mesh.Positions.Length} vertices");
        }
        Console.WriteLine($"{made} rendered, {none} without a mesh; mesh load {load / Math.Max(1, made + none):F1} ms, draw {draw / Math.Max(1, made):F1} ms per part");
        return 0;
    }

    // 32-bit BMP, bottom-up rows.
    static void WriteBmp(string path, int width, int height, byte[] bgraTopDown)
    {
        using var w = new BinaryWriter(File.Create(path));
        var row = width * 4;
        w.Write((ushort)0x4D42); w.Write(54 + row * height); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(height); w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(row * height);
        w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        for (var y = height - 1; y >= 0; y--) w.Write(bgraTopDown, y * row, row);
    }
}
