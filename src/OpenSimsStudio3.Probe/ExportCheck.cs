using OpenSimsStudio3.Formats.Projects;

/// <summary>export &lt;out.package&gt; &lt;project folder…&gt;: merged export of projects, printing what went in or why not.</summary>
static class ExportCheck
{
    public static int Run(string outPath, string[] folders)
    {
        var result = ProjectExport.Export(folders, outPath);
        Console.WriteLine(result.Succeeded
            ? $"exported {result.Resources} resources from {string.Join(", ", result.Projects)} to {outPath}"
            : $"not exported ({result.Problems.Count} problems): {string.Join("; ", result.Problems.Take(3))}");
        if (!result.Succeeded) return 1;
        var package = s3pi.Package.Package.OpenPackage(0, outPath, false);
        var count = package.GetResourceList.Count;
        s3pi.Package.Package.ClosePackage(0, package);
        Console.WriteLine($"re-read: {count} resources");
        return count == result.Resources ? 0 : 1;
    }
}
