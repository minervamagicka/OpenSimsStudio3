using System.Collections.Frozen;
using P = OpenSimsStudio3.Formats.Materials.ShaderParameterKind;

namespace OpenSimsStudio3.Formats.Materials;

/// <summary>
/// Names for the shader and parameter hashes stored in a GEOM's material (MTNF). The game stores only hashes: FNV-32 of
/// the lower-cased ASCII name. The tables below list public names (sources in README.md next to this file); each hash is
/// computed from its name here, never typed in, so a name is shown only when it really hashes to the stored value.
/// </summary>
public static class ShaderNames
{
    /// <summary>Shaders CAS parts use, listed first in pickers.</summary>
    public static readonly IReadOnlyList<string> CasShaderNames =
    [
        "SimSkin", "SimHair", "CasSimHair", "SimGlass", "SimEyes", "SimEyelashes", "SimHorse", "SimPetFur", "SimPetFace",
    ];

    /// <summary>Other known shaders (objects, build mode, effects).</summary>
    static readonly string[] OtherShaderNames =
    [
        "Additive", "BasinWater", "Blueprint", "BuildingWindow", "CASRoom", "Counters", "DropShadow", "Fence",
        "FlatMirror", "Floors", "Foliage", "FullBright", "Gemstones", "GlassForFences", "GlassForObjects",
        "GlassForObjectsTranslucent", "GlassForPortals", "GlassForRabbitHoles", "ImpostorWater", "Instanced",
        "Landmark", "LotImposter", "OutdoorProp", "Painting", "ParticleAnim", "ParticleJet", "Phong", "Plumbob",
        "PreviewWallsAndFloors", "RabbitHoleHighDetail", "RabbitHoleMediumDetail", "Roof", "Rug", "SculptureIce",
        "ShadowMap", "SolidPhong", "Stairs", "StandingWater", "Subtractive", "Trampoline",
    ];

    /// <summary>Known parameter names and the value kind the game stores them with.</summary>
    static readonly (string Name, ShaderParameterKind Kind)[] Parameters =
    [
        ("AlignAcrossDirection", P.Float), ("DimmingCenterHeight", P.Float), ("Transparency", P.Float),
        ("BlendSourceMode", P.Float), ("SharpSpecControl", P.Float), ("RotateSpeedRadsSec", P.Float),
        ("AlignToDirection", P.Float), ("DropShadowStrength", P.Float), ("ContourSmoothing", P.Float),
        ("reflectivity", P.Float), ("BlendOperation", P.Float), ("RotationSpeed", P.Float), ("DimmingRadius", P.Float),
        ("IsGenericBox", P.Float), ("IsSolidObject", P.Float), ("NormalMapScale", P.Float),
        ("NoAutomaticDaylightDimming", P.Float), ("FramesPerSecond", P.Float), ("BloomFactor", P.Float),
        ("EmissiveBloomMultiplier", P.Float), ("IsObject", P.Float), ("IsPartition", P.Float),
        ("RippleSpeed", P.Float), ("UseLampColor", P.Float), ("TextureSpeedScale", P.Float),
        ("NoiseMapScale", P.Float), ("AutoRainbow", P.Float), ("DebouncePower", P.Float),
        ("SpeedStretchFactor", P.Float), ("WindSpeed", P.Float), ("DaytimeOnly", P.Float),
        ("FramesRandomStartFactor", P.Float), ("DeflectionThreshold", P.Float), ("LifetimeSeconds", P.Float),
        ("NormalBumpScale", P.Float), ("DeformerOffset", P.Float), ("EdgeDarkening", P.Float),
        ("OverrideFactor", P.Float), ("EmissiveLightMultiplier", P.Float), ("SharpSpecThreshold", P.Float),
        ("RugSort", P.Float), ("Layer2Shift", P.Float), ("SpecStyle", P.Float), ("FadeDistance", P.Float),
        ("BlendDestMode", P.Float), ("LightingEnabled", P.Float), ("OverrideSpeed", P.Float),
        ("VisibleOnlyAtNight", P.Float), ("UseDiffuseForAlphaTest", P.Float), ("SparkleSpeed", P.Float),
        ("WindStrength", P.Float), ("HaloBlur", P.Float), ("RefractionDistortionScale", P.Float),
        ("DiffuseMapUVChannel", P.Float), ("SpecularMapUVChannel", P.Float), ("ParticleCount", P.Float),
        ("RippleDistanceScale", P.Float), ("DivetScale", P.Float), ("ForceAmount", P.Float), ("AnimSpeed", P.Float),
        ("BackFaceDiffuseContribution", P.Float), ("BounceAmountMeters", P.Float), ("IsFloor", P.Float),
        ("index_of_refraction", P.Float), ("BloomScale", P.Float), ("AlphaMaskThreshold", P.Float),
        ("LightingDirectScale", P.Float), ("AlwaysOn", P.Float), ("Shininess", P.Float), ("FresnelOffset", P.Float),
        ("BouncePower", P.Float), ("ShadowAlphaTest", P.Float),
        ("DiffuseUVScale", P.Float2), ("RippleHeights", P.Float2), ("CutoutValidHeights", P.Float2),
        ("UVTiling", P.Float2), ("SizeScaleEnd", P.Float2), ("StretchRect", P.Float2), ("SizeScaleStart", P.Float2),
        ("WaterScrollSpeedLayer2", P.Float2), ("WaterScrollSpeedLayer1", P.Float2), ("NormalUVScale", P.Float2),
        ("DetailUVScale", P.Float2), ("SpecularUVScale", P.Float2), ("UVScrollSpeed", P.Float2),
        ("Ambient", P.Float3), ("OverrideDirection", P.Float3), ("OverrideVelocity", P.Float3),
        ("CounterMatrixRow1", P.Float3), ("CounterMatrixRow2", P.Float3), ("ForceDirection", P.Float3),
        ("Specular", P.Float3), ("HaloLowColor", P.Float3), ("Emission", P.Float3), ("NormalMapUVSelector", P.Float3),
        ("UVScales", P.Float3), ("LightMapScale", P.Float3), ("Diffuse", P.Float3), ("Reflective", P.Float3),
        ("AmbientUVSelector", P.Float3), ("HighlightColor", P.Float3), ("DiffuseUVSelector", P.Float3),
        ("Transparent", P.Float3), ("VertexColorScale", P.Float3), ("SpecularUVSelector", P.Float3),
        ("EmissionMapUVSelector", P.Float3), ("HaloHighColor", P.Float3), ("RootColor", P.Float3),
        ("ForceVector", P.Float3), ("PositionTweak", P.Float3),
        ("TimelineLength", P.Float4), ("UVScale", P.Float4), ("FrameData", P.Float4), ("AnimDir", P.Float4),
        ("PosScale", P.Float4), ("Births", P.Float4), ("UVOffset", P.Float4), ("PosOffset", P.Float4),
        ("AverageColor", P.Int), ("MaskWidth", P.Int), ("MaskHeight", P.Int),
        ("SparkleCube", P.Texture), ("DropShadowAtlas", P.Texture), ("DirtOverlay", P.Texture),
        ("OverlayTexture", P.Texture), ("JetTexture", P.Texture), ("ColorRamp", P.Texture), ("DiffuseMap", P.Texture),
        ("SelfIlluminationMap", P.Texture), ("NormalMap", P.Texture), ("HaloRamp", P.Texture),
        ("DetailMap", P.Texture), ("SpecularMap", P.Texture), ("AmbientOcclusionMap", P.Texture),
        ("AlphaMap", P.Texture), ("MultiplyMap", P.Texture), ("SpecCompositeTexture", P.Texture),
        ("NoiseMap", P.Texture), ("RoomLightMap", P.Texture), ("EmissionMap", P.Texture), ("RevealMap", P.Texture),
        ("ImposterTextureAOandSI", P.TextureKey), ("ImpostorDetailTexture", P.TextureKey),
        ("ImposterTexture", P.TextureKey), ("ImposterTextureWater", P.TextureKey),
    ];

    static readonly FrozenDictionary<uint, string> ShaderByHash =
        CasShaderNames.Concat(OtherShaderNames).ToFrozenDictionary(Fnv32, n => n);

    static readonly FrozenDictionary<uint, (string Name, ShaderParameterKind Kind)> ParameterByHash =
        Parameters.ToFrozenDictionary(p => Fnv32(p.Name), p => p);

    /// <summary>Every known shader as (hash, name): CAS shaders first, then the rest by name.</summary>
    public static IReadOnlyList<(uint Hash, string Name)> Shaders { get; } =
        CasShaderNames.Select(n => (Fnv32(n), n)).Concat(OtherShaderNames.Order(StringComparer.Ordinal).Select(n => (Fnv32(n), n))).ToList();

    /// <summary>Every known parameter as (hash, name, kind), by name.</summary>
    public static IReadOnlyList<(uint Hash, string Name, ShaderParameterKind Kind)> KnownParameters { get; } =
        Parameters.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => (Fnv32(p.Name), p.Name, p.Kind)).ToList();

    /// <summary>FNV-32 (offset basis 0x811C9DC5, prime 0x01000193, multiply then xor) of the lower-cased ASCII name.</summary>
    public static uint Fnv32(string name)
    {
        var hash = 0x811C9DC5u;
        foreach (var c in name.ToLowerInvariant())
        {
            hash *= 0x01000193u;
            hash ^= (byte)c;
        }
        return hash;
    }

    /// <summary>The shader's name, or null when the hash is not in the table.</summary>
    public static string? ShaderName(uint hash) => ShaderByHash.TryGetValue(hash, out var name) ? name : null;

    /// <summary>The parameter's name, or null when the hash is not in the table.</summary>
    public static string? ParameterName(uint hash) => ParameterByHash.TryGetValue(hash, out var p) ? p.Name : null;

    /// <summary>The kind the game stores a known parameter with; null for unknown hashes.</summary>
    public static ShaderParameterKind? ParameterKind(uint hash) => ParameterByHash.TryGetValue(hash, out var p) ? p.Kind : null;

    /// <summary>Display text: the name when known, else the hash as 0xXXXXXXXX.</summary>
    public static string ShaderLabel(uint hash) => ShaderName(hash) ?? $"0x{hash:X8}";

    /// <summary>Display text: the name when known, else the hash as 0xXXXXXXXX.</summary>
    public static string ParameterLabel(uint hash) => ParameterName(hash) ?? $"0x{hash:X8}";

    /// <summary>
    /// Reads a shader or parameter typed by the user: a hash in hex (0x prefix optional when it is 8 digits), else a
    /// name, hashed. Null for empty text.
    /// </summary>
    public static uint? ParseHashOrName(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text.Length == 8 ? text : null;
        if (hex != null && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var h))
            return h;
        return text.All(c => c < 128) ? Fnv32(text) : null;
    }
}
