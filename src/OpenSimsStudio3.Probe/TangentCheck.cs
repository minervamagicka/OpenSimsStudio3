using System.Numerics;
using OpenSimsStudio3.Formats.Install;
using s3pi.GenericRCOLResource;
using GEOM = meshExpImp.ModelBlocks.GEOM;

/// <summary>
/// tangents dump &lt;dir&gt; [perShader]: writes install GEOMs of several shaders as .simgeom (EA's own data, keep under _local).
/// tangents compare &lt;original dir&gt; &lt;round-tripped dir&gt;: for every pair, relates EA's stored tangent and the
/// re-exported one to the UV basis computed from the mesh (dP/du "U-tangent", dP/dv "V-tangent"): share of vertices
/// where each is +/- U or +/- V (|cos| &gt; 0.9), and EA vs re-export (same / flipped / other).
/// </summary>
static class TangentCheck
{
    public static int Run(string[] args)
    {
        typeof(s3pi.Settings.Settings).GetField("checking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, false);
        return args switch
        {
            ["dump", var dir, ..] => Dump(dir, args.Length > 2 ? int.Parse(args[2]) : 2),
            ["compare", var a, var b] => Compare(a, b),
            ["apply", var a, var b, var outDir] => Apply(a, b, outDir),
            _ => 2,
        };
    }

    static int Dump(string dir, int perShader)
    {
        Directory.CreateDirectory(dir);
        using var index = ResourceIndex.Build(GameInstall.Find(null)!);
        var where = (System.Collections.IDictionary)typeof(ResourceIndex).GetField("_where", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(index)!;
        var counts = new Dictionary<uint, int>();
        var n = 0;
        foreach (ResourceKey key in where.Keys)
        {
            if (key.Type != 0x015A1849 || n >= 40 || index.Read(key) is not { } bytes) continue;
            GEOM geom;
            try { geom = new GenericRCOLResource(0, new MemoryStream(bytes)).ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().First(); } catch { continue; }
            var shader = (uint)geom.Shader;
            if (shader == 0 || geom.VertexData.Count < 300 || !geom.VertexData[0].Vertex.OfType<GEOM.TangentNormalElement>().Any()) continue;
            if (counts.GetValueOrDefault(shader) >= perShader) continue;
            counts[shader] = counts.GetValueOrDefault(shader) + 1;
            File.WriteAllBytes(Path.Combine(dir, $"{shader:X8}_{key.Instance:X16}.simgeom"), bytes);
            n++;
        }
        Console.WriteLine($"wrote {n} GEOMs over {counts.Count} shaders: {string.Join(" ", counts.Select(kv => $"{kv.Key:X8}x{kv.Value}"))}");
        return 0;
    }

    sealed class Tally { public int N, PU, NU, PV, NV, Other; public void Add(Vector3 t, Vector3 u, Vector3 v) { N++; var cu = Vector3.Dot(t, u); var cv = Vector3.Dot(t, v);
        if (cu > 0.9f) PU++; else if (cu < -0.9f) NU++; else if (cv > 0.9f) PV++; else if (cv < -0.9f) NV++; else Other++; }
        public override string ToString() => N == 0 ? "-" : $"+U {P(PU)} -U {P(NU)} +V {P(PV)} -V {P(NV)} other {P(Other)}"; string P(int k) => $"{100.0 * k / N:F0}%"; }

    static int Compare(string originals, string roundTripped)
    {
        var eaAll = new Tally(); var gtAll = new Tally();
        int same = 0, flipped = 0, other = 0;
        foreach (var path in Directory.GetFiles(originals, "*.simgeom").OrderBy(p => p))
        {
            var other2 = Path.Combine(roundTripped, Path.GetFileName(path));
            if (!File.Exists(other2)) continue;
            var a = Load(path); var b = Load(other2);
            if (a.VertexData.Count != b.VertexData.Count) { Console.WriteLine($"{Path.GetFileName(path)}: vertex count differs, skipped"); continue; }
            var (pos, uv, tan) = Read(a); var (_, _, tanB) = Read(b);
            var (du, dv) = UvBasis(a, pos, uv);
            var ea = new Tally(); var gt = new Tally();
            int s = 0, f = 0, o = 0;
            for (var i = 0; i < pos.Length; i++)
            {
                if (du[i].LengthSquared() < 1e-12f || dv[i].LengthSquared() < 1e-12f || tan[i].LengthSquared() < 1e-8f || tanB[i].LengthSquared() < 1e-8f) continue;
                var u = Vector3.Normalize(du[i]); var v = Vector3.Normalize(dv[i]);
                var ta = Vector3.Normalize(tan[i]); var tb = Vector3.Normalize(tanB[i]);
                ea.Add(ta, u, v); gt.Add(tb, u, v); eaAll.Add(ta, u, v); gtAll.Add(tb, u, v);
                var c = Vector3.Dot(ta, tb);
                if (c > 0.9f) s++; else if (c < -0.9f) f++; else o++;
            }
            same += s; flipped += f; other += o;
            Console.WriteLine($"{Path.GetFileName(path),-40} EA: {ea}   re-export: {gt}   EA vs re-export: same {s}, flipped {f}, other {o}");
        }
        Console.WriteLine($"ALL  EA: {eaAll}");
        Console.WriteLine($"ALL  re-export: {gtAll}");
        Console.WriteLine($"ALL  EA vs re-export: same {same}, flipped {flipped}, other {other}");
        return 0;
    }

    // apply <original dir> <edited dir> <out dir>: runs OSS3's re-import tangent rules on each pair and writes the results.
    static int Apply(string originals, string edited, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var path in Directory.GetFiles(originals, "*.simgeom").OrderBy(p => p))
        {
            var e = Path.Combine(edited, Path.GetFileName(path));
            if (!File.Exists(e)) continue;
            var (bytes, restored, horse) = OpenSimsStudio3.Formats.Projects.MeshExchange.ApplyTangentRules(File.ReadAllBytes(path), File.ReadAllBytes(e));
            File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(path)), bytes);
            Console.WriteLine($"{Path.GetFileName(path),-40} vertices {Load(e).VertexData.Count}: restored {restored}, SimHorse along V {horse}");
        }
        return 0;
    }

    static GEOM Load(string path) => new GenericRCOLResource(0, File.OpenRead(path)).ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().First();

    static (Vector3[] Pos, Vector2[] Uv, Vector3[] Tan) Read(GEOM g)
    {
        var n = g.VertexData.Count;
        var pos = new Vector3[n]; var uv = new Vector2[n]; var tan = new Vector3[n];
        for (var i = 0; i < n; i++)
            foreach (var el in g.VertexData[i].Vertex)
                switch (el)
                {
                    case GEOM.TangentNormalElement t: tan[i] = new Vector3(t.X, t.Y, t.Z); break;
                    case GEOM.NormalElement: break;
                    case GEOM.PositionElement p: pos[i] = new Vector3(p.X, p.Y, p.Z); break;
                    case GEOM.UVElement u when uv[i] == default: uv[i] = new Vector2(u.U, u.V); break;
                }
        return (pos, uv, tan);
    }

    /// <summary>Per-vertex dP/du and dP/dv accumulated over its triangles.</summary>
    static (Vector3[] DU, Vector3[] DV) UvBasis(GEOM g, Vector3[] pos, Vector2[] uv)
    {
        var du = new Vector3[pos.Length]; var dv = new Vector3[pos.Length];
        foreach (var f in g.Faces)
        {
            int a = f.VertexDataIndex0, b = f.VertexDataIndex1, c = f.VertexDataIndex2;
            if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;
            var e1 = pos[b] - pos[a]; var e2 = pos[c] - pos[a];
            var d1 = uv[b] - uv[a]; var d2 = uv[c] - uv[a];
            var det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f) continue;
            var t = (e1 * d2.Y - e2 * d1.Y) / det;
            var bt = (e2 * d1.X - e1 * d2.X) / det;
            foreach (var k in new[] { a, b, c }) { du[k] += t; dv[k] += bt; }
        }
        return (du, dv);
    }
}
