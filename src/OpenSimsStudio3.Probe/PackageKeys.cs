using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

/// <summary>keys &lt;package&gt; [type-hex]: every key inside each resource (of one type, if given), marked in-package or not.</summary>
static class PackageKeys
{
    public static int Run(string path, string? typeHex)
    {
        var only = typeHex == null ? (uint?)null : Convert.ToUInt32(typeHex, 16);
        var package = s3pi.Package.Package.OpenPackage(0, path, false);
        var keys = package.GetResourceList.Select(e => ResourceKey.Of(e)).ToHashSet();
        foreach (var e in package.GetResourceList.Where(e => only == null || e.ResourceType == only))
        {
            var key = ResourceKey.Of(e);
            if (key.Type == 0x00AE6C67) continue;
            using var s = ((s3pi.Package.Package)package).GetResource(e);
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            var resource = CasClone.Parse(key, copy.ToArray());
            var inner = CasClone.KeysIn(resource).Select(ResourceKey.Of).ToList();
            Console.WriteLine($"{key}: {inner.Count} keys, {inner.Count(keys.Contains)} in this package");
            if (resource is s3pi.GenericRCOLResource.GenericRCOLResource rcol)
                foreach (var c in rcol.ChunkEntries)
                    Console.WriteLine($"    chunk {c.RCOLBlock.GetType().Name} key {ResourceKey.Of(c.TGIBlock)} {c.RCOLBlock.AsBytes.Length} bytes" +
                        (c.RCOLBlock is s3pi.GenericRCOLResource.VPXY v ? $", {v.Entries.Count} entries, {v.TGIBlocks.Count} TGIs" : ""));
            foreach (var k in inner.Where(k => k.Type is 0x015A1849 or 0x00AE6C67 or 0x736884F1).Take(6))
                Console.WriteLine($"    {k} {(keys.Contains(k) ? "in package" : "NOT in package")}");
        }
        s3pi.Package.Package.ClosePackage(0, package);
        return 0;
    }
}
