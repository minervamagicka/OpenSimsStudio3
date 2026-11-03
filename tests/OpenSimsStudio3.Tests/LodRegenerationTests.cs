using System.Numerics;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Formats.Projects;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;
using GEOM = meshExpImp.ModelBlocks.GEOM;

namespace OpenSimsStudio3.Tests;

/// <summary>LOD regeneration on GEOMs, VPXYs and a CASP built here with s3pi (no EA data).</summary>
public class LodRegenerationTests
{
    const uint Shader = 0x548394B9; // any non-zero shader: the GEOM then carries an MTNF block
    static readonly uint[] BoneHashes = [0x11111111, 0x22222222];
    static readonly GEOM.UsageType[] Format =
    [
        GEOM.UsageType.Position, GEOM.UsageType.Normal, GEOM.UsageType.UV, GEOM.UsageType.BoneAssignment, GEOM.UsageType.Weights,
        GEOM.UsageType.TangentNormal, GEOM.UsageType.Color, GEOM.UsageType.VertexID,
    ];

    /// <summary>A GEOM of the skinned strip with every element TS3 CAS meshes carry, each vertex's values unique to it.</summary>
    static byte[] StripGeom(int along = 40, int across = 8, uint firstId = 0)
    {
        var strip = TestMeshes.SkinnedStrip(along, across);
        var formats = new GEOM.VertexFormatList(null, Format.Select(u => new GEOM.VertexFormat(0, null, u)));
        var vertices = strip.Positions.Select((p, i) =>
        {
            var skin = strip.Skin[i];
            return new GEOM.VertexDataElement(0, null, new GEOM.ElementList(null, new GEOM.VertexElement[]
            {
                new GEOM.PositionElement(0, null, p.X, p.Y, p.Z),
                new GEOM.NormalElement(0, null, 0, 0, 1),
                new GEOM.UVElement(0, null, strip.Uvs[i].X, strip.Uvs[i].Y),
                new GEOM.BoneAssignmentElement(0, null, skin.Bones),
                new GEOM.WeightsElement(0, null, skin.Weights.X, skin.Weights.Y, skin.Weights.Z, skin.Weights.W),
                new GEOM.TangentNormalElement(0, null, 1, 0, 0.001f * i),
                new GEOM.ColorElement(0, null, 0xFF000000u | (uint)i),
                new GEOM.VertexIDElement(0, null, firstId + (uint)i),
            }, formats), formats);
        });
        var faces = Enumerable.Range(0, strip.Indices.Length / 3)
            .Select(t => new GEOM.Face(0, null, (ushort)strip.Indices[3 * t], (ushort)strip.Indices[3 * t + 1], (ushort)strip.Indices[3 * t + 2]));
        var mtnf = new s3pi.GenericRCOLResource.MTNF(0, null, "GEOM");
        var geom = new GEOM(0, null, 5, (s3pi.GenericRCOLResource.ShaderType)Shader, mtnf, 7, 3, formats, vertices, faces, 0, BoneHashes, []);
        var rcol = new GenericRCOLResource(0, null);
        rcol.ChunkEntries.Add(new GenericRCOLResource.ChunkEntry(0, null, new TGIBlock(0, null, "ITG", GeomLoader.GeomType, 0, 1), geom));
        return Bytes(rcol);
    }

    static byte[] Bytes(IResource resource)
    {
        using var copy = new MemoryStream();
        resource.Stream.Position = 0;
        resource.Stream.CopyTo(copy);
        return copy.ToArray();
    }

    static GEOM Parse(byte[] bytes) => new GenericRCOLResource(0, new MemoryStream(bytes)).ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().Single();

    /// <summary>A vertex's elements as comparable text (exact float bits through ToString("R")).</summary>
    static string Describe(GEOM.VertexDataElement v) => string.Join("|", v.Vertex.Select(e => e switch
    {
        GEOM.TangentNormalElement t => $"T{t.X:R},{t.Y:R},{t.Z:R}",
        GEOM.NormalElement n => $"N{n.X:R},{n.Y:R},{n.Z:R}",
        GEOM.PositionElement p => $"P{p.X:R},{p.Y:R},{p.Z:R}",
        GEOM.UVElement u => $"U{u.U:R},{u.V:R}",
        GEOM.VertexIDElement id => $"I{id.ID}",
        GEOM.BoneAssignmentElement b => $"B{b.ID:X8}",
        GEOM.WeightsElement w => $"W{w.W1:R},{w.W2:R},{w.W3:R},{w.W4:R}",
        GEOM.ColorElement c => $"C{OpenSimsStudio3.Formats.Projects.ProjectSession.ColourOf(c):X8}",
        _ => e.GetType().Name,
    }));

    [Fact]
    public void Built_GEOM_round_trips_through_s3pi_with_its_header_and_whole_vertices()
    {
        var source = StripGeom();
        var before = Parse(source);
        var made = GeomLodBuilder.Build(source, 0.25);
        var after = Parse(made.Bytes);

        Assert.Equal(Shader, (uint)after.Shader);
        Assert.NotNull(after.Mtnf);
        Assert.Equal(BoneHashes, after.BoneHashes.ToArray());
        Assert.Equal(before.VertexFormats.Select(f => f.Usage), after.VertexFormats.Select(f => f.Usage));
        Assert.Equal((before.MergeGroup, before.SortOrder, before.SkinIndex), (after.MergeGroup, after.SortOrder, after.SkinIndex));
        Assert.Equal(made.TrianglesAfter, after.Faces.Count);
        Assert.Equal(made.VerticesAfter, after.VertexData.Count);
        Assert.InRange(after.Faces.Count, 1, made.TargetTriangles);
        Assert.True(after.VertexData.Count < before.VertexData.Count);

        // Every kept vertex is one of the source's, with all its elements unchanged; weights sum to 1 on the two bones.
        var original = before.VertexData.Select(Describe).ToHashSet();
        foreach (var v in after.VertexData)
        {
            Assert.Contains(Describe(v), original);
            var w = v.Vertex.OfType<GEOM.WeightsElement>().Single();
            Assert.Equal(1f, w.W1 + w.W2 + w.W3 + w.W4, 5);
            var bones = v.Vertex.Single(e => e.GetType() == typeof(GEOM.BoneAssignmentElement)) as GEOM.BoneAssignmentElement;
            for (var s = 0; s < 4; s++)
                if (new[] { w.W1, w.W2, w.W3, w.W4 }[s] > 0) Assert.InRange((int)((bones!.ID >> (8 * s)) & 0xFF), 0, BoneHashes.Length - 1);
        }
        Assert.All(after.Faces, f => Assert.True(Math.Max(f.VertexDataIndex0, Math.Max(f.VertexDataIndex1, f.VertexDataIndex2)) < after.VertexData.Count));

        // OSS3's own reader takes it, and writing it again through s3pi gives the same bytes.
        var mesh = GeomLoader.Read(new MemoryStream(made.Bytes), "lod");
        Assert.Equal(made.VerticesAfter, mesh.Positions.Length);
        Assert.Equal(made.Bytes, Bytes(new GenericRCOLResource(0, new MemoryStream(made.Bytes))));
        // Same input, same bytes.
        Assert.Equal(made.Bytes, GeomLodBuilder.Build(source, 0.25).Bytes);
    }

    [Fact]
    public void Default_ratios_halve_per_LOD()
    {
        Assert.Equal(1, GeomLodBuilder.DefaultRatio(0));
        Assert.Equal(0.5, GeomLodBuilder.DefaultRatio(1));
        Assert.Equal(0.25, GeomLodBuilder.DefaultRatio(2));
        Assert.Equal(0.125, GeomLodBuilder.DefaultRatio(3));
    }

    // ---- In a project ----

    static readonly ResourceKey CaspKey = new(0x034AEECB, 0, 0x10);
    static readonly ResourceKey VpxyKey = new(0x736884F1, 0, 0x20);
    static readonly ResourceKey Lod0Group0 = new(GeomLoader.GeomType, 0, 0x100), Lod0Group1 = new(GeomLoader.GeomType, 0, 0x101);
    static readonly ResourceKey Lod1Group0 = new(GeomLoader.GeomType, 0, 0x110), Lod1Group1 = new(GeomLoader.GeomType, 0, 0x111);
    static readonly ResourceKey Lod2Group0 = new(GeomLoader.GeomType, 0, 0x120);

    static byte[] Vpxy(params (byte Id, ResourceKey[] Groups)[] lods)
    {
        var keys = lods.SelectMany(l => l.Groups).Distinct().ToList();
        var tgis = keys.Select(k => new TGIBlock(0, null, k.Type, k.Group, k.Instance)).ToList();
        var entries = lods.Select(l => (s3pi.GenericRCOLResource.VPXY.Entry)new s3pi.GenericRCOLResource.VPXY.Entry00(0, null, 0, l.Id,
            l.Groups.Select(g => keys.IndexOf(g)))).ToList();
        var vpxy = new s3pi.GenericRCOLResource.VPXY(0, null, 4, entries, 2, new BoundingBox(0, null), new byte[4], 0, 0, tgis);
        var rcol = new GenericRCOLResource(0, null);
        rcol.ChunkEntries.Add(new GenericRCOLResource.ChunkEntry(0, null, new TGIBlock(0, null, "ITG", VpxyKey.Type, VpxyKey.Group, VpxyKey.Instance), vpxy));
        return Bytes(rcol);
    }

    /// <summary>LOD 0 with two groups, LOD 1 with two, LOD 2 with one (fewer than LOD 0); the lower LODs start as copies of a tiny strip.</summary>
    static ProjectSession Open(TempFolder temp, params (byte Id, ResourceKey[] Groups)[] lods)
    {
        var casp = new CASPartResource.CASPartResource(0, null);
        casp.TGIBlocks.Add(new TGIBlock(0, null, VpxyKey.Type, VpxyKey.Group, VpxyKey.Instance));
        new Project { Id = "lod-test", Name = "Test", Casp = CaspKey.ToString(), Package = "Test.package" }.Write(temp.Path);
        var tiny = StripGeom(2, 1);
        TestPackages.Write(temp["Test.package"], (CaspKey, Bytes(casp)), (VpxyKey, Vpxy(lods)),
            (Lod0Group0, StripGeom()), (Lod0Group1, StripGeom(20, 4, 1000)), (Lod1Group0, tiny), (Lod1Group1, tiny), (Lod2Group0, tiny));
        return ProjectSession.Open(temp.Path, new FakeGame([]));
    }

    static (byte, ResourceKey[])[] ThreeLods => [(0, [Lod0Group0, Lod0Group1]), (1, [Lod1Group0, Lod1Group1]), (2, [Lod2Group0])];

    [Fact]
    public void Regenerate_replaces_the_LODs_groups_from_LOD_0_and_never_touches_LOD_0()
    {
        using var temp = new TempFolder();
        var session = Open(temp, ThreeLods);
        var lod0Before = (session.Read(Lod0Group0)!, session.Read(Lod0Group1)!);
        Assert.Equal([0, 1, 2], session.Lods.Select(l => l.Id));
        Assert.Equal(0.5, LodRegeneration.DefaultRatio(session, session.Lods[1]));
        Assert.Equal(0.25, LodRegeneration.DefaultRatio(session, session.Lods[2]));

        var result = LodRegeneration.Regenerate(session, 1);
        Assert.Equal(0.5, result.Ratio);
        Assert.Equal([Lod1Group0, Lod1Group1], result.Groups.Select(g => g.Key));
        Assert.Equal(640, result.Groups[0].SourceTriangles);
        Assert.Equal(4, result.Groups[0].TrianglesBefore);
        Assert.InRange(result.Groups[0].TrianglesAfter, 1, 320);
        Assert.Empty(result.Warnings);
        Assert.True(session.Dirty);

        // The outline sees the new counts, and LOD 0 is byte for byte the same.
        Assert.Equal(result.Groups[0].TrianglesAfter, session.Lods[1].Groups[0].Triangles);
        Assert.Equal(lod0Before, (session.Read(Lod0Group0)!, session.Read(Lod0Group1)!));

        // LOD 2 has one group: it is made from LOD 0's group 0 at 25%; LOD 0's group 1 gets a warning.
        var low = LodRegeneration.Regenerate(session, 2);
        Assert.Single(low.Groups);
        Assert.InRange(low.Groups[0].TrianglesAfter, 1, 160);
        Assert.Contains(low.Warnings, w => w.Contains("group 1 has no group in LOD 2"));

        // Saved like a re-import: the package holds the new GEOM under the same key.
        session.Save();
        var saved = TestPackages.ReadAll(temp["Test.package"]);
        Assert.Equal(session.Read(Lod1Group0), saved[Lod1Group0]);
        Assert.Equal(lod0Before.Item1, saved[Lod0Group0]);
    }

    [Fact]
    public void LOD_0_cannot_be_regenerated()
    {
        using var temp = new TempFolder();
        var session = Open(temp, ThreeLods);
        Assert.False(LodRegeneration.CanRegenerate(session, session.Lods[0]));
        Assert.Throws<InvalidOperationException>(() => LodRegeneration.Regenerate(session, 0));
        Assert.False(session.Dirty);
    }

    [Fact]
    public void A_group_sharing_LOD_0s_GEOM_is_left_alone()
    {
        using var temp = new TempFolder();
        var session = Open(temp, (0, [Lod0Group0, Lod0Group1]), (1, [Lod0Group0, Lod1Group1]));
        var before = session.Read(Lod0Group0)!;
        var result = LodRegeneration.Regenerate(session, 1);
        Assert.Equal([Lod1Group1], result.Groups.Select(g => g.Key));
        Assert.Contains(result.Warnings, w => w.Contains("same GEOM as LOD 0"));
        Assert.Equal(before, session.Read(Lod0Group0));
    }
}
