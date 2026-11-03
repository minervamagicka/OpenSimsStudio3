using System.Diagnostics;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Textures;
using OpenSimsStudio3.Formats.Thumbnails;
using OpenSimsStudio3.Rendering;

/// <summary>
/// recolourthumbs &lt;project folder&gt; &lt;outDir&gt; [--store]: renders every recolour's CAS thumbnail as Render
/// does (composited recolour on LOD 0, framed, cropped) and writes the three sizes as PNGs to outDir (keep it under
/// _local: EA-derived when the textures are the game's), named recolour-N-SIZE.png, with the keys they would get.
/// --store also writes them into the project and saves it (the package's .bak keeps the previous one).
/// </summary>
static class RecolourThumbCheck
{
    public static int Run(string folder, string outDir, bool store)
    {
        using var game = ResourceIndex.Build(GameInstall.Find(null)!);
        var session = ProjectSession.Open(folder, game);
        if (session.Lods.Count == 0 || session.Mesh(session.Lods[0]) is not { } mesh) { Console.WriteLine("no LOD 0 mesh"); return 1; }
        Directory.CreateDirectory(outDir);
        using var renderer = new OffscreenMeshRenderer();
        Console.WriteLine($"{session.Project.Name}: CASP {session.CaspKey}, {session.Part.Presets.Count} recolours, renderer {renderer.DeviceName}");
        var ids = CasThumbnails.PresetIds(session);
        for (var i = 0; i < ids.Count; i++)
        {
            var clock = Stopwatch.StartNew();
            var ok = CasCompositor.TryComposeDiffuse(session, session.Part.Presets[i].XmlFile.ReadToEnd(), out var texture, out var reason, 1024);
            var image = ImageResize.CropToContent(Thumbnails.RenderRecolour(renderer, mesh, ok ? texture : null), ThumbnailFraming.Margin);
            var pngs = CasThumbnails.Encode(image);
            foreach (var (size, png) in pngs)
                File.WriteAllBytes(Path.Combine(outDir, $"recolour-{i + 1}-{CasThumbnails.Pixels(size)}.png"), png);
            Console.WriteLine($"  recolour {i + 1} (preset ID {ids[i]}): {(ok ? "textured" : "untextured: " + reason)}, cropped {image.Width} px, " +
                              $"{clock.ElapsedMilliseconds} ms; keys {string.Join(", ", CasThumbnails.KeysFor(session.CaspKey, ids, i))}");
            if (store) CasThumbnails.Set(session, i, image);
        }
        if (store) { session.Save(); Console.WriteLine("stored and saved"); }
        return 0;
    }
}
