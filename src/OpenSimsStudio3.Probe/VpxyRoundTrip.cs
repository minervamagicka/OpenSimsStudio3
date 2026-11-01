using OpenSimsStudio3.Formats.Install;
using s3pi.GenericRCOLResource;

/// <summary>vpxyrt: re-serialises every live VPXY in the install with s3pi and reports which differ, and how.</summary>
static class VpxyRoundTrip
{
    public static int Run()
    {
        using var index = ResourceIndex.Build(GameInstall.Find(null)!);
        var keys = typeof(ResourceIndex).GetField("_where", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(index) as System.Collections.IDictionary;
        int same = 0, differ = 0, shown = 0;
        var shapes = new Dictionary<string, int>();
        foreach (ResourceKey key in keys!.Keys)
        {
            if (key.Type != 0x736884F1 || index.Read(key) is not { } bytes) continue;
            var rcol = new GenericRCOLResource(0, new MemoryStream(bytes));
            var vpxy = rcol.ChunkEntries[0].RCOLBlock as VPXY;
            rcol.ChunkEntries[0].RCOLBlock.GetType().GetMethod("OnRCOLChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(rcol.ChunkEntries[0].RCOLBlock, [null, EventArgs.Empty]);
            var again = new MemoryStream();
            rcol.Stream.CopyTo(again);
            var b = again.ToArray();
            if (b.AsSpan().SequenceEqual(bytes)) { same++; continue; }
            differ++;
            var first = 0;
            while (first < Math.Min(b.Length, bytes.Length) && b[first] == bytes[first]) first++;
            var shape = $"len {bytes.Length}->{b.Length}, entries {vpxy?.Entries.Count}, modular {vpxy?.Modular}";
            var bucket = $"delta {b.Length - bytes.Length}, entries {(vpxy?.Entries.Count == 0 ? "0" : ">0")}, modular {vpxy?.Modular}";
            shapes[bucket] = shapes.GetValueOrDefault(bucket) + 1;
            var diffs = Enumerable.Range(0, Math.Min(b.Length, bytes.Length)).Where(i => b[i] != bytes[i]).ToList();
            if (shown < 2) Console.WriteLine($"    differing offsets: {string.Join(",", diffs.Take(12).Select(i => $"0x{i:X}"))}; game tail {Convert.ToHexString(bytes.AsSpan(Math.Max(0, bytes.Length - 24)))}; s3pi tail {Convert.ToHexString(b.AsSpan(Math.Max(0, b.Length - 28)))}");
            if (shown++ < 4)
                Console.WriteLine($"{key}: {shape}; first difference at 0x{first:X}: game {Convert.ToHexString(bytes.AsSpan(first, Math.Min(16, bytes.Length - first)))} s3pi {Convert.ToHexString(b.AsSpan(first, Math.Min(16, b.Length - first)))}");
        }
        Console.WriteLine($"{same} identical, {differ} different");
        foreach (var (s, n) in shapes.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {n,6}  {s}");
        return 0;
    }
}
