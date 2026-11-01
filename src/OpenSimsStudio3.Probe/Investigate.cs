using System.Collections;
using s3pi.GenericRCOLResource;
using s3pi.Interfaces;

/// <summary>
/// investigate &lt;lite|txtc|vpxy|objk|objd|casp&gt; &lt;packages…&gt;: targeted correlation reports for fields s3pi leaves
/// unnamed (see docs/cas-studio/research/undocumented-fields.md). No per-type cap: every resource of the type is read.
/// Read-only. Each report prints value tuples with counts, plus sample keys or names so cases can be looked up.
/// </summary>
static class Investigate
{
    static readonly SortedDictionary<string, int> Tuples = new();
    static readonly Dictionary<string, List<string>> Samples = new();

    static void Count(string tuple, string sample)
    {
        Tuples[tuple] = Tuples.GetValueOrDefault(tuple) + 1;
        if (!Samples.TryGetValue(tuple, out var list)) Samples[tuple] = list = new List<string>();
        if (list.Count < 6 && !list.Contains(sample)) list.Add(sample);
    }

    static string Pack(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        var i = Array.FindIndex(parts, p => p.Equals("GameData", StringComparison.OrdinalIgnoreCase));
        var name = i > 0 ? parts[i - 1].Replace("The Sims 3", "").Trim() : "?";
        return name.Length == 0 ? "Base" : name;
    }

    static IEnumerable<(IResourceIndexEntry Entry, Stream Stream, string Pack)> Resources(string[] packages, uint type)
    {
        foreach (var path in packages)
        {
            var package = s3pi.Package.Package.OpenPackage(0, path, false);
            foreach (var e in package.GetResourceList.Where(e => e.ResourceType == type && !e.IsDeleted))
            {
                var s = ((s3pi.Package.Package)package).GetResource(e);
                if (s != null) yield return (e, s, Pack(path));
            }
        }
    }

    static string Key(IResourceIndexEntry e) => $"{e.ResourceType:X8}:{e.ResourceGroup:X8}:{e.Instance:X16}";

    static Dictionary<ulong, string>? s_objdNames;
    static Dictionary<ulong, ulong>? s_vpxyToObjk;
    static Dictionary<ulong, string>? s_txtcRoles;

    static Dictionary<ulong, string> ObjdNames(string[] packages)
    {
        if (s_objdNames != null) return s_objdNames;
        s_objdNames = new();
        var type = Type.GetType("CatalogResource.ObjectCatalogResource, s3pi.CatalogResource")!;
        foreach (var (e, s, _) in Resources(packages, 0x319E4F1D))
            try { dynamic d = Activator.CreateInstance(type, 0, s)!; s_objdNames.TryAdd(e.Instance, (string)(d.CommonBlock.Name ?? "")); } catch { }
        return s_objdNames;
    }

    static Dictionary<ulong, ulong> VpxyToObjk(string[] packages)
    {
        if (s_vpxyToObjk != null) return s_vpxyToObjk;
        s_vpxyToObjk = new();
        var type = Type.GetType("ObjKeyResource.ObjKeyResource, s3pi.ObjKeyResource")!;
        foreach (var (e, s, _) in Resources(packages, 0x02DC343F))
            try
            {
                dynamic k = Activator.CreateInstance(type, 0, s)!;
                foreach (TGIBlock t in (IEnumerable)k.TGIBlocks)
                    if (t.ResourceType == 0x736884F1) s_vpxyToObjk.TryAdd(t.Instance, e.Instance);
            }
            catch { }
        return s_vpxyToObjk;
    }

    static Dictionary<ulong, string> TxtcRoles(string[] packages)
    {
        if (s_txtcRoles != null) return s_txtcRoles;
        s_txtcRoles = new();
        foreach (var (_, s, _) in Resources(packages, 0x034AEECB))
            try
            {
                var casp = new CASPartResource.CASPartResource(0, s);
                void Mark(IEnumerable<byte> indexes, string role)
                {
                    foreach (var i in indexes)
                        if (i < casp.TGIBlocks.Count && casp.TGIBlocks[i].ResourceType == 0x033A1435)
                            s_txtcRoles.TryAdd(casp.TGIBlocks[i].Instance, $"{role} of {casp.Unknown1} [{casp.Clothing}]");
                }
                Mark(casp.Diffuse1Indexes, "Diffuse1"); Mark(casp.Specular1Indexes, "Specular1");
                Mark(casp.Diffuse2Indexes, "Diffuse2"); Mark(casp.Specular2Indexes, "Specular2");
            }
            catch { }
        return s_txtcRoles;
    }

    public static int Run(string what, string[] packages)
    {
        switch (what)
        {
            case "lite":
                foreach (var (e, s, pack) in Resources(packages, 0x03B4C61D))
                    foreach (var c in new GenericRCOLResource(0, s).ChunkEntries)
                    {
                        dynamic lite = c.RCOLBlock;
                        int lights = lite.LightSources.Count, occluders = lite.Occluders.Count;
                        var size = c.RCOLBlock.AsBytes.Length;
                        Count($"u1=0x{(uint)lite.Unknown1:X} u2={(ushort)lite.Unknown2} lights={lights} occluders={occluders} chunkBytes={size}", $"{pack} {Key(e)}");
                    }
                break;
            case "txtc":
                foreach (var (e, s, pack) in Resources(packages, 0x033A1435))
                {
                    dynamic t = Activator.CreateInstance(Type.GetType("TxtcResource.TxtcResource, s3pi.TxtcResource")!, 0, s)!;
                    dynamic root = t.Root;
                    var role = TxtcRoles(packages).TryGetValue(e.Instance, out var r) ? r : "not referenced by a CASP";
                    var slot = role.Split(' ')[0];
                    Count($"unknown3={root.Unknown3} slot={slot} dataType={root.DataType} superBlocks={((IList)root.SuperBlocks).Count}", role);
                }
                break;
            case "vpxy":
                foreach (var (e, s, pack) in Resources(packages, 0x736884F1))
                    foreach (var c in new GenericRCOLResource(0, s).ChunkEntries)
                    {
                        dynamic v = c.RCOLBlock;
                        var unused = Convert.ToHexString((byte[])v.Unused);
                        if (unused == "00000000") { Count($"unused=0 modular={v.Modular}", $"{pack}"); continue; }
                        var objk = VpxyToObjk(packages).TryGetValue(e.Instance, out var oi) ? oi : 0UL;
                        var name = objk != 0 && ObjdNames(packages).TryGetValue(objk, out var n) ? n : $"(no OBJD) " + Key(e);
                        Count($"unused={unused} entries={((IList)v.Entries).Count} modular={v.Modular}", $"{pack}:{name}");
                    }
                break;
            case "objk":
                foreach (var (e, s, pack) in Resources(packages, 0x02DC343F))
                {
                    dynamic k = Activator.CreateInstance(Type.GetType("ObjKeyResource.ObjKeyResource, s3pi.ObjKeyResource")!, 0, s)!;
                    var comps = new List<string>();
                    foreach (var comp in (IEnumerable)k.Components) comps.Add(comp.ToString()!);
                    var data = new List<string>();
                    foreach (dynamic d in (IEnumerable)k.ComponentData) data.Add((string)d.Key.ToString());
                    var objName = ObjdNames(packages).TryGetValue(e.Instance, out var on) ? on : Key(e);
                    Count($"unknown1={k.Unknown1}", $"{pack}:{objName}");
                }
                break;
            case "objd":
            {
                var flags = new[] { "ObjectTypeFlags2", "FunctionSubCategoryFlags", "SlotPlacementFlags", "BuildCategoryFlags" };
                foreach (var (e, s, pack) in Resources(packages, 0x319E4F1D))
                {
                    var o = Activator.CreateInstance(Type.GetType("CatalogResource.ObjectCatalogResource, s3pi.CatalogResource")!, 0, s)!;
                    dynamic d = o;
                    string name = d.CommonBlock.Name ?? "";
                    foreach (var f in flags)
                    {
                        object value;
                        try { value = o.GetType().GetProperty(f)!.GetValue(o)!; } catch { continue; }
                        var enumType = value.GetType();
                        var bits = Convert.ToUInt64(value);
                        for (var b = 0; b < 64; b++)
                        {
                            if ((bits & (1UL << b)) == 0) continue;
                            var bv = Enum.ToObject(enumType, 1UL << b);
                            var bn = Enum.IsDefined(enumType, bv) ? bv.ToString()! : "";
                            if (bn.Length == 0 || bn.StartsWith("Unknown") || bn.StartsWith("Unused"))
                                Count($"{f} bit 0x{1UL << b:X} {bn}", $"{pack}:{name}");
                        }
                    }
                }
                break;
            }
            case "casp":
                foreach (var (e, s, pack) in Resources(packages, 0x034AEECB))
                {
                    var casp = new CASPartResource.CASPartResource(0, s);
                    var bits = (uint)casp.ClothingCategory;
                    foreach (var b in new uint[] { 0x8000, 0x10000, 0x20000 })
                        if ((bits & b) != 0) Count($"category bit 0x{b:X5} on {casp.Clothing} (all category bits 0x{bits & 0xFFFFF:X5})", $"{pack}:{casp.Unknown1}");
                }
                break;
            default:
                Console.Error.WriteLine("investigate <lite|txtc|vpxy|objk|objd|casp> <packages…>");
                return 2;
        }
        foreach (var (tuple, n) in Tuples.OrderByDescending(t => t.Value))
            Console.WriteLine($"{n,7}  {tuple}   e.g. {string.Join(" | ", Samples[tuple])}");
        return 0;
    }
}
