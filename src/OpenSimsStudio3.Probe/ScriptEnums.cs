using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>
/// scriptenums &lt;assembly folder&gt; &lt;type-name regex&gt;: lists enum types (and their member names and values) whose full
/// name matches, from managed assemblies read as metadata only (MetadataLoadContext: no code is loaded or run).
/// Used to name catalog/CAS flag bits from the game's own script assemblies; prints names and values, nothing else.
/// </summary>
static class ScriptEnums
{
    public static int Run(string folder, string pattern)
    {
        var dlls = Directory.GetFiles(folder, "*.dll").Where(f => !f.Contains(".retargeted.")).ToList();
        var resolver = new PathAssemblyResolver(dlls.Concat(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")));
        using var context = new MetadataLoadContext(resolver, "mscorlib");
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        foreach (var dll in dlls)
        {
            Assembly asm;
            try { asm = context.LoadFromAssemblyPath(dll); } catch { continue; }
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types.Where(t => t.IsEnum && regex.IsMatch(t.FullName ?? "")))
            {
                Console.WriteLine($"ENUM {Path.GetFileName(dll)} {t.FullName}");
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    // Signed enums may hold negative members; print their bit pattern.
                    var raw = f.GetRawConstantValue()!;
                    var bits = raw is long or int or short or sbyte ? unchecked((ulong)Convert.ToInt64(raw)) : Convert.ToUInt64(raw);
                    Console.WriteLine($"  0x{bits:X} {f.Name}");
                }
            }
        }
        return 0;
    }
}
