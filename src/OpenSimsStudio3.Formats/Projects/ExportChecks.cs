using System.Globalization;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>How bad a check result is. Errors stop the export; warnings don't.</summary>
public enum CheckSeverity { Ok, Warning, Error }

/// <summary>What a check's Show link jumps to.</summary>
public enum CheckTargetKind { None, Project, Package, Resource, OutputPath, TestProfile }

/// <summary>
/// Where a check applies: a project folder, its package, a resource key in it, the output path or the test profile.
/// <see cref="Path" /> is the file or folder to show (empty for <see cref="CheckTargetKind.None" />).
/// </summary>
public sealed record CheckTarget(CheckTargetKind Kind, string Path = "", ResourceKey? Key = null)
{
    public static readonly CheckTarget None = new(CheckTargetKind.None);
}

/// <summary>One line of the export window's Checks list.</summary>
public sealed record ExportCheck(CheckSeverity Severity, string Label, CheckTarget Target)
{
    public static ExportCheck Ok(string label) => new(CheckSeverity.Ok, label, CheckTarget.None);
    public bool IsProblem => Severity != CheckSeverity.Ok;
}

/// <summary>A project that passed the read checks: its name and package path.</summary>
public sealed record ExportSource(string Folder, string Name, string Package);

/// <summary>The checks of an export, and the projects that can be read.</summary>
public sealed record ExportPlan(string OutPath, IReadOnlyList<ExportCheck> Checks, IReadOnlyList<ExportSource> Sources)
{
    public bool CanExport => Checks.All(c => c.Severity != CheckSeverity.Error);
    public IEnumerable<ExportCheck> Problems => Checks.Where(c => c.IsProblem);
}

/// <summary>
/// The export checks (2026-11-03). Errors (the export is refused): a project that can't be read, a missing project
/// package, an output path that is a project's own package, and the same key in two projects (the game would load only
/// one). Warnings: an empty project package, and a package without the project's CAS part (or without any CASP when
/// project.json names none). Each check that passes adds one "Ok" line, so the list reads the same for every export.
/// </summary>
public static class ExportChecks
{
    public const uint CaspType = 0x034AEECB;

    public static ExportPlan Run(IReadOnlyList<string> projectFolders, string outPath)
    {
        var checks = new List<ExportCheck>();
        var sources = new List<ExportSource>();
        var owner = new Dictionary<ResourceKey, string>();
        var collisions = new List<ExportCheck>();
        var output = outPath.Length == 0 ? null : Path.GetFullPath(outPath);
        int readable = 0, packaged = 0, filled = 0, withCasp = 0;

        if (output == null) checks.Add(new ExportCheck(CheckSeverity.Error, "No output path", new CheckTarget(CheckTargetKind.OutputPath)));

        foreach (var folder in projectFolders)
        {
            Project project;
            try { project = Project.Read(folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                checks.Add(new ExportCheck(CheckSeverity.Error, $"{folder}: {error.Message}", new CheckTarget(CheckTargetKind.Project, folder)));
                continue;
            }
            readable++;
            var package = Path.Combine(folder, project.Package);
            if (output != null && string.Equals(Path.GetFullPath(package), output, StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(new ExportCheck(CheckSeverity.Error, $"{project.Name}: the export would overwrite this project's own package",
                    new CheckTarget(CheckTargetKind.OutputPath, outPath)));
                continue;
            }
            List<ResourceKey> keys;
            try { keys = ReadKeys(package); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                checks.Add(new ExportCheck(CheckSeverity.Error, $"{project.Name}: package can't be read ({error.Message})", new CheckTarget(CheckTargetKind.Project, folder)));
                continue;
            }
            packaged++;
            sources.Add(new ExportSource(folder, project.Name, package));

            if (keys.Count == 0)
            {
                checks.Add(new ExportCheck(CheckSeverity.Warning, $"{project.Name}: package is empty", new CheckTarget(CheckTargetKind.Package, package)));
                continue;
            }
            filled++;
            var casp = TryParseKey(project.Casp);
            if (casp is { } named ? keys.Contains(named) : keys.Any(k => k.Type == CaspType)) withCasp++;
            else checks.Add(new ExportCheck(CheckSeverity.Warning,
                casp is { } k ? $"{project.Name}: CAS part {k} is not in the package" : $"{project.Name}: no CAS part in the package",
                new CheckTarget(CheckTargetKind.Package, package, casp)));

            foreach (var key in keys)
            {
                if (owner.TryGetValue(key, out var other))
                    collisions.Add(new ExportCheck(CheckSeverity.Error, $"{key} is in both \"{other}\" and \"{project.Name}\"",
                        new CheckTarget(CheckTargetKind.Resource, package, key)));
                else owner[key] = project.Name;
            }
        }
        checks.AddRange(collisions);

        // One Ok line per check that every project passed (in the order of the list in the summary above).
        var count = projectFolders.Count;
        var oks = new List<ExportCheck>();
        if (count > 0 && readable == count) oks.Add(ExportCheck.Ok(count == 1 ? "Project" : "Projects"));
        if (count > 0 && packaged == count) oks.Add(ExportCheck.Ok("Package"));
        if (count > 0 && filled == count && withCasp == count) oks.Add(ExportCheck.Ok("CAS part"));
        if (count > 1 && packaged == count && collisions.Count == 0) oks.Add(ExportCheck.Ok("Keys unique"));
        if (output != null && checks.All(c => c.Target.Kind != CheckTargetKind.OutputPath)) oks.Add(ExportCheck.Ok("Output path"));
        if (count == 0) checks.Add(new ExportCheck(CheckSeverity.Error, "No project", CheckTarget.None));
        return new ExportPlan(outPath, [.. oks, .. checks], sources);
    }

    static List<ResourceKey> ReadKeys(string package)
    {
        if (!File.Exists(package)) throw new FileNotFoundException("missing", package);
        var p = s3pi.Package.Package.OpenPackage(0, package, false);
        try { return p.GetResourceList.Where(e => !e.IsDeleted).Select(e => ResourceKey.Of(e)).ToList(); }
        finally { s3pi.Package.Package.ClosePackage(0, p); }
    }

    /// <summary>Parses a key written by <see cref="ResourceKey.ToString" /> (TTTTTTTT:GGGGGGGG:IIIIIIIIIIIIIIII).</summary>
    public static ResourceKey? TryParseKey(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 3) return null;
        return uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var t)
               && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
               && ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var i)
            ? new ResourceKey(t, g, i) : null;
    }
}
