using System.Numerics;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Textures;
using OpenSimsStudio3.Formats.Thumbnails;

namespace OpenSimsStudio3.Tests;

/// <summary>CAS thumbnail keys, PNG, resizing, framing and the store/read round trip; every image and package is made here.</summary>
public class CasThumbnailsTests
{
    static readonly ResourceKey CaspKey = new(0x034AEECB, 0, 0xABCDEF0123456789);

    static DdsImage Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = (r, g, b, a);
        return new DdsImage(w, h, px);
    }

    static DdsImage Noise(int w, int h, int seed)
    {
        var px = new byte[w * h * 4];
        new Random(seed).NextBytes(px);
        return new DdsImage(w, h, px);
    }

    static ProjectSession Open(TempFolder temp, params uint[] presetIds)
    {
        var casp = new CASPartResource.CASPartResource(0, null);
        foreach (var id in presetIds) casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, "<preset><complate name=\"X\" reskey=\"\" /></preset>", id));
        using var bytes = new MemoryStream();
        casp.Stream.Position = 0;
        casp.Stream.CopyTo(bytes);
        new Project { Id = "thumb-test", Name = "Test", Casp = CaspKey.ToString(), Package = "Test.package" }.Write(temp.Path);
        TestPackages.Write(temp["Test.package"], (CaspKey, bytes.ToArray()));
        return ProjectSession.Open(temp.Path, new FakeGame([]));
    }

    // ---- Key mapping ----

    [Fact]
    public void Keys_use_the_cas_thumbnail_types_the_casp_instance_and_the_preset_id_as_group()
    {
        Assert.Equal(new ResourceKey(0x626F60CC, 3, CaspKey.Instance), CasThumbnails.KeyFor(CaspKey, 3, ThumbnailSize.Small));
        Assert.Equal(new ResourceKey(0x626F60CD, 3, CaspKey.Instance), CasThumbnails.KeyFor(CaspKey, 3, ThumbnailSize.Medium));
        Assert.Equal(new ResourceKey(0x626F60CE, 3, CaspKey.Instance), CasThumbnails.KeyFor(CaspKey, 3, ThumbnailSize.Large));
        Assert.Equal([32, 128, 256], CasThumbnails.Sizes.Select(CasThumbnails.Pixels));
    }

    [Fact]
    public void A_pack_group_keeps_its_top_byte_and_the_item_key_is_the_casp_group()
    {
        var packCasp = CaspKey with { Group = 0x0B000000 };
        Assert.Equal(0x0B000005u, CasThumbnails.KeyFor(packCasp, 5, ThumbnailSize.Large).Group);
        Assert.Equal(0x0B000000u, CasThumbnails.ItemKeyFor(packCasp, ThumbnailSize.Large).Group);
        // A preset ID wider than 24 bits loses its top byte to the pack byte.
        Assert.Equal(0x0B345678u, CasThumbnails.KeyFor(packCasp, 0x12345678, ThumbnailSize.Large).Group);
    }

    [Fact]
    public void First_recolour_also_writes_the_item_thumbnail_unless_another_recolour_lives_there()
    {
        Assert.Equal(6, CasThumbnails.KeysFor(CaspKey, [1, 2], 0).Count);
        Assert.Equal(3, CasThumbnails.KeysFor(CaspKey, [1, 2], 1).Count);
        // Preset ID 0 is the item key itself: written once.
        Assert.Equal(3, CasThumbnails.KeysFor(CaspKey, [0, 1], 0).Count);
        // The second recolour has ID 0, so the first must not overwrite its thumbnail.
        Assert.Equal(3, CasThumbnails.KeysFor(CaspKey, [1, 0], 0).Count);
    }

    // ---- PNG ----

    [Fact]
    public void Png_round_trips_rgba_exactly()
    {
        var image = Noise(37, 19, 1);
        var png = Png.Encode(image);
        Assert.True(Png.IsPng(png));
        var back = Png.Decode(png);
        Assert.Equal((37, 19), (back.Width, back.Height));
        Assert.Equal(image.Rgba, back.Rgba);
    }

    [Fact]
    public void Png_chunks_carry_correct_crcs()
    {
        // The CRC of an empty IEND chunk is the well-known AE426082.
        Assert.Equal(0xAE426082u, Png.Crc32("IEND"u8, []));
        var png = Png.Encode(Solid(4, 4, 1, 2, 3));
        Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82], png[^12..]);
    }

    [Fact]
    public void Png_decoder_refuses_what_it_cannot_read()
    {
        Assert.Throws<InvalidDataException>(() => Png.Decode([1, 2, 3]));
        var png = Png.Encode(Solid(2, 2, 0, 0, 0));
        png[8 + 8 + 12] = 1;  // IHDR interlace byte
        Assert.Throws<InvalidDataException>(() => Png.Decode(png));
    }

    // ---- Resize ----

    [Fact]
    public void Resize_to_the_same_size_is_identity_for_opaque_images()
    {
        var image = Noise(9, 7, 2);
        for (var i = 3; i < image.Rgba.Length; i += 4) image.Rgba[i] = 255;
        Assert.Equal(image.Rgba, ImageResize.Resize(image, 9, 7).Rgba);
    }

    [Fact]
    public void Shrinking_averages_area_and_weights_colour_by_alpha()
    {
        // Left half opaque red, right half transparent black: one pixel of red at half alpha, not dark red.
        var image = new DdsImage(2, 1, [255, 0, 0, 255, 0, 0, 0, 0]);
        var one = ImageResize.Resize(image, 1, 1);
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, one.Rgba);

        var grey = new DdsImage(2, 2, [0, 0, 0, 255, 100, 100, 100, 255, 200, 200, 200, 255, 100, 100, 100, 255]);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, ImageResize.Resize(grey, 1, 1).Rgba);

        // A 3:2 shrink covers fractional pixels: the middle source pixel is split between the two targets.
        var row = new DdsImage(3, 1, [0, 0, 0, 255, 90, 90, 90, 255, 180, 180, 180, 255]);
        Assert.Equal(new byte[] { 30, 30, 30, 255, 150, 150, 150, 255 }, ImageResize.Resize(row, 2, 1).Rgba);
    }

    [Fact]
    public void Enlarging_is_bilinear_and_keeps_solid_colours()
    {
        Assert.All(ImageResize.Resize(Solid(3, 3, 10, 20, 30), 32, 32).Rgba.Chunk(4), p => Assert.Equal(new byte[] { 10, 20, 30, 255 }, p));
        var two = new DdsImage(2, 1, [0, 0, 0, 255, 200, 200, 200, 255]);
        var four = ImageResize.Resize(two, 4, 1).Rgba.Where((_, i) => i % 4 == 0).ToArray();
        Assert.Equal(new byte[] { 0, 50, 150, 200 }, four);
    }

    [Fact]
    public void Fit_square_centres_a_wide_image_on_transparency()
    {
        var wide = ImageResize.FitSquare(Solid(40, 20, 255, 255, 255), 32);
        Assert.Equal((32, 32), (wide.Width, wide.Height));
        byte Alpha(int x, int y) => wide.Rgba[(y * 32 + x) * 4 + 3];
        Assert.Equal(0, Alpha(16, 0));
        Assert.Equal(0, Alpha(16, 31));
        Assert.Equal(255, Alpha(16, 16));
        Assert.Equal(255, Alpha(0, 8));
        Assert.Equal(255, Alpha(31, 23));
        Assert.Equal(0, Alpha(0, 7));
        Assert.Equal(0, Alpha(0, 24));
    }

    // ---- Store and read ----

    [Fact]
    public void Set_writes_every_size_as_png_and_reads_back_after_save()
    {
        using var temp = new TempFolder();
        var session = Open(temp, 1, 2);
        var keys = CasThumbnails.Set(session, 1, Solid(300, 200, 40, 160, 90));
        Assert.Equal(CasThumbnails.Sizes.Select(s => CasThumbnails.KeyFor(CaspKey, 2, s)), keys);
        Assert.True(session.Dirty);
        session.Save();

        var saved = TestPackages.ReadAll(temp["Test.package"]);
        foreach (var size in CasThumbnails.Sizes)
        {
            var png = Png.Decode(saved[CasThumbnails.KeyFor(CaspKey, 2, size)]);
            var side = CasThumbnails.Pixels(size);
            Assert.Equal((side, side), (png.Width, png.Height));
            var centre = (side / 2 * side + side / 2) * 4;
            Assert.Equal(new byte[] { 40, 160, 90, 255 }, png.Rgba[centre..(centre + 4)]);
        }
        Assert.DoesNotContain(saved.Keys, k => k.Type == CasThumbnails.LargeType && k.Group == 1);
        Assert.DoesNotContain(saved.Keys, k => k.Type == CasThumbnails.LargeType && k.Group == 0);

        var reopened = ProjectSession.Open(temp.Path, new FakeGame([]));
        var large = CasThumbnails.Read(reopened, 1);
        Assert.NotNull(large);
        Assert.Equal(256, large.Width);
        Assert.Null(CasThumbnails.Read(reopened, 0));
    }

    [Fact]
    public void First_recolour_fills_the_item_thumbnail_and_read_falls_back_to_it()
    {
        using var temp = new TempFolder();
        var session = Open(temp, 7, 8);
        var keys = CasThumbnails.Set(session, 0, Solid(64, 64, 255, 0, 0));
        Assert.Equal(6, keys.Count);
        Assert.Contains(CasThumbnails.ItemKeyFor(CaspKey, ThumbnailSize.Small), session.PackageKeys);

        // Only the item thumbnail left: the first recolour shows it.
        Assert.Equal(3, CasThumbnails.Remove(session, 7));
        Assert.NotNull(CasThumbnails.Read(session, 0, ThumbnailSize.Medium));
        Assert.Null(CasThumbnails.Read(session, 1, ThumbnailSize.Medium));
    }

    [Fact]
    public void Remove_takes_out_a_recolours_thumbnails_only()
    {
        using var temp = new TempFolder();
        var session = Open(temp, 1, 2);
        CasThumbnails.Set(session, 0, Solid(8, 8, 1, 1, 1));
        CasThumbnails.Set(session, 1, Solid(8, 8, 2, 2, 2));
        Assert.Equal(3, CasThumbnails.Remove(session, 2));
        Assert.Equal(0, CasThumbnails.Remove(session, 2));
        Assert.Equal(6, session.PackageKeys.Count(k => CasThumbnails.IsThumbnailType(k.Type)));
    }

    [Fact]
    public void Set_replaces_an_earlier_thumbnail()
    {
        using var temp = new TempFolder();
        var session = Open(temp, 1);
        CasThumbnails.Set(session, 0, Solid(8, 8, 0, 0, 255));
        CasThumbnails.Set(session, 0, Solid(8, 8, 0, 255, 0));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, CasThumbnails.Read(session, 0)!.Rgba[..4]);
        Assert.Equal(6, session.PackageKeys.Count(k => CasThumbnails.IsThumbnailType(k.Type)));
    }

    // ---- Framing ----

    public static TheoryData<Vector3, Vector3> Boxes => new()
    {
        { new Vector3(-0.3f, 0.9f, -0.15f), new Vector3(0.3f, 1.5f, 0.2f) },      // a top
        { new Vector3(-0.2f, 0.0f, -0.2f), new Vector3(0.2f, 1.1f, 0.25f) },     // trousers: tall
        { new Vector3(-0.12f, 1.55f, -0.15f), new Vector3(0.12f, 1.85f, 0.12f) }, // hair: off-centre, deep
        { new Vector3(-0.6f, 0.0f, -0.05f), new Vector3(0.6f, 0.1f, 0.05f) },     // flat and wide
        { new Vector3(0.1f, 0.1f, 0.1f), new Vector3(0.1f, 0.1f, 0.1f) },         // a point
    };

    [Theory]
    [MemberData(nameof(Boxes))]
    public void Framing_fits_the_box_inside_the_margin_and_centres_it(Vector3 min, Vector3 max)
    {
        var camera = ThumbnailFraming.Frame(min, max);
        var (lo, hi) = ThumbnailFraming.ProjectedBounds(camera, min, max);
        var limit = 1 - ThumbnailFraming.Margin + 1e-3f;
        Assert.True(lo.X >= -limit && lo.Y >= -limit && hi.X <= limit && hi.Y <= limit, $"{lo} {hi}");
        // Centred on both axes.
        Assert.InRange((lo.X + hi.X) / 2, -2e-3f, 2e-3f);
        Assert.InRange((lo.Y + hi.Y) / 2, -2e-3f, 2e-3f);
        if (max != min)
        {
            // The longer side reaches the margin: the item fills the frame.
            var longer = Math.Max(hi.X - lo.X, hi.Y - lo.Y);
            Assert.InRange(longer, 2 * (1 - ThumbnailFraming.Margin) - 0.02f, 2 * (1 - ThumbnailFraming.Margin) + 2e-3f);
        }
        // Every corner lies between the near and far planes.
        var vp = camera.ViewProjection;
        foreach (var c in new[] { min, max, new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, min.Y, min.Z) })
        {
            var clip = Vector4.Transform(new Vector4(c, 1), vp);
            Assert.InRange(clip.Z / clip.W, 0f, 1f);
        }
    }

    [Fact]
    public void Framing_looks_at_the_front_from_slightly_above()
    {
        var camera = ThumbnailFraming.Frame(new Vector3(-0.3f, 0.9f, -0.15f), new Vector3(0.3f, 1.5f, 0.2f));
        var toEye = Vector3.Normalize(camera.Eye - camera.Target);
        Assert.True(toEye.Z > 0.99f, $"{toEye}");
        Assert.InRange(toEye.Y, 0.05f, 0.12f);
        Assert.InRange(MathF.Abs(toEye.X), 0f, 1e-5f);
        // Y points down in Vulkan clip space: the top of the item projects above (more negative than) its bottom.
        Assert.True(camera.Project(new Vector3(0, 1.5f, 0)).Y < camera.Project(new Vector3(0, 0.9f, 0)).Y);
    }
}
