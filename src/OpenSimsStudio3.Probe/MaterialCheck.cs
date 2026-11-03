using OpenSimsStudio3.Formats.Materials;
using OpenSimsStudio3.Formats.Meshes;

/// <summary>
/// materials &lt;package&gt;... [--show]: reads every GEOM's material (GeomMaterial) and checks, per GEOM, that writing
/// it back unedited gives the original bytes, and that an edit (first float + 1, then back) also gives them back.
/// Prints a tally of shaders and of unknown hashes; --show lists every GEOM's shader and parameters. Read-only.
/// </summary>
static class MaterialCheck
{
    public static int Run(string[] args)
    {
        var show = args.Contains("--show");
        int geoms = 0, none = 0, failed = 0, mismatched = 0;
        var shaders = new Dictionary<string, int>();
        var unknown = new Dictionary<uint, int>();
        foreach (var path in args.Where(a => a != "--show"))
        {
            var (package, entries) = GeomLoader.Open(path);
            try
            {
                foreach (var entry in entries)
                {
                    byte[] bytes;
                    using (var s = ((s3pi.Package.Package)package).GetResource(entry.IndexEntry))
                    using (var copy = new MemoryStream()) { s.CopyTo(copy); bytes = copy.ToArray(); }
                    geoms++;
                    GeomMaterial? m;
                    try { m = GeomMaterial.Read(bytes); }
                    catch (Exception e) { failed++; Console.WriteLine($"{entry.Key}: read failed: {e.GetType().Name}: {e.Message}"); continue; }
                    if (m == null) { none++; continue; }
                    shaders[m.ShaderLabel] = shaders.GetValueOrDefault(m.ShaderLabel) + 1;
                    foreach (var p in m.Parameters.Where(p => p.Name == null)) unknown[p.Field] = unknown.GetValueOrDefault(p.Field) + 1;
                    if (show)
                    {
                        Console.WriteLine($"{entry.Key} {m.ShaderLabel}");
                        foreach (var p in m.Parameters) Console.WriteLine($"    {p.Label,-28} {p.Kind,-10} {p.ValueText}");
                    }
                    try
                    {
                        if (!m.Write(bytes).AsSpan().SequenceEqual(bytes)) { mismatched++; Console.WriteLine($"{entry.Key}: unedited write differs"); continue; }
                        if (m.Parameters.FirstOrDefault(p => p.IsFloat) is { } f)
                        {
                            var before = f.Floats;
                            f.SetFloat(0, before[0] + 1);
                            var edited = m.Write(bytes);
                            f.Floats = before;
                            var reverted = GeomMaterial.Read(edited)!;
                            reverted.Find(f.Field)!.Floats = before;
                            if (!reverted.Write(edited).AsSpan().SequenceEqual(bytes)) { mismatched++; Console.WriteLine($"{entry.Key}: edit then revert differs"); }
                        }
                    }
                    catch (Exception e) { failed++; Console.WriteLine($"{entry.Key}: write failed: {e.GetType().Name}: {e.Message}"); }
                }
            }
            finally { s3pi.Package.Package.ClosePackage(0, package); }
        }
        Console.WriteLine($"{geoms} GEOMs: {geoms - none - failed - mismatched} materials byte-exact, {none} without material (shader 0), {mismatched} differ, {failed} failed");
        foreach (var (name, count) in shaders.OrderByDescending(s => s.Value)) Console.WriteLine($"  shader {name}: {count}");
        foreach (var (hash, count) in unknown.OrderByDescending(s => s.Value)) Console.WriteLine($"  unknown parameter 0x{hash:X8}: {count}");
        return mismatched == 0 && failed == 0 ? 0 : 1;
    }
}
