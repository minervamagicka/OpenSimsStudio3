using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

/// <summary>
/// session &lt;project folder&gt;: opens a project, prints its LODs and groups, adds the Athletic category and toggles
/// Valid-for-random, saves, reopens, and checks the edits stuck and every other resource is byte-identical.
/// Writes only inside the project folder (the package, its .bak and project.json).
/// </summary>
static class SessionCheck
{
    public static int Run(string folder, bool showOnly = false)
    {
        using var game = ResourceIndex.Build(GameInstall.Find(null)!);
        var session = ProjectSession.Open(folder, game);
        if (showOnly)
        {
            Console.WriteLine($"{session.Project.Name}: categories 0x{(uint)session.Part.ClothingCategory:X8} ({session.Part.ClothingCategory}), " +
                              $"ages {session.Part.AgeGender.Age}, genders {session.Part.AgeGender.Gender}, modified {session.Project.Modified:u}");
            return 0;
        }
        Console.WriteLine($"{session.Project.Name}: {session.Project.Kind}, CASP {session.CaspKey}, {session.Part.Presets.Count} recolours, {session.PackageKeys.Count} resources");
        foreach (var lod in session.Lods)
            Console.WriteLine($"  LOD {lod.Id}: {lod.Groups.Count} groups, {lod.Vertices} vertices: " +
                              string.Join(", ", lod.Groups.Select(g => $"{g.Vertices}v{(g.InProject ? "" : " (game)")}{(g.HasVertexColour ? " colour" : "")}")));
        var before = session.PackageKeys.ToDictionary(k => k, k => session.Read(k)!);
        var categories = session.Part.ClothingCategory;
        var random = (uint)session.Part.ClothingCategory & 0x200000;
        session.Part.ClothingCategory = (CASPartResource.ClothingCategoryFlags)(((uint)categories | 0x20) ^ 0x200000);
        session.MarkChanged();
        session.Save();

        var again = ProjectSession.Open(folder, game);
        var failures = 0;
        var expected = ((uint)categories | 0x20) ^ 0x200000;
        if ((uint)again.Part.ClothingCategory != expected) { Console.WriteLine($"FAIL: categories 0x{(uint)again.Part.ClothingCategory:X} expected 0x{expected:X}"); failures++; }
        foreach (var (key, bytes) in before)
            if (key != session.CaspKey && !again.Read(key)!.AsSpan().SequenceEqual(bytes)) { Console.WriteLine($"FAIL: {key} changed"); failures++; }
        if (!File.Exists(again.PackagePath + ".bak")) { Console.WriteLine("FAIL: no .bak"); failures++; }
        // Restore, so the test leaves the project as it was apart from the .bak.
        again.Part.ClothingCategory = categories;
        again.MarkChanged();
        again.Save();
        var restored = ProjectSession.Open(folder, game);
        if (!restored.Read(session.CaspKey)!.AsSpan().SequenceEqual(before[session.CaspKey])) { Console.WriteLine("FAIL: restore is not byte-identical to the original CASP"); failures++; }
        Console.WriteLine(failures == 0 ? $"PASS: edit saved and reloaded; {before.Count - 1} other resources unchanged; restore byte-identical (random flag was {(random != 0 ? "on" : "off")})" : $"{failures} failures");
        return failures == 0 ? 0 : 1;
    }
}
