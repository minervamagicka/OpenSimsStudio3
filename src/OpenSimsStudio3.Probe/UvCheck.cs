using System.Diagnostics;
using OpenSimsStudio3.Formats.Meshes;

/// <summary>
/// uvs &lt;package&gt;... [--all]: runs the UV checks (UvAnalysis) on every GEOM in the packages and prints the groups with
/// problems (every group with --all), then a tally per kind and the slowest check. For calibrating the tolerances
/// against real items. Read-only.
/// </summary>
static class UvCheck
{
    public static int Run(string[] args)
    {
        var all = args.Contains("--all");
        var tally = new Dictionary<UvIssueKind, int>();
        int geoms = 0, clean = 0, failed = 0;
        var slowest = (Ms: 0.0, Key: "", Triangles: 0);
        foreach (var path in args.Where(a => a != "--all"))
        {
            var (package, entries) = GeomLoader.Open(path);
            try
            {
                foreach (var entry in entries)
                {
                    GeomMesh mesh;
                    try { mesh = GeomLoader.Load(package, entry); }
                    catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { failed++; continue; }
                    geoms++;
                    var watch = Stopwatch.StartNew();
                    var report = UvAnalysis.Analyze(mesh);
                    var ms = watch.Elapsed.TotalMilliseconds;
                    if (ms > slowest.Ms) slowest = (ms, entry.Key, mesh.TriangleCount);
                    if (report.IsClean) clean++;
                    foreach (var issue in report.Issues) tally[issue.Kind] = tally.GetValueOrDefault(issue.Kind) + 1;
                    if (report.IsClean && !all) continue;
                    var issues = report.IsClean ? "clean" : string.Join(", ", report.Issues.Select(i =>
                        $"{i.Kind}{(i.Channel > 0 ? $"@uv{i.Channel + 1}" : "")}={i.Count}{(i.Kind == UvIssueKind.OutOfRange ? $" ({i.Vertices} v)" : "")}"));
                    Console.WriteLine($"{entry.Key} shader=0x{mesh.Shader:X8} tris={mesh.TriangleCount} uvsets={report.Channels} {ms:F1} ms: {issues}");
                }
            }
            finally { s3pi.Package.Package.ClosePackage(0, package); }
        }
        Console.WriteLine($"UV_PROBE geoms={geoms} clean={clean} unreadable={failed} " +
                          string.Join(" ", Enum.GetValues<UvIssueKind>().Select(k => $"{k}={tally.GetValueOrDefault(k)}")) +
                          $" slowest={slowest.Ms:F1}ms ({slowest.Key}, {slowest.Triangles} tris)");
        return 0;
    }
}
