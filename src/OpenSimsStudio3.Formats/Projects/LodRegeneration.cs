using System.Numerics;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>One regenerated mesh group: its key, LOD 0's size, what it had before and what it has now.</summary>
public sealed record RegeneratedGroup(int Group, ResourceKey Key, int SourceTriangles, int TrianglesBefore, int TrianglesAfter, int TargetTriangles,
    int VerticesAfter);

public sealed record LodRegenerationResult(int Lod, double Ratio, IReadOnlyList<RegeneratedGroup> Groups, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds a lower LOD's mesh groups from the source LOD (the first VPXY LOD, LOD 0, the most detailed) with
/// <see cref="GeomLodBuilder" />, and stores them in the project the way a mesh re-import does (<see cref="ProjectSession.Replace" />,
/// same key, saved with the project). Group n of the LOD is made from group n of LOD 0. The source LOD is never changed,
/// nor is any GEOM it uses. The VPXY is not changed either: a LOD keeps the groups it lists.
/// </summary>
public static class LodRegeneration
{
    /// <summary>The LOD lower LODs are made from (the first, normally LOD 0); null when the part has no mesh.</summary>
    public static MeshLod? Source(ProjectSession session) => session.Lods.Count > 0 ? session.Lods[0] : null;

    /// <summary>Whether <paramref name="lod" /> can be regenerated: any LOD but the source.</summary>
    public static bool CanRegenerate(ProjectSession session, MeshLod lod) => session.Lods.Count > 1 && lod.Id != session.Lods[0].Id;

    /// <summary>The default share of LOD 0's triangles for a LOD, by its place after the source (50%, 25%, 12.5%).</summary>
    public static double DefaultRatio(ProjectSession session, MeshLod lod) =>
        GeomLodBuilder.DefaultRatio(session.Lods.Select(l => l.Id).ToList().IndexOf(lod.Id));

    /// <summary>
    /// Regenerates every group of LOD <paramref name="lodId" /> that has a counterpart in the source LOD. All groups are
    /// built before any is stored, so a failure (InvalidDataException) leaves the project unchanged.
    /// </summary>
    public static LodRegenerationResult Regenerate(ProjectSession session, int lodId, double? ratio = null, SimplifyOptions? options = null)
    {
        var source = Source(session) ?? throw new InvalidOperationException("the part has no mesh");
        var lod = session.Lods.FirstOrDefault(l => l.Id == lodId) ?? throw new ArgumentException($"no LOD {lodId}", nameof(lodId));
        if (!CanRegenerate(session, lod)) throw new InvalidOperationException($"LOD {lodId} is the source LOD; it is never regenerated");
        var share = ratio ?? DefaultRatio(session, lod);

        var warnings = new List<string>();
        var sourceKeys = source.Groups.Select(g => g.Key).ToHashSet();
        var built = new List<(RegeneratedGroup Result, byte[] Bytes)>();
        for (var g = 0; g < lod.Groups.Count; g++)
        {
            var target = lod.Groups[g];
            if (g >= source.Groups.Count)
            {
                warnings.Add($"Group {g} has no group {g} in LOD {source.Id} to be made from; it was left as it is.");
                continue;
            }
            if (sourceKeys.Contains(target.Key))
            {
                warnings.Add($"Group {g} uses the same GEOM as LOD {source.Id}; it was left as it is so LOD {source.Id} does not change.");
                continue;
            }
            var from = source.Groups[g];
            var bytes = session.Read(from.Key) ?? throw new InvalidDataException($"LOD {source.Id} group {g} ({from.Key}) is not in the project or the game");
            var lod0 = GeomLoader.Read(new MemoryStream(bytes), from.Key.ToString());
            var made = GeomLodBuilder.Build(bytes, share, MorphDeltas(session, lod0), options);
            built.Add((new RegeneratedGroup(g, target.Key, made.TrianglesBefore, target.Triangles, made.TrianglesAfter, made.TargetTriangles, made.VerticesAfter), made.Bytes));

            if (target.Shader != 0 && target.Shader != from.Shader)
                warnings.Add($"Group {g} had shader 0x{target.Shader:X8}; it now has LOD {source.Id}'s, 0x{from.Shader:X8}.");
            if (made.TrianglesAfter > made.TargetTriangles * 1.1)
                warnings.Add($"Group {g}: {made.TrianglesAfter:N0} triangles, more than the {made.TargetTriangles:N0} asked. Borders, seams " +
                             "and double-sided or non-manifold parts are kept as they are.");
        }
        for (var g = lod.Groups.Count; g < source.Groups.Count; g++)
            warnings.Add($"LOD {source.Id} group {g} has no group in LOD {lod.Id}; adding groups to a LOD is not built, so it has no lower detail there.");
        if (built.Count > 0 && Enum.GetValues<BlendGeometry.Morph>().Any(m => BlendGeometry.Find(session, session.Part, m) != null))
            warnings.Add($"The new meshes keep LOD {source.Id}'s VertexIDs. The morphs (BGEO) were not rebuilt for LOD {lod.Id}: " +
                         "check fat, fit, thin and pregnant at this LOD's distance in game.");

        foreach (var (result, bytes) in built) session.Replace(result.Key, bytes);
        return new LodRegenerationResult(lod.Id, share, built.Select(b => b.Result).ToList(), warnings);
    }

    /// <summary>The part's BGEO position deltas for <paramref name="mesh" />, one array per morph it has (matched by VertexID).</summary>
    static List<Vector3[]> MorphDeltas(ProjectSession session, GeomMesh mesh)
    {
        var deltas = new List<Vector3[]>();
        foreach (var morph in Enum.GetValues<BlendGeometry.Morph>())
        {
            try
            {
                if (BlendGeometry.Find(session, session.Part, morph) is not { } key || session.Read(key) is not { } bytes) continue;
                var parsed = BlendGeometry.Parse(bytes);
                if (BlendGeometry.LodFor(parsed, mesh) is { } block && BlendGeometry.Read(parsed, block, mesh) is { PositionDeltas: > 0 } d)
                    deltas.Add(d.Positions);
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException or IndexOutOfRangeException) { }
        }
        return deltas;
    }
}
