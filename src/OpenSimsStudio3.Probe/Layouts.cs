using System.Text;
using System.Text.RegularExpressions;
using s3pi.Interfaces;

/// <summary>
/// layouts &lt;regex&gt; &lt;outDir&gt; &lt;packages…&gt;: finds UI layout resources (LAYO, XML text) whose text matches the regex,
/// saves each match under outDir (keep it under _local: EA data) and prints its key, size and the image keys it names.
/// Read-only on the packages.
/// </summary>
static class Layouts
{
    const uint Layo = 0x025C95B6;
    static readonly Regex ImageKey = new(@"0x(?<t>[0-9A-Fa-f]{8}):0x(?<g>[0-9A-Fa-f]{8}):0x(?<i>[0-9A-Fa-f]{16})", RegexOptions.Compiled);

    public static int Run(string pattern, string outDir, string[] packages)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        Directory.CreateDirectory(outDir);
        var seen = new HashSet<ulong>();
        foreach (var path in packages)
        {
            var package = s3pi.Package.Package.OpenPackage(0, path, false);
            foreach (var e in package.GetResourceList.Where(e => e.ResourceType == Layo && !e.IsDeleted))
            {
                var stream = ((s3pi.Package.Package)package).GetResource(e);
                if (stream == null) continue;
                var text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                if (!regex.IsMatch(text) || !seen.Add(e.Instance)) continue;
                File.WriteAllText(Path.Combine(outDir, $"{e.Instance:X16}.xml"), text);
                var images = ImageKey.Matches(text).Select(m => m.Value).Distinct().ToList();
                Console.WriteLine($"{Path.GetFileName(path)} {e.ResourceGroup:X8}:{e.Instance:X16} {text.Length} chars, {images.Count} keys: {string.Join(" ", images.Take(6))}");
            }
        }
        // Loading-screen backgrounds are the PNG (0x2F7D0004) sharing the layout's instance; they can live in any
        // package given (the base game keeps them in DeltaBuild/FullBuild, not UI.package). First match wins.
        var exported = 0;
        foreach (var path in packages)
        {
            var package = s3pi.Package.Package.OpenPackage(0, path, false);
            foreach (var r in package.GetResourceList.Where(r => r.ResourceType == 0x2F7D0004 && seen.Contains(r.Instance) && !r.IsDeleted))
            {
                var file = Path.Combine(outDir, $"{r.Instance:X16}.png");
                if (File.Exists(file) || ((s3pi.Package.Package)package).GetResource(r) is not { } ps) continue;
                using (var f = File.Create(file)) ps.CopyTo(f);
                exported++;
            }
        }
        Console.WriteLine($"{seen.Count} layouts, {exported} background images exported");
        return 0;
    }
}
