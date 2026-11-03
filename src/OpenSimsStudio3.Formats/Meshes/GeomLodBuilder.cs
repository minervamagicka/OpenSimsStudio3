using System.Numerics;
using s3pi.GenericRCOLResource;
using GEOM = meshExpImp.ModelBlocks.GEOM;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>What <see cref="GeomLodBuilder.Build" /> made.</summary>
public sealed record GeomLodResult(byte[] Bytes, int VerticesBefore, int VerticesAfter, int TrianglesBefore, int TrianglesAfter, int TargetTriangles);

/// <summary>
/// Builds a lower-detail GEOM from a higher one with <see cref="MeshSimplifier" />. The result is the source GEOM re-written
/// through s3pi with fewer vertices and faces: shader and MTNF, vertex format, bone hashes, skin index, merge group, sort
/// order and TGI list are kept as they are, and every kept vertex keeps all its elements unchanged (position, normal, UV
/// sets, bones and weights, tangent, colour, VertexID).
/// </summary>
public static class GeomLodBuilder
{
    /// <summary>
    /// Detail of each LOD as a share of LOD 0's triangles: LOD k gets 0.5^k (50%, 25%, 12.5%), which follows the halving
    /// EA's own CAS LODs roughly show. A guess, to be checked against the LOD switch in game.
    /// </summary>
    public static double DefaultRatio(int lodRank) => lodRank <= 0 ? 1 : Math.Pow(0.5, lodRank);

    /// <param name="source">The source GEOM resource (RCOL bytes).</param>
    /// <param name="ratio">Share of the source's triangles to keep (0–1).</param>
    /// <param name="morphDeltas">Optional per-vertex deltas in the source's vertex order (e.g. fat, fit, thin, special BGEO
    /// position deltas); vertices that morph differently are merged last.</param>
    public static GeomLodResult Build(byte[] source, double ratio, IReadOnlyList<Vector3[]>? morphDeltas = null, SimplifyOptions? options = null)
    {
        var rcol = new GenericRCOLResource(0, new MemoryStream(source));
        var geom = rcol.ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().FirstOrDefault() ?? throw new InvalidDataException("no GEOM chunk");
        var count = geom.VertexData.Count;
        if (count == 0 || geom.Faces.Count == 0) throw new InvalidDataException("the GEOM has no vertices or faces");

        var positions = new Vector3[count];
        var skin = new VertexSkin[count];
        bool hasBones = false, hasWeights = false;
        for (var i = 0; i < count; i++)
        {
            uint bones = 0;
            var weights = Vector4.Zero;
            foreach (var element in geom.VertexData[i].Vertex)
            {
                // Exact types: NormalElement and TangentNormalElement derive from PositionElement, VertexIDElement from BoneAssignmentElement.
                var type = element.GetType();
                if (type == typeof(GEOM.PositionElement)) { var p = (GEOM.PositionElement)element; positions[i] = new Vector3(p.X, p.Y, p.Z); }
                else if (type == typeof(GEOM.BoneAssignmentElement)) { bones = ((GEOM.BoneAssignmentElement)element).ID; hasBones = true; }
                else if (type == typeof(GEOM.WeightsElement)) { var w = (GEOM.WeightsElement)element; weights = new Vector4(w.W1, w.W2, w.W3, w.W4); hasWeights = true; }
            }
            skin[i] = new VertexSkin(bones, weights);
        }

        var indices = new uint[geom.Faces.Count * 3];
        for (var f = 0; f < geom.Faces.Count; f++)
        {
            var face = geom.Faces[f];
            (indices[3 * f], indices[3 * f + 1], indices[3 * f + 2]) = (face.VertexDataIndex0, face.VertexDataIndex1, face.VertexDataIndex2);
        }
        foreach (var index in indices)
            if (index >= count) throw new InvalidDataException($"face index {index} out of range ({count} vertices)");

        var input = new SimplifierInput(positions, indices) { Skin = hasBones && hasWeights ? skin : [] };
        var deltas = morphDeltas?.Where(d => d.Length == count).ToList() ?? [];
        if (deltas.Count > 0)
        {
            var stride = 3 * deltas.Count;
            var attributes = new float[stride * count];
            for (var v = 0; v < count; v++)
                for (var m = 0; m < deltas.Count; m++)
                {
                    var d = deltas[m][v];
                    (attributes[v * stride + 3 * m], attributes[v * stride + 3 * m + 1], attributes[v * stride + 3 * m + 2]) = (d.X, d.Y, d.Z);
                }
            input = input with { Attributes = attributes, AttributeStride = stride };
        }

        var simplified = MeshSimplifier.Simplify(input, ratio, options);

        var formats = geom.VertexFormats;
        geom.VertexData = new GEOM.VertexDataList(null,
            simplified.SourceVertices.Select(v => new GEOM.VertexDataElement(0, null, geom.VertexData[v].Vertex, formats)), formats);
        var faces = new List<GEOM.Face>(simplified.TriangleCount);
        for (var t = 0; t < simplified.TriangleCount; t++)
            faces.Add(new GEOM.Face(0, null, (ushort)simplified.Indices[3 * t], (ushort)simplified.Indices[3 * t + 1], (ushort)simplified.Indices[3 * t + 2]));
        geom.Faces = new GEOM.FaceList(null, faces);

        using var copy = new MemoryStream();
        rcol.Stream.Position = 0;
        rcol.Stream.CopyTo(copy);
        return new GeomLodResult(copy.ToArray(), count, simplified.SourceVertices.Length, simplified.TrianglesBefore, simplified.TriangleCount,
            simplified.TargetTriangles);
    }
}
