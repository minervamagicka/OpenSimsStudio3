using CASPartResource;
using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Meshes;
using GEOM = meshExpImp.ModelBlocks.GEOM;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>
/// A mesh group's skinning, from its GEOM: the bone-hash list, the vertices whose weights point past it, the vertices
/// with no weight at all, and the skin (BONE) resource GEOM.SkinIndex names in the GEOM's own key list.
/// </summary>
public sealed record BoneReport(int Bones, int Weighted, int BadReferences, int Unweighted, ResourceKey? Skin)
{
    public const uint BoneType = 0x00AE6C67;
    public static readonly BoneReport None = new(0, 0, 0, 0, null);

    /// <summary>
    /// Reads the GEOM's skinning. Each vertex's BoneAssignment packs four byte indices into the bone-hash list (byte k for
    /// weight k, lowest byte first); an index only counts when its weight is above zero.
    /// </summary>
    public static BoneReport Of(GEOM geom)
    {
        int weighted = 0, bad = 0, unweighted = 0;
        foreach (var vertex in geom.VertexData)
        {
            var weights = vertex.Vertex.OfType<GEOM.WeightsElement>().FirstOrDefault();
            if (weights == null) continue;
            var indices = vertex.Vertex.OfType<GEOM.BoneAssignmentElement>().FirstOrDefault()?.ID ?? 0;
            float[] w = [weights.W1, weights.W2, weights.W3, weights.W4];
            if (w.Sum() <= 1e-3f) { unweighted++; continue; }
            weighted++;
            for (var k = 0; k < 4; k++)
                if (w[k] > 0 && ((indices >> (8 * k)) & 0xFF) >= geom.BoneHashes.Count) { bad++; break; }
        }
        var tgis = geom.TGIBlocks;
        ResourceKey? skin = geom.SkinIndex >= 0 && geom.SkinIndex < tgis.Count ? ResourceKey.Of(tgis[geom.SkinIndex]) : null;
        return new BoneReport(geom.BoneHashes.Count, weighted, bad, unweighted, skin is { Type: BoneType } found ? found : null);
    }
}

/// <summary>Whether a part has a morph: no link in the CASP, a link that resolves to a BBLN and its BGEO, or a broken link.</summary>
public enum MorphState { None, Present, Broken }

/// <summary>
/// Outfit categories per species (Sims3.SimIFace.CAS.OutfitCategories; see research/undocumented-fields.md) and the
/// ages each species uses. The editor builds its category checkboxes from these lists; the checks use them too.
/// </summary>
public static class OutfitCategories
{
    /// <summary>The CASP ClothingCategory bits that are outfit categories; the bits above are flags (maternity, random…).</summary>
    public const uint Mask = 0xFFFFF;

    public static readonly (uint Bit, string Label)[] Human =
    [
        (0x1, "Naked"), (0x2, "Everyday"), (0x4, "Formalwear"), (0x8, "Sleepwear"), (0x10, "Swimwear"), (0x20, "Athletic"),
        (0x40000, "Outerwear"), (0x100, "Career"), (0x200, "Aux career"), (0x80, "Martial arts"), (0x400, "Makeover"),
        (0x40, "Singed"), (0x800, "Skinny dip towel"), (0x8000, "Massage"), (0x10000, "Child imagination"),
        (0x20000, "Special"), (0x80000, "Supernatural"),
    ];
    public static readonly (uint Bit, string Label)[] Horse = [(0x1, "Naked"), (0x2, "Everyday"), (0x1000, "Racing"), (0x2000, "Jumping"), (0x4000, "Bridle")];
    public static readonly (uint Bit, string Label)[] Pet = [(0x1, "Naked"), (0x2, "Everyday")];

    public static (uint Bit, string Label)[] For(SpeciesType species) => species switch
    {
        SpeciesType.Human or SpeciesType.None => Human,
        SpeciesType.Horse => Horse,
        _ => Pet,
    };

    /// <summary>
    /// Ages a species has no life stage for. Pets and horses are puppy/kitten/foal (Child), adult and elder; baby,
    /// toddler and teen never apply to them. Young adult is left out on purpose: whether retail pet parts set it is
    /// not confirmed, so it is not warned about.
    /// </summary>
    public static AgeFlags UnusedAges(SpeciesType species) =>
        species is SpeciesType.Human or SpeciesType.None ? 0 : AgeFlags.Baby | AgeFlags.Toddler | AgeFlags.Teen;
}

/// <summary>
/// The checks of one project's own content (2026-11-03), shared by the editor's Warnings list and the export window.
/// Each problem is one short label with a severity, an area and the editor place it applies to; longer explanations go in
/// <see cref="ExportCheck.Tip" />. Errors would break the part in game; everything else is a warning.
/// <list type="bullet">
/// <item>Flags: no outfit categories, ages or genders; no species; categories or ages the species doesn't use.</item>
/// <item>Mesh: a VPXY the CASP names but nobody has; a mesh group missing or unreadable (errors); hair vertex colour all zero.</item>
/// <item>Bones: a group with no bones; weights that point past the bone list (error); vertices with no weight; a skin
/// (BONE) resource that can't be found (error). Bone hashes are not matched against the game's rig: OSS3 has no rig
/// reader, and the hash convention of the BONE names is unconfirmed.</item>
/// <item>Morphs: a broken fat/fit/thin/special link; and for a meshed human body part, no fat, fit or thin morph.</item>
/// <item>LODs: a LOD with no groups; a LOD with more vertices than the one before it.</item>
/// <item>UVs: <see cref="UvReport.Warnings" /> per group (hair leaves out overlaps).</item>
/// <item>Textures: no recolours; an unreadable recolour; a used texture slot whose key is in neither the project nor
/// the game (error); and, for the project's own textures, not a DDS, or a size that is not a power of two or above 2048.
/// Game textures are only looked up, not read, so the check stays cheap enough to run on every edit.</item>
/// </list>
/// </summary>
public static class PartChecks
{
    public const uint ImgType = 0x00B2D882, VpxyType = 0x736884F1;
    public const int MaxTextureSize = 2048;

    // Hair layers its strands over the same texture on purpose, so its UV overlaps are not warned about. Shader IDs
    // are FNV-32 of the lower-cased name: SimHair and CasSimHair (research/undocumented-fields.md, "GEOM tangents").
    static readonly HashSet<uint> HairShaders = [0x84FD7152, 0xFCF80CE1];

    public static bool IsHairShader(uint shader) => HairShaders.Contains(shader);

    /// <summary>
    /// Every problem of the session's part. With <paramref name="gameChecked" /> false (no game install to look in), a
    /// texture the project lacks is a warning instead of an error, since it may well be in the game.
    /// </summary>
    public static IReadOnlyList<ExportCheck> Run(ProjectSession session, bool gameChecked = true)
    {
        var list = new List<ExportCheck>();
        var folder = session.Folder;
        Flags(session.Part, folder, list);
        Mesh(session, list);
        Morphs(session, list);
        Lods(session, list);
        Textures(session, gameChecked, list);
        return list;
    }

    static ExportCheck Problem(CheckSeverity severity, CheckArea area, string label, CheckTarget target, string? tip = null) =>
        new(severity, label, target) { Area = area, Tip = tip };

    static void Flags(CASPartResource.CASPartResource part, string folder, List<ExportCheck> list)
    {
        var at = new CheckTarget(CheckTargetKind.Properties, folder);
        void Warn(string label, string? tip = null) => list.Add(Problem(CheckSeverity.Warning, CheckArea.Flags, label, at, tip));

        var species = part.AgeGender.Species;
        var categories = (uint)part.ClothingCategory & OutfitCategories.Mask;
        if (categories == 0) Warn("No outfit categories", "The part shows in no CAS category.");
        if (part.AgeGender.Age == 0) Warn("No ages");
        if (part.AgeGender.Gender == 0) Warn("No genders");
        if (species == SpeciesType.None) Warn("No species");

        var known = OutfitCategories.For(species).Aggregate(0u, (all, c) => all | c.Bit);
        var foreign = OutfitCategories.Human.Concat(OutfitCategories.Horse).Where(c => (categories & c.Bit) != 0 && (known & c.Bit) == 0)
            .Select(c => c.Label).Distinct().ToList();
        if (foreign.Count > 0) Warn($"Outfit categories {species} doesn't use: {string.Join(", ", foreign)}");
        var ages = part.AgeGender.Age & OutfitCategories.UnusedAges(species);
        if (ages != 0) Warn($"Ages {species} doesn't use: {string.Join(", ", Enum.GetValues<AgeFlags>().Where(a => ages.HasFlag(a)))}");
    }

    static string At(int lod, int group) => $"LOD {lod}, Group {group}";

    static CheckTarget GroupTarget(string folder, int lod, int group) => new(CheckTargetKind.MeshGroup, folder) { Lod = lod, Group = group };

    static void Mesh(ProjectSession session, List<ExportCheck> list)
    {
        var folder = session.Folder;
        var vpxy = session.Part.TGIBlocks.Select(ResourceKey.Of).FirstOrDefault(k => k.Type == VpxyType);
        if (vpxy.Type != 0 && session.Lods.Count == 0)
            list.Add(Problem(CheckSeverity.Error, CheckArea.Mesh, session.Contains(vpxy) ? "Mesh list (VPXY) unreadable" : "Mesh list (VPXY) missing",
                new CheckTarget(CheckTargetKind.Mesh, folder, vpxy), vpxy.ToString()));

        foreach (var lod in session.Lods)
            for (var g = 0; g < lod.Groups.Count; g++)
            {
                var group = lod.Groups[g];
                var at = GroupTarget(folder, lod.Id, g);
                if (group.Vertices == 0)
                {
                    list.Add(Problem(CheckSeverity.Error, CheckArea.Mesh, $"{(session.Contains(group.Key) ? "Mesh unreadable" : "Mesh missing")} · {At(lod.Id, g)}",
                        at, group.Key.ToString()));
                    continue;
                }
                if (group.VertexColourAllZero)
                    list.Add(Problem(CheckSeverity.Warning, CheckArea.Mesh, $"Vertex colour all zero · {At(lod.Id, g)}", at, "Hair with all-zero vertex colour renders black."));
                Bones(session, group, lod.Id, g, at, list);
                foreach (var issue in group.UvCheck.Warnings(IsHairShader(group.Shader)))
                    list.Add(Problem(CheckSeverity.Warning, CheckArea.Uvs, $"{UvWording.Label(issue)} · {At(lod.Id, g)}", at, UvWording.Tip(issue)));
            }
    }

    static void Bones(ProjectSession session, MeshGroup group, int lod, int g, CheckTarget at, List<ExportCheck> list)
    {
        var bones = group.Bones;
        void Add(CheckSeverity severity, string label, string tip) => list.Add(Problem(severity, CheckArea.Bones, $"{label} · {At(lod, g)}", at, tip));
        // With no bone list at all the weights mean nothing, so only that is reported.
        if (bones.Bones == 0) Add(CheckSeverity.Warning, "No bones", "The group has no bone list, so it will not follow the Sim.");
        else if (bones.BadReferences > 0)
            Add(CheckSeverity.Error, "Weights on missing bones", $"{bones.BadReferences:N0} vertices are weighted to bones past the group's {bones.Bones} bones.");
        if (bones.Bones > 0 && bones.Unweighted > 0)
            Add(CheckSeverity.Warning, "Vertices without weights", $"{bones.Unweighted:N0} vertices have no bone weight and stay put when the Sim moves.");
        if (bones.Skin is { } skin && !session.Contains(skin))
            Add(CheckSeverity.Error, "Skin (BONE) missing", $"{skin} is in neither the project nor the game.");
    }

    /// <summary>The part's link for one morph: the CASP's BlendInfo index into its key list, which must name a BBLN that names a BGEO.</summary>
    public static MorphState Morph(IResourceSource source, CASPartResource.CASPartResource part, BlendGeometry.Morph morph)
    {
        var index = morph switch
        {
            BlendGeometry.Morph.Fat => part.BlendInfoFatIndex, BlendGeometry.Morph.Fit => part.BlendInfoFitIndex,
            BlendGeometry.Morph.Thin => part.BlendInfoThinIndex, _ => part.BlendInfoSpecialIndex,
        };
        if (index >= part.TGIBlocks.Count || ResourceKey.Of(part.TGIBlocks[index]).Type != BlendGeometry.BblnType) return MorphState.None;
        try { return BlendGeometry.Find(source, part, morph) != null ? MorphState.Present : MorphState.Broken; }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException or System.Reflection.TargetInvocationException)
        {
            return MorphState.Broken;
        }
    }

    public static string MorphName(BlendGeometry.Morph morph) => morph == BlendGeometry.Morph.Special ? "special" : morph.ToString().ToLowerInvariant();

    static void Morphs(ProjectSession session, List<ExportCheck> list)
    {
        var part = session.Part;
        var at = new CheckTarget(CheckTargetKind.Morphs, session.Folder);
        var states = Enum.GetValues<BlendGeometry.Morph>().ToDictionary(m => m, m => Morph(session, part, m));
        foreach (var (morph, state) in states.Where(s => s.Value == MorphState.Broken))
            list.Add(Problem(CheckSeverity.Warning, CheckArea.Morphs, $"{char.ToUpperInvariant(MorphName(morph)[0])}{MorphName(morph)[1..]} morph link broken", at,
                "The CASP names a blend (BBLN) or blend geometry (BGEO) that is in neither the project nor the game."));
        // Body-shape morphs are expected on meshed human clothing and body parts; hair and accessories go without.
        if (session.Lods.Count == 0 || part.AgeGender.Species is not (SpeciesType.Human or SpeciesType.None) || !part.DataType.HasFlag(DataTypeFlags.Body)) return;
        var missing = new[] { BlendGeometry.Morph.Fat, BlendGeometry.Morph.Fit, BlendGeometry.Morph.Thin }.Where(m => states[m] == MorphState.None).Select(MorphName).ToList();
        if (missing.Count > 0)
            list.Add(Problem(CheckSeverity.Warning, CheckArea.Morphs, $"No {string.Join(", ", missing)} morph", at, "The part won't follow the Sim's body shape."));
    }

    static void Lods(ProjectSession session, List<ExportCheck> list)
    {
        MeshLod? previous = null;
        foreach (var lod in session.Lods)
        {
            var at = new CheckTarget(CheckTargetKind.Lod, session.Folder) { Lod = lod.Id };
            if (lod.Groups.Count == 0) list.Add(Problem(CheckSeverity.Warning, CheckArea.Lods, $"No mesh groups · LOD {lod.Id}", at));
            else if (previous is { Groups.Count: > 0 } && lod.Vertices > previous.Vertices)
                list.Add(Problem(CheckSeverity.Warning, CheckArea.Lods, $"More vertices than LOD {previous.Id} · LOD {lod.Id}", at,
                    $"LOD {lod.Id} has {lod.Vertices:N0} vertices, LOD {previous.Id} {previous.Vertices:N0}; later LODs are shown further away and should be lighter."));
            previous = lod;
        }
    }

    static void Textures(ProjectSession session, bool gameChecked, List<ExportCheck> list)
    {
        var folder = session.Folder;
        var part = session.Part;
        if (part.Presets.Count == 0)
        {
            list.Add(Problem(CheckSeverity.Warning, CheckArea.Textures, "No recolours", new CheckTarget(CheckTargetKind.Recolour, folder)));
            return;
        }
        // One line per texture, listing the recolours that use it, so a shared texture doesn't repeat.
        var uses = new Dictionary<ResourceKey, (string Slot, List<int> Recolours)>();
        for (var i = 0; i < part.Presets.Count; i++)
        {
            PresetModel model;
            try { model = session.Preset(i); }
            catch (Exception error) when (error is System.Xml.XmlException or InvalidDataException)
            {
                list.Add(Problem(CheckSeverity.Warning, CheckArea.Textures, $"Recolour {i + 1} unreadable", new CheckTarget(CheckTargetKind.Recolour, folder) { Recolour = i }, error.Message));
                continue;
            }
            foreach (var texture in model.Textures)
            {
                if (RecolourTextures.IsEmpty(texture.Key) || !model.IsTextureUsed(texture.Name)) continue;
                if (!uses.TryGetValue(texture.Key, out var use)) uses[texture.Key] = use = (texture.Name, []);
                use.Recolours.Add(i);
            }
        }
        foreach (var (key, (slot, recolours)) in uses)
        {
            var where = recolours.Count == 1 ? $"Recolour {recolours[0] + 1}" : $"Recolours {string.Join(", ", recolours.Select(r => r + 1))}";
            var at = new CheckTarget(CheckTargetKind.Recolour, folder, key) { Recolour = recolours[0] };
            void Add(CheckSeverity severity, string label, string tip) => list.Add(Problem(severity, CheckArea.Textures, $"{label}: {slot} · {where}", at, tip));
            if (!session.PackageKeys.Contains(key))
            {
                if (!session.Contains(key)) Add(gameChecked ? CheckSeverity.Error : CheckSeverity.Warning, gameChecked ? "Texture missing" : "Texture not in the project",
                    gameChecked ? $"{key} is in neither the project nor the game." : $"{key} is not in the project; no game install was given to look in.");
                continue;
            }
            var info = TextureInfo.Read(session.Read(key));
            if (info == null) Add(CheckSeverity.Warning, "Not a DDS texture", key.ToString());
            else if (!IsPowerOfTwo(info.Width) || !IsPowerOfTwo(info.Height) || info.Width > MaxTextureSize || info.Height > MaxTextureSize)
                Add(CheckSeverity.Warning, $"Texture size {info.Width}×{info.Height}",
                    $"Textures should be a power of two on each side and at most {MaxTextureSize}×{MaxTextureSize}.");
        }
    }

    static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;
}
