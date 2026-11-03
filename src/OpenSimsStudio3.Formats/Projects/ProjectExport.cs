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
/// future OpenTS3-only data. The output path must not be one of the projects' own packages. The checks are
/// <see cref="ExportChecks" />.
/// </summary>
public static class ProjectExport
{
    public static ExportResult Export(IReadOnlyList<string> projectFolders, string outPath) => Export(ExportChecks.Run(projectFolders, outPath));

    /// <summary>Writes the export <paramref name="plan" /> checked, to its path; nothing is written when a check failed with an error.</summary>
    public static ExportResult Export(ExportPlan plan)
    {
        var outPath = plan.OutPath;
        var reads = plan.Sources.Select(s => (s.Name, s.Package)).ToList();
        var problems = plan.Checks.Where(c => c.Severity == CheckSeverity.Error).Select(c => c.Label).ToList();
        if (problems.Count > 0) return new ExportResult(0, reads.Select(r => r.Name).ToList(), problems);

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
        return new ExportResult(count, reads.Select(r => r.Name).ToList(), problems);
    }
}
