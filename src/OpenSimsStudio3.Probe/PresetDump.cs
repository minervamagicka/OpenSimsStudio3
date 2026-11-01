using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

/// <summary>presets &lt;project&gt; &lt;outDir&gt;: writes each recolour's preset XML (EA data; keep outDir under _local) and a summary.</summary>
static class PresetDump
{
    public static int Run(string folder, string outDir)
    {
        using var game = ResourceIndex.Build(GameInstall.Find(null)!);
        var session = ProjectSession.Open(folder, game);
        Directory.CreateDirectory(outDir);
        for (var i = 0; i < session.Part.Presets.Count; i++)
        {
            var xml = session.Part.Presets[i].XmlFile.ReadToEnd();
            File.WriteAllText(Path.Combine(outDir, $"preset{i}.xml"), xml);
            Console.WriteLine($"preset {i}: id {session.Part.Presets[i].Unknown1}, {xml.Length} chars");
        }
        Console.WriteLine("TGI list:");
        for (var i = 0; i < session.Part.TGIBlocks.Count; i++) Console.WriteLine($"  [{i}] {ResourceKey.Of(session.Part.TGIBlocks[i])}");
        Console.WriteLine($"diffuse1 {string.Join(",", session.Part.Diffuse1Indexes)} specular1 {string.Join(",", session.Part.Specular1Indexes)} diffuse2 {string.Join(",", session.Part.Diffuse2Indexes)} specular2 {string.Join(",", session.Part.Specular2Indexes)}");
        return 0;
    }
}
