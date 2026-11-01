using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Install;

/// <summary>presetrt: parses and re-writes every install CASP preset's XML with PresetModel; counts exact matches, shows first differences.</summary>
static class PresetRoundTrip
{
    public static int Run()
    {
        using var index = ResourceIndex.Build(GameInstall.Find(null)!);
        var where = (System.Collections.IDictionary)typeof(ResourceIndex).GetField("_where", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(index)!;
        int presets = 0, same = 0, failed = 0, shown = 0;
        var complates = new Dictionary<string, int>();
        foreach (ResourceKey key in where.Keys)
        {
            if (key.Type != 0x034AEECB || index.Read(key) is not { } bytes) continue;
            var casp = new CASPartResource.CASPartResource(0, new MemoryStream(bytes));
            foreach (var p in casp.Presets)
            {
                presets++;
                var xml = p.XmlFile.ReadToEnd();
                try
                {
                    var model = PresetModel.Parse(xml);
                    complates[model.Complate] = complates.GetValueOrDefault(model.Complate) + 1;
                    var again = model.ToXml();
                    if (again == xml) { same++; continue; }
                    var i = 0;
                    while (i < Math.Min(xml.Length, again.Length) && xml[i] == again[i]) i++;
                    if (shown++ < 3) Console.WriteLine($"{key}: differs at {i}: [{xml.Substring(Math.Max(0, i - 30), Math.Min(80, xml.Length - Math.Max(0, i - 30)))}] vs [{again.Substring(Math.Max(0, i - 30), Math.Min(80, again.Length - Math.Max(0, i - 30)))}]");
                }
                catch (Exception error) { failed++; if (shown++ < 3) Console.WriteLine($"{key}: {error.Message}"); }
            }
        }
        Console.WriteLine($"{presets} presets: {same} byte-identical after parse and write, {presets - same - failed} different, {failed} unparsable");
        Console.WriteLine("complates: " + string.Join(", ", complates.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        return 0;
    }
}
