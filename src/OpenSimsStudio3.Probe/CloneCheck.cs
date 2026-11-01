using System.Diagnostics;
using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

/// <summary>
/// clone &lt;casp name&gt; &lt;out.package&gt;: clones one game CAS part with <see cref="CasClone" />, then checks the output:
/// every copy re-parses; no key inside a copy still names an original that was copied; every key inside resolves to the
/// clone package or the install. Writes only the given output package.
/// </summary>
static class CloneCheck
{
    static ResourceIndex? s_index;

    public static int Run(string name, string outPath)
    {
        if (name.StartsWith("sample:", StringComparison.Ordinal)) return Sample(int.Parse(name[7..]), outPath);
        var install = GameInstall.Find(null)!;
        var clock = Stopwatch.StartNew();
        var index = s_index ??= ResourceIndex.Build(install);
        Console.WriteLine($"index: {index.Count} keys in {clock.ElapsedMilliseconds} ms");
        // replace:<n> makes default replacements of n parts per category: every copy must equal the game's bytes.
        if (name.StartsWith("replace:", StringComparison.Ordinal))
        {
            var per = int.Parse(name[8..]);
            var parts = CasCatalog.Load(install).Items.GroupBy(i => i.Category)
                .SelectMany(g => g.Where((_, k) => k % Math.Max(1, g.Count() / per) == 0).Take(per)).ToList();
            int same = 0, differ = 0;
            foreach (var part in parts)
            {
                var path = Path.Combine(outPath, part.Name + ".package");
                Directory.CreateDirectory(outPath);
                var r = CasClone.Clone(index, new ResourceKey(0x034AEECB, part.Group, part.Instance), "replace", path, keepKeys: true);
                var package = s3pi.Package.Package.OpenPackage(0, path, false);
                foreach (var e in package.GetResourceList)
                {
                    var key = ResourceKey.Of(e);
                    if (ReadAll(((s3pi.Package.Package)package).GetResource(e)).AsSpan().SequenceEqual(index.Read(key))) same++;
                    else { differ++; Console.WriteLine($"DIFF {part.Name}: {key}"); }
                }
                s3pi.Package.Package.ClosePackage(0, package);
                if (r.Copied.Any(kv => kv.Key != kv.Value)) { differ++; Console.WriteLine($"FAIL {part.Name}: a key changed"); }
            }
            Console.WriteLine($"{parts.Count} replacements: {same} resources byte-identical to the game, {differ} different");
            return differ == 0 ? 0 : 1;
        }
        // pkg:<path> clones every CAS part of a custom-content package, resolving through it and then the game.
        if (name.StartsWith("pkg:", StringComparison.Ordinal))
        {
            using var cc = new PackageOverGame(name[4..], index);
            var all = CasCatalog.FromPackage(name[4..]).Items;
            Console.WriteLine($"{all.Count} CAS parts in {name[4..]}");
            var worst = 0;
            foreach (var part in all) worst = Math.Max(worst, CloneAndVerify(cc, part, Path.Combine(outPath, part.Name + ".package")));
            return worst;
        }
        var item = CasCatalog.Load(install).Items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item == null) { Console.WriteLine("no such CAS part"); return 1; }
        return CloneAndVerify(index, item, outPath);
    }

    static int CloneAndVerify(IResourceSource index, CasItem item, string outPath)
    {
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var result = CasClone.Clone(index, new ResourceKey(0x034AEECB, item.Group, item.Instance), Guid.NewGuid().ToString("N"), outPath);
        Console.WriteLine($"cloned {item.Name} in {clock.ElapsedMilliseconds} ms -> {result.NewCasp}");
        foreach (var (old, fresh) in result.Copied) Console.WriteLine($"  copied {old} -> {fresh}");
        Console.WriteLine($"  game references kept: {result.GameReferences.Count}; missing from install: {result.Missing.Count} {string.Join(" ", result.Missing.Take(5))}");

        var failures = 0;
        var package = s3pi.Package.Package.OpenPackage(0, outPath, false);
        var inPackage = package.GetResourceList.Select(e => ResourceKey.Of(e)).ToHashSet();
        foreach (var fresh in result.Copied.Values)
            if (!inPackage.Contains(fresh)) { Console.WriteLine($"FAIL: {fresh} missing from the package"); failures++; }
        foreach (var e in package.GetResourceList)
        {
            var key = ResourceKey.Of(e);
            if (key.Type == 0x00AE6C67) continue;
            var resource = CasClone.Parse(key, ReadAll(((s3pi.Package.Package)package).GetResource(e)));
            // Structure must survive, not just keys: every RCOL chunk re-reads as its real type under its own key,
            // and the copy holds as many keys as the original (a blob with no keys would otherwise pass vacuously).
            var original = result.Copied.First(kv => kv.Value == key).Key;
            var source = CasClone.Parse(original, index.Read(original)!);
            if (resource is s3pi.GenericRCOLResource.GenericRCOLResource rcol && source is s3pi.GenericRCOLResource.GenericRCOLResource was)
                for (var c = 0; c < rcol.ChunkEntries.Count; c++)
                {
                    var chunk = rcol.ChunkEntries[c];
                    if (chunk.RCOLBlock.GetType().Name == "DefaultRCOL") { Console.WriteLine($"FAIL: {key} chunk {c} re-reads untyped"); failures++; }
                    // EA leaves some chunk keys zero (GEOM); a chunk key changes only when it was one of the copied keys.
                    var old = ResourceKey.Of(was.ChunkEntries[c].TGIBlock);
                    var expected = result.Copied.TryGetValue(old, out var moved) ? moved : old;
                    if (ResourceKey.Of(chunk.TGIBlock) != expected) { Console.WriteLine($"FAIL: {key} chunk {c} key reads {ResourceKey.Of(chunk.TGIBlock)}, expected {expected}"); failures++; }
                }
            var before = CasClone.KeysIn(source).Count();
            var after = CasClone.KeysIn(resource).Count();
            if (before != after) { Console.WriteLine($"FAIL: {key} holds {after} keys, its original {original} {before}"); failures++; }
            foreach (var inner in CasClone.KeysIn(resource).Select(ResourceKey.Of).Where(k => k.Type != 0))
            {
                if (result.Copied.ContainsKey(inner)) { Console.WriteLine($"FAIL: {key} still names the original {inner}"); failures++; }
                else if (!inPackage.Contains(inner) && !index.Contains(inner) && !result.Missing.Contains(inner))
                { Console.WriteLine($"FAIL: {key} names {inner}, which resolves nowhere"); failures++; }
            }
        }
        s3pi.Package.Package.ClosePackage(0, package);
        Console.WriteLine(failures == 0 ? $"PASS: {inPackage.Count} resources, all references resolve" : $"{failures} failures");
        return failures == 0 ? 0 : 1;
    }

    static byte[] ReadAll(Stream s)
    {
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    static int Sample(int perGroup, string outDir)
    {
        var install = GameInstall.Find(null)!;
        var catalog = CasCatalog.Load(install);
        var names = catalog.Items.GroupBy(i => i.Category)
            .SelectMany(g => g.Where((_, k) => k % Math.Max(1, g.Count() / perGroup) == 0).Take(perGroup)).Select(i => i.Name).ToList();
        int pass = 0, fail = 0;
        foreach (var n in names)
        {
            var writer = Console.Out;
            var log = new StringWriter();
            Console.SetOut(log);
            int code;
            try { code = Run(n, Path.Combine(outDir, n + ".package")); }
            catch (Exception error) { log.WriteLine($"EXCEPTION {error.GetType().Name}: {error.Message}"); code = 2; }
            Console.SetOut(writer);
            var lines = log.ToString().Split(Environment.NewLine);
            Console.WriteLine($"{(code == 0 ? "PASS" : "FAIL")} {n}: {lines.FirstOrDefault(l => l.StartsWith("PASS") || l.Contains("failures") || l.StartsWith("EXCEPTION"))?.Trim()}");
            if (code != 0) foreach (var l in lines.Where(l => l.StartsWith("FAIL") || l.StartsWith("EXCEPTION")).Take(3)) Console.WriteLine("    " + l.Trim());
            if (code == 0) pass++; else fail++;
        }
        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
