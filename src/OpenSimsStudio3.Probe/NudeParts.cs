using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;

/// <summary>Lists the install's nude CAS parts (the Whole Sim body), with ages, genders, part type and instance.</summary>
static class NudeParts
{
    public static int Run()
    {
        var install = GameInstall.Find(null) ?? throw new InvalidOperationException("no install");
        var catalog = CasCatalog.Load(install);
        foreach (var i in catalog.Items.Where(i => (i.Name.Contains("Nude", StringComparison.OrdinalIgnoreCase) || i.Part is CASPartResource.ClothingType.Face or CASPartResource.ClothingType.Scalp) && i.Species is CASPartResource.SpeciesType.Human or 0))
            Console.WriteLine($"{i.Name,-32} {i.Part,-10} {i.Ages,-40} {i.Genders,-14} {i.Key} {Path.GetFileName(i.Package)}");
        return 0;
    }
}
