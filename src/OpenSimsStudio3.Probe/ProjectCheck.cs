using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

/// <summary>newproject &lt;casp name&gt; &lt;projectsRoot&gt;: creates a project the way the app's Create project does, then reads it back.</summary>
static class ProjectCheck
{
    public static int Run(string name, string root)
    {
        var install = GameInstall.Find(null)!;
        var item = CasCatalog.Load(install).Items.First(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var index = ResourceIndex.Build(install);
        Directory.CreateDirectory(root);
        var folder = Project.CreateCasClone(root, "Test " + item.Name, "Both", index, new ResourceKey(0x034AEECB, item.Group, item.Instance), item.Name, item.ProductMajor);
        var project = Project.Read(folder);
        var package = Path.Combine(folder, project.Package);
        Console.WriteLine($"{folder}\n  {project.Name} target {project.Target} from {project.SourceName} ({project.SourceKey}) -> {project.Casp}");
        Console.WriteLine($"  files: {string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName))}; package {new FileInfo(package).Length} bytes");
        var again = Project.CreateCasClone(root, "Test " + item.Name, "Both", index, new ResourceKey(0x034AEECB, item.Group, item.Instance), item.Name, item.ProductMajor);
        Console.WriteLine($"  same name again -> {Path.GetFileName(again)}; new CASP {Project.Read(again).Casp} (differs: {Project.Read(again).Casp != project.Casp})");
        return 0;
    }
}
