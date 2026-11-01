using System.Numerics;
using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using s3pi.GenericRCOLResource;

/// <summary>
/// bgeo &lt;part-name&gt;... [--census]: decodes the four BGEO morphs of each named CAS part against every VPXY LOD and
/// prints coverage and plausibility (fat outward, thin inward, magnitudes), plus the error against the EA morph GEOM
/// (named partpart_lodN_morph, FNV-32 of the lowercased name, group of the base GEOM) where one exists. --census parses
/// every BGEO the catalog reaches and reports header constants and decode failures. Read-only.
/// </summary>
static class BgeoCheck
{
    const uint Caspt = 0x034AEECB, Vpxy = 0x736884F1, Geom = 0x015A1849;
    static readonly BlendGeometry.Morph[] Morphs = Enum.GetValues<BlendGeometry.Morph>();

    public static int Run(string[] args)
    {
        var install = GameInstall.Find(null)!;
        var catalog = CasCatalog.Load(install);
        using var game = ResourceIndex.Build(install);
        var failures = 0;
        foreach (var name in args.Where(a => !a.StartsWith("--")))
        {
            var item = catalog.Items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item == null) { Console.WriteLine($"{name}: not in catalog"); failures++; continue; }
            failures += Part(game, item);
        }
        if (args.Contains("--census")) failures += Census(game, catalog);
        Console.WriteLine($"BGEO_PROBE failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    static int Part(ResourceIndex game, CasItem item)
    {
        var casp = new CASPartResource.CASPartResource(0, new MemoryStream(game.Read(new ResourceKey(Caspt, item.Group, item.Instance))!));
        var lods = Lods(game, casp);
        Console.WriteLine($"== {item.Name} ({item.Category}, ages {item.Ages}, {item.Genders}): VPXY LODs " +
                          string.Join(", ", lods.Select(l => $"{l.Id}:{l.Mesh.Positions.Length}v/{l.Mesh.VertexIds.Distinct().Count()}ids")));
        var failures = 0;
        foreach (var morph in Morphs)
        {
            if (BlendGeometry.Find(game, casp, morph) is not { } key) { Console.WriteLine($"  {morph,-7} no BGEO"); continue; }
            var parsed = BlendGeometry.Parse(game.Read(key)!);
            var blocks = string.Join(" ", parsed.Entries.Select(e => $"ag={e.AgeGender:X} region={e.Region:X} [" +
                string.Join(" ", e.Lods.Select(b => $"{b.FirstVertexId}+{b.VertexCount}/{b.DeltaCount}")) + "]"));
            Console.WriteLine($"  {morph,-7} {key} v=0x{parsed.Version:X} {blocks}");
            foreach (var (id, group, mesh) in lods)
            {
                var block = BlendGeometry.LodFor(parsed, mesh);
                if (block == null) { Console.WriteLine($"    lod{id}: no block covers IDs {mesh.VertexIds.DefaultIfEmpty().Min()}..{mesh.VertexIds.DefaultIfEmpty().Max()}"); continue; }
                MorphDeltas d;
                try { d = BlendGeometry.Read(parsed, block.Value, mesh)!; }
                catch (InvalidDataException e) { Console.WriteLine($"    lod{id}: DECODE FAILED {e.Message}"); failures++; continue; }
                var b = parsed.Entries[0].Lods[block.Value];
                var denseIds = mesh.VertexIds.Distinct().Count() == b.VertexCount;
                var (min, max) = mesh.Bounds();
                var moved = Enumerable.Range(0, mesh.Positions.Length).Where(v => d.Positions[v].Length() > 0.001f).ToList();
                var outward = moved.Count == 0 ? 0 : moved.Count(v => Vector3.Dot(d.Positions[v], mesh.Normals[v]) > 0) / (double)moved.Count;
                var maxDelta = d.Positions.Max(p => p.Length());
                // Roughness: mean |dP| difference across triangle edges, over the same with the deltas shuffled between
                // vertices. Correctly indexed deltas vary smoothly (well below 1); mis-indexed ones look like the shuffle (about 1).
                var shuffled = d.Positions.ToArray();
                new Random(1).Shuffle(shuffled);
                var rough = EdgeDiff(mesh.Indices, d.Positions) / Math.Max(EdgeDiff(mesh.Indices, shuffled), 1e-12);
                var line = $"    lod{id}->block{block} covered {d.Covered}/{mesh.Positions.Length} ids-dense {denseIds} posDeltas {d.PositionDeltas} nrmDeltas {d.NormalDeltas} " +
                           $"moved>1mm {moved.Count} outward {outward:P0} max|dP| {maxDelta:F4} rough {rough:F2} height {max.Y - min.Y:F3}";
                if (block != id) { line += " BLOCK!=ENTRYID"; failures++; }
                if (d.Covered != mesh.Positions.Length) failures++;
                var morphKey = new ResourceKey(Geom, group, Fnv32($"{item.Name}{item.Name}_lod{id}_{morph}"));
                if (game.Read(morphKey) is { } mb)
                {
                    var m = GeomLoader.Read(new MemoryStream(mb), morphKey.ToString());
                    var sameIds = m.VertexIds.SequenceEqual(mesh.VertexIds);
                    var errP = Enumerable.Range(0, m.Positions.Length).Max(v => (m.Positions[v] - d.Positions[v]).Length());
                    // One normal delta per VertexID: vertices that share an ID (hard edges, UV seams) can differ in the morph GEOM.
                    var shared = mesh.VertexIds.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
                    var unique = Enumerable.Range(0, m.Normals.Length).Where(v => !shared.Contains(mesh.VertexIds[v])).ToList();
                    var errN = unique.Count == 0 ? 0 : unique.Max(v => (m.Normals[v] - d.Normals[v]).Length());
                    var seamOff = Enumerable.Range(0, m.Normals.Length).Count(v => shared.Contains(mesh.VertexIds[v]) && (m.Normals[v] - d.Normals[v]).Length() > 0.0005f);
                    line += $" | morphGEOM ids-equal {sameIds} maxErrP {errP:F5} maxErrN(unique ids) {errN:F5} shared-id normals differing {seamOff}";
                    if (!sameIds || errP > 0.0005f || errN > 0.0005f) failures++;
                }
                Console.WriteLine(line);
            }
        }
        return failures;
    }

    /// <summary>VPXY Entry00 LODs of the part, each the merge of its GEOMs, with the group of the first GEOM.</summary>
    static List<(int Id, uint Group, GeomMesh Mesh)> Lods(ResourceIndex game, CASPartResource.CASPartResource casp)
    {
        var result = new List<(int, uint, GeomMesh)>();
        var vk = casp.TGIBlocks.Select(ResourceKey.Of).FirstOrDefault(k => k.Type == Vpxy);
        if (vk.Type == 0 || game.Read(vk) is not { } vb) return result;
        var vpxy = new GenericRCOLResource(0, new MemoryStream(vb)).ChunkEntries.Select(c => c.RCOLBlock).OfType<VPXY>().First();
        foreach (var lod in vpxy.Entries.OfType<VPXY.Entry00>().GroupBy(e => (int)e.EntryID))
        {
            var keys = lod.SelectMany(e => (IEnumerable<int>)e.TGIIndexes).Select(i => ResourceKey.Of(vpxy.TGIBlocks[i])).Where(k => k.Type == Geom).Distinct().ToList();
            var meshes = keys.Select(k => game.Read(k) is { } b ? GeomLoader.Read(new MemoryStream(b), k.ToString()) : null).OfType<GeomMesh>().ToList();
            if (meshes.Count > 0) result.Add((lod.Key, keys[0].Group, GeomMesh.Merge($"lod{lod.Key}", meshes)));
        }
        return result;
    }

    /// <summary>Every BGEO reachable from catalog CASPs: header constants, block layout and a full decode of each block.</summary>
    static int Census(ResourceIndex game, CasCatalog catalog)
    {
        var seen = new HashSet<ResourceKey>();
        var tally = new SortedDictionary<string, int>();
        var examples = new SortedDictionary<string, List<string>>();
        string current = "";
        void Count(string what)
        {
            tally[what] = tally.GetValueOrDefault(what) + 1;
            if (!examples.TryGetValue(what, out var list)) examples[what] = list = [];
            if (list.Count < 3) list.Add(current);
        }
        var failures = 0;
        foreach (var item in catalog.Items)
        {
            CASPartResource.CASPartResource casp;
            try { casp = new CASPartResource.CASPartResource(0, new MemoryStream(game.Read(new ResourceKey(Caspt, item.Group, item.Instance))!)); }
            catch (Exception) { Count("casp unreadable"); continue; }
            foreach (var morph in Morphs)
            {
                ResourceKey? found;
                try { found = BlendGeometry.Find(game, casp, morph); }
                catch (Exception) { Count("bbln unreadable"); continue; }
                if (found is not { } key) { Count($"no bgeo ({morph})"); continue; }
                if (!seen.Add(key)) continue;
                current = $"{item.Name} {morph} {key}";
                var bytes = game.Read(key)!;
                BlendGeometry.Parsed parsed;
                try { parsed = BlendGeometry.Parse(bytes); }
                catch (InvalidDataException e) { Count($"parse failed: {e.Message}"); failures++; continue; }
                Count($"version 0x{parsed.Version:X}");
                Count($"header +0x0C={BitConverter.ToUInt32(bytes, 12)} +0x18={BitConverter.ToUInt32(bytes, 24)} +0x1C={BitConverter.ToUInt32(bytes, 28)}");
                Count($"entries {parsed.Entries.Length}");
                if (parsed.VertexWords.Length == 0) { Count("empty"); continue; }
                for (var ei = 0; ei < parsed.Entries.Length; ei++)
                {
                    var e = parsed.Entries[ei];
                    Count($"region 0x{e.Region:X} agegender 0x{e.AgeGender:X}");
                    Count("blocks used " + string.Join("", e.Lods.Select(b => b.VertexCount > 0 ? "1" : "0")));
                    for (var l = 0; l < e.Lods.Length; l++)
                    {
                        if (e.Lods[l].VertexCount == 0) continue;
                        var ids = Enumerable.Range(e.Lods[l].FirstVertexId, e.Lods[l].VertexCount).Select(i => (uint)i).ToArray();
                        var fake = new GeomMesh("ids", new Vector3[ids.Length], new Vector3[ids.Length], [], 0, 0) { VertexIds = ids };
                        try { BlendGeometry.Read(parsed, l, fake, ei); Count("block decoded"); }
                        catch (InvalidDataException) { Count("block DECODE FAILED"); failures++; }
                    }
                    for (var l = 1; l < e.Lods.Length; l++)
                        if (e.Lods[l].VertexCount > 0 && e.Lods[l - 1].VertexCount > 0)
                            Count(e.Lods[l].FirstVertexId == e.Lods[l - 1].FirstVertexId + e.Lods[l - 1].VertexCount ? "block ids contiguous with previous" : "block ids NOT contiguous");
                }
            }
        }
        Console.WriteLine($"== census: {seen.Count} distinct BGEOs from {catalog.Items.Count} catalog parts");
        foreach (var (what, n) in tally) Console.WriteLine($"  {n,7}  {what}   e.g. {string.Join("; ", examples[what].Where(e => e.Length > 0))}");
        return failures;
    }

    static double EdgeDiff(uint[] indices, Vector3[] deltas)
    {
        double sum = 0;
        for (var t = 0; t < indices.Length; t += 3)
            for (var k = 0; k < 3; k++) sum += (deltas[indices[t + k]] - deltas[indices[t + (k + 1) % 3]]).Length();
        return sum;
    }

    static uint Fnv32(string s)
    {
        var h = 0x811C9DC5u;
        foreach (var c in s.ToLowerInvariant()) { h *= 0x01000193u; h ^= (byte)c; }
        return h;
    }
}
