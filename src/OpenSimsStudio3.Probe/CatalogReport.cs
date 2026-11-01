using System.Diagnostics;
using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;

/// <summary>cascatalog [gameRoot]: loads the item browser's CAS catalog and prints counts, timing and thumbnail coverage.</summary>
static class CatalogReport
{
    public static int Run(string? chosenRoot)
    {
        var install = GameInstall.Find(chosenRoot);
        if (install == null) { Console.WriteLine("game: not found"); return 1; }
        var clock = Stopwatch.StartNew();
        var catalog = CasCatalog.Load(install);
        Console.WriteLine($"{catalog.Items.Count} CAS parts in {clock.ElapsedMilliseconds} ms");
        foreach (var g in catalog.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-18} {g.Count(),6}  thumbnails {g.Count(catalog.HasThumbnail),6}  human {g.Count(i => i.Species is CASPartResource.SpeciesType.Human or CASPartResource.SpeciesType.None),6}");
        foreach (var g in catalog.Items.GroupBy(i => i.ProductMajor).OrderBy(g => g.Key))
            Console.Write($"{GameProducts.Code(g.Key)}={g.Count()} ");
        Console.WriteLine();
        clock.Restart();
        var sample = catalog.Items.Where(catalog.HasThumbnail).Take(50).ToList();
        var bytes = sample.Sum(i => catalog.Thumbnail(i)?.Length ?? 0);
        Console.WriteLine($"50 thumbnails read in {clock.ElapsedMilliseconds} ms ({bytes / 50} bytes each on average)");
        return 0;
    }
}
