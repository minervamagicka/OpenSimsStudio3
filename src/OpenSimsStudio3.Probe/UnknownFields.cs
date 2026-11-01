using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

/// <summary>
/// unknowns: tabulates every field s3pi leaves unnamed (properties called Unknown*, reserved, unused, padding…) and
/// every flag bit with no name or an "Unknown" name, across all resources s3pi can parse in the given packages.
/// For each field it reports the value distribution, and for each value the resource context (part type for CASPs,
/// shader for meshes and materials, chunk tag otherwise) and the package it came from, so correlations stand out.
/// Read-only: packages are opened without write access.
/// </summary>
static class UnknownFields
{
    static readonly Regex UnnamedName = new(@"unknown|reserved|unused|padding|^pad\d*$|^unk\d*$|^dummy", RegexOptions.IgnoreCase);
    static readonly HashSet<string> SkipProperties = new()
    {
        "ContentFields", "Value", "Stream", "AsBytes", "RecommendedApiVersion", "RequestedApiVersion",
        "ParentVertexFormats", "ParentTGIBlocks", "TGIBlocks", "TGIBlock", "Resources", "IsBlocking",
        // Per-vertex / per-face geometry: large and fully named; skipping keeps the walk fast.
        "VertexData", "Faces", "Vertex", "Buffer", "Indices", "Positions", "Normals", "UVs", "Deltas",
    };
    // Animation (Jazz) and raw-data resources: large, not CC-authoring relevant here.
    static readonly HashSet<uint> SkipTypes = new() { 0x02D5DF13, 0x220557DA /* STBL */ };
    const int MaxPerType = 6000, MaxDepth = 8;

    sealed class Field
    {
        public int Seen;
        public readonly Dictionary<string, Bucket> Values = new();
    }

    sealed class Bucket
    {
        public int Count;
        public readonly Dictionary<string, int> Contexts = new();
        public readonly Dictionary<string, int> Packages = new();
    }

    // key: "<resource or chunk tag>|<property path>"
    static readonly Dictionary<string, Field> Fields = new();
    static readonly Dictionary<string, int> ParsedByTag = new();

    public static int Run(string[] packages)
    {
        var handlers = DiscoverHandlers();
        Console.Error.WriteLine($"s3pi wrappers for {handlers.Count} resource types");
        var perType = new Dictionary<uint, int>();
        var failures = new Dictionary<string, int>();
        foreach (var path in packages)
        {
            var pack = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))))) ?? "?";
            pack = pack.Replace("The Sims 3", "").Trim();
            if (pack.Length == 0) pack = "Base";
            var package = s3pi.Package.Package.OpenPackage(0, path, false);
            foreach (var entry in package.GetResourceList)
            {
                if (entry.IsDeleted || SkipTypes.Contains(entry.ResourceType) || !handlers.TryGetValue(entry.ResourceType, out var ctor)) continue;
                perType.TryGetValue(entry.ResourceType, out var n);
                if (n >= MaxPerType) continue;
                perType[entry.ResourceType] = n + 1;
                using var stream = ((s3pi.Package.Package)package).GetResource(entry);
                if (stream == null) continue;
                try
                {
                    var resource = (AResource)ctor.Invoke(new object[] { 0, stream });
                    if (resource is GenericRCOLResource rcol)
                        foreach (var chunk in rcol.ChunkEntries)
                            Walk(chunk.RCOLBlock, chunk.RCOLBlock.Tag.Trim(), Context(chunk.RCOLBlock, null), pack);
                    else
                        Walk(resource, resource.GetType().Name, Context(resource, null), pack);
                }
                catch (Exception e)
                {
                    var key = $"{entry.ResourceType:X8} {e.GetType().Name}";
                    failures[key] = failures.GetValueOrDefault(key) + 1;
                }
            }
        }

        foreach (var (tag, n) in ParsedByTag.OrderBy(t => t.Key)) Console.WriteLine($"PARSED {tag} {n}");
        foreach (var (key, n) in failures) Console.WriteLine($"PARSE_FAIL {key} x{n}");
        foreach (var (key, field) in Fields.OrderBy(f => f.Key))
        {
            var (tag, fieldPath) = (key.Split('|')[0], key.Split('|')[1]);
            Console.WriteLine($"FIELD {tag} {fieldPath} seen={field.Seen} distinct={field.Values.Count}");
            foreach (var (value, b) in field.Values.OrderByDescending(v => v.Value.Count).Take(12))
            {
                string Top(Dictionary<string, int> d) => string.Join(", ", d.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key}:{x.Value}"));
                Console.WriteLine($"  {value,-28} n={b.Count,-7} ctx[{Top(b.Contexts)}] packs[{Top(b.Packages)}]");
            }
        }
        return 0;
    }

    /// <summary>Resource type → (int apiVersion, Stream) constructor, from every s3pi AResourceHandler.</summary>
    static Dictionary<uint, ConstructorInfo> DiscoverHandlers()
    {
        // Load every s3pi wrapper assembly beside us so their handlers can be found.
        var folder = Path.GetDirectoryName(typeof(GenericRCOLResource).Assembly.Location)!;
        foreach (var dll in Directory.GetFiles(folder, "s3pi.*.dll"))
            try { System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dll); } catch { }
        var map = new Dictionary<uint, ConstructorInfo>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types.Where(t => !t.IsAbstract && typeof(AResourceHandler).IsAssignableFrom(t)))
            {
                if (t.GetConstructor(Type.EmptyTypes) is not { } hc) continue;
                AResourceHandler handler;
                try { handler = (AResourceHandler)hc.Invoke(null); } catch { continue; }
                foreach (var (resourceType, ids) in handler)
                {
                    var ctor = resourceType.GetConstructor(new[] { typeof(int), typeof(Stream) });
                    if (ctor == null) continue;
                    foreach (var id in ids)
                        if (uint.TryParse(id.Replace("0x", ""), NumberStyles.HexNumber, null, out var key) && key != 0) map.TryAdd(key, ctor);
                }
            }
        }
        return map;
    }

    static string Context(object o, string? parent)
    {
        foreach (var name in new[] { "Clothing", "Shader" })
        {
            var p = o.GetType().GetProperty(name);
            if (p != null && p.GetIndexParameters().Length == 0)
                try { return $"{name}={p.GetValue(o)}"; } catch { }
        }
        return parent ?? "-";
    }

    static void Walk(object root, string tag, string context, string pack)
    {
        ParsedByTag[tag] = ParsedByTag.GetValueOrDefault(tag) + 1;
        WalkObject(root, tag, "", context, pack, 0);
    }

    static void WalkObject(object o, string tag, string path, string context, string pack, int depth)
    {
        if (depth > MaxDepth) return;
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || SkipProperties.Contains(p.Name)) continue;
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            if (v == null) continue;
            var here = path.Length == 0 ? p.Name : path + "." + p.Name;
            var t = v.GetType();

            if (t.IsEnum && t.GetCustomAttribute<FlagsAttribute>() != null)
            {
                var bits = Convert.ToUInt64(v);
                for (var b = 0; b < 64; b++)
                {
                    if ((bits & (1UL << b)) == 0) continue;
                    var bitValue = Enum.ToObject(t, 1UL << b);
                    var name = Enum.IsDefined(t, bitValue) ? bitValue.ToString()! : null;
                    if (name == null || UnnamedName.IsMatch(name)) Record(tag, $"{here}.bit[0x{1UL << b:X}{(name == null ? "" : " " + name)}]", "set", context, pack);
                }
                if (UnnamedName.IsMatch(p.Name)) Record(tag, here, v.ToString()!, context, pack);
                continue;
            }
            if (UnnamedName.IsMatch(p.Name) && IsLeaf(t)) { Record(tag, here, Format(v), context, pack); continue; }
            if (t == typeof(string) || t.IsPrimitive || t.IsEnum || v is TGIBlock) continue;
            if (v is IEnumerable list && v is not string && v is not byte[])
            {
                foreach (var item in list)
                    if (item is AApiVersionedFields child) WalkObject(child, tag, here + "[]", context, pack, depth + 1);
                continue;
            }
            if (v is AApiVersionedFields nested) WalkObject(nested, tag, here, Context(nested, context), pack, depth + 1);
        }
    }

    static bool IsLeaf(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(byte[]) || typeof(IEnumerable).IsAssignableFrom(t);

    static string Format(object v) => v switch
    {
        float f => f.ToString("G6", CultureInfo.InvariantCulture),
        double d => d.ToString("G6", CultureInfo.InvariantCulture),
        byte[] bytes => $"bytes[{bytes.Length}]:{Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 8))}",
        string s => s.Length > 40 ? s[..40] + "…" : s,
        uint u => $"0x{u:X8}",
        ulong ul => $"0x{ul:X16}",
        IEnumerable e => $"list[{e.Cast<object>().Count()}]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "?",
    };

    static void Record(string tag, string path, string value, string context, string pack)
    {
        var key = tag + "|" + path;
        if (!Fields.TryGetValue(key, out var field)) Fields[key] = field = new Field();
        field.Seen++;
        if (!field.Values.TryGetValue(value, out var bucket))
        {
            if (field.Values.Count >= 200) value = "(other)";
            if (!field.Values.TryGetValue(value, out bucket)) field.Values[value] = bucket = new Bucket();
        }
        bucket.Count++;
        bucket.Contexts[context] = bucket.Contexts.GetValueOrDefault(context) + 1;
        bucket.Packages[pack] = bucket.Packages.GetValueOrDefault(pack) + 1;
    }
}
