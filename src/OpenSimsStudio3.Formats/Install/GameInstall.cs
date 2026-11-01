// Registry enum values are only touched through RegistryValue, which returns null off Windows.
#pragma warning disable CA1416
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenSimsStudio3.Formats.Install;

/// <summary>One installed product: its folder (holding Game\Bin and GameData), its major, name and icon file.</summary>
public sealed record GameProduct(string Root, int Major, string Name)
{
    public string Code => GameProducts.Code(Major);

    /// <summary>The product's .ico under Game\Bin, or null when the folder does not have it.</summary>
    public string? IconPath
    {
        get
        {
            var file = GameProducts.IconFileName(Major);
            var path = file == null ? null : Path.Combine(Root, "Game", "Bin", file);
            return path != null && File.Exists(path) ? path : null;
        }
    }
}

/// <summary>
/// A retail The Sims 3 install. Root is the base game folder (the one holding Game\Bin and GameData); each pack lives in
/// its own folder (retail: "The Sims 3 Late Night" next to the base folder) with the same Game/GameData layout and
/// is recognised by the TS3EPnn.exe / Sims3EPnn.ico (SP for stuff packs) in its Game\Bin. Everything here is read-only.
/// </summary>
public sealed record GameInstall(GameProduct BaseGame, IReadOnlyList<GameProduct> Packs)
{
    public string Root => BaseGame.Root;

    /// <summary>Game version from Game\Bin\skuversion.txt ("GameVersion = 1.67.2.024017"), empty when unreadable.</summary>
    public string Version { get; init; } = "";

    static readonly Regex PackFile = new(@"^(?:TS3|Sims3)(EP|SP)(\d\d)(?:\.exe|\.ico)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex VersionLine = new(@"^\s*GameVersion\s*=\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The chosen folder when it is an install, otherwise the first one found in the registry, the EA/Origin default folders or a Steam library.</summary>
    public static GameInstall? Find(string? chosenRoot)
    {
        if (!string.IsNullOrWhiteSpace(chosenRoot) && TryOpen(chosenRoot) is { } chosen) return chosen;
        return Candidates().Select(TryOpen).FirstOrDefault(i => i != null);
    }

    /// <summary>The install at the folder (needs Game\Bin and GameData\Shared\Packages\FullBuild0.package), or null.</summary>
    public static GameInstall? TryOpen(string folder)
    {
        string root;
        try { root = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!Directory.Exists(Path.Combine(root, "Game", "Bin"))
            || !File.Exists(Path.Combine(root, "GameData", "Shared", "Packages", "FullBuild0.package"))) return null;

        // Packs sit beside the base folder in retail; folders inside it are checked too (unverified for other stores).
        var packs = new Dictionary<int, GameProduct>();
        var parent = Path.GetDirectoryName(root);
        foreach (var candidate in (parent != null ? Children(parent) : []).Concat(Children(root)))
        {
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) continue;
            var major = PackMajor(candidate);
            if (major > 0 && Directory.Exists(Path.Combine(candidate, "GameData", "Shared")))
                packs.TryAdd(major, new GameProduct(candidate, major, GameProducts.Name(major)!));
        }
        return new GameInstall(new GameProduct(root, 1, GameProducts.Name(1)!), packs.OrderBy(p => p.Key).Select(p => p.Value).ToArray())
        { Version = ReadVersion(root) };
    }

    static int PackMajor(string folder)
    {
        try
        {
            var bin = Path.Combine(folder, "Game", "Bin");
            if (!Directory.Exists(bin)) return 0;
            foreach (var file in Directory.EnumerateFiles(bin))
                if (PackFile.Match(Path.GetFileName(file)) is { Success: true } m)
                    return GameProducts.FromKind(m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return 0;
    }

    static string ReadVersion(string root)
    {
        try { return VersionLine.Match(File.ReadAllText(Path.Combine(root, "Game", "Bin", "skuversion.txt"))) is { Success: true } m ? m.Groups[1].Value : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    static IEnumerable<string> Children(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Usual places, best first: the installer's registry value, EA app / Origin / disc defaults, Steam libraries.</summary>
    static IEnumerable<string> Candidates()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            if (RegistryValue(RegistryHive.LocalMachine, view, @"SOFTWARE\Sims\The Sims 3", "Install Dir") is { } dir) yield return dir;

        foreach (var programs in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var p = Environment.GetFolderPath(programs);
            if (p.Length == 0) continue;
            yield return Path.Combine(p, "Electronic Arts", "The Sims 3");
            yield return Path.Combine(p, "Origin Games", "The Sims 3");
            yield return Path.Combine(p, "EA Games", "The Sims 3");
        }

        foreach (var library in SteamLibraries())
            yield return Path.Combine(library, "steamapps", "common", "The Sims 3");
    }

    static IEnumerable<string> SteamLibraries()
    {
        var steam = RegistryValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        yield return steam;
        string text;
        try { text = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        // libraryfolders.vdf: "path"  "D:\\SteamLibrary" (backslashes doubled).
        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace("\\\\", "\\");
    }

    static string? RegistryValue(RegistryHive hive, RegistryView view, string key, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var sub = baseKey.OpenSubKey(key);
            return sub?.GetValue(name) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
