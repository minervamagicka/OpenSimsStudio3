using meshExpImp.ModelBlocks;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Materials;
using OpenSimsStudio3.Formats.Meshes;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Tests;

/// <summary>
/// The GEOM material model, on GEOMs built here with s3pi (one triangle, a material with one parameter of every kind).
/// No game data: the keys and values are made up for the test.
/// </summary>
public class GeomMaterialTests
{
    static readonly ResourceKey Diffuse = new(0x00B2D882, 0, 0x1111111111111111);
    static readonly ResourceKey Specular = new(0x00B2D882, 0, 0x2222222222222222);
    static readonly ResourceKey Normal = new(0x00B2D882, 0, 0x3333333333333333);
    static readonly ResourceKey Inline = new(0x00B2D882, 0x00000001, 0x4444444444444444);

    static uint H(string name) => ShaderNames.Fnv32(name);

    /// <summary>A SimSkin GEOM; its key list holds a skin key, then the diffuse and specular textures.</summary>
    static byte[] BuildGeom(uint shader = 0x548394B9)
    {
        var formats = new GEOM.VertexFormatList(null, new[] { GEOM.UsageType.Position, GEOM.UsageType.UV }.Select(u => new GEOM.VertexFormat(0, null, u)));
        (float X, float Y)[] corners = [(0, 0), (1, 0), (0, 1)];
        var vertices = corners.Select(c => new GEOM.VertexDataElement(0, null, new GEOM.ElementList(null, new GEOM.VertexElement[]
        {
            new GEOM.PositionElement(0, null, c.X, c.Y, 0), new GEOM.UVElement(0, null, c.X, c.Y),
        }, formats), formats)).ToList();
        var keys = new[] { new TGIBlock(0, null, 0x8EAF13DE, 0, 0x5555555555555555), Tgi(Diffuse), Tgi(Specular) };

        var mtnf = new MTNF(0, null, "GEOM") { MTNFUnknown1 = 0x12345678 };
        mtnf.SData = new ShaderDataList(null, new ShaderData[]
        {
            new ElementTextureRef(0, null, (FieldType)H("DiffuseMap"), 1, null),
            new ElementTextureRef(0, null, (FieldType)H("SpecularMap"), 2, null),
            new ElementFloat(0, null, (FieldType)H("Shininess"), 25.5f),
            new ElementFloat2(0, null, (FieldType)H("DiffuseUVScale"), 1f, 2f),
            new ElementFloat3(0, null, (FieldType)H("Diffuse"), 0.25f, 0.5f, 0.75f),
            new ElementFloat4(0, null, (FieldType)H("UVOffset"), 1f, 2f, 3f, 4f),
            new ElementInt(0, null, (FieldType)H("MaskWidth"), 512),
            new ElementTextureKey(0, null, (FieldType)H("ImposterTexture"), Inline.Type, Inline.Group, Inline.Instance),
        }, null, "GEOM");
        var geom = new GEOM(0, null, 5, (ShaderType)shader, mtnf, 0, 0, formats, vertices, [new GEOM.Face(0, null, 0, 1, 2)], 0, [], keys);
        var rcol = new GenericRCOLResource(0, null);
        rcol.ChunkEntries.Add(new GenericRCOLResource.ChunkEntry(0, null, new TGIBlock(0, null, "ITG", GeomLoader.GeomType, 0, 1), geom));
        using var copy = new MemoryStream();
        rcol.Stream.Position = 0;
        rcol.Stream.CopyTo(copy);
        // Re-read once so the bytes are what s3pi itself reads and writes (as a GEOM from a package would be).
        return copy.ToArray();
    }

    static TGIBlock Tgi(ResourceKey k) => new(0, null, k.Type, k.Group, k.Instance);

    static GeomMaterial ReadMaterial(byte[] bytes) => GeomMaterial.Read(bytes) ?? throw new Xunit.Sdk.XunitException("no material");

    [Fact]
    public void Reads_shader_and_every_parameter_kind()
    {
        var m = ReadMaterial(BuildGeom());
        Assert.Equal(0x548394B9u, m.Shader);
        Assert.Equal("SimSkin", m.ShaderLabel);
        Assert.Equal(8, m.Parameters.Count);

        var diffuse = m.Find(H("DiffuseMap"))!;
        Assert.Equal(ShaderParameterKind.Texture, diffuse.Kind);
        Assert.Equal("DiffuseMap", diffuse.Name);
        Assert.Equal(Diffuse, diffuse.Texture);
        Assert.Equal(1, diffuse.TextureIndex);
        Assert.Equal(Specular, m.Find(H("SpecularMap"))!.Texture);
        Assert.Equal([25.5f], m.Find(H("Shininess"))!.Floats);
        Assert.Equal([1f, 2f], m.Find(H("DiffuseUVScale"))!.Floats);
        Assert.Equal([0.25f, 0.5f, 0.75f], m.Find(H("Diffuse"))!.Floats);
        Assert.Equal([1f, 2f, 3f, 4f], m.Find(H("UVOffset"))!.Floats);
        Assert.Equal(512, m.Find(H("MaskWidth"))!.IntValue);
        var inline = m.Find(H("ImposterTexture"))!;
        Assert.Equal(ShaderParameterKind.TextureKey, inline.Kind);
        Assert.Equal(Inline, inline.Texture);
    }

    [Fact]
    public void Unedited_material_writes_the_original_bytes()
    {
        var original = BuildGeom();
        var written = ReadMaterial(original).Write(original);
        Assert.Equal(original, written);
    }

    [Fact]
    public void Edited_then_reverted_material_writes_the_original_bytes()
    {
        var original = BuildGeom();
        var m = ReadMaterial(original);
        var p = m.Find(H("Shininess"))!;
        p.SetFloat(0, 3f);
        p.SetFloat(0, 25.5f);
        Assert.Equal(original, m.Write(original));
    }

    [Fact]
    public void Edits_of_every_kind_survive_write_and_reread()
    {
        var original = BuildGeom();
        var m = ReadMaterial(original);
        m.Find(H("Shininess"))!.Floats = [7f];
        m.Find(H("DiffuseUVScale"))!.SetFloat(1, -3.5f);
        m.Find(H("Diffuse"))!.Floats = [1f, 0f, 0.5f];
        m.Find(H("UVOffset"))!.SetFloat(3, 0.125f);
        m.Find(H("MaskWidth"))!.IntValue = 1024;
        m.Find(H("SpecularMap"))!.Texture = Diffuse; // already in the key list: reused, no new key
        m.Find(H("DiffuseMap"))!.Texture = Normal;   // new key: appended
        m.Find(H("ImposterTexture"))!.Texture = Specular;

        var bytes = m.Write(original);
        Assert.NotEqual(original, bytes);
        var r = ReadMaterial(bytes);
        Assert.Equal([7f], r.Find(H("Shininess"))!.Floats);
        Assert.Equal([1f, -3.5f], r.Find(H("DiffuseUVScale"))!.Floats);
        Assert.Equal([1f, 0f, 0.5f], r.Find(H("Diffuse"))!.Floats);
        Assert.Equal([1f, 2f, 3f, 0.125f], r.Find(H("UVOffset"))!.Floats);
        Assert.Equal(1024, r.Find(H("MaskWidth"))!.IntValue);
        Assert.Equal(Diffuse, r.Find(H("SpecularMap"))!.Texture);
        Assert.Equal(1, r.Find(H("SpecularMap"))!.TextureIndex);
        Assert.Equal(Normal, r.Find(H("DiffuseMap"))!.Texture);
        Assert.Equal(3, r.Find(H("DiffuseMap"))!.TextureIndex);
        Assert.Equal(Specular, r.Find(H("ImposterTexture"))!.Texture);
        Assert.Equal(4, r.Keys.Count);
        Assert.Equal(new ResourceKey(0x8EAF13DE, 0, 0x5555555555555555), r.Keys[0]);

        // The mesh itself is untouched.
        var mesh = GeomLoader.Read(new MemoryStream(bytes), "edited");
        Assert.Equal(3, mesh.Positions.Length);
        Assert.Equal(GeomLoader.Read(new MemoryStream(original), "original").Uvs, mesh.Uvs);

        // And the edited file round-trips byte-exactly through s3pi like any other GEOM.
        Assert.True(OpenSimsStudio3.Formats.Validation.RoundTrip.Rcol(bytes).Passed);
        Assert.Equal(bytes, r.Write(bytes));
    }

    [Fact]
    public void Shader_change_add_and_remove()
    {
        var original = BuildGeom();
        var m = ReadMaterial(original);
        m.Shader = ShaderNames.Fnv32("SimHair");
        Assert.True(m.Remove(H("MaskWidth")));
        Assert.False(m.Remove(H("MaskWidth")));
        var added = m.Add(H("RootColor"), ShaderParameterKind.Float3);
        added.Floats = [0.1f, 0.2f, 0.3f];
        m.Add(H("NormalMap"), ShaderParameterKind.Texture, Normal);
        m.Add(H("AverageColor"), ShaderParameterKind.Int).IntValue = -5;
        Assert.Throws<InvalidOperationException>(() => m.Add(H("Shininess"), ShaderParameterKind.Float));
        Assert.Throws<ArgumentException>(() => m.Shader = 0);

        var r = ReadMaterial(m.Write(original));
        Assert.Equal(0x84FD7152u, r.Shader);
        Assert.Equal("SimHair", r.ShaderLabel);
        Assert.Null(r.Find(H("MaskWidth")));
        Assert.Equal(10, r.Parameters.Count);
        Assert.Equal([0.1f, 0.2f, 0.3f], r.Find(H("RootColor"))!.Floats);
        Assert.Equal(Normal, r.Find(H("NormalMap"))!.Texture);
        Assert.Equal(-5, r.Find(H("AverageColor"))!.IntValue);
        // New parameters go at the end, in the order added.
        Assert.Equal(["RootColor", "NormalMap", "AverageColor"], r.Parameters.Skip(7).Select(p => p.Label));
    }

    [Fact]
    public void Removing_every_parameter_leaves_an_empty_material()
    {
        var original = BuildGeom();
        var m = ReadMaterial(original);
        foreach (var p in m.Parameters.ToList()) m.Remove(p.Field);
        var r = ReadMaterial(m.Write(original));
        Assert.Empty(r.Parameters);
        Assert.Equal(0x548394B9u, r.Shader);
    }

    [Fact]
    public void Unknown_hashes_keep_their_value_and_show_as_hex()
    {
        var original = BuildGeom(shader: 0xDEADBEEF);
        var m = ReadMaterial(original);
        Assert.Equal("0xDEADBEEF", m.ShaderLabel);
        m.Add(0x0BADF00D, ShaderParameterKind.Float).Floats = [1.5f];
        var r = ReadMaterial(m.Write(original));
        Assert.Equal(0xDEADBEEFu, r.Shader);
        Assert.Equal("0x0BADF00D", r.Find(0x0BADF00D)!.Label);
        Assert.Null(r.Find(0x0BADF00D)!.Name);
    }

    [Fact]
    public void Type_checks_on_values()
    {
        var m = ReadMaterial(BuildGeom());
        Assert.Throws<ArgumentException>(() => m.Find(H("Diffuse"))!.Floats = [1f]);
        Assert.Throws<InvalidOperationException>(() => m.Find(H("MaskWidth"))!.SetFloat(0, 1f));
        Assert.Throws<InvalidOperationException>(() => m.Find(H("Shininess"))!.Texture = Diffuse);
        Assert.Throws<ArgumentNullException>(() => m.Find(H("ImposterTexture"))!.Texture = null);
    }

    [Fact]
    public void Morph_geoms_have_no_material()
    {
        var formats = new GEOM.VertexFormatList(null, [new GEOM.VertexFormat(0, null, GEOM.UsageType.Position)]);
        var vertices = Enumerable.Range(0, 3).Select(i => new GEOM.VertexDataElement(0, null,
            new GEOM.ElementList(null, new GEOM.VertexElement[] { new GEOM.PositionElement(0, null, i, 0, 0) }, formats), formats)).ToList();
        var geom = new GEOM(0, null, 5, 0, null, 0, 0, formats, vertices, [new GEOM.Face(0, null, 0, 1, 2)], 0, [], []);
        var rcol = new GenericRCOLResource(0, null);
        rcol.ChunkEntries.Add(new GenericRCOLResource.ChunkEntry(0, null, new TGIBlock(0, null, "ITG", GeomLoader.GeomType, 0, 1), geom));
        using var copy = new MemoryStream();
        rcol.Stream.Position = 0;
        rcol.Stream.CopyTo(copy);
        Assert.Null(GeomMaterial.Read(copy.ToArray()));
    }
}

/// <summary>The shader and parameter name table: every entry's hash is computed from its name.</summary>
public class ShaderNamesTests
{
    [Theory]
    [InlineData("", 0x811C9DC5u)]
    [InlineData("a", 0x050C5D7Eu)]          // FNV-1 (multiply, then xor) of "a"
    [InlineData("SimSkin", 0x548394B9u)]
    [InlineData("simskin", 0x548394B9u)]    // names are hashed lower-cased
    [InlineData("SimHair", 0x84FD7152u)]
    [InlineData("CasSimHair", 0xFCF80CE1u)]
    [InlineData("SimHorse", 0x05F75EF1u)]
    [InlineData("SimPetFur", 0xA78F71C4u)]
    [InlineData("SimPetFace", 0x93B210C6u)]
    [InlineData("DiffuseMap", 0x6CC0FD85u)]
    [InlineData("SpecularMap", 0xAD528A60u)]
    [InlineData("NormalMap", 0x6E56548Au)]
    public void Fnv32_matches_known_values(string name, uint expected) => Assert.Equal(expected, ShaderNames.Fnv32(name));

    [Fact]
    public void Every_shader_name_round_trips_through_its_hash()
    {
        Assert.NotEmpty(ShaderNames.Shaders);
        Assert.Equal(ShaderNames.Shaders.Count, ShaderNames.Shaders.Select(s => s.Hash).Distinct().Count());
        foreach (var (hash, name) in ShaderNames.Shaders)
        {
            Assert.Equal(ShaderNames.Fnv32(name), hash);
            Assert.Equal(name, ShaderNames.ShaderName(hash));
        }
        Assert.Equal(ShaderNames.CasShaderNames, ShaderNames.Shaders.Take(ShaderNames.CasShaderNames.Count).Select(s => s.Name));
    }

    [Fact]
    public void Every_parameter_name_round_trips_through_its_hash()
    {
        Assert.Equal(ShaderNames.KnownParameters.Count, ShaderNames.KnownParameters.Select(p => p.Hash).Distinct().Count());
        foreach (var (hash, name, kind) in ShaderNames.KnownParameters)
        {
            Assert.Equal(ShaderNames.Fnv32(name), hash);
            Assert.Equal(name, ShaderNames.ParameterName(hash));
            Assert.Equal(kind, ShaderNames.ParameterKind(hash));
        }
    }

    /// <summary>
    /// Cross-check with the hashes s3pi's own enums carry (an independent public listing): every name both know agrees,
    /// and our parameter kinds match s3pi's per-name type comments where we list the name.
    /// </summary>
    [Fact]
    public void Table_agrees_with_s3pi_enums()
    {
        var shaderMatches = 0;
        foreach (var value in Enum.GetValues<ShaderType>())
            if (ShaderNames.ShaderName((uint)value) is { } name) { Assert.Equal(value.ToString(), name); shaderMatches++; }
        Assert.True(shaderMatches >= 40, $"only {shaderMatches} s3pi shaders recognised");

        var fieldMatches = 0;
        foreach (var value in Enum.GetValues<FieldType>())
            if (ShaderNames.ParameterName((uint)value) is { } name) { Assert.Equal(value.ToString(), name); fieldMatches++; }
        Assert.Equal(ShaderNames.KnownParameters.Count, fieldMatches);
    }

    [Theory]
    [InlineData("0x548394B9", 0x548394B9u)]
    [InlineData("548394b9", 0x548394B9u)]
    [InlineData("SimSkin", 0x548394B9u)]
    [InlineData(" diffusemap ", 0x6CC0FD85u)]
    public void Parses_hash_or_name(string text, uint expected) => Assert.Equal(expected, ShaderNames.ParseHashOrName(text));

    [Fact]
    public void Unknown_hashes_show_as_hex()
    {
        Assert.Null(ShaderNames.ShaderName(0x12345678));
        Assert.Equal("0x12345678", ShaderNames.ShaderLabel(0x12345678));
        Assert.Equal("0x0000ABCD", ShaderNames.ParameterLabel(0xABCD));
        Assert.Null(ShaderNames.ParseHashOrName("  "));
    }
}
