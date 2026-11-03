using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Textures;

namespace OpenSimsStudio3.Tests;

/// <summary>Texture-slot edits on a project whose package, CASP and presets are all made by the test.</summary>
public class RecolourTexturesTests
{
    static readonly ResourceKey CaspKey = new(0x034AEECB, 0, 0x10);
    static readonly ResourceKey Multiplier = new(RecolourTextures.Img, 0, 0x1001);
    static readonly ResourceKey MaskA = new(RecolourTextures.Img, 0, 0x2001);
    static readonly ResourceKey MaskB = new(RecolourTextures.Img, 0, 0x2002);
    static readonly ResourceKey GameSpecular = new(RecolourTextures.Img, 0x00000001, 0x3001);

    static string Preset(ResourceKey mask) =>
        "<preset><complate name=\"TestRgbMask\" reskey=\"\">" +
        $"<value key=\"Multiplier\" value=\"{Key(Multiplier)}\" />" +
        $"<value key=\"Mask\" value=\"{Key(mask)}\" />" +
        $"<value key=\"Clothing Specular\" value=\"{Key(GameSpecular)}\" />" +
        "</complate></preset>";

    static string Key(ResourceKey k) => $"key:{k.Type:X8}:{k.Group:X8}:{k.Instance:X16}";

    static byte[] Dds(byte shade) => DdsEncoder.Encode(new DdsImage(4, 4, Enumerable.Repeat(shade, 64).ToArray()), DdsFormat.Dxt5);

    /// <summary>A project folder: project.json and a package with a two-recolour CASP and its own textures (the specular is the game's).</summary>
    static ProjectSession Open(TempFolder temp)
    {
        var casp = new CASPartResource.CASPartResource(0, null);
        casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, Preset(MaskA), 1));
        casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, Preset(MaskB), 2));
        using var bytes = new MemoryStream();
        casp.Stream.Position = 0;
        casp.Stream.CopyTo(bytes);
        new Project { Id = "test-project", Name = "Test", Casp = CaspKey.ToString(), Package = "Test.package" }.Write(temp.Path);
        TestPackages.Write(temp["Test.package"], (CaspKey, bytes.ToArray()), (Multiplier, Dds(10)), (MaskA, Dds(20)), (MaskB, Dds(30)));
        return ProjectSession.Open(temp.Path, new FakeGame(new() { [GameSpecular] = Dds(40) }));
    }

    [Fact]
    public void Replace_adds_a_new_resource_and_points_only_this_recolour_at_it()
    {
        using var temp = new TempFolder();
        var session = Open(temp);
        var dds = Dds(99);
        var key = RecolourTextures.Replace(session, 1, "Mask", dds, allRecolours: false);

        Assert.Equal(RecolourTextures.Img, key.Type);
        Assert.NotEqual(MaskB, key);
        Assert.Equal(dds, session.Read(key));
        Assert.Equal(key, RecolourTextures.Slot(session, 1, "Mask"));
        Assert.Equal(MaskA, RecolourTextures.Slot(session, 0, "Mask"));
        Assert.True(session.Dirty);
        // The clone's own texture is not removed, even though no recolour uses it any more.
        Assert.Contains(MaskB, session.PackageKeys);

        session.Save();
        var saved = TestPackages.ReadAll(temp["Test.package"]);
        Assert.Equal(dds, saved[key]);
        Assert.Equal(key, RecolourTextures.Slot(ProjectSession.Open(temp.Path, new FakeGame([])), 1, "Mask"));
    }

    [Fact]
    public void Same_bytes_get_the_same_key_and_a_replaced_import_is_dropped()
    {
        using var temp = new TempFolder();
        var session = Open(temp);
        var first = RecolourTextures.Replace(session, 0, "Mask", Dds(50), allRecolours: false);
        Assert.Equal(first, RecolourTextures.Replace(session, 1, "Mask", Dds(50), allRecolours: false));

        var second = RecolourTextures.Replace(session, 0, "Mask", Dds(60), allRecolours: false);
        Assert.Contains(first, session.PackageKeys); // recolour 2 still uses it
        RecolourTextures.Replace(session, 1, "Mask", Dds(60), allRecolours: false);
        Assert.DoesNotContain(first, session.PackageKeys);
        Assert.Contains(second, session.PackageKeys);
    }

    [Fact]
    public void Replace_in_all_recolours_and_share_across()
    {
        using var temp = new TempFolder();
        var session = Open(temp);
        Assert.True(RecolourTextures.IsShared(session, "Multiplier"));
        Assert.False(RecolourTextures.IsShared(session, "Mask"));

        var key = RecolourTextures.Replace(session, 0, "Mask", Dds(70), allRecolours: true);
        Assert.True(RecolourTextures.IsShared(session, "Mask"));
        Assert.Equal(key, RecolourTextures.Slot(session, 1, "Mask"));

        // Diverge recolour 2, then share recolour 2's texture back across all.
        var own = RecolourTextures.Replace(session, 1, "Mask", Dds(80), allRecolours: false);
        Assert.False(RecolourTextures.IsShared(session, "Mask"));
        Assert.Equal(1, RecolourTextures.ShareAcross(session, 1, "Mask"));
        Assert.Equal(own, RecolourTextures.Slot(session, 0, "Mask"));
        Assert.DoesNotContain(key, session.PackageKeys);
    }

    [Fact]
    public void Clear_empties_the_slot_and_keeps_it_listed()
    {
        using var temp = new TempFolder();
        var session = Open(temp);
        var imported = RecolourTextures.Replace(session, 0, "Clothing Specular", Dds(5), allRecolours: false);
        Assert.Equal(GameSpecular.Group, imported.Group);

        RecolourTextures.Clear(session, 0, "Clothing Specular", allRecolours: false);
        Assert.Equal(RecolourTextures.EmptyKey, RecolourTextures.Slot(session, 0, "Clothing Specular"));
        Assert.Equal(GameSpecular, RecolourTextures.Slot(session, 1, "Clothing Specular"));
        Assert.DoesNotContain(imported, session.PackageKeys);

        RecolourTextures.Clear(session, 1, "Multiplier", allRecolours: true);
        Assert.True(RecolourTextures.IsShared(session, "Multiplier"));
        Assert.True(RecolourTextures.IsEmpty(RecolourTextures.Slot(session, 0, "Multiplier")!.Value));
        Assert.Contains(Multiplier, session.PackageKeys); // not an import: kept

        // An empty slot takes an import again; with no old group, the new key's group is 0.
        Assert.Equal(0u, RecolourTextures.Replace(session, 0, "Multiplier", Dds(6), allRecolours: false).Group);
    }

    [Fact]
    public void Unknown_slot_is_refused()
    {
        using var temp = new TempFolder();
        var session = Open(temp);
        Assert.Throws<KeyNotFoundException>(() => RecolourTextures.Replace(session, 0, "Overlay", Dds(1), false));
        Assert.Throws<KeyNotFoundException>(() => RecolourTextures.Clear(session, 0, "Overlay", false));
        Assert.Null(RecolourTextures.Slot(session, 5, "Mask"));
    }
}
