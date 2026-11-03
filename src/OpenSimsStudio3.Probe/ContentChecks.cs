using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

/// <summary>
/// checks &lt;project folder…&gt;: the export window's checks of the projects' content (flags, mesh, bones, morphs, LODs,
/// UVs, textures) against the installed game, one line each with severity, editor place and tooltip. Reads only.
/// </summary>
static class ContentChecks
{
    public static int Run(string[] folders)
    {
        using var game = ResourceIndex.Build(GameInstall.Find(null)!);
        var checks = ExportChecks.Content(folders, game);
        foreach (var c in checks)
        {
            var where = c.Target.Kind switch
            {
                CheckTargetKind.MeshGroup => $"group {c.Target.Lod}/{c.Target.Group}",
                CheckTargetKind.Lod => $"LOD {c.Target.Lod}",
                CheckTargetKind.Recolour => $"recolour {c.Target.Recolour + 1}",
                CheckTargetKind.None => "",
                var kind => kind.ToString(),
            };
            Console.WriteLine($"{c.Severity,-7} {c.Area,-8} {c.Label}{(where.Length > 0 ? $"  [{where}]" : "")}{(c.Tip != null ? $"  ({c.Tip})" : "")}");
        }
        var errors = checks.Count(c => c.Severity == CheckSeverity.Error);
        Console.WriteLine($"{checks.Count} lines, {errors} errors, {checks.Count(c => c.Severity == CheckSeverity.Warning)} warnings");
        return errors == 0 ? 0 : 1;
    }
}
