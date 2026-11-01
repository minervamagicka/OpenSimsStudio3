using Vector3 = System.Numerics.Vector3;
using meshExpImp.ModelBlocks;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>Reads Sim meshes (GEOM, 0x015A1849) out of packages through s3pi.</summary>
public static class GeomLoader
{
    public const uint GeomType = 0x015A1849;

    /// <summary>One GEOM's key inside a package.</summary>
    public sealed record GeomEntry(ulong Instance, uint Group, IResourceIndexEntry IndexEntry)
    {
        public string Key => $"{GeomType:X8}:{Group:X8}:{Instance:X16}";
    }

    /// <summary>Lists every GEOM in a package (opened read-only).</summary>
    public static (IPackage Package, List<GeomEntry> Geoms) Open(string packagePath)
    {
        var package = s3pi.Package.Package.OpenPackage(0, packagePath, false);
        var geoms = package.GetResourceList
            .Where(e => e.ResourceType == GeomType && !e.IsDeleted)
            .Select(e => new GeomEntry(e.Instance, e.ResourceGroup, e))
            .ToList();
        return (package, geoms);
    }

    public static GeomMesh Load(IPackage package, GeomEntry entry)
    {
        using var stream = ((s3pi.Package.Package)package).GetResource(entry.IndexEntry);
        return Read(stream, entry.Key);
    }

    /// <summary>Parses one GEOM resource (an RCOL with a single GEOM chunk).</summary>
    public static GeomMesh Read(Stream stream, string name)
    {
        var rcol = new GenericRCOLResource(0, stream);
        var geom = rcol.ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().FirstOrDefault()
            ?? throw new InvalidDataException($"{name}: no GEOM chunk in this RCOL resource");

        var count = geom.VertexData.Count;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new System.Numerics.Vector2[count];
        var hasNormals = false;
        var hasUvs = false;
        var ids = new uint[count];
        var hasIds = false;
        var uvSeen = new bool[count];
        bool hasUvsFor(int v) => uvSeen[v];
        for (var i = 0; i < count; i++)
        {
            foreach (var element in geom.VertexData[i].Vertex)
            {
                // NormalElement derives from PositionElement, so match the exact type.
                if (element.GetType() == typeof(GEOM.PositionElement))
                {
                    var p = (GEOM.PositionElement)element;
                    positions[i] = new Vector3(p.X, p.Y, p.Z);
                }
                else if (element is GEOM.NormalElement n)
                {
                    normals[i] = new Vector3(n.X, n.Y, n.Z);
                    hasNormals = true;
                }
                else if (element is GEOM.VertexIDElement id)
                {
                    ids[i] = id.ID;
                    hasIds = true;
                }
                else if (element is GEOM.UVElement uv && !hasUvsFor(i))
                {
                    uvs[i] = new System.Numerics.Vector2(uv.U, uv.V);
                    hasUvs = true;
                    uvSeen[i] = true;
                }
            }
        }

        var indices = new uint[geom.Faces.Count * 3];
        for (var f = 0; f < geom.Faces.Count; f++)
        {
            var face = geom.Faces[f];
            indices[3 * f] = face.VertexDataIndex0;
            indices[3 * f + 1] = face.VertexDataIndex1;
            indices[3 * f + 2] = face.VertexDataIndex2;
        }
        foreach (var index in indices)
            if (index >= count) throw new InvalidDataException($"{name}: face index {index} out of range ({count} vertices)");

        // Morph GEOMs (shader 0) carry deltas, not a drawable shape; they still load, the caller decides.
        if (!hasNormals) normals = ComputeNormals(positions, indices);
        return new GeomMesh(name, positions, normals, indices, geom.BoneHashes.Count, (uint)geom.Shader) { Uvs = hasUvs ? uvs : [], VertexIds = hasIds ? ids : [] };
    }

    static Vector3[] ComputeNormals(Vector3[] positions, uint[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (var i = 0; i < indices.Length; i += 3)
        {
            var (a, b, c) = (positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]);
            var n = Vector3.Cross(b - a, c - a);
            normals[indices[i]] += n; normals[indices[i + 1]] += n; normals[indices[i + 2]] += n;
        }
        for (var i = 0; i < normals.Length; i++)
            normals[i] = normals[i] == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(normals[i]);
        return normals;
    }
}
