using meshExpImp.ModelBlocks;
using OpenSimsStudio3.Formats.Meshes;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Tests;

/// <summary>GeomLoader's UV sets, on a GEOM built here with s3pi (one triangle, two UV sets).</summary>
public class GeomLoaderUvTests
{
    static byte[] TwoUvSetGeom()
    {
        var formats = new GEOM.VertexFormatList(null, new[] { GEOM.UsageType.Position, GEOM.UsageType.UV, GEOM.UsageType.UV }
            .Select(u => new GEOM.VertexFormat(0, null, u)));
        (float X, float Y, float U0, float V0, float U1, float V1)[] corners = [(0, 0, 0.1f, 0.2f, 0.5f, 0.6f), (1, 0, 0.3f, 0.2f, 0.7f, 0.6f), (0, 1, 0.1f, 0.4f, 0.5f, 0.8f)];
        var vertices = corners.Select(c => new GEOM.VertexDataElement(0, null, new GEOM.ElementList(null, new GEOM.VertexElement[]
        {
            new GEOM.PositionElement(0, null, c.X, c.Y, 0), new GEOM.UVElement(0, null, c.U0, c.V0), new GEOM.UVElement(0, null, c.U1, c.V1),
        }, formats), formats)).ToList();
        // RCOL chunk keys are stored instance-type-group. Shader 0 needs no MTNF block (morph GEOMs are stored this way).
        var geom = new GEOM(0, null, 5, 0, null, 0, 0, formats, vertices, [new GEOM.Face(0, null, 0, 1, 2)], 0, [], []);
        var rcol = new GenericRCOLResource(0, null);
        rcol.ChunkEntries.Add(new GenericRCOLResource.ChunkEntry(0, null, new TGIBlock(0, null, "ITG", GeomLoader.GeomType, 0, 1), geom));
        using var copy = new MemoryStream();
        rcol.Stream.Position = 0;
        rcol.Stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void Every_uv_set_is_read_in_order()
    {
        var mesh = GeomLoader.Read(new MemoryStream(TwoUvSetGeom()), "test");
        Assert.Equal(2, mesh.UvChannels.Count);
        Assert.Equal(new System.Numerics.Vector2(0.3f, 0.2f), mesh.Uvs[1]);
        Assert.Equal(new System.Numerics.Vector2(0.7f, 0.6f), mesh.ExtraUvs[0][1]);
        Assert.True(UvAnalysis.Analyze(mesh).IsClean);
        Assert.Equal(2, UvAnalysis.Analyze(mesh).Channels);
    }
}
