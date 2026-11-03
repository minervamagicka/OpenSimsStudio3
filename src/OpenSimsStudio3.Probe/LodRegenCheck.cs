using System.Diagnostics;
using OpenSimsStudio3.Formats.Meshes;

/// <summary>
/// lodregen &lt;package&gt;...: builds 50%, 25% and 12.5% LODs (GeomLodBuilder) of every shaded GEOM in the packages and
/// prints the triangles reached against the target, the time, and whether the result reads back. For checking the
/// simplifier against real items before trying Regenerate in the app. Read-only: nothing is written.
/// </summary>
static class LodRegenCheck
{
    static readonly double[] Ratios = [0.5, 0.25, 0.125];

    public static int Run(string[] paths)
    {
        int geoms = 0, failed = 0, short_ = 0;
        foreach (var path in paths)
        {
            var (package, entries) = GeomLoader.Open(path);
            try
            {
                foreach (var entry in entries)
                {
                    byte[] bytes;
                    using (var s = ((s3pi.Package.Package)package).GetResource(entry.IndexEntry))
                    using (var copy = new MemoryStream()) { s.CopyTo(copy); bytes = copy.ToArray(); }
                    GeomMesh source;
                    try { source = GeomLoader.Read(new MemoryStream(bytes), entry.Key); }
                    catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { failed++; continue; }
                    if (source.Shader == 0 || source.TriangleCount == 0) continue; // morph GEOMs carry deltas, not a shape
                    geoms++;
                    var line = $"{entry.Key}  {source.TriangleCount,6:N0} tris";
                    foreach (var ratio in Ratios)
                    {
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            var made = GeomLodBuilder.Build(bytes, ratio);
                            var ms = watch.Elapsed.TotalMilliseconds;
                            GeomLoader.Read(new MemoryStream(made.Bytes), entry.Key);
                            var flag = made.TrianglesAfter > made.TargetTriangles * 1.1 ? "!" : " ";
                            if (flag == "!") short_++;
                            line += $"  {ratio:P1}: {made.TrianglesAfter,6:N0}/{made.TargetTriangles,6:N0}{flag} {ms,6:N0} ms";
                        }
                        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException)
                        {
                            line += $"  {ratio:P1}: failed ({e.Message})";
                            failed++;
                        }
                    }
                    Console.WriteLine(line);
                }
            }
            finally { s3pi.Package.Package.ClosePackage(0, package); }
        }
        Console.WriteLine($"{geoms} GEOMs; {short_} LODs more than 10% above target (marked !: borders, seams, double-sided parts are kept); {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
