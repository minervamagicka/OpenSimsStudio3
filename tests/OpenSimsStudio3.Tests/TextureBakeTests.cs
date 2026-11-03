using System.Text;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Textures;

namespace OpenSimsStudio3.Tests;

/// <summary>
/// Bake on a project whose CASP, presets, textures and complate templates are all made by the test. The templates are
/// small stand-ins written for this test in the complate step language the compositor reads (Compositing/README.md),
/// not EA's: a white fill, two masked pattern channels, the Multiplier multiplied in, then an overlay and a stencil
/// blended on top.
/// </summary>
public class TextureBakeTests
{
    const int Size = 16;
    const uint TemplateType = 0x0333406C;
    static readonly ResourceKey CaspKey = new(0x034AEECB, 0, 0x10);
    static readonly ResourceKey Part = new(TemplateType, 0, 0xC001);
    static readonly ResourceKey Unfilled = new(TemplateType, 0, 0xC002);
    static readonly ResourceKey Hair = new(TemplateType, 0, 0xC003);
    static readonly ResourceKey Solid = new(TemplateType, 0, 0xC004);
    static readonly ResourceKey Multiplier = new(RecolourTextures.Img, 0x7, 0x1001);
    static readonly ResourceKey Mask = new(RecolourTextures.Img, 0, 0x2001);
    static readonly ResourceKey Overlay = new(RecolourTextures.Img, 0, 0x3001);
    static readonly ResourceKey Stencil = new(RecolourTextures.Img, 0, 0x4001);

    static string Key(ResourceKey k) => $"key:{k.Type:X8}:{k.Group:X8}:{k.Instance:X16}";

    static string PartTemplate(bool fill) =>
        "<complate name=\"TestRgbMask\"><variables>" +
        "<param name=\"Pattern A Enabled\" default=\"False\" /><param name=\"Pattern B Enabled\" default=\"False\" />" +
        "<param name=\"Stencil A Enabled\" default=\"False\" /></variables>" +
        "<texturePart part=\"Body\"><destination textureName=\"diffuse\">" +
        "<step type=\"SetTarget\" renderTarget=\"RenderTarget_A\" />" +
        $"<step type=\"ColorFill\" color=\"{(fill ? "1,1,1,1" : "0,0,0,1")}\" />" +
        "<step type=\"DrawFabric\" pattern=\"($daeFileName)Pattern A\" mask=\"($Mask)\" maskSelect=\"1,0,0,0\" maskBias=\"-0.5\" enabled=\"($Pattern A Enabled)\" colorWrite=\"Color\" />" +
        "<step type=\"DrawFabric\" pattern=\"($daeFileName)Pattern B\" mask=\"($Mask)\" maskSelect=\"0,1,0,0\" maskBias=\"-0.5\" enabled=\"($Pattern B Enabled)\" colorWrite=\"Color\" />" +
        "<step type=\"DrawImage\" texture=\"($Multiplier)\" enableBlending=\"true\" srcBlend=\"DestColor\" dstBlend=\"Zero\" colorWrite=\"Color\" />" +
        "<step type=\"DrawImage\" texture=\"($Overlay)\" enableBlending=\"true\" srcBlend=\"SrcAlpha\" dstBlend=\"InvSrcAlpha\" colorWrite=\"Color\" />" +
        "<step type=\"DrawImage\" texture=\"($Stencil A)\" enableBlending=\"true\" srcBlend=\"SrcAlpha\" dstBlend=\"InvSrcAlpha\" colorWrite=\"Color\" enabled=\"($Stencil A Enabled)\" />" +
        "</destination></texturePart></complate>";

    const string SolidTemplate =
        "<complate name=\"TestSolid\"><texturePart><destination textureName=\"diffuse\">" +
        "<step type=\"ColorFill\" color=\"($Color)\" /></destination></texturePart></complate>";

    const string HairTemplate =
        "<complate name=\"TestHair\"><texturePart><destination textureName=\"diffuse\">" +
        "<step type=\"DrawImage\" texture=\"($Diffuse Map)\" /></destination></texturePart></complate>";

    static string Pattern(string variable, string color) =>
        $"<pattern variable=\"{variable}\" reskey=\"{Key(Solid)}\" name=\"solid\"><value key=\"Color\" value=\"{color}\" /></pattern>";

    /// <summary>A CasRgbMask-like recolour: two solid channels, and optionally an overlay and an enabled stencil.</summary>
    static string RgbPreset(string colorA, string colorB, bool overlay = true, bool stencil = true, ResourceKey? template = null) =>
        $"<preset><complate name=\"TestRgbMask\" reskey=\"{Key(template ?? Part)}\">" +
        $"<value key=\"Multiplier\" value=\"{Key(Multiplier)}\" />" +
        $"<value key=\"Mask\" value=\"{Key(Mask)}\" />" +
        $"<value key=\"Overlay\" value=\"{Key(overlay ? Overlay : RecolourTextures.EmptyKey)}\" />" +
        $"<value key=\"Stencil A\" value=\"{Key(Stencil)}\" />" +
        $"<value key=\"Stencil A Enabled\" value=\"{(stencil ? "True" : "False")}\" />" +
        "<value key=\"Pattern A Enabled\" value=\"True\" /><value key=\"Pattern B Enabled\" value=\"True\" />" +
        "<value key=\"Pattern A\" value=\"solid\" /><value key=\"Pattern B\" value=\"solid\" />" +
        Pattern("Pattern A", colorA) + Pattern("Pattern B", colorB) +
        "</complate></preset>";

    static string HairPreset() =>
        $"<preset><complate name=\"TestHair\" reskey=\"{Key(Hair)}\"><value key=\"Diffuse Map\" value=\"{Key(Multiplier)}\" /></complate></preset>";

    static byte[] Image(Func<int, int, (int R, int G, int B, int A)> pixel)
    {
        var rgba = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var p = (y * Size + x) * 4;
                (rgba[p], rgba[p + 1], rgba[p + 2], rgba[p + 3]) = ((byte)r, (byte)g, (byte)b, (byte)a);
            }
        return rgba;
    }

    static byte[] Encode(Func<int, int, (int, int, int, int)> pixel, DdsFormat format) =>
        DdsEncoder.Encode(new DdsImage(Size, Size, Image(pixel)), format);

    /// <summary>Opens a project with the given presets. The Multiplier is a grey ramp in DXT1; the mask splits left (R) and right (G).</summary>
    static ProjectSession Open(TempFolder temp, params string[] presets) => Open(temp, translucent: false, presets);

    /// <param name="translucent">The Multiplier is DXT5 with its right half at alpha 128, and the part template writes its alpha.</param>
    static ProjectSession Open(TempFolder temp, bool translucent, params string[] presets)
    {
        var casp = new CASPartResource.CASPartResource(0, null);
        for (var i = 0; i < presets.Length; i++) casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, presets[i], (uint)(i + 1)));
        using var bytes = new MemoryStream();
        casp.Stream.Position = 0;
        casp.Stream.CopyTo(bytes);
        new Project { Id = "bake-test", Name = "Bake", Casp = CaspKey.ToString(), Package = "Bake.package" }.Write(temp.Path);
        TestPackages.Write(temp["Bake.package"], (CaspKey, bytes.ToArray()),
            (Multiplier, Encode((x, y) => (128 + x * 8, 128 + x * 8, 128 + y * 8, translucent && x >= Size / 2 ? 128 : 255), translucent ? DdsFormat.Dxt5 : DdsFormat.Dxt1)),
            (Mask, Encode((x, _) => x < Size / 2 ? (255, 0, 0, 255) : (0, 255, 0, 255), DdsFormat.A8R8G8B8)),
            (Overlay, Encode((x, y) => x < 4 && y < 4 ? (0, 0, 255, 128) : (0, 0, 0, 0), DdsFormat.A8R8G8B8)),
            (Stencil, Encode((x, y) => x >= 12 && y >= 12 ? (255, 255, 0, 160) : (0, 0, 0, 0), DdsFormat.A8R8G8B8)));
        var templates = new Dictionary<ResourceKey, byte[]>
        {
            [Part] = Encoding.UTF8.GetBytes(translucent ? PartTemplate(fill: true).Replace("dstBlend=\"Zero\" colorWrite=\"Color\"", "dstBlend=\"Zero\" colorWrite=\"All\"") : PartTemplate(fill: true)),
            [Unfilled] = Encoding.UTF8.GetBytes(PartTemplate(fill: false)),
            [Hair] = Encoding.UTF8.GetBytes(HairTemplate),
            [Solid] = Encoding.UTF8.GetBytes(SolidTemplate),
        };
        return ProjectSession.Open(temp.Path, new FakeGame(templates));
    }

    static CompositedTexture Compose(ProjectSession session, int preset) =>
        CasCompositor.ComposeDiffuse(session, session.Preset(preset).ToXml(), 2048) ?? throw new InvalidOperationException("compose failed");

    static int MaxDifference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Max();

    [Fact]
    public void Bake_writes_the_composed_recolour_into_the_multiplier_and_neutralises_the_preset()
    {
        using var temp = new TempFolder();
        var session = Open(temp, RgbPreset("1,0.2,0.2,1", "0.2,0.6,1,1"), RgbPreset("0.3,1,0.3,1", "1,1,0,1"));
        var before = Compose(session, 0);
        var other = Compose(session, 1);

        var plan = TextureBake.Bake(session, 0, allRecolours: false);

        // Stored at the slot, in the slot's format and the composed size.
        var key = RecolourTextures.Slot(session, 0, TextureBake.Slot)!.Value;
        Assert.NotEqual(Multiplier, key);
        Assert.Equal(Multiplier.Group, key.Group);
        Assert.Equal(plan.Dds, session.Read(key));
        Assert.Equal(DdsFormat.Dxt1, plan.Format);
        var info = OpenSimsStudio3.Formats.Cas.TextureInfo.Read(plan.Dds)!;
        Assert.Equal(("DXT1", Size, Size), (info.Format, info.Width, info.Height));

        // The stored texture is the compositor's output, within DXT1 error.
        var decoded = DdsDecoder.Decode(plan.Dds);
        Assert.True(TextureBake.Difference(decoded.Rgba, before.Rgba) < 3, $"mean {TextureBake.Difference(decoded.Rgba, before.Rgba)}");
        Assert.True(MaxDifference(decoded.Rgba, before.Rgba) <= 24, $"max {MaxDifference(decoded.Rgba, before.Rgba)}");

        // The semi-transparent overlay and stencil would be drawn twice: Bake needed every edit.
        Assert.True(plan.Matches);
        Assert.Equal(new BakeEdits(true, true, true), plan.Edits);
        var model = session.Preset(0);
        Assert.All(model.Channels, c => Assert.False(c.Enabled));
        Assert.False(model.IsTextureUsed("Stencil A"));
        Assert.True(RecolourTextures.IsEmpty(RecolourTextures.Slot(session, 0, "Overlay")!.Value));

        // The recolour now composes to the bake.
        var after = Compose(session, 0);
        Assert.True(TextureBake.Difference(after.Rgba, before.Rgba) <= plan.Difference + 1e-9);
        Assert.True(MaxDifference(after.Rgba, before.Rgba) <= 24);

        // The other recolour is untouched.
        Assert.Equal(Multiplier, RecolourTextures.Slot(session, 1, TextureBake.Slot));
        Assert.All(session.Preset(1).Channels, c => Assert.True(c.Enabled));
        Assert.Equal(other.Rgba, Compose(session, 1).Rgba);
        Assert.Contains(Multiplier, session.PackageKeys);
        Assert.Contains(Overlay, session.PackageKeys); // not an import: kept

        // It survives a save.
        session.Save();
        var reopened = ProjectSession.Open(temp.Path, session.Game);
        Assert.Equal(key, RecolourTextures.Slot(reopened, 0, TextureBake.Slot));
        Assert.Equal(plan.Dds, reopened.Read(key));
    }

    [Fact]
    public void Bake_makes_only_the_edits_the_recolour_needs()
    {
        using var temp = new TempFolder();
        var session = Open(temp, RgbPreset("1,0.5,0,1", "0,0.5,1,1", overlay: false, stencil: false));
        var before = Compose(session, 0);

        var model = session.Preset(0);
        Assert.Equal([new BakeEdits(true, false, false)], TextureBake.Candidates(model));

        var plan = TextureBake.Bake(session, 0, allRecolours: false);
        Assert.True(plan.Matches);
        Assert.Equal(new BakeEdits(true, false, false), plan.Edits);
        Assert.Equal("channels off", plan.Edits.ToString());
        Assert.True(MaxDifference(Compose(session, 0).Rgba, before.Rgba) <= 24);
        // The stencil stays as it was (off), and so does the Overlay value.
        Assert.False(session.Preset(0).IsTextureUsed("Stencil A"));
        Assert.Equal(RecolourTextures.EmptyKey, RecolourTextures.Slot(session, 0, "Overlay"));
    }

    [Fact]
    public void Bake_in_all_recolours_shares_the_bake_and_edits_every_preset()
    {
        using var temp = new TempFolder();
        var session = Open(temp, RgbPreset("1,0,0,1", "0,0,1,1"), RgbPreset("0,1,0,1", "1,1,1,1"), HairPreset());
        Assert.False(RecolourTextures.IsShared(session, TextureBake.Slot)); // the hair recolour has no Multiplier
        var before = Compose(session, 1);

        var plan = TextureBake.Bake(session, 1, allRecolours: true);

        var key = RecolourTextures.Slot(session, 1, TextureBake.Slot)!.Value;
        Assert.Equal(key, RecolourTextures.Slot(session, 0, TextureBake.Slot));
        Assert.Null(RecolourTextures.Slot(session, 2, TextureBake.Slot));
        for (var i = 0; i < 2; i++)
        {
            Assert.All(session.Preset(i).Channels, c => Assert.False(c.Enabled));
            Assert.True(RecolourTextures.IsEmpty(RecolourTextures.Slot(session, i, "Overlay")!.Value));
            // Every recolour now shows recolour 2's look.
            Assert.True(MaxDifference(Compose(session, i).Rgba, before.Rgba) <= 24);
        }
        Assert.Equal(HairPreset(), session.Preset(2).ToXml());
        Assert.True(plan.Matches);
    }

    [Fact]
    public void Bake_twice_with_the_same_result_reuses_the_texture()
    {
        using var temp = new TempFolder();
        var session = Open(temp, RgbPreset("1,0,0,1", "0,0,1,1", overlay: false, stencil: false), RgbPreset("1,0,0,1", "0,0,1,1", overlay: false, stencil: false));
        TextureBake.Bake(session, 0, allRecolours: false);
        TextureBake.Bake(session, 1, allRecolours: false);
        Assert.Equal(RecolourTextures.Slot(session, 0, TextureBake.Slot), RecolourTextures.Slot(session, 1, TextureBake.Slot));
    }

    [Fact]
    public void A_template_that_cannot_be_neutralised_reports_no_match()
    {
        using var temp = new TempFolder();
        // A black fill under the patterns: with the channels off the Multiplier multiplies black.
        var session = Open(temp, RgbPreset("1,0,0,1", "0,0,1,1", template: Unfilled));
        var plan = TextureBake.Plan(session, 0);
        Assert.False(plan.Matches);
        Assert.True(plan.Difference > 10);
        // Planning changes nothing.
        Assert.Equal(Multiplier, RecolourTextures.Slot(session, 0, TextureBake.Slot));
        Assert.All(session.Preset(0).Channels, c => Assert.True(c.Enabled));
        Assert.False(session.Dirty);
    }

    [Fact]
    public void A_translucent_bake_keeps_the_alpha_the_template_composes_to()
    {
        using var temp = new TempFolder();
        var session = Open(temp, translucent: true, RgbPreset("1,0.5,0.5,1", "0.5,0.5,1,1", overlay: false, stencil: false));
        var before = Compose(session, 0);
        Assert.Contains(before.Rgba.Where((_, i) => i % 4 == 3), a => a is > 100 and < 160);

        var plan = TextureBake.Bake(session, 0, allRecolours: false);
        Assert.Equal(DdsFormat.Dxt5, plan.Format);
        // This template does not multiply colour by alpha, so the composed colour is stored as is (dividing by alpha
        // would brighten the translucent half twice over), and the recolour composes back to it.
        Assert.True(plan.Matches, $"difference {plan.Difference}");
        Assert.True(TextureBake.Difference(DdsDecoder.Decode(plan.Dds).Rgba, before.Rgba) < 3);
        Assert.True(MaxDifference(Compose(session, 0).Rgba, before.Rgba) <= 24);
    }

    [Fact]
    public void Bake_applies_only_to_the_multiplier_of_a_patterned_recolour()
    {
        using var temp = new TempFolder();
        var session = Open(temp, RgbPreset("1,0,0,1", "0,0,1,1"), HairPreset());
        Assert.True(TextureBake.Applies(session, 0, "Multiplier"));
        Assert.False(TextureBake.Applies(session, 0, "Mask"));
        Assert.False(TextureBake.Applies(session, 0, "Overlay"));
        Assert.False(TextureBake.Applies(session, 1, "Diffuse Map"));
        Assert.False(TextureBake.Applies(session, 1, "Multiplier"));
        Assert.False(TextureBake.Applies(session, 5, "Multiplier"));
        Assert.Throws<InvalidOperationException>(() => TextureBake.Plan(session, 1));
    }

    [Fact]
    public void An_uncomposable_recolour_is_refused()
    {
        using var temp = new TempFolder();
        var missing = new ResourceKey(TemplateType, 0, 0xDEAD);
        var session = Open(temp, RgbPreset("1,0,0,1", "0,0,1,1", template: missing));
        var error = Assert.Throws<InvalidOperationException>(() => TextureBake.Plan(session, 0));
        Assert.Contains("not found", error.Message);
    }
}
