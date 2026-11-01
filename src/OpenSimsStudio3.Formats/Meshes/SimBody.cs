using CASPartResource;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>
/// The rest of a Sim around a CAS part, for the editor's Whole Sim view: EA's nude top, bottom and shoes, the face
/// and the scalp for the part's age and gender, minus the slot the part itself fills. Parts are found by name: a game
/// CASP's instance is the FNV-1 64 of its lower-cased name in group 0 (checked for afTopNude, afFace and afScalp).
/// Young adults wear the adult body (afTopNude is YoungAdult+Adult) but have their own face (yfFace). Humans only.
/// </summary>
public static class SimBody
{
    const uint Casp = 0x034AEECB;

    enum Region { Top, Bottom, Shoes, Face, Scalp }

    /// <summary>The body parts with their morphs, so the body follows the editor's body-shape sliders; empty for non-humans.</summary>
    public static IReadOnlyList<MorphedMesh> Around(IResourceSource game, CASPartResource.CASPartResource part)
    {
        if (part.AgeGender.Species is not (SpeciesType.Human or 0)) return [];
        var ages = part.AgeGender.Age;
        var age = new[] { (AgeFlags.YoungAdult, 'y'), (AgeFlags.Adult, 'a'), (AgeFlags.Teen, 't'), (AgeFlags.Elder, 'e'),
            (AgeFlags.Child, 'c'), (AgeFlags.Toddler, 'p'), (AgeFlags.Baby, 'b') }.FirstOrDefault(a => ages.HasFlag(a.Item1)).Item2;
        if (age == default) return [];
        var gender = part.AgeGender.Gender.HasFlag(GenderFlags.Female) ? 'f' : 'm';

        var meshes = new List<MorphedMesh>();
        foreach (var region in Regions(part.Clothing))
        {
            var stem = region switch
            {
                Region.Top => "TopNude", Region.Bottom => "BottomNude", Region.Shoes => "ShoesNude", Region.Face => "Face", _ => "Scalp",
            };
            // The exact age first; young adults fall back to the adult part, and unisex (u) parts are tried last.
            var prefixes = new[] { $"{age}{gender}", age == 'y' ? $"a{gender}" : null, $"{age}u" }.OfType<string>();
            foreach (var prefix in prefixes)
            {
                var key = new ResourceKey(Casp, 0, CasClone.Fnv64((prefix + stem).ToLowerInvariant()));
                if (CasMeshes.Lod0(game, key) is not { } mesh || game.Read(key) is not { } caspBytes) continue;
                meshes.Add(MorphedMesh.Load(game, new CASPartResource.CASPartResource(0, new MemoryStream(caspBytes)), mesh));
                break;
            }
        }
        return meshes;
    }

    static IEnumerable<Region> Regions(ClothingType part) => part switch
    {
        ClothingType.Top => [Region.Bottom, Region.Shoes, Region.Face, Region.Scalp],
        ClothingType.Bottom => [Region.Top, Region.Shoes, Region.Face, Region.Scalp],
        ClothingType.Body => [Region.Shoes, Region.Face, Region.Scalp],
        ClothingType.Shoes => [Region.Top, Region.Bottom, Region.Face, Region.Scalp],
        ClothingType.Face => [Region.Top, Region.Bottom, Region.Shoes, Region.Scalp],
        ClothingType.Scalp => [Region.Top, Region.Bottom, Region.Shoes, Region.Face],
        _ => [Region.Top, Region.Bottom, Region.Shoes, Region.Face, Region.Scalp],
    };
}
