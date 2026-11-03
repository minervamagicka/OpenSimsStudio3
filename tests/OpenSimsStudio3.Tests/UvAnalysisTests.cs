using System.Diagnostics;
using System.Numerics;
using OpenSimsStudio3.Formats.Meshes;

namespace OpenSimsStudio3.Tests;

/// <summary>UV checks on meshes built here: grids, quads, mirrored halves, stacked layers. No game data.</summary>
public class UvAnalysisTests
{
    /// <summary>A mesh under construction: positions, one UV set and triangle indices.</summary>
    sealed class TestMesh
    {
        public readonly List<Vector3> Positions = [];
        public readonly List<Vector2> Uvs = [];
        public readonly List<uint> Indices = [];

        public int Triangles => Indices.Count / 3;

        public uint Vertex(Vector3 p, Vector2 uv)
        {
            Positions.Add(p);
            Uvs.Add(uv);
            return (uint)(Positions.Count - 1);
        }

        public void Triangle(uint a, uint b, uint c) => Indices.AddRange([a, b, c]);

        /// <summary>
        /// A grid of n×m quads (two triangles each) with shared vertices. <paramref name="place" /> maps the 0–1 grid
        /// coordinate to a 3D position, <paramref name="uv" /> to a UV. Triangles wind counter-clockwise in grid space.
        /// </summary>
        public TestMesh Grid(int n, int m, Func<Vector2, Vector3> place, Func<Vector2, Vector2> uv)
        {
            var first = (uint)Positions.Count;
            for (var y = 0; y <= m; y++)
            for (var x = 0; x <= n; x++)
            {
                var g = new Vector2((float)x / n, (float)y / m);
                Vertex(place(g), uv(g));
            }
            uint At(int x, int y) => first + (uint)(y * (n + 1) + x);
            for (var y = 0; y < m; y++)
            for (var x = 0; x < n; x++)
            {
                Triangle(At(x, y), At(x + 1, y), At(x + 1, y + 1));
                Triangle(At(x, y), At(x + 1, y + 1), At(x, y + 1));
            }
            return this;
        }

        public TestMesh Quad(Vector2 uvMin, Vector2 uvMax, float z = 0, float x = 0) =>
            Grid(1, 1, g => new Vector3(x + g.X, g.Y, z), g => uvMin + g * (uvMax - uvMin));

        public UvReport Analyze(UvTolerances? tolerances = null) =>
            UvAnalysis.Analyze(Positions.ToArray(), [Uvs.ToArray()], Indices.ToArray(), true, tolerances);
    }

    static Vector3 Flat(Vector2 g) => new(g.X, g.Y, 0);

    [Fact]
    public void A_clean_grid_has_no_issues()
    {
        // Shared edges and shared vertices everywhere: none may count as overlap.
        var report = new TestMesh().Grid(40, 30, Flat, g => g).Analyze();
        Assert.True(report.IsClean, string.Join(", ", report.Issues.Select(i => $"{i.Kind}:{i.Count}")));
        Assert.Equal(1, report.Channels);
        Assert.Equal(2400, report.TriangleCount);
    }

    [Fact]
    public void Islands_that_touch_at_edges_and_corners_are_not_overlaps()
    {
        // Four separate islands (own vertices) meeting along edges and at the centre corner, plus a gap-free row.
        var mesh = new TestMesh()
            .Quad(new(0, 0), new(0.5f, 0.5f), x: 0).Quad(new(0.5f, 0), new(1, 0.5f), x: 2)
            .Quad(new(0, 0.5f), new(0.5f, 1), x: 4).Quad(new(0.5f, 0.5f), new(1, 1), x: 6);
        Assert.True(mesh.Analyze().IsClean);
    }

    [Fact]
    public void Uvs_outside_0_1_are_counted_per_vertex_with_their_triangles()
    {
        var mesh = new TestMesh().Grid(2, 1, Flat, g => g);
        mesh.Uvs[2] = new Vector2(1.5f, 0);    // bottom-right corner
        mesh.Uvs[3] = new Vector2(0, -0.25f);  // top-left corner
        var report = mesh.Analyze();
        var issue = Assert.Single(report.Issues, i => i.Kind == UvIssueKind.OutOfRange);
        Assert.Equal(0, issue.Channel);
        Assert.Equal(2, issue.Vertices);
        // Vertex 2 (x=2,y=0) is in triangle 2; vertex 3 (x=0,y=1) is in triangle 1.
        Assert.Equal([1, 2], issue.Triangles);
    }

    [Fact]
    public void Uvs_just_past_the_edge_are_within_tolerance()
    {
        var mesh = new TestMesh().Grid(2, 2, Flat, g => g * 1.0005f - new Vector2(0.00025f));
        Assert.Null(mesh.Analyze().Find(UvIssueKind.OutOfRange));
        Assert.NotNull(mesh.Analyze(new UvTolerances(Range: 1e-5f)).Find(UvIssueKind.OutOfRange));
    }

    [Fact]
    public void Non_finite_uvs_are_out_of_range_and_skipped_elsewhere()
    {
        var mesh = new TestMesh().Grid(1, 1, Flat, g => g);
        mesh.Uvs[0] = new Vector2(float.NaN, 0);
        var report = mesh.Analyze();
        Assert.Equal(1, report.Find(UvIssueKind.OutOfRange)!.Vertices);
        Assert.Null(report.Find(UvIssueKind.Degenerate));
        Assert.Null(report.Find(UvIssueKind.Flipped));
    }

    [Fact]
    public void A_uv_triangle_collapsed_to_a_line_on_a_proper_3d_triangle_is_degenerate()
    {
        var mesh = new TestMesh();
        var a = mesh.Vertex(new(0, 0, 0), new(0.1f, 0.1f));
        var b = mesh.Vertex(new(1, 0, 0), new(0.2f, 0.2f));
        var c = mesh.Vertex(new(0, 1, 0), new(0.3f, 0.3f)); // collinear in UV
        mesh.Triangle(a, b, c);
        var d = mesh.Vertex(new(2, 0, 0), new(0.5f, 0.5f));
        var e = mesh.Vertex(new(3, 0, 0), new(0.5f, 0.5f));
        var f = mesh.Vertex(new(2, 1, 0), new(0.5f, 0.5f)); // collapsed to a point
        mesh.Triangle(d, e, f);
        var issue = mesh.Analyze().Find(UvIssueKind.Degenerate);
        Assert.Equal([0, 1], issue!.Triangles);
    }

    [Fact]
    public void A_degenerate_3d_triangle_with_degenerate_uvs_is_not_reported()
    {
        var mesh = new TestMesh();
        var a = mesh.Vertex(new(0, 0, 0), new(0.1f, 0.1f));
        var b = mesh.Vertex(new(1, 0, 0), new(0.2f, 0.1f));
        var c = mesh.Vertex(new(2, 0, 0), new(0.3f, 0.1f)); // collinear in 3D too
        mesh.Triangle(a, b, c);
        Assert.True(mesh.Analyze().IsClean);
    }

    [Fact]
    public void Tiny_but_well_shaped_triangles_are_not_degenerate()
    {
        // A 0.1 mm patch mapped to a 1e-5 UV square: small, not degenerate.
        var mesh = new TestMesh().Grid(4, 4, g => new Vector3(g * 1e-4f, 0), g => new Vector2(0.5f) + g * 1e-5f);
        Assert.True(mesh.Analyze().IsClean);
    }

    [Fact]
    public void A_mirrored_island_on_its_own_is_flipped_but_not_overlapping()
    {
        var mesh = new TestMesh()
            .Grid(4, 4, Flat, g => g * 0.4f)                                                     // 32 normal triangles
            .Grid(1, 1, g => new Vector3(g.X + 5, g.Y, 0), g => new Vector2(0.9f - g.X * 0.3f, 0.6f + g.Y * 0.3f)); // 2 mirrored
        var report = mesh.Analyze();
        Assert.Equal([32, 33], report.Find(UvIssueKind.Flipped)!.Triangles);
        Assert.Null(report.Find(UvIssueKind.Overlap));
        Assert.Null(report.Find(UvIssueKind.MirroredOverlap));
    }

    [Fact]
    public void Mirrored_halves_on_the_same_uvs_are_a_mirrored_overlap()
    {
        // Left and right halves of a strip share one UV region, the right half mirrored (the usual symmetric top).
        var mesh = new TestMesh()
            .Grid(4, 4, g => new Vector3(-g.X, g.Y, 0), g => g * 0.5f)
            .Grid(4, 4, g => new Vector3(g.X, g.Y, 0), g => new Vector2(0.5f - g.X * 0.5f, g.Y * 0.5f));
        var report = mesh.Analyze();
        Assert.Equal(64, report.Find(UvIssueKind.MirroredOverlap)!.Count);
        Assert.Equal(32, report.Find(UvIssueKind.Flipped)!.Count);
        Assert.Null(report.Find(UvIssueKind.Overlap));
    }

    [Fact]
    public void Two_islands_on_the_same_uvs_overlap()
    {
        var mesh = new TestMesh().Quad(new(0.1f, 0.1f), new(0.5f, 0.5f), x: 0).Quad(new(0.3f, 0.3f), new(0.7f, 0.7f), x: 3);
        var report = mesh.Analyze();
        Assert.Equal([0, 1, 2, 3], report.Find(UvIssueKind.Overlap)!.Triangles);
        Assert.Null(report.Find(UvIssueKind.MirroredOverlap));
        Assert.Null(report.Find(UvIssueKind.Flipped));
    }

    [Fact]
    public void Overlap_tolerance_ignores_a_hairline_and_catches_a_real_overlap()
    {
        // Side by side, the second island reaching back over the first by 5e-5 (ignored) or 1e-3 (reported).
        static UvReport Run(float reach) => new TestMesh()
            .Quad(new(0.1f, 0.1f), new(0.5f, 0.5f), x: 0).Quad(new(0.5f - reach, 0.1f), new(0.9f, 0.5f), x: 3).Analyze();
        Assert.True(Run(5e-5f).IsClean);
        Assert.Equal(4, Run(1e-3f).Find(UvIssueKind.Overlap)!.Count);
    }

    [Fact]
    public void Double_sided_faces_sharing_their_uvs_are_not_overlaps()
    {
        var mesh = new TestMesh().Grid(3, 3, Flat, g => g * 0.5f);
        // The back face: same positions and UVs as separate vertices, reversed winding.
        var count = mesh.Positions.Count;
        for (var i = 0; i < count; i++) mesh.Vertex(mesh.Positions[i], mesh.Uvs[i]);
        var front = mesh.Indices.ToArray();
        for (var t = 0; t < front.Length; t += 3) mesh.Triangle(front[t] + (uint)count, front[t + 2] + (uint)count, front[t + 1] + (uint)count);
        var report = mesh.Analyze();
        Assert.Null(report.Find(UvIssueKind.Overlap));
        Assert.Null(report.Find(UvIssueKind.MirroredOverlap));
        // Half the triangles still wind the other way in UV: that is reported, as it is.
        Assert.Equal(18, report.Find(UvIssueKind.Flipped)!.Count);
    }

    [Fact]
    public void A_triangle_inside_another_overlaps()
    {
        var mesh = new TestMesh();
        mesh.Triangle(mesh.Vertex(new(0, 0, 0), new(0, 0)), mesh.Vertex(new(1, 0, 0), new(1, 0)), mesh.Vertex(new(0, 1, 0), new(0, 1)));
        mesh.Triangle(mesh.Vertex(new(5, 0, 0), new(0.1f, 0.1f)), mesh.Vertex(new(6, 0, 0), new(0.2f, 0.1f)), mesh.Vertex(new(5, 1, 0), new(0.1f, 0.2f)));
        Assert.Equal([0, 1], mesh.Analyze().Find(UvIssueKind.Overlap)!.Triangles);
    }

    [Fact]
    public void A_far_out_uv_does_not_hide_overlaps()
    {
        // One stray triangle far away stretches the bounds; the grid must still separate the rest.
        var mesh = new TestMesh().Grid(20, 20, Flat, g => g * 0.5f).Quad(new(0.2f, 0.2f), new(0.3f, 0.3f), x: 5).Quad(new(500, 500), new(501, 501), x: 9);
        var report = mesh.Analyze();
        Assert.Equal(2, report.Find(UvIssueKind.OutOfRange)!.Count);
        var overlap = report.Find(UvIssueKind.Overlap)!;
        Assert.Contains(800, overlap.Triangles);
        Assert.Contains(801, overlap.Triangles);
        Assert.DoesNotContain(802, overlap.Triangles);
    }

    [Fact]
    public void Missing_uvs_are_reported_only_when_expected()
    {
        var mesh = new TestMesh().Grid(2, 2, Flat, g => g);
        var positions = mesh.Positions.ToArray();
        var indices = mesh.Indices.ToArray();
        var missing = UvAnalysis.Analyze(positions, [], indices, expectsUvs: true).Find(UvIssueKind.MissingUvs);
        Assert.Equal(8, missing!.Count);
        Assert.True(UvAnalysis.Analyze(positions, [], indices, expectsUvs: false).IsClean);
        // A UV set of the wrong length counts as missing.
        Assert.NotNull(UvAnalysis.Analyze(positions, [new Vector2[3]], indices).Find(UvIssueKind.MissingUvs));
    }

    [Fact]
    public void Each_uv_set_is_checked_and_labelled_with_its_channel()
    {
        var mesh = new TestMesh().Grid(2, 2, Flat, g => g);
        var second = mesh.Uvs.Select(uv => uv * 2).ToArray(); // outside 0–1
        var report = UvAnalysis.Analyze(mesh.Positions.ToArray(), [mesh.Uvs.ToArray(), second], mesh.Indices.ToArray());
        Assert.Equal(2, report.Channels);
        Assert.Null(report.Find(UvIssueKind.OutOfRange, 0));
        Assert.Equal(1, report.Find(UvIssueKind.OutOfRange, 1)!.Channel);
    }

    [Fact]
    public void The_geom_mesh_overload_uses_its_uv_sets_and_skips_morphs()
    {
        var mesh = new TestMesh().Grid(2, 2, Flat, g => g * 3);
        var positions = mesh.Positions.ToArray();
        var geom = new GeomMesh("test", positions, new Vector3[positions.Length], mesh.Indices.ToArray(), 0, 0x548394B9) { Uvs = mesh.Uvs.ToArray() };
        Assert.NotNull(UvAnalysis.Analyze(geom).Find(UvIssueKind.OutOfRange));
        Assert.True(UvAnalysis.Analyze(geom with { Uvs = [], Shader = 0 }).IsClean);
        Assert.NotNull(UvAnalysis.Analyze(geom with { Uvs = [] }).Find(UvIssueKind.MissingUvs));
        Assert.Equal(2, (geom with { ExtraUvs = [mesh.Uvs.ToArray()] }).UvChannels.Count);
    }

    [Fact]
    public void An_empty_mesh_is_clean_and_bad_indices_throw()
    {
        Assert.True(UvAnalysis.Analyze([], [], []).IsClean);
        Assert.Throws<ArgumentOutOfRangeException>(() => UvAnalysis.Analyze([Vector3.Zero], [[Vector2.Zero]], [0, 1, 2]));
    }

    [Fact]
    public void Triangle_overlap_test_handles_shared_edges_vertices_and_containment()
    {
        Vector2 a = new(0, 0), b = new(1, 0), c = new(0, 1), d = new(1, 1);
        Assert.False(UvAnalysis.TrianglesOverlap(a, b, c, b, d, c, 1e-4f));           // shared edge
        Assert.False(UvAnalysis.TrianglesOverlap(a, b, c, b, new(2, 0), new(1.5f, 1), 1e-4f)); // shared vertex
        Assert.True(UvAnalysis.TrianglesOverlap(a, b, c, a, b, c, 1e-4f));            // identical
        Assert.True(UvAnalysis.TrianglesOverlap(a, b, c, new(0.1f, 0.1f), new(0.2f, 0.1f), new(0.1f, 0.2f), 1e-4f));
    }

    [Fact]
    public void A_ten_thousand_triangle_mesh_checks_fast()
    {
        // 71×71 quads (10,082 triangles), plus a second layer of the same size stacked on the same UVs at another
        // depth: the worst ordinary case (every triangle overlaps).
        var clean = new TestMesh().Grid(71, 71, Flat, g => g);
        var stacked = new TestMesh().Grid(50, 50, Flat, g => g).Grid(50, 50, g => new Vector3(g, 1), g => g);
        UvAnalysis.Analyze(new TestMesh().Grid(4, 4, Flat, g => g).Positions.ToArray(), [new Vector2[25]], new uint[3]); // warm up

        var watch = Stopwatch.StartNew();
        var cleanReport = clean.Analyze();
        var cleanTime = watch.Elapsed;
        watch.Restart();
        var stackedReport = stacked.Analyze();
        var stackedTime = watch.Elapsed;

        Assert.True(cleanReport.IsClean);
        Assert.Equal(10_000, stackedReport.Find(UvIssueKind.Overlap)!.Count);
        // Generous bounds for slow CI machines; locally both take a few milliseconds.
        Assert.True(cleanTime < TimeSpan.FromSeconds(1), $"clean grid took {cleanTime.TotalMilliseconds:F0} ms");
        Assert.True(stackedTime < TimeSpan.FromSeconds(1), $"stacked grid took {stackedTime.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Warnings_fold_mirrored_flips_and_skip_overlaps_on_layered_hair()
    {
        // Mirrored halves (0-63), a normal block (64-95) so counter-clockwise is the majority, and a mirrored island
        // of its own (96-97): the halves' flips fold into the mirrored overlap, the island's stay.
        var mesh = new TestMesh()
            .Grid(4, 4, g => new Vector3(-g.X, g.Y, 0), g => g * 0.5f)
            .Grid(4, 4, g => new Vector3(g.X, g.Y, 0), g => new Vector2(0.5f - g.X * 0.5f, g.Y * 0.5f))
            .Grid(4, 4, g => new Vector3(g.X + 2, g.Y, 0), g => new Vector2(0.55f, 0) + g * 0.4f)
            .Grid(1, 1, g => new Vector3(g.X + 5, g.Y, 0), g => new Vector2(0.9f - g.X * 0.3f, 0.6f + g.Y * 0.3f));
        var report = mesh.Analyze();
        var warnings = report.Warnings().ToList();
        Assert.Equal([96, 97], warnings.Single(w => w.Kind == UvIssueKind.Flipped).Triangles);
        Assert.Contains(warnings, w => w.Kind == UvIssueKind.MirroredOverlap);

        var hair = report.Warnings(layeredByDesign: true).ToList();
        Assert.DoesNotContain(hair, w => w.Kind is UvIssueKind.Overlap or UvIssueKind.MirroredOverlap);
        Assert.Equal(34, hair.Single(w => w.Kind == UvIssueKind.Flipped).Count);

        // Mirrored halves alone: no separate flipped warning.
        var halves = new TestMesh()
            .Grid(4, 4, g => new Vector3(-g.X, g.Y, 0), g => g * 0.5f)
            .Grid(4, 4, g => new Vector3(g.X, g.Y, 0), g => new Vector2(0.5f - g.X * 0.5f, g.Y * 0.5f)).Analyze();
        Assert.DoesNotContain(halves.Warnings(), w => w.Kind == UvIssueKind.Flipped);
    }
}
