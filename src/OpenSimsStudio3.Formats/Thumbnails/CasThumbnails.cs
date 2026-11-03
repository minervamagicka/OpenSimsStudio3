using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Textures;

namespace OpenSimsStudio3.Formats.Thumbnails;

/// <summary>The three CAS thumbnail sizes TS3 custom content carries.</summary>
public enum ThumbnailSize { Small, Medium, Large }

/// <summary>
/// A recolour's CAS thumbnails: PNG images in the project package, one per size, keyed by <see cref="KeyFor" />.
/// Sources and confidence: README.md in this folder. <see cref="Set" /> writes all sizes from one image (fitted to a
/// transparent square, resized, PNG-encoded); <see cref="Read" /> gives one back. Changes go through
/// <see cref="ProjectSession.Replace" /> and are written on save.
/// </summary>
public static class CasThumbnails
{
    public const uint SmallType = 0x626F60CC, MediumType = 0x626F60CD, LargeType = 0x626F60CE;

    public static readonly IReadOnlyList<ThumbnailSize> Sizes = [ThumbnailSize.Small, ThumbnailSize.Medium, ThumbnailSize.Large];

    public static uint TypeOf(ThumbnailSize size) => size switch
    {
        ThumbnailSize.Small => SmallType,
        ThumbnailSize.Medium => MediumType,
        _ => LargeType,
    };

    /// <summary>Square side in pixels: 32, 128, 256.</summary>
    public static int Pixels(ThumbnailSize size) => size switch
    {
        ThumbnailSize.Small => 32,
        ThumbnailSize.Medium => 128,
        _ => 256,
    };

    public static bool IsThumbnailType(uint type) => type is SmallType or MediumType or LargeType;

    // ---------------------------------------------------------------------------------------------------------------
    // THE KEY MAPPING. Every thumbnail key OSS3 reads or writes comes from these two functions. Provisional (README.md):
    //   type     = 0x626F60CC / CD / CE for 32 / 128 / 256 px
    //   instance = the CASP's instance
    //   group    = the preset ID (CASP preset "Unknown1") for a recolour, keeping the CASP group's top byte (EP hair
    //              carries its pack there); the CASP's own group for the item thumbnail.
    // If in-game checks show otherwise, change only these two functions (and the README).
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The key of one recolour's thumbnail at one size.</summary>
    public static ResourceKey KeyFor(ResourceKey casp, uint presetId, ThumbnailSize size) =>
        new(TypeOf(size), (casp.Group & 0xFF000000) | (presetId & 0x00FFFFFF), casp.Instance);

    /// <summary>The key of the item's own thumbnail (the CASP's group), shown where no preset thumbnail applies.</summary>
    public static ResourceKey ItemKeyFor(ResourceKey casp, ThumbnailSize size) => new(TypeOf(size), casp.Group, casp.Instance);

    /// <summary>
    /// Every key <see cref="Set" /> writes for the recolour at <paramref name="presetIndex" />: its own three, plus the
    /// item's three for the first recolour, unless another recolour's thumbnail already lives at those keys.
    /// </summary>
    public static IReadOnlyList<ResourceKey> KeysFor(ResourceKey casp, IReadOnlyList<uint> presetIds, int presetIndex)
    {
        var keys = Sizes.Select(s => KeyFor(casp, presetIds[presetIndex], s)).ToList();
        if (presetIndex == 0)
        {
            var othersAtItem = Enumerable.Range(1, presetIds.Count - 1).Any(i => KeyFor(casp, presetIds[i], ThumbnailSize.Large) == ItemKeyFor(casp, ThumbnailSize.Large));
            if (!othersAtItem) keys.AddRange(Sizes.Select(s => ItemKeyFor(casp, s)).Where(k => !keys.Contains(k)));
        }
        return keys;
    }

    /// <summary>The PNGs for every size: the image fitted to each square size and encoded.</summary>
    public static IReadOnlyDictionary<ThumbnailSize, byte[]> Encode(DdsImage image) =>
        Sizes.ToDictionary(s => s, s => Png.Encode(ImageResize.FitSquare(image, Pixels(s))));

    /// <summary>Writes the recolour's thumbnails (all sizes) from one image of any size; returns the keys written.</summary>
    public static IReadOnlyList<ResourceKey> Set(ProjectSession session, int presetIndex, DdsImage image)
    {
        var ids = PresetIds(session);
        if (presetIndex < 0 || presetIndex >= ids.Count) throw new ArgumentOutOfRangeException(nameof(presetIndex));
        var pngs = Encode(image);
        var keys = KeysFor(session.CaspKey, ids, presetIndex);
        foreach (var key in keys) session.Replace(key, pngs[SizeOf(key.Type)]);
        return keys;
    }

    /// <summary>The recolour's thumbnail image bytes at one size (the project's, else the game's), or null.</summary>
    public static byte[]? ReadBytes(ProjectSession session, int presetIndex, ThumbnailSize size = ThumbnailSize.Large)
    {
        var ids = PresetIds(session);
        if (presetIndex < 0 || presetIndex >= ids.Count) return null;
        return session.Read(KeyFor(session.CaspKey, ids[presetIndex], size))
               ?? (presetIndex == 0 ? session.Read(ItemKeyFor(session.CaspKey, size)) : null);
    }

    /// <summary>The recolour's thumbnail decoded, or null when there is none or it is not a PNG this codec reads.</summary>
    public static DdsImage? Read(ProjectSession session, int presetIndex, ThumbnailSize size = ThumbnailSize.Large)
    {
        var bytes = ReadBytes(session, presetIndex, size);
        if (bytes == null || !Png.IsPng(bytes)) return null;
        try { return Png.Decode(bytes); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// Takes a recolour's own thumbnails out of the project (before deleting the recolour); the item thumbnail stays.
    /// Returns how many were removed.
    /// </summary>
    public static int Remove(ProjectSession session, uint presetId)
    {
        var item = Sizes.Select(s => ItemKeyFor(session.CaspKey, s)).ToHashSet();
        return Sizes.Select(s => KeyFor(session.CaspKey, presetId, s)).Where(k => !item.Contains(k)).Count(session.Remove);
    }

    public static IReadOnlyList<uint> PresetIds(ProjectSession session) => session.Part.Presets.Select(p => p.Unknown1).ToList();

    static ThumbnailSize SizeOf(uint type) => type switch
    {
        SmallType => ThumbnailSize.Small,
        MediumType => ThumbnailSize.Medium,
        LargeType => ThumbnailSize.Large,
        _ => throw new ArgumentException($"{type:X8} is not a CAS thumbnail type", nameof(type)),
    };
}
