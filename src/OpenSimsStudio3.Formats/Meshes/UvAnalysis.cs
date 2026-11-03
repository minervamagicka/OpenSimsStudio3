using System.Numerics;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>The kinds of UV problem <see cref="UvAnalysis" /> reports.</summary>
public enum UvIssueKind
{
    /// <summary>The mesh has triangles and should have UVs, but this UV set is absent (or has the wrong length).</summary>
    MissingUvs,
    /// <summary>UVs outside the 0–1 square (or not finite).</summary>
    OutOfRange,
    /// <summary>The UV triangle has (nearly) no area while the 3D triangle has a proper shape.</summary>
    Degenerate,
    /// <summary>The UV triangle winds the other way from most of the group's triangles.</summary>
    Flipped,
    /// <summary>The UV triangle overlaps another with the same winding.</summary>
    Overlap,
    /// <summary>The UV triangle overlaps another with the opposite winding (a mirrored copy).</summary>
    MirroredOverlap,
}

/// <summary>
/// One UV problem in one mesh group. <see cref="Triangles" /> are triangle indices (face numbers, ascending) into the
/// group's index list; <see cref="Vertices" /> is the number of distinct vertices outside 0–1 for
/// <see cref="UvIssueKind.OutOfRange" />, else 0.
/// </summary>
public sealed record UvIssue(UvIssueKind Kind, int Channel, int[] Triangles, int Vertices = 0)
{
    public int Count => Triangles.Length;
}

/// <summary>Every UV problem found in one mesh group. <see cref="Channels" /> is the number of UV sets it has.</summary>
public sealed record UvReport(int Channels, int TriangleCount, IReadOnlyList<UvIssue> Issues)
{
    public static readonly UvReport None = new(0, 0, []);

    public bool IsClean => Issues.Count == 0;

    public UvIssue? Find(UvIssueKind kind, int channel = 0) => Issues.FirstOrDefault(i => i.Kind == kind && i.Channel == channel);
}

/// <summary>
/// Tolerances for <see cref="UvAnalysis" />.
/// <para><see cref="Range" />: how far past 0 or 1 a UV may go, in UV units (1e-3 is about one texel at 1024).</para>
/// <para><see cref="Shape" />: a triangle counts as degenerate when |cross| / (longest edge)² is below this, i.e. its
/// height is under this fraction of its longest edge. Scale-free, so tiny but well-shaped triangles are fine.</para>
/// <para><see cref="Overlap" />: two UV triangles overlap only when they interpenetrate by more than this, in UV units, on
/// every separating axis (1e-4 is about a tenth of a texel at 1024). Shared edges and vertices never count.</para>
/// </summary>
public sealed record UvTolerances(float Range = 1e-3f, float Shape = 1e-4f, float Overlap = 1e-4f)
{
    public static readonly UvTolerances Default = new();
}

/// <summary>
/// Checks one mesh group's UV layout: missing UV sets, UVs outside 0–1, degenerate and flipped UV triangles, and UV
/// triangles that overlap each other. Pure code over positions, UVs and triangle indices. OSS3 only reports these; it
/// never repacks UVs (docs/ui/DESIGN.md, "No UV repacking").
/// </summary>
public static class UvAnalysis
{
    // Positions this close (metres, squared) count as the same point when spotting double-sided faces.
    const float SamePointSquared = 1e-12f;
    // The overlap grid is at most this many cells a side.
    const int MaxGridSide = 512;
    const float GridLimitLow = -1, GridLimitHigh = 2;

    /// <summary>Analyses a loaded GEOM. Morph GEOMs (shader 0) carry no drawable surface, so they are not expected to have UVs.</summary>
    public static UvReport Analyze(GeomMesh mesh, UvTolerances? tolerances = null) =>
        Analyze(mesh.Positions, mesh.UvChannels, mesh.Indices, expectsUvs: mesh.Shader != 0, tolerances);

    /// <summary>
    /// Analyses a triangle list. <paramref name="uvChannels" /> holds one UV per position per set. A set whose length
    /// does not match the positions is reported as missing.
    /// </summary>
    public static UvReport Analyze(Vector3[] positions, IReadOnlyList<Vector2[]> uvChannels, uint[] indices, bool expectsUvs = true,
        UvTolerances? tolerances = null)
    {
        var t = tolerances ?? UvTolerances.Default;
        var triangleCount = indices.Length / 3;
        foreach (var index in indices)
            if (index >= positions.Length) throw new ArgumentOutOfRangeException(nameof(indices), $"index {index} is past the {positions.Length} positions");
        if (triangleCount == 0) return new UvReport(uvChannels.Count, 0, []);

        var issues = new List<UvIssue>();
        if (uvChannels.Count == 0)
        {
            if (expectsUvs) issues.Add(new UvIssue(UvIssueKind.MissingUvs, 0, Enumerable.Range(0, triangleCount).ToArray()));
            return new UvReport(0, triangleCount, issues);
        }

        var solid = new bool[triangleCount];
        for (var f = 0; f < triangleCount; f++)
        {
            var (a, b, c) = (positions[indices[3 * f]], positions[indices[3 * f + 1]], positions[indices[3 * f + 2]]);
            solid[f] = ShapeOf(Vector3.Cross(b - a, c - a).Length(), MaxEdgeSquared(a, b, c)) >= t.Shape;
        }

        for (var channel = 0; channel < uvChannels.Count; channel++)
        {
            var uvs = uvChannels[channel];
            if (uvs.Length != positions.Length)
            {
                issues.Add(new UvIssue(UvIssueKind.MissingUvs, channel, Enumerable.Range(0, triangleCount).ToArray()));
                continue;
            }
            AnalyzeChannel(channel, positions, uvs, indices, solid, t, issues);
        }
        return new UvReport(uvChannels.Count, triangleCount, issues);
    }

    static void AnalyzeChannel(int channel, Vector3[] positions, Vector2[] uvs, uint[] indices, bool[] solid, UvTolerances t, List<UvIssue> issues)
    {
        var triangleCount = solid.Length;

        // Outside 0–1: distinct vertices, and the triangles that use them.
        var outside = new bool[uvs.Length];
        var counted = new bool[uvs.Length];
        var outVertices = 0;
        var outTriangles = new List<int>();
        for (var f = 0; f < triangleCount; f++)
        {
            var any = false;
            for (var k = 0; k < 3; k++)
            {
                var v = (int)indices[3 * f + k];
                if (!counted[v]) { counted[v] = true; outside[v] = !InRange(uvs[v], t.Range); if (outside[v]) outVertices++; }
                any |= outside[v];
            }
            if (any) outTriangles.Add(f);
        }
        if (outTriangles.Count > 0) issues.Add(new UvIssue(UvIssueKind.OutOfRange, channel, outTriangles.ToArray(), outVertices));

        // Winding (+1 / -1) of each usable UV triangle; 0 for degenerate or non-finite ones.
        var winding = new sbyte[triangleCount];
        var degenerate = new List<int>();
        int positive = 0, negative = 0;
        for (var f = 0; f < triangleCount; f++)
        {
            var (a, b, c) = (uvs[indices[3 * f]], uvs[indices[3 * f + 1]], uvs[indices[3 * f + 2]]);
            if (!Finite(a) || !Finite(b) || !Finite(c)) continue;
            var cross = Cross(b - a, c - a);
            if (ShapeOf(MathF.Abs(cross), MaxEdgeSquared(a, b, c)) < t.Shape)
            {
                // A UV sliver on a 3D sliver is expected; on a proper 3D triangle it smears the texture.
                if (solid[f]) degenerate.Add(f);
                continue;
            }
            if (cross > 0) { winding[f] = 1; positive++; }
            else { winding[f] = -1; negative++; }
        }
        if (degenerate.Count > 0) issues.Add(new UvIssue(UvIssueKind.Degenerate, channel, degenerate.ToArray()));

        // Flipped: the minority winding. A tie keeps counter-clockwise as the majority.
        var majority = positive >= negative ? 1 : -1;
        if (positive > 0 && negative > 0)
        {
            var flipped = new List<int>();
            for (var f = 0; f < triangleCount; f++)
                if (winding[f] == -majority) flipped.Add(f);
            issues.Add(new UvIssue(UvIssueKind.Flipped, channel, flipped.ToArray()));
        }

        var (same, mirrored) = FindOverlaps(positions, uvs, indices, winding, t.Overlap);
        if (same.Count > 0) issues.Add(new UvIssue(UvIssueKind.Overlap, channel, same.ToArray()));
        if (mirrored.Count > 0) issues.Add(new UvIssue(UvIssueKind.MirroredOverlap, channel, mirrored.ToArray()));
    }

    /// <summary>
    /// Finds overlapping UV triangles with a uniform grid over their UV bounds. The cell size follows the median
    /// triangle size and the grid stops at -1..2, so one stray far-away UV cannot crowd everything into one cell. Each
    /// pair is tested once, in the first cell both cover.
    /// </summary>
    static (List<int> Same, List<int> Mirrored) FindOverlaps(Vector3[] positions, Vector2[] uvs, uint[] indices, sbyte[] winding, float tolerance)
    {
        var triangleCount = winding.Length;
        var live = new List<int>();
        for (var f = 0; f < triangleCount; f++) if (winding[f] != 0) live.Add(f);
        var same = new List<int>();
        var mirrored = new List<int>();
        if (live.Count < 2) return (same, mirrored);

        var boxMin = new Vector2[triangleCount];
        var boxMax = new Vector2[triangleCount];
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        var extents = new float[live.Count];
        for (var n = 0; n < live.Count; n++)
        {
            var f = live[n];
            var (a, b, c) = (uvs[indices[3 * f]], uvs[indices[3 * f + 1]], uvs[indices[3 * f + 2]]);
            boxMin[f] = Vector2.Min(a, Vector2.Min(b, c));
            boxMax[f] = Vector2.Max(a, Vector2.Max(b, c));
            lo = Vector2.Min(lo, boxMin[f]);
            hi = Vector2.Max(hi, boxMax[f]);
            var size = boxMax[f] - boxMin[f];
            extents[n] = MathF.Max(size.X, size.Y);
        }
        Array.Sort(extents);
        var cell = MathF.Max(extents[extents.Length / 2], 1e-6f);
        // The grid covers at most -1..2; triangles beyond are clamped into the border cells, which keeps every
        // overlapping pair in a shared cell (clamping is monotonic) and stops stray UVs from stretching the cells.
        var (clampLo, clampHi) = (Vector2.Max(lo, new Vector2(GridLimitLow)), Vector2.Min(hi, new Vector2(GridLimitHigh)));
        if (clampLo.X < clampHi.X) (lo.X, hi.X) = (clampLo.X, clampHi.X);
        if (clampLo.Y < clampHi.Y) (lo.Y, hi.Y) = (clampLo.Y, clampHi.Y);
        var span = hi - lo;
        var nx = Math.Clamp((int)MathF.Ceiling(span.X / cell), 1, MaxGridSide);
        var ny = Math.Clamp((int)MathF.Ceiling(span.Y / cell), 1, MaxGridSide);
        var scale = new Vector2(nx / MathF.Max(span.X, 1e-12f), ny / MathF.Max(span.Y, 1e-12f));
        (int X0, int Y0, int X1, int Y1) Cells(int f) =>
        (
            Math.Clamp((int)((boxMin[f].X - lo.X) * scale.X), 0, nx - 1), Math.Clamp((int)((boxMin[f].Y - lo.Y) * scale.Y), 0, ny - 1),
            Math.Clamp((int)((boxMax[f].X - lo.X) * scale.X), 0, nx - 1), Math.Clamp((int)((boxMax[f].Y - lo.Y) * scale.Y), 0, ny - 1)
        );

        // Bucket triangles by cell (compressed rows: start offsets, then members).
        var start = new int[nx * ny + 1];
        var cells = new (int X0, int Y0, int X1, int Y1)[triangleCount];
        foreach (var f in live)
        {
            var r = cells[f] = Cells(f);
            for (var y = r.Y0; y <= r.Y1; y++)
            for (var x = r.X0; x <= r.X1; x++)
                start[y * nx + x + 1]++;
        }
        for (var i = 1; i < start.Length; i++) start[i] += start[i - 1];
        var members = new int[start[^1]];
        var fill = (int[])start.Clone();
        foreach (var f in live)
        {
            var r = cells[f];
            for (var y = r.Y0; y <= r.Y1; y++)
            for (var x = r.X0; x <= r.X1; x++)
                members[fill[y * nx + x]++] = f;
        }

        var sameHit = new bool[triangleCount];
        var mirroredHit = new bool[triangleCount];
        for (var y = 0; y < ny; y++)
        for (var x = 0; x < nx; x++)
        {
            var (from, to) = (start[y * nx + x], start[y * nx + x + 1]);
            for (var p = from; p < to; p++)
            {
                var i = members[p];
                for (var q = p + 1; q < to; q++)
                {
                    var j = members[q];
                    // Test the pair only in the first cell both cover.
                    if (Math.Max(cells[i].X0, cells[j].X0) != x || Math.Max(cells[i].Y0, cells[j].Y0) != y) continue;
                    var hits = winding[i] == winding[j] ? sameHit : mirroredHit;
                    if (hits[i] && hits[j]) continue;
                    if (boxMin[i].X > boxMax[j].X - tolerance || boxMin[j].X > boxMax[i].X - tolerance ||
                        boxMin[i].Y > boxMax[j].Y - tolerance || boxMin[j].Y > boxMax[i].Y - tolerance) continue;
                    if (!TrianglesOverlap(uvs[indices[3 * i]], uvs[indices[3 * i + 1]], uvs[indices[3 * i + 2]],
                            uvs[indices[3 * j]], uvs[indices[3 * j + 1]], uvs[indices[3 * j + 2]], tolerance)) continue;
                    // Double-sided faces (the same 3D triangle twice) share their UVs on purpose.
                    if (SamePlace(positions, indices, i, j)) continue;
                    hits[i] = hits[j] = true;
                }
            }
        }
        for (var f = 0; f < triangleCount; f++)
        {
            if (sameHit[f]) same.Add(f);
            if (mirroredHit[f]) mirrored.Add(f);
        }
        return (same, mirrored);
    }

    /// <summary>
    /// Separating-axis test for two non-degenerate 2D triangles: they overlap when, on every edge normal of either,
    /// their projections overlap by more than <paramref name="tolerance" />. Touching along an edge or at a vertex
    /// leaves a zero-overlap axis, so it does not count.
    /// </summary>
    public static bool TrianglesOverlap(Vector2 a0, Vector2 a1, Vector2 a2, Vector2 b0, Vector2 b1, Vector2 b2, float tolerance)
    {
        return !(Separates(a0, a1) || Separates(a1, a2) || Separates(a2, a0) ||
                 Separates(b0, b1) || Separates(b1, b2) || Separates(b2, b0));

        bool Separates(Vector2 from, Vector2 to)
        {
            var edge = to - from;
            var length = edge.Length();
            if (length == 0) return false;
            var axis = new Vector2(-edge.Y, edge.X) / length;
            var (minA, maxA) = Project(axis, a0, a1, a2);
            var (minB, maxB) = Project(axis, b0, b1, b2);
            return MathF.Min(maxA, maxB) - MathF.Max(minA, minB) <= tolerance;
        }
    }

    static (float Min, float Max) Project(Vector2 axis, Vector2 a, Vector2 b, Vector2 c)
    {
        var (pa, pb, pc) = (Vector2.Dot(axis, a), Vector2.Dot(axis, b), Vector2.Dot(axis, c));
        return (MathF.Min(pa, MathF.Min(pb, pc)), MathF.Max(pa, MathF.Max(pb, pc)));
    }

    static bool SamePlace(Vector3[] positions, uint[] indices, int i, int j)
    {
        for (var k = 0; k < 3; k++)
        {
            var p = positions[indices[3 * i + k]];
            if (Vector3.DistanceSquared(p, positions[indices[3 * j]]) > SamePointSquared &&
                Vector3.DistanceSquared(p, positions[indices[3 * j + 1]]) > SamePointSquared &&
                Vector3.DistanceSquared(p, positions[indices[3 * j + 2]]) > SamePointSquared) return false;
        }
        return true;
    }

    static bool InRange(Vector2 uv, float slack) =>
        Finite(uv) && uv.X >= -slack && uv.X <= 1 + slack && uv.Y >= -slack && uv.Y <= 1 + slack;

    static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    // |cross| / longest edge², 0 for a triangle collapsed to a point. An equilateral triangle scores about 0.87.
    static float ShapeOf(float absCross, float maxEdgeSquared) => maxEdgeSquared <= 1e-30f ? 0 : absCross / maxEdgeSquared;

    static float MaxEdgeSquared(Vector2 a, Vector2 b, Vector2 c) =>
        MathF.Max(Vector2.DistanceSquared(a, b), MathF.Max(Vector2.DistanceSquared(b, c), Vector2.DistanceSquared(c, a)));

    static float MaxEdgeSquared(Vector3 a, Vector3 b, Vector3 c) =>
        MathF.Max(Vector3.DistanceSquared(a, b), MathF.Max(Vector3.DistanceSquared(b, c), Vector3.DistanceSquared(c, a)));
}
