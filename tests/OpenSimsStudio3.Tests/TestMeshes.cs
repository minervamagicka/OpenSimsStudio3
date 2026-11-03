using System.Numerics;
using OpenSimsStudio3.Formats.Meshes;

namespace OpenSimsStudio3.Tests;

/// <summary>Meshes built in code for the simplifier tests: a grid, a cylinder with a UV seam, a skinned strip.</summary>
static class TestMeshes
{
    public sealed record Mesh(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, uint[] Indices)
    {
        public VertexSkin[] Skin { get; init; } = [];
        public SimplifierInput Input => new(Positions, Indices) { Skin = Skin };
    }

    /// <summary>An n×n quad grid over [0,1]² in XY with a gentle height field, facing +Z.</summary>
    public static Mesh Grid(int n, float bump = 0.02f)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        for (var y = 0; y <= n; y++)
            for (var x = 0; x <= n; x++)
            {
                float u = (float)x / n, v = (float)y / n;
                positions.Add(new Vector3(u, v, bump * MathF.Sin(4 * u) * MathF.Cos(3 * v)));
                uvs.Add(new Vector2(u, v));
            }
        return new Mesh(positions.ToArray(), Enumerable.Repeat(Vector3.UnitZ, positions.Count).ToArray(), uvs.ToArray(), Quads(n, n, n + 1));
    }

    /// <summary>
    /// An open cylinder (radius 1, height 2, along Y), outward-facing, UV-unwrapped with a seam at angle 0: the seam column
    /// is stored twice, at U = 0 and U = 1, with the same positions.
    /// </summary>
    public static Mesh Cylinder(int segments, int rings)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        for (var r = 0; r <= rings; r++)
            for (var s = 0; s <= segments; s++)
            {
                var angle = 2 * MathF.PI * (s % segments) / segments;
                var n = new Vector3(MathF.Cos(angle), 0, -MathF.Sin(angle));
                positions.Add(new Vector3(n.X, 2f * r / rings, n.Z));
                normals.Add(n);
                uvs.Add(new Vector2((float)s / segments, (float)r / rings));
            }
        return new Mesh(positions.ToArray(), normals.ToArray(), uvs.ToArray(), Quads(segments, rings, segments + 1));
    }

    /// <summary>A strip along X (length 4, width 1) skinned to bones 0 and 1, blending linearly from one to the other.</summary>
    public static Mesh SkinnedStrip(int along, int across)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var skin = new List<VertexSkin>();
        for (var y = 0; y <= across; y++)
            for (var x = 0; x <= along; x++)
            {
                float u = (float)x / along, v = (float)y / across;
                positions.Add(new Vector3(4 * u, v, 0.05f * MathF.Sin(6 * u)));
                uvs.Add(new Vector2(u, v));
                skin.Add(new VertexSkin(0x0100, new Vector4(1 - u, u, 0, 0)));
            }
        return new Mesh(positions.ToArray(), Enumerable.Repeat(Vector3.UnitZ, positions.Count).ToArray(), uvs.ToArray(), Quads(along, across, along + 1))
            { Skin = skin.ToArray() };
    }

    /// <summary>Two counter-clockwise triangles per cell of a w×h grid of vertices laid out row by row.</summary>
    static uint[] Quads(int w, int h, int stride)
    {
        var indices = new List<uint>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var a = (uint)(y * stride + x);
                uint b = a + 1, c = a + (uint)stride, d = c + 1;
                indices.AddRange([a, b, d, a, d, c]);
            }
        return indices.ToArray();
    }
}
