namespace OpenSimsStudio3.Formats.Projects;

/// <summary>What a copy to a test profile will do: where the package goes and which cache files it will delete.</summary>
public sealed record TestProfilePlan(string UserFolder, string Destination, IReadOnlyList<string> CachesToDelete, IReadOnlyList<ExportCheck> Checks)
{
    public bool CanCopy => Checks.All(c => c.Severity != CheckSeverity.Error);
}

/// <summary>What a copy to a test profile did.</summary>
public sealed record TestProfileResult(string Copied, IReadOnlyList<string> Deleted);

/// <summary>
/// Copies an exported .package into a Sims 3 user folder (Documents\Electronic Arts\The Sims 3, or a copy of it kept
/// for testing) under Mods\Packages, and deletes that folder's CAS cache files so the game rebuilds them. Only the
/// files named in <see cref="CasCacheFiles" />, directly in the chosen user folder, are ever deleted; nothing else in
/// the folder or outside it is touched, and nothing at all when the plan has an error.
/// </summary>
public static class TestProfile
{
    /// <summary>
    /// The CAS caches in the user folder's root. Player guides agree on five cache files there (CASPartCache,
    /// compositorCache, scriptCache, simCompositorCache, socialCache) that the game rebuilds when missing, and map
    /// CASPartCache.package to CAS parts and simCompositorCache.package to the Sims' composed textures; the other three
    /// cover Build/Buy objects, script mods and social features. Sources (checked 2026-11-03): the SimsWiki/MTS page
    /// "Game Help:Sims 3 Delete Cache Files", The Sims Wiki's "Game guide: Multiple ways to improve The Sims 3's
    /// performance", and TheSimsTree's "How to add mods to The Sims 3 and clear cache" (see Sims3Pack/README.md).
    /// </summary>
    public static readonly IReadOnlyList<string> CasCacheFiles = ["CASPartCache.package", "simCompositorCache.package"];

    /// <summary>Files whose presence marks a folder as a Sims 3 user folder.</summary>
    static readonly string[] UserFolderMarks = ["Options.ini", "Saves", "Mods", "CASPartCache.package"];

    /// <summary>The usual user folder: Documents\Electronic Arts\The Sims 3.</summary>
    public static string DefaultUserFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts", "The Sims 3");

    public static TestProfilePlan Plan(string userFolder, string packagePath, bool clearCaches)
    {
        var checks = new List<ExportCheck>();
        var target = new CheckTarget(CheckTargetKind.TestProfile, userFolder);
        if (userFolder.Length == 0 || !Directory.Exists(userFolder))
        {
            checks.Add(new ExportCheck(CheckSeverity.Error, "Test profile folder not found", target));
            return new TestProfilePlan(userFolder, "", [], checks);
        }
        var root = Path.GetFullPath(userFolder);
        if (Path.GetPathRoot(root) is { } drive && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(drive), StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new ExportCheck(CheckSeverity.Error, "Test profile can't be a drive root", target));
            return new TestProfilePlan(root, "", [], checks);
        }
        if (!UserFolderMarks.Any(m => Path.Exists(Path.Combine(root, m))))
            checks.Add(new ExportCheck(CheckSeverity.Error, "Not a Sims 3 user folder", target));
        if (!File.Exists(Path.Combine(root, "Mods", "Resource.cfg")))
            checks.Add(new ExportCheck(CheckSeverity.Warning, "No Mods\\Resource.cfg in the test profile", target));

        var destination = Path.Combine(root, "Mods", "Packages", Path.GetFileName(packagePath));
        var caches = clearCaches ? CasCacheFiles.Select(f => Path.Combine(root, f)).Where(IsOwnedCache(root)).ToList() : [];
        if (checks.Count == 0) checks.Add(ExportCheck.Ok("Test profile"));
        return new TestProfilePlan(root, destination, caches, checks);
    }

    /// <summary>A cache path is deleted only if it is a plain file (not a link or folder) directly in the user folder.</summary>
    static Func<string, bool> IsOwnedCache(string root) => path =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
        && CasCacheFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        && File.Exists(path)
        && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    /// <summary>Copies the package and deletes the plan's caches. Throws if the plan has an error.</summary>
    public static TestProfileResult Apply(TestProfilePlan plan, string packagePath)
    {
        if (!plan.CanCopy) throw new InvalidOperationException("The test-profile copy has unresolved problems.");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)!);
        File.Copy(packagePath, plan.Destination, overwrite: true);
        var deleted = new List<string>();
        foreach (var cache in plan.CachesToDelete.Where(IsOwnedCache(plan.UserFolder)))
        {
            File.Delete(cache);
            deleted.Add(cache);
        }
        return new TestProfileResult(plan.Destination, deleted);
    }
}
