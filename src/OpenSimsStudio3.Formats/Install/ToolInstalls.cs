using System.Security;
using System.Text.RegularExpressions;

namespace OpenSimsStudio3.Formats.Install;

/// <summary>A Blender install, whether GEOM Tools is installed for its version, and whether Blender Launcher marks it favourite.</summary>
public sealed record BlenderInstall(string Executable, string Version, bool HasGeomTools, bool LauncherFavorite = false);

/// <summary>
/// Finds the other programs OSS3 works with, read-only and from fixed places only:
/// Blender under Program Files\Blender Foundation, and the builds of Blender Launcher (its "Blender Launcher.ini", in a
/// folder named Blender_Launcher* under Documents, Desktop or LocalAppData, gives library_folder and favorite_path).
/// A Blender's version is the "X.Y" folder every build keeps beside blender.exe. GEOM Tools counts as installed when
/// %APPDATA%\Blender Foundation\Blender\X.Y\scripts\addons\io_simgeom exists (the add-on's package name in GEOM Tools 2.x);
/// OpenTS3 when its per-user folder %LOCALAPPDATA%\OpenTS3 exists.
/// </summary>
public static class ToolInstalls
{
    static readonly Regex MajorMinor = new(@"^\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>Every Blender found: the Blender Launcher favourite first, then newest version first.</summary>
    public static IReadOnlyList<BlenderInstall> FindBlender()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender");
        var executables = new List<(string Exe, bool Favorite)>();
        foreach (var programs in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            foreach (var folder in Children(Path.Combine(Environment.GetFolderPath(programs), "Blender Foundation")))
                executables.Add((Path.Combine(folder, "blender.exe"), false));
        foreach (var (library, favorite) in BlenderLauncherLibraries())
            foreach (var group in Children(library))
                foreach (var build in Children(group))
                    executables.Add((Path.Combine(build, "blender.exe"), favorite != null && SamePath(build, favorite)));

        var found = new List<BlenderInstall>();
        foreach (var (exe, favorite) in executables.DistinctBy(e => e.Exe, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(exe) || VersionOf(exe) is not { } version) continue;
            found.Add(new BlenderInstall(exe, version, Directory.Exists(Path.Combine(config, version, "scripts", "addons", "io_simgeom")), favorite));
        }
        return found.OrderByDescending(b => b.LauncherFavorite)
            .ThenByDescending(b => Version.TryParse(b.Version, out var v) ? v : new Version()).ToArray();
    }

    /// <summary>
    /// The Blender to open meshes in: the chosen executable when given and present; otherwise the Blender Launcher
    /// favourite with GEOM Tools; otherwise the newest with GEOM Tools. Null when none has GEOM Tools.
    /// </summary>
    public static BlenderInstall? PreferredBlender(string? chosenExecutable)
    {
        var all = FindBlender();
        if (!string.IsNullOrWhiteSpace(chosenExecutable) && File.Exists(chosenExecutable))
            return all.FirstOrDefault(b => SamePath(b.Executable, chosenExecutable))
                   ?? new BlenderInstall(chosenExecutable, VersionOf(chosenExecutable) ?? "?", true);
        return all.FirstOrDefault(b => b.HasGeomTools);
    }

    /// <summary>The OpenTS3 user folder when it exists; null otherwise.</summary>
    public static string? FindOpenTS3()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTS3");
        return Directory.Exists(root) ? root : null;
    }

    static string? VersionOf(string exe) =>
        Children(Path.GetDirectoryName(exe)!).Select(Path.GetFileName).FirstOrDefault(n => n != null && MajorMinor.IsMatch(n));

    static IEnumerable<(string Library, string? Favorite)> BlenderLauncherLibraries()
    {
        foreach (var place in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.LocalApplicationData })
            foreach (var folder in Children(Environment.GetFolderPath(place)))
            {
                var name = Path.GetFileName(folder);
                if (!name.StartsWith("Blender_Launcher", StringComparison.OrdinalIgnoreCase) && !name.Equals("Blender Launcher", StringComparison.OrdinalIgnoreCase)) continue;
                var ini = Path.Combine(folder, "Blender Launcher.ini");
                if (!File.Exists(ini)) continue;
                string? library = null, favorite = null;
                try
                {
                    foreach (var line in File.ReadLines(ini))
                    {
                        if (line.StartsWith("library_folder=", StringComparison.OrdinalIgnoreCase)) library = line[15..].Trim();
                        else if (line.StartsWith("favorite_path=", StringComparison.OrdinalIgnoreCase)) favorite = line[14..].Trim();
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                if (library != null && Directory.Exists(library)) yield return (library, favorite);
            }
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    static string[] Children(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }
}
