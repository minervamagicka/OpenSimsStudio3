using OpenSimsStudio3.Formats.Install;
using s3pi.GenericRCOLResource;

/// <summary>vpxy &lt;instance-hex&gt;: prints a VPXY's entries (type, EntryID, TGI indexes) and its TGI list. Read-only.</summary>
static class VpxyDump
{
    public static int Run(string instanceHex)
    {
        using var index = ResourceIndex.Build(GameInstall.Find(null)!);
        var key = new ResourceKey(0x736884F1, 1, ulong.Parse(instanceHex, System.Globalization.NumberStyles.HexNumber));
        var rcol = new GenericRCOLResource(0, new MemoryStream(index.Read(key)!));
        var vpxy = rcol.ChunkEntries.Select(c => c.RCOLBlock).OfType<VPXY>().First();
        for (var i = 0; i < vpxy.TGIBlocks.Count; i++) Console.WriteLine($"  tgi[{i}] {ResourceKey.Of(vpxy.TGIBlocks[i])}");
        foreach (var e in vpxy.Entries)
            Console.WriteLine(e switch
            {
                VPXY.Entry00 a => $"  Entry00 id={a.EntryID} indexes=[{string.Join(",", (IEnumerable<int>)a.TGIIndexes)}]",
                VPXY.Entry01 b => $"  Entry01 index={b.TGIIndex}",
                _ => "  ?",
            });
        return 0;
    }
}
