using System.Diagnostics;
using OpenSimsStudio3.Formats.Install;

/// <summary>install [gameRoot]: prints what the start screen's Found strip would show. Read-only; writes nothing.</summary>
static class InstallReport
{
    public static int Run(string? chosenRoot)
    {
        var clock = Stopwatch.StartNew();
        var install = GameInstall.Find(chosenRoot);
        if (install == null)
        {
            Console.WriteLine("game: not found");
            return 1;
        }
        Console.WriteLine($"game: {install.Root} version {install.Version} icon {install.BaseGame.IconPath ?? "none"} ({clock.ElapsedMilliseconds} ms)");
        foreach (var pack in install.Packs)
            Console.WriteLine($"  {pack.Major,2} {pack.Code,-5} {pack.Name,-26} icon {(pack.IconPath != null ? Path.GetFileName(pack.IconPath) : "none")}");

        clock.Restart();
        var screens = LoadingScreens.Open(install);
        var indexMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var png = screens.RandomBackground(new Random());
        Console.WriteLine($"loading screens: {screens.Count} (index {indexMs} ms); random background {(png == null ? "none" : $"{png.Length} bytes")} ({clock.ElapsedMilliseconds} ms)");

        foreach (var b in ToolInstalls.FindBlender())
            Console.WriteLine($"blender: {b.Version} {(b.HasGeomTools ? "with GEOM Tools" : "no GEOM Tools")}{(b.LauncherFavorite ? " (Launcher favourite)" : "")} {b.Executable}");
        Console.WriteLine($"preferred for Edit in Blender: {ToolInstalls.PreferredBlender(null)?.Executable ?? "none"}");
        Console.WriteLine($"opents3: {ToolInstalls.FindOpenTS3() ?? "not found"}");
        return 0;
    }
}
