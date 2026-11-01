using CASPartResource;

namespace OpenSimsStudio3.Formats.Cas;

/// <summary>
/// Names for CASP fields that s3pi leaves as Unknown*. Evidence for each is in
/// docs/cas-studio/research/undocumented-fields.md.
/// </summary>
public static class CasPartFields
{
    /// <summary>
    /// "Unique texture space" (s3pi <c>Unknown2</c>, the byte after SortPriority). When set, the part gets its own
    /// cell of its region in the Sim's composite texture atlas, so its UV0 can use its own 0..1 layout instead of
    /// the shared body layout. Trade-off: the region's cells get smaller, so texel density drops. Retail sets it
    /// (value 1) on meshed accessories, beards and one hat family; never on tops, bottoms, full body or shoes.
    /// Any non-zero value counts as set.
    /// </summary>
    public static bool UsesUniqueTextureSpace(this CASPartResource.CASPartResource casp) => casp.Unknown2 != 0;

    public static void SetUsesUniqueTextureSpace(this CASPartResource.CASPartResource casp, bool value) =>
        casp.Unknown2 = value ? (byte)1 : (byte)0;

    /// <summary>The part's internal name (s3pi <c>Unknown1</c>), e.g. <c>afBottomSkirtLong</c>.</summary>
    public static string InternalName(this CASPartResource.CASPartResource casp) => casp.Unknown1;

    /// <summary>
    /// Footstep material (s3pi <c>Unknown4</c>; TSRW shows it as Extras › "Filename2"): <c>bare</c> on clothing,
    /// <c>sand</c> on sandals, <c>heel</c> on heels, <c>leath</c> on boots.
    /// </summary>
    public static string FootstepMaterial(this CASPartResource.CASPartResource casp) => casp.Unknown4;

    /// <summary>For hair: the CAS part worn without a hat (TGI-list index; s3pi <c>CasPart1Index</c>).</summary>
    public static byte HairWithoutHatIndex(this CASPartResource.CASPartResource casp) => casp.CasPart1Index;

    /// <summary>For hair: the child version of the hair (TGI-list index; s3pi <c>CasPart2Index</c>).</summary>
    public static byte ChildVersionIndex(this CASPartResource.CASPartResource casp) => casp.CasPart2Index;
}
