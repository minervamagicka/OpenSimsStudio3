using System.Numerics;
using System.Reflection;
using OpenSimsStudio3.Formats.Install;
using GEOM = meshExpImp.ModelBlocks.GEOM;
using s3pi.GenericRCOLResource;

/// <summary>
/// haircolor [package…]: the vertex COLOR stream of SimHair GEOMs (shader 0x84FD7152). Without packages it reads every
/// live GEOM of the install; with packages, only theirs (read-only). Reports presence, default values (the engine fills
/// a missing stream with ARGB 0x800080FF), the alpha spread, how RGB decoded as a
/// -1..1 vector lines up with the tangent, the normal, the UV-V gradient and the outward direction, and whether s3pi
/// re-writes each GEOM byte for byte.
/// </summary>
static class HairColor
{
    const uint SimHair = 0x84FD7152, Geom = 0x015A1849, Default = 0x800080FF;
    static readonly FieldInfo Argb = typeof(GEOM.ColorElement).GetField("argb", BindingFlags.NonPublic | BindingFlags.Instance)!;

    sealed class Stats
    {
        public int StrictFail, Unreadable, AllWhite, AllZero, AllOneOther, Varying;
        public readonly List<string> ZeroExamples = new();
        public string? Current;
        public int Geoms, WithColor, Vertices, DefaultValues, RoundTripSame, RoundTripDiffer, Unit;
        public readonly int[] Alpha = new int[256];
        public double CosTangent, CosNormal, CosUvV, CosOutward, AbsTangent, AbsUvV, AbsOutward;
        public int Correlated;
        public readonly Dictionary<uint, int> TopValues = new();
        public int Grey;
        // Per-GEOM Pearson correlation of the grey level with V, height, distance from the centre and the U coordinate.
        public double RV, RY, RDist, RU, RVSq, RShell, RShellSq; public int RGeoms;
        public readonly int[] Steps = new int[21];
    }

    public static int Run(string[] packages)
    {
        if (packages is ["--dump", var dir]) return Dump(dir);
        if (packages is ["--compare", var a, var b]) return Compare(a, b);
        // --lenient: s3pi copies its checking flag into statics on first use, so it must be off before any parse.
        if (packages is ["--lenient", ..]) { SetChecking(false); packages = packages[1..]; }
        var stats = new Stats();
        if (packages.Length == 0)
        {
            using var index = ResourceIndex.Build(GameInstall.Find(null)!);
            var where = (System.Collections.IDictionary)typeof(ResourceIndex).GetField("_where", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(index)!;
            foreach (ResourceKey key in where.Keys)
                if (key.Type == Geom && index.Read(key) is { } bytes) Measure(bytes, stats);
        }
        else
            foreach (var path in packages)
            {
                var package = s3pi.Package.Package.OpenPackage(0, path, false);
                foreach (var e in package.GetResourceList.Where(e => e.ResourceType == Geom && !e.IsDeleted))
                {
                    stats.Current = $"{Path.GetFileName(path)} {e.ResourceGroup:X8}:{e.Instance:X16}";
                    using var s = ((s3pi.Package.Package)package).GetResource(e);
                    using var copy = new MemoryStream();
                    s.CopyTo(copy);
                    // CC GEOMs often carry an inconsistent MTNF length (GEOM Tools); count them, then read leniently.
                    try { Measure(copy.ToArray(), stats); }
                    catch (Exception)
                    {
                        stats.StrictFail++;
                        SetChecking(false);
                        try { Measure(copy.ToArray(), stats); }
                        catch (Exception error) { stats.Unreadable++; if (stats.Unreadable <= 2) Console.WriteLine($"  unreadable {stats.Current}: {(error.InnerException ?? error).Message}"); }
                        SetChecking(true);
                    }
                }
                s3pi.Package.Package.ClosePackage(0, package);
            }

        Console.WriteLine($"SimHair GEOMs {stats.Geoms}, with a COLOR stream {stats.WithColor}; vertices with colour {stats.Vertices}, " +
                          $"equal to the engine default 0x{Default:X8}: {stats.DefaultValues}");
        Console.WriteLine($"GEOMs with colour by content: all white {stats.AllWhite}, all zero {stats.AllZero}, one other value {stats.AllOneOther}, varying {stats.Varying}");
        if (stats.ZeroExamples.Count > 0) Console.WriteLine("  all-zero examples: " + string.Join("; ", stats.ZeroExamples));
        if (stats.StrictFail > 0) Console.WriteLine($"GEOMs failing s3pi's strict parse: {stats.StrictFail} (read leniently; unreadable even then: {stats.Unreadable})");
        Console.WriteLine($"s3pi re-write of GEOMs with colour: {stats.RoundTripSame} byte-identical, {stats.RoundTripDiffer} different");
        if (stats.Vertices == 0) return 0;
        Console.WriteLine($"grey (R=G=B): {100.0 * stats.Grey / stats.Vertices:F2}% of coloured vertices");
        var g = Math.Max(1, stats.RGeoms);
        Console.WriteLine($"per-GEOM correlation of the grey level (mean r over {stats.RGeoms} GEOMs with varying grey): V {stats.RV / g:F3} (mean r^2 {stats.RVSq / g:F3}), " +
                          $"U {stats.RU / g:F3}, height {stats.RY / g:F3}, distance from centre {stats.RDist / g:F3}");
        Console.WriteLine($"  shell depth (1 = outermost in its direction): mean r {stats.RShell / g:F3}, mean r^2 {stats.RShellSq / g:F3}");
        Console.WriteLine($"grey levels on 5% steps: {100.0 * stats.Steps.Sum() / stats.Vertices:F1}% of vertices; by step: " +
            string.Join(" ", Enumerable.Range(0, 21).Where(k => stats.Steps[k] > 0).Select(k => $"{k * 5}%:{100.0 * stats.Steps[k] / stats.Vertices:F1}")));
        Console.WriteLine($"RGB as a -1..1 vector: {100.0 * stats.Unit / stats.Vertices:F1}% have length 0.9-1.1");
        var n = Math.Max(1, stats.Correlated);
        Console.WriteLine($"mean cos (signed | absolute) of the RGB vector with: tangent {stats.CosTangent / n:F3} | {stats.AbsTangent / n:F3}; " +
                          $"normal {stats.CosNormal / n:F3}; UV-V gradient {stats.CosUvV / n:F3} | {stats.AbsUvV / n:F3}; outward {stats.CosOutward / n:F3} | {stats.AbsOutward / n:F3}  (n={stats.Correlated})");
        Console.WriteLine("alpha histogram (value: share): " + string.Join(" ", Enumerable.Range(0, 256).Where(a => stats.Alpha[a] * 200 > stats.Vertices)
            .Select(a => $"{a}:{100.0 * stats.Alpha[a] / stats.Vertices:F1}%")));
        Console.WriteLine("most common ARGB values: " + string.Join(" ", stats.TopValues.OrderByDescending(kv => kv.Value).Take(8)
            .Select(kv => $"{kv.Key:X8}x{kv.Value}")));
        return 0;
    }

    static void SetChecking(bool on) =>
        typeof(s3pi.Settings.Settings).GetField("checking", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, on);

    static void Measure(byte[] bytes, Stats stats)
    {
        var rcol = new GenericRCOLResource(0, new MemoryStream(bytes));
        if (rcol.ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().FirstOrDefault() is not { } geom || (uint)geom.Shader != SimHair) return;
        stats.Geoms++;
        var verts = geom.VertexData;
        if (verts.Count == 0 || !verts[0].Vertex.OfType<GEOM.ColorElement>().Any()) return;
        stats.WithColor++;
        var again = new MemoryStream();
        rcol.Stream.CopyTo(again);
        if (again.ToArray().AsSpan().SequenceEqual(bytes)) stats.RoundTripSame++; else stats.RoundTripDiffer++;

        var count = verts.Count;
        var distinct = verts.SelectMany(v => v.Vertex.OfType<GEOM.ColorElement>()).Select(c => (uint)Argb.GetValue(c)!).Distinct().ToList();
        if (distinct is [0xFFFFFFFF]) stats.AllWhite++;
        else if (distinct is [0u]) { stats.AllZero++; if (stats.ZeroExamples.Count < 6 && stats.Current != null) stats.ZeroExamples.Add(stats.Current); }
        else if (distinct.Count == 1) stats.AllOneOther++;
        else stats.Varying++;
        var pos = new Vector3[count]; var nrm = new Vector3[count]; var tan = new Vector3[count]; var uv = new Vector2[count]; var col = new uint[count];
        for (var i = 0; i < count; i++)
        {
            foreach (var el in verts[i].Vertex)
                switch (el)
                {
                    case GEOM.TangentNormalElement t: tan[i] = new Vector3(t.X, t.Y, t.Z); break;
                    case GEOM.NormalElement m: nrm[i] = new Vector3(m.X, m.Y, m.Z); break;
                    case GEOM.PositionElement p: pos[i] = new Vector3(p.X, p.Y, p.Z); break;
                    case GEOM.UVElement u when uv[i] == default: uv[i] = new Vector2(u.U, u.V); break;
                    case GEOM.ColorElement c: col[i] = (uint)Argb.GetValue(c)!; break;
                }
        }
        // Per-vertex direction of increasing V (the strand direction along a hair card), from the triangles.
        var dv = new Vector3[count];
        var idx = geom.Faces.SelectMany(f => new[] { (int)f.VertexDataIndex0, (int)f.VertexDataIndex1, (int)f.VertexDataIndex2 }).ToArray();
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            if (a >= count || b >= count || c >= count) continue;
            var e1 = pos[b] - pos[a]; var e2 = pos[c] - pos[a];
            var d1 = uv[b] - uv[a]; var d2 = uv[c] - uv[a];
            var det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f) continue;
            var bitangent = (e2 * d1.X - e1 * d2.X) / det;
            dv[a] += bitangent; dv[b] += bitangent; dv[c] += bitangent;
        }
        var centre = pos.Aggregate(Vector3.Zero, (s, p) => s + p) / count;
        var grey = col.Select(c => (double)((c >> 16) & 0xFF)).ToArray();
        static double Pearson(double[] a, double[] b)
        {
            double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (var k = 0; k < a.Length; k++) { sab += (a[k] - ma) * (b[k] - mb); saa += (a[k] - ma) * (a[k] - ma); sbb += (b[k] - mb) * (b[k] - mb); }
            return saa < 1e-9 || sbb < 1e-9 ? double.NaN : sab / Math.Sqrt(saa * sbb);
        }
        var rv = Pearson(grey, uv.Select(x => (double)x.Y).ToArray());
        if (!double.IsNaN(rv))
        {
            stats.RV += rv; stats.RVSq += rv * rv; stats.RGeoms++;
            stats.RY += Pearson(grey, pos.Select(x => (double)x.Y).ToArray());
            stats.RDist += Pearson(grey, pos.Select(x => (double)(x - centre).Length()).ToArray());
            stats.RU += Pearson(grey, uv.Select(x => (double)x.X).ToArray());
            // Shell depth: radius over the largest radius in the same direction bin (12 x 6 around the centre); 1 = outermost.
            int Bin(Vector3 d) { var a = MathF.Atan2(d.Z, d.X); var e = MathF.Asin(Math.Clamp(d.Y / Math.Max(d.Length(), 1e-6f), -1, 1));
                return (int)((a + MathF.PI) / (2 * MathF.PI) * 11.999f) * 6 + (int)((e + MathF.PI / 2) / MathF.PI * 5.999f); }
            var outer = new float[72];
            foreach (var q in pos) { var d = q - centre; var b = Bin(d); outer[b] = Math.Max(outer[b], d.Length()); }
            var shell = pos.Select(q => { var d = q - centre; var o = outer[Bin(d)]; return o > 0 ? (double)(d.Length() / o) : 1.0; }).ToArray();
            var rs = Pearson(grey, shell);
            if (!double.IsNaN(rs)) { stats.RShell += rs; stats.RShellSq += rs * rs; }
        }
        for (var i = 0; i < count; i++)
        {
            stats.Vertices++;
            var argb = col[i];
            if (argb == Default) stats.DefaultValues++;
            if (((argb >> 16) & 0xFF) == ((argb >> 8) & 0xFF) && ((argb >> 8) & 0xFF) == (argb & 0xFF)) stats.Grey++;
            var level = ((argb >> 16) & 0xFF) / 255.0 * 20;
            if (Math.Abs(level - Math.Round(level)) < 0.06) stats.Steps[(int)Math.Round(level)]++;
            stats.Alpha[argb >> 24]++;
            stats.TopValues[argb] = stats.TopValues.GetValueOrDefault(argb) + 1;
            var v = new Vector3(((argb >> 16) & 0xFF) / 127.5f - 1, ((argb >> 8) & 0xFF) / 127.5f - 1, (argb & 0xFF) / 127.5f - 1);
            if (v.Length() is > 0.9f and < 1.1f) stats.Unit++;
            if (v.LengthSquared() < 1e-4f || tan[i].LengthSquared() < 1e-8f || dv[i].LengthSquared() < 1e-12f) continue;
            var vn = Vector3.Normalize(v);
            double Cos(Vector3 w) => w.LengthSquared() < 1e-12f ? 0 : Vector3.Dot(vn, Vector3.Normalize(w));
            var ct = Cos(tan[i]); var cu = Cos(dv[i]); var co = Cos(pos[i] - centre);
            stats.CosTangent += ct; stats.AbsTangent += Math.Abs(ct);
            stats.CosNormal += Cos(nrm[i]);
            stats.CosUvV += cu; stats.AbsUvV += Math.Abs(cu);
            stats.CosOutward += co; stats.AbsOutward += Math.Abs(co);
            stats.Correlated++;
        }
    }

    // --dump <dir>: writes three install SimHair GEOMs whose COLOR stream varies, as raw .simgeom (RCOL) files, for tool round trips.
    static int Dump(string dir)
    {
        Directory.CreateDirectory(dir);
        using var index = ResourceIndex.Build(GameInstall.Find(null)!);
        var where = (System.Collections.IDictionary)typeof(ResourceIndex).GetField("_where", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(index)!;
        var written = 0;
        foreach (ResourceKey key in where.Keys)
        {
            if (key.Type != Geom || written >= 3 || index.Read(key) is not { } bytes) continue;
            var rcol = new GenericRCOLResource(0, new MemoryStream(bytes));
            if (rcol.ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().FirstOrDefault() is not { } geom || (uint)geom.Shader != SimHair) continue;
            var colours = geom.VertexData.SelectMany(v => v.Vertex.OfType<GEOM.ColorElement>()).Select(c => (uint)Argb.GetValue(c)!).Distinct().Count();
            if (colours < 6) continue;
            File.WriteAllBytes(Path.Combine(dir, $"{key.Instance:X16}.simgeom"), bytes);
            Console.WriteLine($"{key}: {geom.VertexData.Count} vertices, {colours} distinct colours");
            written++;
        }
        return 0;
    }

    // --compare <a.simgeom> <b.simgeom>: per-vertex COLOR comparison (same vertex order assumed), plus whole-file equality.
    static int Compare(string a, string b)
    {
        // Tool output may carry inconsistent length fields (GEOM Tools' MTNF); read leniently, as the game does not use them here.
        typeof(s3pi.Settings.Settings).GetField("checking", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        uint[] Colours(string path, out int count)
        {
            var geom = new GenericRCOLResource(0, File.OpenRead(path)).ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().First();
            count = geom.VertexData.Count;
            return geom.VertexData.Select(v => v.Vertex.OfType<GEOM.ColorElement>().Select(c => (uint)Argb.GetValue(c)!).DefaultIfEmpty(0xDEADBEEF).First()).ToArray();
        }
        var ca = Colours(a, out var na); var cb = Colours(b, out var nb);
        var same = 0; var maxDelta = 0; var shown = 0;
        for (var i = 0; i < Math.Min(na, nb); i++)
        {
            if (ca[i] == cb[i]) { same++; continue; }
            for (var sh = 0; sh < 32; sh += 8) maxDelta = Math.Max(maxDelta, Math.Abs((int)((ca[i] >> sh) & 0xFF) - (int)((cb[i] >> sh) & 0xFF)));
            if (shown++ < 4) Console.WriteLine($"  vertex {i}: {ca[i]:X8} -> {cb[i]:X8}");
        }
        // Geometry: largest per-vertex change of each float element kind (same vertex order assumed).
        GEOM Load(string path) => new GenericRCOLResource(0, File.OpenRead(path)).ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().First();
        var ga = Load(a); var gb = Load(b);
        var worst = new Dictionary<string, float>();
        for (var i = 0; i < Math.Min(ga.VertexData.Count, gb.VertexData.Count); i++)
        {
            var ea = ga.VertexData[i].Vertex; var eb = gb.VertexData[i].Vertex;
            void Track(string kind, IEnumerable<float> x, IEnumerable<float> y) =>
                worst[kind] = Math.Max(worst.GetValueOrDefault(kind), x.Zip(y, (p, q) => Math.Abs(p - q)).DefaultIfEmpty(0).Max());
            static float[] Xyz(GEOM.PositionElement e) => [e.X, e.Y, e.Z];
            var ta = ea.OfType<GEOM.TangentNormalElement>().FirstOrDefault(); var tb = eb.OfType<GEOM.TangentNormalElement>().FirstOrDefault();
            var na2 = ea.OfType<GEOM.NormalElement>().FirstOrDefault(); var nb2 = eb.OfType<GEOM.NormalElement>().FirstOrDefault();
            var pa = ea.OfType<GEOM.PositionElement>().First(e => e is not GEOM.NormalElement and not GEOM.TangentNormalElement);
            var pb = eb.OfType<GEOM.PositionElement>().First(e => e is not GEOM.NormalElement and not GEOM.TangentNormalElement);
            Track("position", Xyz(pa), Xyz(pb));
            if (na2 != null && nb2 != null) Track("normal", Xyz(na2), Xyz(nb2));
            if (ta != null && tb != null) Track("tangent", Xyz(ta), Xyz(tb));
            var ua = ea.OfType<GEOM.UVElement>().ToList(); var ub = eb.OfType<GEOM.UVElement>().ToList();
            for (var k = 0; k < Math.Min(ua.Count, ub.Count); k++) Track($"uv{k}", [ua[k].U, ua[k].V], [ub[k].U, ub[k].V]);
        }
        Console.WriteLine("  largest change: " + string.Join(", ", worst.Select(kv => $"{kv.Key} {kv.Value:G3}")));
        Console.WriteLine($"{Path.GetFileName(b)}: vertices {na} -> {nb}; colour identical on {same} of {Math.Min(na, nb)}; largest byte change {maxDelta}; " +
                          $"stream present after: {!cb.Contains(0xDEADBEEF)}; whole file identical: {File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b))}");
        return 0;
    }
}
