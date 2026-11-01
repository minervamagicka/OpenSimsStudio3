using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>What an export wrote, and what stopped it.</summary>
public sealed record ExportResult(int Resources, IReadOnlyList<string> Projects, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Problems.Count == 0;
}

/// <summary>
/// Writes one or more projects into one .package (the user's "merged export", 2026-10-29). Every project's resources go
/// in as they are. Nothing is written when two projects hold the same key, because the game would load only one of them;
/// default replacements of the same game part collide this way too. A merge's target is the strictest of its projects'
/// (Retail before Both before OpenTS3Only); OSS3 resources are all retail-valid today, so the target only gates
/// future OpenTS3-only data. The output path must not be one of the projects' own packages.
/// </summary>
public static class ProjectExport
{
    public static ExportResult Export(IReadOnlyList<string> projectFolders, string outPath)
    {
        var problems = new List<string>();
        var owner = new Dictionary<ResourceKey, string>();
        var reads = new List<(string Project, string Package)>();
        foreach (var folder in projectFolders)
        {
            Project project;
            try { project = Project.Read(folder); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                problems.Add($"{folder}: {error.Message}");
                continue;
            }
            var package = Path.Combine(folder, project.Package);
            if (string.Equals(Path.GetFullPath(package), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{project.Name}: the export would overwrite this project's own package");
                continue;
            }
            reads.Add((project.Name, package));
            var p = s3pi.Package.Package.OpenPackage(0, package, false);
            try
            {
                foreach (var e in p.GetResourceList.Where(e => !e.IsDeleted))
                {
                    var key = ResourceKey.Of(e);
                    if (owner.TryGetValue(key, out var other)) problems.Add($"{key} is in both \"{other}\" and \"{project.Name}\"");
                    else owner[key] = project.Name;
                }
            }
            finally { s3pi.Package.Package.ClosePackage(0, p); }
        }
        if (problems.Count > 0) return new ExportResult(0, reads.Select(r => r.Project).ToList(), problems);

        var output = s3pi.Package.Package.NewPackage(0);
        var count = 0;
        try
        {
            foreach (var (_, path) in reads)
            {
                var p = s3pi.Package.Package.OpenPackage(0, path, false);
                try
                {
                    foreach (var e in p.GetResourceList.Where(e => !e.IsDeleted))
                    {
                        using var s = ((s3pi.Package.Package)p).GetResource(e);
                        var copy = new MemoryStream();
                        s.CopyTo(copy);
                        copy.Position = 0;
                        output.AddResource(e, copy, true)!.Compressed = 0xFFFF;
                        count++;
                    }
                }
                finally { s3pi.Package.Package.ClosePackage(0, p); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            output.SaveAs(outPath);
        }
        finally { s3pi.Package.Package.ClosePackage(0, output); }
        return new ExportResult(count, reads.Select(r => r.Project).ToList(), problems);
    }
}
