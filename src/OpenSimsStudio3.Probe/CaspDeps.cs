using System.Collections;
using OpenSimsStudio3.Formats.Install;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

/// <summary>
/// caspdeps &lt;name|instance-hex&gt; [depth]: prints the resources one CAS part references, resolved across the install
/// (base game and packs), as the input to cloning. Read-only. Follows CASP and TXTC TGI lists and, for RCOL resources
/// (VPXY, GEOM), the external resource list plus every chunk's TGI list.
/// </summary>
static class CaspDeps
{
    static readonly Dictionary<uint, string> Names = new()
    {
        [0x034AEECB] = "CASP", [0x736884F1] = "VPXY", [0x015A1849] = "GEOM", [0x033A1435] = "TXTC", [0x00B2D882] = "_IMG",
        [0x067CAA11] = "BGEO", [0x0355E0A6] = "BOND", [0x0358B08A] = "FACE", [0x0354796A] = "TONE", [0x00AE6C67] = "BONE",
        [0x0333406C] = "_XML", [0x02DC343F] = "OBJK", [0x01D10F34] = "MLOD", [0x01661233] = "MODL", [0x8EAF13DE] = "_RIG",
        [0x02019972] = "MTST", [0x01D0E75D] = "MATD", [0x0580A2B4] = "THUM",
    };

    public static int Run(string what, int depth)
    {
        var install = GameInstall.Find(null)!;
        var index = new Dictionary<(uint, uint, ulong), string>();
        var roots = new[] { install.BaseGame }.Concat(install.Packs).Select(p => Path.Combine(p.Root, "GameData", "Shared")).ToList();
        var packages = roots.Where(Directory.Exists)
            .SelectMany(r => Directory.GetFiles(r, "*.package", SearchOption.AllDirectories))
            .OrderBy(p => Path.GetFileName(p).StartsWith("Delta", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        foreach (var path in packages)
        {
            var package = s3pi.Package.Package.OpenPackage(0, path, false);
            foreach (var e in package.GetResourceList)
                if (!e.IsDeleted) index.TryAdd((e.ResourceType, e.ResourceGroup, e.Instance), path);
            s3pi.Package.Package.ClosePackage(0, package);
        }
        Console.WriteLine($"indexed {index.Count} resources in {packages.Count} packages");

        (uint, uint, ulong)? start = null;
        if (ulong.TryParse(what, System.Globalization.NumberStyles.HexNumber, null, out var instance))
            start = index.Keys.FirstOrDefault(k => k.Item1 == 0x034AEECB && k.Item3 == instance);
        else
            foreach (var key in index.Keys.Where(k => k.Item1 == 0x034AEECB))
            {
                var casp = (CASPartResource.CASPartResource)Load(key, index[key])!;
                if (casp.Unknown1.TrimEnd('\0').Equals(what, StringComparison.OrdinalIgnoreCase)) { start = key; break; }
            }
        if (start is not { } root || root.Item1 == 0) { Console.WriteLine("CASP not found"); return 1; }
        Walk(root, index, 0, depth, new HashSet<(uint, uint, ulong)>());
        return 0;
    }

    static void Walk((uint T, uint G, ulong I) key, Dictionary<(uint, uint, ulong), string> index, int level, int depth, HashSet<(uint, uint, ulong)> seen)
    {
        var found = index.TryGetValue(key, out var path);
        var again = !seen.Add(key);
        Console.WriteLine($"{new string(' ', level * 2)}{Names.GetValueOrDefault(key.T, $"{key.T:X8}")} {key.T:X8}:{key.G:X8}:{key.I:X16} " +
                          $"{(found ? Path.GetFileName(path) : "NOT IN INSTALL")}{(again ? " (again)" : "")}");
        if (!found || again || level >= depth) return;
        foreach (var child in References(key, path!).Distinct())
            Walk(child, index, level + 1, depth, seen);
    }

    static IEnumerable<(uint, uint, ulong)> References((uint T, uint G, ulong I) key, string path)
    {
        var resource = Load(key, path);
        switch (resource)
        {
            case CASPartResource.CASPartResource casp:
                foreach (var t in casp.TGIBlocks) yield return (t.ResourceType, t.ResourceGroup, t.Instance);
                break;
            case GenericRCOLResource rcol:
                foreach (var t in rcol.Resources) yield return (t.ResourceType, t.ResourceGroup, t.Instance);
                foreach (var chunk in rcol.ChunkEntries)
                    if (chunk.RCOLBlock.GetType().GetProperty("TGIBlocks")?.GetValue(chunk.RCOLBlock) is IEnumerable list)
                        foreach (TGIBlock t in list) yield return (t.ResourceType, t.ResourceGroup, t.Instance);
                break;
            case { } other when other.GetType().GetProperty("TGIBlocks")?.GetValue(other) is IEnumerable list:
                foreach (TGIBlock t in list) yield return (t.ResourceType, t.ResourceGroup, t.Instance);
                break;
        }
    }

    static IResource? Load((uint T, uint G, ulong I) key, string path)
    {
        var package = s3pi.Package.Package.OpenPackage(0, path, false);
        try
        {
            var entry = ((s3pi.Package.Package)package).Find(e => e.ResourceType == key.T && e.ResourceGroup == key.G && e.Instance == key.I && !e.IsDeleted);
            if (entry == null) return null;
            var s = ((s3pi.Package.Package)package).GetResource(entry);
            return key.T switch
            {
                0x034AEECB => new CASPartResource.CASPartResource(0, s),
                0x736884F1 or 0x015A1849 => new GenericRCOLResource(0, s),
                0x033A1435 => (IResource)Activator.CreateInstance(Type.GetType("TxtcResource.TxtcResource, s3pi.TxtcResource")!, 0, s)!,
                _ => null,
            };
        }
        finally
        {
            s3pi.Package.Package.ClosePackage(0, package);
        }
    }
}
