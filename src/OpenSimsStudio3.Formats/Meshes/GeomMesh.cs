using System.Numerics;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>
/// A GEOM's drawable geometry in our own plain form: interleaved-free arrays the viewport can upload directly.
/// Coordinates stay as stored in the file (Y-up, metres).
/// </summary>
public sealed record GeomMesh(
    string Name,
    Vector3[] Positions,
    Vector3[] Normals,
    uint[] Indices,
    int BoneCount,
    uint Shader)
{
    public int TriangleCount => Indices.Length / 3;

    /// <summary>First UV set per vertex, as stored (empty when the GEOM has none).</summary>
    public Vector2[] Uvs { get; init; } = [];

    /// <summary>VertexID element per vertex, as stored (empty when the GEOM has none). Morph GEOMs repeat the base's IDs.</summary>
    public uint[] VertexIds { get; init; } = [];

    /// <summary>Joins meshes into one (offsetting indices); UVs are kept when every part has them.</summary>
    public static GeomMesh Merge(string name, IReadOnlyList<GeomMesh> meshes)
    {
        if (meshes.Count == 1) return meshes[0];
        var indices = new List<uint>();
        var offset = 0u;
        foreach (var m in meshes) { indices.AddRange(m.Indices.Select(i => i + offset)); offset += (uint)m.Positions.Length; }
        var uvs = meshes.All(m => m.Uvs.Length == m.Positions.Length) ? meshes.SelectMany(m => m.Uvs).ToArray() : [];
        var ids = meshes.All(m => m.VertexIds.Length == m.Positions.Length) ? meshes.SelectMany(m => m.VertexIds).ToArray() : [];
        return new GeomMesh(name, meshes.SelectMany(m => m.Positions).ToArray(), meshes.SelectMany(m => m.Normals).ToArray(), indices.ToArray(),
            meshes.Max(m => m.BoneCount), meshes[0].Shader) { Uvs = uvs, VertexIds = ids };
    }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}
