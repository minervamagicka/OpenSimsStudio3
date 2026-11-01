using System.Collections.Concurrent;
using CASPartResource;
using OpenSimsStudio3.Formats.Install;
using s3pi.Interfaces;

namespace OpenSimsStudio3.Formats.Catalog;

/// <summary>The browser's groups of CAS parts (the start screen's "Clone a game item" categories).</summary>
public enum CasGroup { Tops, Bottoms, FullBody, Shoes, Accessories, Hair, MakeupAndOverlays, PetParts, Other }

/// <summary>One game CAS part as the item browser shows it. <see cref="Name" /> is the CASP's internal name (s3pi Unknown1).</summary>
public sealed record CasItem(
    ulong Instance, uint Group, string Name, ClothingType Part, CasGroup Category,
    AgeFlags Ages, GenderFlags Genders, SpeciesType Species, uint OutfitCategories, int Presets,
    int ProductMajor, string Package)
{
    public string Key => $"034AEECB:{Group:X8}:{Instance:X16}";
}

/// <summary>
/// Every CAS part the player's install has, read-only. Packages are taken from the base game and each pack
/// (GameData/Shared/Packages and the base game's GameData/Shared/DeltaPackages). Patch packages (DeltaBuild*) are read
/// before FullBuild*, so the first copy of a key seen is the current one; a deleted entry seen first hides the part.
/// Thumbnails are the medium CAS thumbnail (0x0580A2B5) with the part's instance in each product's
/// Thumbnails/AllThumbnails.package; they are read on demand into memory and never written anywhere.
/// </summary>
public sealed class CasCatalog
{
    const uint CaspType = 0x034AEECB;
    const uint ThumbnailMedium = 0x0580A2B5;

    readonly Dictionary<ulong, string> _thumbnailPackage;
    readonly ConcurrentDictionary<string, IPackage> _openThumbnailPackages = new(StringComparer.OrdinalIgnoreCase);

    CasCatalog(IReadOnlyList<CasItem> items, Dictionary<ulong, string> thumbnails)
    {
        Items = items;
        _thumbnailPackage = thumbnails;
    }

    public IReadOnlyList<CasItem> Items { get; }

    public static CasCatalog Load(GameInstall install, IProgress<string>? progress = null)
    {
        var products = new[] { install.BaseGame }.Concat(install.Packs).ToArray();
        var packages = new List<(string Path, int Major)>();
        foreach (var product in products)
        {
            var shared = Path.Combine(product.Root, "GameData", "Shared", "Packages");
            packages.AddRange(Files(shared, "DeltaBuild*.package").Select(p => (p, product.Major)));
        }
        // The base game's DeltaPackages\pNN folders patch product NN.
        foreach (var folder in Directories(Path.Combine(install.Root, "GameData", "Shared", "DeltaPackages")))
            if (int.TryParse(Path.GetFileName(folder).TrimStart('p', 'P'), out var major))
                packages.AddRange(Files(folder, "*.package").Select(p => (p, major)));
        foreach (var product in products)
        {
            var shared = Path.Combine(product.Root, "GameData", "Shared", "Packages");
            packages.AddRange(Files(shared, "FullBuild*.package").Select(p => (p, product.Major)));
        }

        var seen = new HashSet<ulong>();
        var items = new List<CasItem>();
        foreach (var (path, major) in packages)
        {
            progress?.Report(Path.GetFileName(path));
            WithPackage(path, package =>
            {
                foreach (var e in package.GetResourceList)
                {
                    if (e.ResourceType != CaspType || !seen.Add(e.Instance) || e.IsDeleted) continue;
                    using var s = ((s3pi.Package.Package)package).GetResource(e);
                    if (s != null && ItemOf(e, s, major, path) is { } item) items.Add(item);
                }
            });
        }

        var thumbnails = new Dictionary<ulong, string>();
        foreach (var product in products)
        {
            var path = Path.Combine(product.Root, "Thumbnails", "AllThumbnails.package");
            if (!File.Exists(path)) continue;
            WithPackage(path, package =>
            {
                foreach (var e in package.GetResourceList)
                    if (e.ResourceType == ThumbnailMedium && !e.IsDeleted) thumbnails.TryAdd(e.Instance, path);
            });
        }
        return new CasCatalog(items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray(), thumbnails);
    }

    /// <summary>
    /// The CAS parts of one custom-content package (for "Clone an existing package"); product 0 marks them as custom
    /// content. Game thumbnails are not looked up for them (OSS3 renders its own).
    /// </summary>
    public static CasCatalog FromPackage(string packagePath)
    {
        var items = new List<CasItem>();
        WithPackage(packagePath, package =>
        {
            foreach (var e in package.GetResourceList)
            {
                if (e.ResourceType != CaspType || e.IsDeleted) continue;
                using var s = ((s3pi.Package.Package)package).GetResource(e);
                if (s != null && ItemOf(e, s, 0, packagePath) is { } item) items.Add(item);
            }
        });
        return new CasCatalog(items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray(), new Dictionary<ulong, string>());
    }

    static CasItem? ItemOf(IResourceIndexEntry e, Stream s, int major, string path)
    {
        try
        {
            var casp = new CASPartResource.CASPartResource(0, s);
            return new CasItem(e.Instance, e.ResourceGroup, casp.Unknown1.TrimEnd('\0'), casp.Clothing, GroupOf(casp.Clothing),
                casp.AgeGender.Age, casp.AgeGender.Gender, casp.AgeGender.Species, (uint)casp.ClothingCategory, casp.Presets.Count, major, path);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"OSS3: CASP {e.ResourceGroup:X8}:{e.Instance:X16} in {path} could not be read ({error.Message}).");
            return null;
        }
    }

    public bool HasThumbnail(CasItem item) => _thumbnailPackage.ContainsKey(item.Instance);

    /// <summary>The part's medium thumbnail as encoded image bytes, or null. Keeps thumbnail packages open for reuse.</summary>
    public byte[]? Thumbnail(CasItem item)
    {
        if (!_thumbnailPackage.TryGetValue(item.Instance, out var path)) return null;
        try
        {
            var package = _openThumbnailPackages.GetOrAdd(path, p => s3pi.Package.Package.OpenPackage(0, p, false));
            lock (package)
            {
                var entry = ((s3pi.Package.Package)package).Find(e => e.ResourceType == ThumbnailMedium && e.Instance == item.Instance && !e.IsDeleted);
                if (entry == null || ((s3pi.Package.Package)package).GetResource(entry) is not { } s) return null;
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                return copy.ToArray();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Browser group of a part type. Beards sit with hair; socks, gloves and garters with accessories, as in CAS;
    /// scalps, faces and other non-wearable parts go to <see cref="CasGroup.Other" />.
    /// </summary>
    public static CasGroup GroupOf(ClothingType part) => part switch
    {
        ClothingType.Hair or ClothingType.Beard => CasGroup.Hair,
        ClothingType.Top => CasGroup.Tops,
        ClothingType.Bottom => CasGroup.Bottoms,
        ClothingType.Body => CasGroup.FullBody,
        ClothingType.Shoes => CasGroup.Shoes,
        >= ClothingType.FirstAccessory and <= ClothingType.Ring_Rt => CasGroup.Accessories,
        ClothingType.Glove or ClothingType.Socks or ClothingType.EarringL or ClothingType.EarringR or ClothingType.ArmBand
            or ClothingType.LeftGarter or ClothingType.RightGarter => CasGroup.Accessories,
        >= ClothingType.Lipstick and <= ClothingType.EyeColor => CasGroup.MakeupAndOverlays,
        ClothingType.Mascara or ClothingType.Moles or ClothingType.Freckles or ClothingType.Weathering or ClothingType.Tattoo
            or ClothingType.TattooTemplate or ClothingType.BirthMark or ClothingType.Dental => CasGroup.MakeupAndOverlays,
        >= ClothingType.BodyHairChestUpper and <= ClothingType.BodyHairLegsFeet => CasGroup.MakeupAndOverlays,
        >= ClothingType.PetBody => CasGroup.PetParts,
        _ => CasGroup.Other,
    };

    static string[] Files(string folder, string pattern) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, pattern).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : [];

    static string[] Directories(string folder) =>
        Directory.Exists(folder) ? Directory.GetDirectories(folder).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : [];

    static void WithPackage(string path, Action<IPackage> read)
    {
        IPackage? package = null;
        try
        {
            package = s3pi.Package.Package.OpenPackage(0, path, false);
            read(package);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"OSS3: {path} could not be read ({error.Message}).");
        }
        finally
        {
            if (package != null) s3pi.Package.Package.ClosePackage(0, package);
        }
    }
}
