using CASPartResource;
using meshExpImp.ModelBlocks;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Textures;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Tests;

/// <summary>
/// The content checks (PartChecks, ExportChecks.Content) on projects whose CASP, VPXY, GEOMs, blends and textures are all
/// built by the test. Each check is run passing and failing.
/// </summary>
public class PartChecksTests
{
    const uint Bbln = BlendGeometry.BblnType, Bgeo = BlendGeometry.BgeoType;
    static readonly ResourceKey CaspKey = new(ExportChecks.CaspType, 0, 0x10);
    static readonly ResourceKey VpxyKey = new(PartChecks.VpxyType, 0, 0x20);
    static readonly ResourceKey Geom1 = new(GeomLoader.GeomType, 0, 0x31), Geom2 = new(GeomLoader.GeomType, 0, 0x32);
    static readonly ResourceKey Multiplier = new(PartChecks.ImgType, 0, 0x41), Mask = new(PartChecks.ImgType, 0, 0x42);
    static readonly ResourceKey Skin = new(BoneReport.BoneType, 0, 0x50);

    // ---- Builders ----

    /// <summary>One vertex: position, UV, bone indices (byte k for weight k) and weights; an optional vertex colour.</summary>
    sealed record V(float X, float Y, float U, float Vv, uint Bones = 0, float W1 = 1, float W2 = 0, uint? Colour = null);

    static readonly V[] Triangle = [new(0, 0, 0.1f, 0.1f), new(1, 0, 0.5f, 0.1f), new(0, 1, 0.1f, 0.5f)];

    static byte[] Rcol(uint type, ulong instance, ARCOLBlock block)
    {
        var rcol = new GenericRCOLResource(0, null);
        rcol.ChunkEntries.Add(new GenericRCOLResource.ChunkEntry(0, null, new TGIBlock(0, null, "ITG", type, 0, instance), block));
        using var copy = new MemoryStream();
        rcol.Stream.Position = 0;
        rcol.Stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>A GEOM of the given vertices as consecutive triangles, with a bone-hash list and its own key list (for the skin).</summary>
    static byte[] Geom(V[]? vertices = null, uint[]? bones = null, ResourceKey? skin = null)
    {
        vertices ??= Triangle;
        var usages = new List<GEOM.UsageType> { GEOM.UsageType.Position, GEOM.UsageType.UV, GEOM.UsageType.BoneAssignment, GEOM.UsageType.Weights };
        if (vertices.Any(v => v.Colour != null)) usages.Add(GEOM.UsageType.Color);
        var formats = new GEOM.VertexFormatList(null, usages.Select(u => new GEOM.VertexFormat(0, null, u)));
        var data = vertices.Select(v =>
        {
            var elements = new List<GEOM.VertexElement>
            {
                new GEOM.PositionElement(0, null, v.X, v.Y, 0), new GEOM.UVElement(0, null, v.U, v.Vv),
                new GEOM.BoneAssignmentElement(0, null, v.Bones), new GEOM.WeightsElement(0, null, v.W1, v.W2, 0, 0),
            };
            if (v.Colour is { } c) elements.Add(new GEOM.ColorElement(0, null, c));
            return new GEOM.VertexDataElement(0, null, new GEOM.ElementList(null, elements, formats), formats);
        }).ToList();
        var faces = Enumerable.Range(0, vertices.Length / 3).Select(f => new GEOM.Face(0, null, (ushort)(3 * f), (ushort)(3 * f + 1), (ushort)(3 * f + 2)));
        var tgis = skin is { } s ? new[] { new TGIBlock(0, null, s.Type, s.Group, s.Instance) } : [];
        var geom = new GEOM(0, null, 5, 0, null, 0, 0, formats, data, faces, 0, bones ?? [0xB0E5], tgis);
        return Rcol(GeomLoader.GeomType, 1, geom);
    }

    /// <summary>A VPXY: one entry per LOD ID, each naming keys of its list by index.</summary>
    static byte[] Vpxy(ResourceKey[] keys, params (byte Lod, int[] Indexes)[] lods)
    {
        var entries = lods.Select(l => (VPXY.Entry)new VPXY.Entry00(0, null, 0, l.Lod, l.Indexes));
        var vpxy = new VPXY(0, null, 4, entries, 2, new BoundingBox(0, null), new byte[4], 0, 0,
            keys.Select(k => new TGIBlock(0, null, k.Type, k.Group, k.Instance)));
        return Rcol(PartChecks.VpxyType, VpxyKey.Instance, vpxy);
    }

    /// <summary>A BBLN (v8) naming a BGEO.</summary>
    static byte[] Blend(ResourceKey bgeo)
    {
        var blend = new FaceClothingResource(0, null);
        blend.BlendGeometry = new TGIBlock(0, null, bgeo.Type, bgeo.Group, bgeo.Instance);
        return Bytes(blend);
    }

    static byte[] Bytes(IResource resource)
    {
        using var copy = new MemoryStream();
        resource.Stream.Position = 0;
        resource.Stream.CopyTo(copy);
        return copy.ToArray();
    }

    static string Key(ResourceKey k) => $"key:{k.Type:X8}:{k.Group:X8}:{k.Instance:X16}";

    static string Preset(params (string Slot, ResourceKey Key)[] textures) =>
        "<preset><complate name=\"CasRgbMask\" reskey=\"\">" + string.Concat(textures.Select(t => $"<value key=\"{t.Slot}\" value=\"{Key(t.Key)}\" />")) +
        "</complate></preset>";

    static byte[] Dds(int width = 4, int height = 4) =>
        DdsEncoder.Encode(new DdsImage(width, height, Enumerable.Repeat((byte)128, width * height * 4).ToArray()), DdsFormat.Dxt5);

    /// <summary>
    /// A clean meshed part: a human YA/adult female everyday body part, one recolour with its two textures in the
    /// project, one LOD of one group, and fat/fit/thin morphs whose blends are in the game.
    /// </summary>
    sealed class Part
    {
        public readonly CASPartResource.CASPartResource Casp = new(0, null);
        public readonly Dictionary<ResourceKey, byte[]> Project = new();
        public readonly Dictionary<ResourceKey, byte[]> Game = new();

        public Part(bool mesh = true, bool morphs = true)
        {
            Casp.AgeGender = new AgeGenderFlags(0, null, AgeFlags.YoungAdult | AgeFlags.Adult, GenderFlags.Female, SpeciesType.Human, 0);
            Casp.ClothingCategory = ClothingCategoryFlags.Everyday;
            Casp.DataType = DataTypeFlags.Body;
            Casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, Preset(("Multiplier", Multiplier), ("Mask", Mask)), 1));
            Project[Multiplier] = Dds();
            Project[Mask] = Dds();
            Casp.TGIBlocks.Add(new TGIBlock(0, null, CaspKey.Type, 0, 0));
            if (mesh)
            {
                Link(VpxyKey);
                Project[VpxyKey] = Vpxy([Geom1], (1, [0]));
                Project[Geom1] = Geom();
            }
            if (!morphs) return;
            var i = 0;
            foreach (var morph in new[] { BlendGeometry.Morph.Fat, BlendGeometry.Morph.Fit, BlendGeometry.Morph.Thin })
            {
                i++;
                var bbln = new ResourceKey(Bbln, 0, (ulong)(0x60 + i));
                var bgeo = new ResourceKey(Bgeo, 0, (ulong)(0x60 + i));
                Game[bbln] = Blend(bgeo);
                Game[bgeo] = [1];
                SetMorph(morph, Link(bbln));
            }
        }

        public byte Link(ResourceKey key)
        {
            Casp.TGIBlocks.Add(new TGIBlock(0, null, key.Type, key.Group, key.Instance));
            return (byte)(Casp.TGIBlocks.Count - 1);
        }

        public void SetMorph(BlendGeometry.Morph morph, byte index)
        {
            switch (morph)
            {
                case BlendGeometry.Morph.Fat: Casp.BlendInfoFatIndex = index; break;
                case BlendGeometry.Morph.Fit: Casp.BlendInfoFitIndex = index; break;
                case BlendGeometry.Morph.Thin: Casp.BlendInfoThinIndex = index; break;
                default: Casp.BlendInfoSpecialIndex = index; break;
            }
        }

        public string Write(TempFolder temp, string name = "Part")
        {
            var folder = Directory.CreateDirectory(temp[name]).FullName;
            new Project { Id = name, Name = name, Casp = CaspKey.ToString(), Package = name + ".package" }.Write(folder);
            TestPackages.Write(Path.Combine(folder, name + ".package"), [(CaspKey, Bytes(Casp)), .. Project.Select(kv => (kv.Key, kv.Value))]);
            return folder;
        }

        public IReadOnlyList<ExportCheck> Check(bool gameChecked = true)
        {
            using var temp = new TempFolder();
            var session = ProjectSession.Open(Write(temp), new FakeGame(Game));
            return PartChecks.Run(session, gameChecked);
        }
    }

    static ExportCheck One(IReadOnlyList<ExportCheck> checks, CheckArea area) => Assert.Single(checks, c => c.Area == area);

    // ---- A clean part ----

    [Fact]
    public void A_clean_part_has_no_problems_and_every_area_reads_ok()
    {
        var part = new Part();
        Assert.Empty(part.Check());

        using var temp = new TempFolder();
        var checks = ExportChecks.Content([part.Write(temp)], new FakeGame(part.Game));
        Assert.Equal(["Item flags", "Mesh", "Bones and weights", "Morphs", "LODs", "UVs", "Recolours and textures"], checks.Select(c => c.Label));
        Assert.All(checks, c => Assert.Equal(CheckSeverity.Ok, c.Severity));
    }

    // ---- Flags ----

    [Fact]
    public void No_categories_ages_or_genders_warn_on_the_properties()
    {
        var part = new Part();
        part.Casp.ClothingCategory = 0;
        part.Casp.AgeGender = new AgeGenderFlags(0, null, 0, 0, SpeciesType.Human, 0);
        var checks = part.Check();
        Assert.Equal(["No outfit categories", "No ages", "No genders"], checks.Select(c => c.Label));
        Assert.All(checks, c =>
        {
            Assert.Equal(CheckSeverity.Warning, c.Severity);
            Assert.Equal(CheckArea.Flags, c.Area);
            Assert.Equal(CheckTargetKind.Properties, c.Target.Kind);
            Assert.True(c.Target.IsEditor);
        });
    }

    [Fact]
    public void Flag_bits_above_the_categories_are_not_categories()
    {
        var part = new Part();
        part.Casp.ClothingCategory = (ClothingCategoryFlags)0x200000; // valid for random only
        Assert.Equal("No outfit categories", One(part.Check(), CheckArea.Flags).Label);
    }

    [Fact]
    public void No_species_warns()
    {
        var part = new Part(mesh: false, morphs: false);
        part.Casp.AgeGender = new AgeGenderFlags(0, null, AgeFlags.Adult, GenderFlags.Male, SpeciesType.None, 0);
        Assert.Equal("No species", One(part.Check(), CheckArea.Flags).Label);
    }

    [Fact]
    public void Categories_and_ages_the_species_does_not_use_warn()
    {
        var horse = new Part(mesh: false, morphs: false);
        horse.Casp.AgeGender = new AgeGenderFlags(0, null, AgeFlags.Adult, GenderFlags.Female, SpeciesType.Horse, 0);
        horse.Casp.ClothingCategory = ClothingCategoryFlags.Everyday | ClothingCategoryFlags.Formalwear | (ClothingCategoryFlags)0x1000;
        Assert.Equal("Outfit categories Horse doesn't use: Formalwear", One(horse.Check(), CheckArea.Flags).Label);

        var cat = new Part(mesh: false, morphs: false);
        cat.Casp.AgeGender = new AgeGenderFlags(0, null, AgeFlags.Teen | AgeFlags.Adult, GenderFlags.Male, SpeciesType.Cat, 0);
        cat.Casp.ClothingCategory = (ClothingCategoryFlags)0x1000; // Racing: horses only
        var checks = cat.Check().Select(c => c.Label).ToList();
        Assert.Equal(["Outfit categories Cat doesn't use: Racing", "Ages Cat doesn't use: Teen"], checks);
    }

    [Fact]
    public void Category_lists_follow_the_species()
    {
        Assert.Same(OutfitCategories.Human, OutfitCategories.For(SpeciesType.None));
        Assert.Same(OutfitCategories.Horse, OutfitCategories.For(SpeciesType.Horse));
        Assert.Same(OutfitCategories.Pet, OutfitCategories.For(SpeciesType.LittleDog));
        Assert.All(OutfitCategories.Human, c => Assert.Equal(c.Bit, c.Bit & OutfitCategories.Mask));
    }

    // ---- Mesh ----

    [Fact]
    public void A_missing_mesh_list_is_an_error_on_the_mesh()
    {
        var part = new Part(morphs: false);
        part.Project.Remove(VpxyKey);
        var check = One(part.Check(), CheckArea.Mesh);
        Assert.Equal(CheckSeverity.Error, check.Severity);
        Assert.Equal("Mesh list (VPXY) missing", check.Label);
        Assert.Equal(CheckTargetKind.Mesh, check.Target.Kind);
    }

    [Fact]
    public void A_missing_or_unreadable_group_is_an_error_on_that_group()
    {
        var part = new Part();
        part.Project[VpxyKey] = Vpxy([Geom1, Geom2], (1, [0, 1]));
        // Geom2 is in neither the project nor the game.
        var check = One(part.Check(), CheckArea.Mesh);
        Assert.Equal(CheckSeverity.Error, check.Severity);
        Assert.Equal("Mesh missing · LOD 1, Group 1", check.Label);
        Assert.Equal(new CheckTarget(CheckTargetKind.MeshGroup, check.Target.Path) { Lod = 1, Group = 1 }, check.Target);

        part.Project[Geom2] = [1, 2, 3];
        Assert.Equal("Mesh unreadable · LOD 1, Group 1", One(part.Check(), CheckArea.Mesh).Label);
    }

    [Fact]
    public void All_zero_vertex_colour_warns_and_other_colour_does_not()
    {
        var part = new Part();
        part.Project[Geom1] = Geom(Triangle.Select(v => v with { Colour = 0u }).ToArray());
        var check = One(part.Check(), CheckArea.Mesh);
        Assert.Equal((CheckSeverity.Warning, "Vertex colour all zero · LOD 1, Group 0"), (check.Severity, check.Label));

        part.Project[Geom1] = Geom(Triangle.Select(v => v with { Colour = 0xFF808080u }).ToArray());
        Assert.Empty(part.Check());
    }

    // ---- Bones ----

    [Fact]
    public void Bone_report_reads_packed_indices_and_weights()
    {
        V[] vertices =
        [
            new(0, 0, 0.1f, 0.1f, Bones: 0x0100, W1: 0.5f, W2: 0.5f), // bones 0 and 1: fine with two bones
            new(1, 0, 0.5f, 0.1f, Bones: 0x0200, W1: 0.5f, W2: 0.5f), // bone 2: past the list
            new(0, 1, 0.1f, 0.5f, Bones: 0x0005, W1: 0, W2: 0),         // no weight: index ignored
        ];
        var geom = new GenericRCOLResource(0, new MemoryStream(Geom(vertices, [1, 2], Skin))).ChunkEntries.Select(c => c.RCOLBlock).OfType<GEOM>().Single();
        Assert.Equal(new BoneReport(2, 2, 1, 1, Skin), BoneReport.Of(geom));
    }

    [Fact]
    public void Bone_problems_are_reported_on_their_group()
    {
        var part = new Part();
        part.Project[Geom1] = Geom(bones: []);
        var none = One(part.Check(), CheckArea.Bones);
        Assert.Equal((CheckSeverity.Warning, "No bones · LOD 1, Group 0"), (none.Severity, none.Label));
        Assert.Equal(CheckTargetKind.MeshGroup, none.Target.Kind);

        part.Project[Geom1] = Geom([Triangle[0] with { Bones = 3 }, Triangle[1], Triangle[2]]);
        var bad = One(part.Check(), CheckArea.Bones);
        Assert.Equal((CheckSeverity.Error, "Weights on missing bones · LOD 1, Group 0"), (bad.Severity, bad.Label));

        part.Project[Geom1] = Geom([Triangle[0] with { W1 = 0 }, Triangle[1], Triangle[2]]);
        Assert.Equal("Vertices without weights · LOD 1, Group 0", One(part.Check(), CheckArea.Bones).Label);
    }

    [Fact]
    public void A_skin_must_be_in_the_project_or_the_game()
    {
        var part = new Part();
        part.Project[Geom1] = Geom(skin: Skin);
        var check = One(part.Check(), CheckArea.Bones);
        Assert.Equal((CheckSeverity.Error, "Skin (BONE) missing · LOD 1, Group 0"), (check.Severity, check.Label));

        part.Game[Skin] = [0];
        Assert.Empty(part.Check());
    }

    // ---- Morphs ----

    [Fact]
    public void A_meshed_human_body_part_without_morphs_warns()
    {
        var part = new Part(morphs: false);
        var check = One(part.Check(), CheckArea.Morphs);
        Assert.Equal((CheckSeverity.Warning, "No fat, fit, thin morph"), (check.Severity, check.Label));
        Assert.Equal(CheckTargetKind.Morphs, check.Target.Kind);
    }

    [Fact]
    public void Hair_accessories_overlays_and_pets_are_not_expected_to_have_morphs()
    {
        var hair = new Part(morphs: false);
        hair.Casp.DataType = DataTypeFlags.Hair;
        Assert.Empty(hair.Check());

        var overlay = new Part(mesh: false, morphs: false);
        Assert.Empty(overlay.Check());

        var dog = new Part(morphs: false);
        dog.Casp.AgeGender = new AgeGenderFlags(0, null, AgeFlags.Adult, GenderFlags.Male, SpeciesType.Dog, 0);
        Assert.Empty(dog.Check());
    }

    [Fact]
    public void A_broken_morph_link_warns_and_the_state_says_so()
    {
        var part = new Part();
        var bbln = new ResourceKey(Bbln, 0, 0x99);
        part.SetMorph(BlendGeometry.Morph.Special, part.Link(bbln)); // in neither the project nor the game
        var check = One(part.Check(), CheckArea.Morphs);
        Assert.Equal("Special morph link broken", check.Label);

        // A BBLN whose BGEO is missing is broken too.
        part.Game[bbln] = Blend(new ResourceKey(Bgeo, 0, 0x99));
        Assert.Equal("Special morph link broken", One(part.Check(), CheckArea.Morphs).Label);

        var game = new FakeGame(part.Game);
        Assert.Equal(MorphState.Present, PartChecks.Morph(game, part.Casp, BlendGeometry.Morph.Fat));
        Assert.Equal(MorphState.Broken, PartChecks.Morph(game, part.Casp, BlendGeometry.Morph.Special));
        Assert.Equal(MorphState.None, PartChecks.Morph(game, new Part(morphs: false).Casp, BlendGeometry.Morph.Fat));
    }

    // ---- LODs ----

    [Fact]
    public void A_later_lod_with_more_vertices_warns_on_that_lod()
    {
        var part = new Part();
        V[] two = [.. Triangle, .. Triangle.Select(v => v with { U = v.U + 0.4f })];
        part.Project[Geom2] = Geom(two);
        part.Project[VpxyKey] = Vpxy([Geom1, Geom2], (1, [0]), (2, [1]));
        var check = One(part.Check(), CheckArea.Lods);
        Assert.Equal((CheckSeverity.Warning, "More vertices than LOD 1 · LOD 2"), (check.Severity, check.Label));
        Assert.Equal(new CheckTarget(CheckTargetKind.Lod, check.Target.Path) { Lod = 2 }, check.Target);

        part.Project[VpxyKey] = Vpxy([Geom2, Geom1], (1, [0]), (2, [1]));
        Assert.Empty(part.Check());
    }

    [Fact]
    public void A_lod_with_no_groups_warns()
    {
        var part = new Part();
        part.Project[VpxyKey] = Vpxy([Geom1, Multiplier], (1, [0]), (2, [1])); // LOD 2 names only a texture
        Assert.Equal("No mesh groups · LOD 2", One(part.Check(), CheckArea.Lods).Label);
    }

    // ---- UVs ----

    [Fact]
    public void Uv_problems_warn_on_their_group()
    {
        var part = new Part();
        part.Project[Geom1] = Geom(Triangle.Select(v => v with { U = v.U + 1 }).ToArray());
        var check = One(part.Check(), CheckArea.Uvs);
        Assert.Equal((CheckSeverity.Warning, "UVs outside 0–1 · LOD 1, Group 0"), (check.Severity, check.Label));
        Assert.Equal(CheckTargetKind.MeshGroup, check.Target.Kind);
        Assert.NotNull(check.Tip);
    }

    // ---- Textures ----

    [Fact]
    public void A_missing_texture_is_an_error_unless_no_game_was_given()
    {
        var part = new Part();
        part.Project.Remove(Mask);
        var check = One(part.Check(), CheckArea.Textures);
        Assert.Equal((CheckSeverity.Error, "Texture missing: Mask · Recolour 1"), (check.Severity, check.Label));
        Assert.Equal(new CheckTarget(CheckTargetKind.Recolour, check.Target.Path, Mask) { Recolour = 0 }, check.Target);

        var unchecked_ = One(part.Check(gameChecked: false), CheckArea.Textures);
        Assert.Equal((CheckSeverity.Warning, "Texture not in the project: Mask · Recolour 1"), (unchecked_.Severity, unchecked_.Label));

        part.Game[Mask] = [0]; // game textures are looked up, not read
        Assert.Empty(part.Check());
    }

    [Fact]
    public void A_shared_missing_texture_is_one_line_naming_every_recolour()
    {
        var part = new Part();
        var gone = new ResourceKey(PartChecks.ImgType, 0, 0x77);
        part.Casp.Presets.Clear();
        part.Casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, Preset(("Multiplier", gone)), 1));
        part.Casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, Preset(("Multiplier", Multiplier)), 2));
        part.Casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, Preset(("Multiplier", gone)), 3));
        Assert.Equal("Texture missing: Multiplier · Recolours 1, 3", One(part.Check(), CheckArea.Textures).Label);
    }

    [Fact]
    public void Empty_and_disabled_slots_are_not_checked()
    {
        var part = new Part();
        var gone = new ResourceKey(PartChecks.ImgType, 0, 0x77);
        part.Casp.Presets[0].XmlFile = new StringReader(
            "<preset><complate name=\"CasRgbMask\" reskey=\"\">" +
            $"<value key=\"Multiplier\" value=\"{Key(Multiplier)}\" /><value key=\"Mask\" value=\"{Key(new ResourceKey(PartChecks.ImgType, 0, 0))}\" />" +
            $"<value key=\"Logo\" value=\"{Key(gone)}\" /><value key=\"Logo Enabled\" value=\"False\" />" +
            "</complate></preset>");
        Assert.Empty(part.Check());
    }

    [Fact]
    public void Project_textures_must_be_dds_of_a_sane_size()
    {
        var part = new Part();
        part.Project[Mask] = new byte[200];
        Assert.Equal("Not a DDS texture: Mask · Recolour 1", One(part.Check(), CheckArea.Textures).Label);

        part.Project[Mask] = Dds(12, 8);
        var check = One(part.Check(), CheckArea.Textures);
        Assert.Equal((CheckSeverity.Warning, "Texture size 12×8: Mask · Recolour 1"), (check.Severity, check.Label));

        part.Project[Mask] = Dds(8, 4);
        Assert.Empty(part.Check());
    }

    [Fact]
    public void No_recolours_or_an_unreadable_one_warns()
    {
        var part = new Part();
        part.Casp.Presets.Clear();
        var none = One(part.Check(), CheckArea.Textures);
        Assert.Equal(("No recolours", CheckTargetKind.Recolour, -1), (none.Label, none.Target.Kind, none.Target.Recolour));

        part = new Part();
        part.Casp.Presets.Add(new CASPartResource.CASPartResource.Preset(0, null, "<preset>no complate</preset>", 2));
        var bad = One(part.Check(), CheckArea.Textures);
        Assert.Equal(("Recolour 2 unreadable", 1), (bad.Label, bad.Target.Recolour));
    }

    // ---- Export: content of several projects ----

    [Fact]
    public void Several_projects_prefix_their_names_and_ok_lines_cover_only_clean_areas()
    {
        using var temp = new TempFolder();
        var good = new Part();
        var bad = new Part();
        bad.Casp.AgeGender = new AgeGenderFlags(0, null, 0, GenderFlags.Female, SpeciesType.Human, 0);
        var game = new FakeGame(good.Game); // both parts link the same morph blends
        var checks = ExportChecks.Content([good.Write(temp, "Good"), bad.Write(temp, "Bad")], game);
        Assert.Equal("Bad: No ages", Assert.Single(checks, c => c.IsProblem).Label);
        Assert.DoesNotContain("Item flags", checks.Select(c => c.Label));
        Assert.Contains("Mesh", checks.Select(c => c.Label));
    }

    [Fact]
    public void Without_a_game_missing_textures_are_warnings()
    {
        using var temp = new TempFolder();
        var part = new Part();
        part.Project.Remove(Mask);
        var checks = ExportChecks.Content([part.Write(temp)], null);
        Assert.Contains(checks, c => c is { Severity: CheckSeverity.Warning, Label: "Texture not in the project: Mask · Recolour 1" });
        // The morph blends are in the (absent) game: their links read as broken.
        Assert.DoesNotContain(checks, c => c.Severity == CheckSeverity.Error);
    }

    [Fact]
    public void A_project_that_cannot_be_opened_gets_no_ok_lines_and_an_unparsable_casp_warns()
    {
        using var temp = new TempFolder();
        var missing = Directory.CreateDirectory(temp["Missing"]).FullName; // no project.json: Run reports it
        Assert.Empty(ExportChecks.Content([missing], NoResources.Instance));

        var broken = Directory.CreateDirectory(temp["Broken"]).FullName;
        new Project { Name = "Broken", Casp = CaspKey.ToString(), Package = "Broken.package" }.Write(broken);
        TestPackages.Write(Path.Combine(broken, "Broken.package"), (CaspKey, [1, 2, 3]));
        var check = Assert.Single(ExportChecks.Content([broken], NoResources.Instance));
        Assert.Equal(CheckSeverity.Warning, check.Severity);
        Assert.StartsWith("Broken: CAS part can't be read", check.Label);
        Assert.Equal(CheckTargetKind.Package, check.Target.Kind);
    }

    [Fact]
    public void Editor_targets_are_marked_and_file_targets_are_not()
    {
        Assert.True(new CheckTarget(CheckTargetKind.Recolour, "x").IsEditor);
        Assert.True(new CheckTarget(CheckTargetKind.Properties, "x").IsEditor);
        Assert.False(new CheckTarget(CheckTargetKind.TestProfile, "x").IsEditor);
        Assert.False(CheckTarget.None.IsEditor);
        Assert.Equal(-1, CheckTarget.None.Lod);
    }

    [Fact]
    public void Uv_wording_is_shared_with_the_checks()
    {
        Assert.Equal("UV overlap (UV2)", UvWording.Label(new UvIssue(UvIssueKind.Overlap, 1, [0, 1])));
        Assert.True(UvWording.IsDanger(UvIssueKind.MissingUvs));
        Assert.True(PartChecks.IsHairShader(0x84FD7152));
        Assert.False(PartChecks.IsHairShader(0x548394B9));
    }
}
