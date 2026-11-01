using OpenSimsStudio3.Formats.Install;
using Morph = OpenSimsStudio3.Formats.Meshes.BlendGeometry.Morph;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>Body-shape weights as CAS sets them: Weight runs thin (-1) to fat (+1); Fitness and Pregnancy run 0 to 1.</summary>
public readonly record struct BodyShape(float Weight, float Fitness, float Pregnancy)
{
    public bool IsNeutral => Weight == 0 && Fitness == 0 && Pregnancy == 0;
}

/// <summary>
/// A mesh with its part's fat, fit, thin and special (pregnancy) BGEO deltas, matched by VertexID. <see cref="Apply" />
/// adds the weighted position deltas and renormalises the normals after adding the weighted normal deltas (how the
/// game applies normal deltas is unconfirmed; see cas-formats.md section 2.6).
/// </summary>
public sealed class MorphedMesh
{
    readonly MorphDeltas?[] _deltas;

    MorphedMesh(GeomMesh mesh, MorphDeltas?[] deltas)
    {
        Mesh = mesh;
        _deltas = deltas;
    }

    public GeomMesh Mesh { get; }

    public bool Has(Morph morph) => _deltas[(int)morph] is { PositionDeltas: > 0 };

    /// <summary>Reads the part's BGEOs from <paramref name="source" />; a morph it lacks (or can't read) stays at zero.</summary>
    public static MorphedMesh Load(IResourceSource source, CASPartResource.CASPartResource casp, GeomMesh mesh)
    {
        var deltas = new MorphDeltas?[4];
        foreach (var morph in Enum.GetValues<Morph>())
        {
            try
            {
                if (BlendGeometry.Find(source, casp, morph) is not { } key || source.Read(key) is not { } bytes) continue;
                var parsed = BlendGeometry.Parse(bytes);
                if (BlendGeometry.LodFor(parsed, mesh) is { } lod) deltas[(int)morph] = BlendGeometry.Read(parsed, lod, mesh);
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException or IndexOutOfRangeException) { }
        }
        return new MorphedMesh(mesh, deltas);
    }

    public GeomMesh Apply(BodyShape shape)
    {
        if (shape.IsNeutral) return Mesh;
        var weights = new[] { Math.Max(shape.Weight, 0), shape.Fitness, Math.Max(-shape.Weight, 0), shape.Pregnancy };
        var positions = (System.Numerics.Vector3[])Mesh.Positions.Clone();
        var normals = (System.Numerics.Vector3[])Mesh.Normals.Clone();
        for (var m = 0; m < 4; m++)
        {
            if (weights[m] == 0 || _deltas[m] is not { } d) continue;
            for (var v = 0; v < positions.Length; v++)
            {
                positions[v] += weights[m] * d.Positions[v];
                normals[v] += weights[m] * d.Normals[v];
            }
        }
        for (var v = 0; v < normals.Length; v++)
            if (normals[v].LengthSquared() > 1e-12f) normals[v] = System.Numerics.Vector3.Normalize(normals[v]);
        return Mesh with { Positions = positions, Normals = normals };
    }
}
