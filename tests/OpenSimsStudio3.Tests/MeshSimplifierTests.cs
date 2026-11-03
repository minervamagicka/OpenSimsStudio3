using System.Diagnostics;
using System.Numerics;
using OpenSimsStudio3.Formats.Meshes;

namespace OpenSimsStudio3.Tests;

/// <summary>The LOD simplifier on meshes built in code (no EA data).</summary>
public class MeshSimplifierTests
{
    static readonly double[] Ratios = [0.5, 0.25, 0.125];

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(0.125)]
    public void Grid_reaches_the_target_triangle_count(double ratio)
    {
        var grid = TestMeshes.Grid(40);
        var result = MeshSimplifier.Simplify(grid.Input, ratio);
        Assert.Equal(3200, result.TrianglesBefore);
        Assert.Equal((int)Math.Ceiling(3200 * ratio), result.TargetTriangles);
        Assert.InRange(result.TriangleCount, (int)(result.TargetTriangles * 0.95), result.TargetTriangles);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(0.125)]
    public void Cylinder_with_a_seam_reaches_the_target_triangle_count(double ratio)
    {
        var cylinder = TestMeshes.Cylinder(48, 24);
        var result = MeshSimplifier.Simplify(cylinder.Input, ratio);
        Assert.InRange(result.TriangleCount, (int)(result.TargetTriangles * 0.95), result.TargetTriangles);
    }

    [Fact]
    public void Output_vertices_are_input_vertices_in_input_order()
    {
        var grid = TestMeshes.Grid(20);
        var result = MeshSimplifier.Simplify(grid.Input, 0.25);
        Assert.True(result.SourceVertices.SequenceEqual(result.SourceVertices.Order().Distinct()));
        Assert.All(result.Indices, i => Assert.InRange((int)i, 0, result.SourceVertices.Length - 1));
        // Every output vertex is used by a triangle.
        Assert.Equal(result.SourceVertices.Length, result.Indices.Distinct().Count());
    }

    [Fact]
    public void Grid_border_is_kept_exactly()
    {
        var grid = TestMeshes.Grid(30);
        var before = BorderEdges(grid.Positions, grid.Indices);
        foreach (var ratio in Ratios)
        {
            var result = MeshSimplifier.Simplify(grid.Input, ratio);
            var after = BorderEdges(Positions(grid, result), result.Indices);
            Assert.Equal(before.Count, after.Count);
            Assert.True(before.SetEquals(after), $"border changed at {ratio}");
        }
    }

    [Fact]
    public void Cylinder_seam_is_never_crossed_or_opened()
    {
        var cylinder = TestMeshes.Cylinder(48, 24);
        var ends = BorderEdges(cylinder.Positions, cylinder.Indices);
        // Before: only the two end rings are open in position space (the seam is closed there).
        Assert.All(ends, e => Assert.True(e.A.Y == e.B.Y && e.A.Y is 0f or 2f));
        foreach (var ratio in Ratios)
        {
            var result = MeshSimplifier.Simplify(cylinder.Input, ratio);
            var uvs = result.SourceVertices.Select(v => cylinder.Uvs[v]).ToArray();
            for (var t = 0; t < result.TriangleCount; t++)
            {
                var us = new[] { uvs[result.Indices[3 * t]].X, uvs[result.Indices[3 * t + 1]].X, uvs[result.Indices[3 * t + 2]].X };
                // A triangle across the seam would span U from near 0 to near 1.
                Assert.True(us.Max() - us.Min() < 0.5f, $"triangle {t} crosses the UV seam at {ratio}");
            }
            // No new border: the seam's two sides still meet, so the surface did not open along it.
            Assert.True(ends.SetEquals(BorderEdges(Positions(cylinder, result), result.Indices)), $"the seam opened at {ratio}");
            // Seam vertices still come in pairs at one position (U = 0 and U = 1).
            var seam = result.SourceVertices.Where(v => cylinder.Uvs[v].X is 0f or 1f).GroupBy(v => cylinder.Positions[v]).ToList();
            Assert.NotEmpty(seam);
            Assert.All(seam, g => Assert.Equal(new[] { 0f, 1f }, g.Select(v => cylinder.Uvs[v].X).Order().ToArray()));
        }
    }

    [Fact]
    public void Cylinder_seam_is_simplified_along_itself()
    {
        var cylinder = TestMeshes.Cylinder(48, 24);
        var result = MeshSimplifier.Simplify(cylinder.Input, 0.125);
        var seamPoints = result.SourceVertices.Where(v => cylinder.Uvs[v].X == 0f).Count();
        Assert.True(seamPoints < 25, $"{seamPoints} seam points kept of 25");
    }

    [Fact]
    public void Skinned_strip_keeps_valid_weights_on_the_original_bones()
    {
        var strip = TestMeshes.SkinnedStrip(60, 12);
        foreach (var ratio in Ratios)
        {
            var result = MeshSimplifier.Simplify(strip.Input, ratio);
            Assert.True(result.TriangleCount <= result.TargetTriangles * 1.1, $"{result.TriangleCount} of {result.TargetTriangles}");
            foreach (var v in result.SourceVertices)
            {
                var skin = strip.Skin[v];
                Assert.Equal(1f, skin.Weights.X + skin.Weights.Y + skin.Weights.Z + skin.Weights.W, 5);
                for (var s = 0; s < 4; s++)
                    if (skin.Weight(s) > 0) Assert.Contains(skin.Bone(s), new[] { 0, 1 });
            }
        }
    }

    [Fact]
    public void Skin_penalty_keeps_differently_weighted_vertices_apart_for_longer()
    {
        // Same flat strip, once with a hard switch from bone 0 to bone 1 in the middle: the switch line survives.
        var strip = TestMeshes.SkinnedStrip(40, 8);
        var hard = strip.Skin.Select((s, i) => strip.Positions[i].X < 2f ? new VertexSkin(0x0100, new Vector4(1, 0, 0, 0)) : new VertexSkin(0x0100, new Vector4(0, 1, 0, 0))).ToArray();
        var flat = strip.Positions.Select(p => p with { Z = 0 }).ToArray();
        var result = MeshSimplifier.Simplify(new SimplifierInput(flat, strip.Indices) { Skin = hard }, 0.25);
        foreach (var t in Enumerable.Range(0, result.TriangleCount))
        {
            var xs = Enumerable.Range(0, 3).Select(k => flat[result.SourceVertices[result.Indices[3 * t + k]]].X).ToArray();
            // No triangle reaches from well inside one bone's half to well inside the other's.
            Assert.False(xs.Min() < 1.8f && xs.Max() > 2.2f, $"triangle {t} spans {xs.Min()}..{xs.Max()}");
        }
    }

    [Fact]
    public void No_degenerate_or_flipped_triangles_are_introduced()
    {
        foreach (var mesh in new[] { TestMeshes.Grid(30), TestMeshes.Cylinder(48, 24), TestMeshes.SkinnedStrip(60, 12) })
            foreach (var ratio in Ratios)
            {
                var result = MeshSimplifier.Simplify(mesh.Input, ratio);
                for (var t = 0; t < result.TriangleCount; t++)
                {
                    var v = Enumerable.Range(0, 3).Select(k => result.SourceVertices[result.Indices[3 * t + k]]).ToArray();
                    var cross = Vector3.Cross(mesh.Positions[v[1]] - mesh.Positions[v[0]], mesh.Positions[v[2]] - mesh.Positions[v[0]]);
                    Assert.True(cross.Length() > 1e-7f, $"triangle {t} is degenerate at {ratio}");
                    var normal = mesh.Normals[v[0]] + mesh.Normals[v[1]] + mesh.Normals[v[2]];
                    Assert.True(Vector3.Dot(cross, normal) > 0, $"triangle {t} is flipped at {ratio}");
                }
            }
    }

    [Fact]
    public void Output_is_deterministic()
    {
        var cylinder = TestMeshes.Cylinder(32, 16);
        var a = MeshSimplifier.Simplify(cylinder.Input, 0.25);
        var b = MeshSimplifier.Simplify(cylinder.Input with { Positions = (Vector3[])cylinder.Positions.Clone() }, 0.25);
        Assert.Equal(a.SourceVertices, b.SourceVertices);
        Assert.Equal(a.Indices, b.Indices);
    }

    [Fact]
    public void Ratio_one_keeps_every_triangle()
    {
        var grid = TestMeshes.Grid(10);
        var result = MeshSimplifier.Simplify(grid.Input, 1);
        Assert.Equal(grid.Indices, result.Indices);
    }

    [Fact]
    public void Morph_attributes_keep_differently_morphing_vertices_apart_for_longer()
    {
        // A flat grid whose right half has a large morph delta: the line where the morph starts survives.
        var grid = TestMeshes.Grid(32, bump: 0);
        var attributes = grid.Positions.SelectMany(p => p.X > 0.5f ? new[] { 0f, 0f, 0.1f } : new[] { 0f, 0f, 0f }).ToArray();
        var result = MeshSimplifier.Simplify(grid.Input with { Attributes = attributes, AttributeStride = 3 }, 0.25);
        for (var t = 0; t < result.TriangleCount; t++)
        {
            var xs = Enumerable.Range(0, 3).Select(k => grid.Positions[result.SourceVertices[result.Indices[3 * t + k]]].X).ToArray();
            Assert.False(xs.Min() < 0.45f && xs.Max() > 0.6f, $"triangle {t} spans {xs.Min()}..{xs.Max()}");
        }
    }

    [Fact]
    public void A_20k_triangle_mesh_simplifies_quickly()
    {
        var grid = TestMeshes.Grid(100, bump: 0.1f); // 20,000 triangles
        Assert.Equal(20000, grid.Indices.Length / 3);
        MeshSimplifier.Simplify(TestMeshes.Grid(8).Input, 0.5); // warm up the JIT
        var clock = Stopwatch.StartNew();
        var result = MeshSimplifier.Simplify(grid.Input, 0.125);
        clock.Stop();
        Assert.InRange(result.TriangleCount, 2375, 2500);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed.TotalMilliseconds:N0} ms");
    }

    [Fact]
    public void Bad_input_is_refused()
    {
        Assert.Throws<ArgumentException>(() => MeshSimplifier.Simplify(new SimplifierInput([Vector3.Zero], [0, 1, 2]), 0.5));
        Assert.Throws<ArgumentException>(() => MeshSimplifier.Simplify(new SimplifierInput([Vector3.Zero], [0, 0]), 0.5));
    }

    static Vector3[] Positions(TestMeshes.Mesh mesh, SimplifiedMesh result) => result.SourceVertices.Select(v => mesh.Positions[v]).ToArray();

    /// <summary>Edges used by one triangle, by position (so split seam vertices count as one point).</summary>
    static HashSet<(Vector3 A, Vector3 B)> BorderEdges(Vector3[] positions, uint[] indices)
    {
        var uses = new Dictionary<(Vector3, Vector3), int>();
        for (var t = 0; t < indices.Length / 3; t++)
            for (var k = 0; k < 3; k++)
            {
                var (a, b) = (positions[indices[3 * t + k]], positions[indices[3 * t + (k + 1) % 3]]);
                var key = Less(a, b) ? (a, b) : (b, a);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        return uses.Where(e => e.Value == 1).Select(e => e.Key).ToHashSet();
    }

    static bool Less(Vector3 a, Vector3 b) => a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;
}
