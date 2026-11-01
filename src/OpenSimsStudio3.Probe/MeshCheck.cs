using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

/// <summary>
/// mesh export|import &lt;project&gt; &lt;lod&gt; &lt;group&gt; &lt;file.simgeom&gt;: the editor's Edit in Blender halves.
/// import also saves the project, reopens it and reports the group. Writes only the file or the project.
/// </summary>
static class MeshCheck
{
    public static int Run(string mode, string folder, int lodId, int groupIndex, string file)
    {
        if (mode == "import")
            typeof(s3pi.Settings.Settings).GetField("checking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, false);
        using var game = ResourceIndex.Build(GameInstall.Find(null)!);
        var session = ProjectSession.Open(folder, game);
        var group = session.Lods.First(l => l.Id == lodId).Groups[groupIndex];
        if (mode == "export")
        {
            MeshExchange.Export(session, group.Key, file);
            Console.WriteLine($"exported {group.Key} ({group.Vertices} vertices, colour {group.HasVertexColour}) to {file}");
            return 0;
        }
        var result = MeshExchange.Import(session, group.Key, file);
        Console.WriteLine($"imported: vertices {result.VerticesBefore} -> {result.VerticesAfter}, triangles {result.TrianglesBefore} -> {result.TrianglesAfter}, " +
                          $"colour {result.ColourBefore} -> {result.ColourAfter}; warnings: {(result.Warnings.Count == 0 ? "none" : string.Join(" | ", result.Warnings))}");
        session.Save();
        var again = ProjectSession.Open(folder, game);
        var g2 = again.Lods.First(l => l.Id == lodId).Groups[groupIndex];
        Console.WriteLine($"after save and reopen: {g2.Key} {g2.Vertices} vertices, colour {g2.HasVertexColour}, all-zero {g2.VertexColourAllZero}, in project {g2.InProject}");
        return 0;
    }
}
