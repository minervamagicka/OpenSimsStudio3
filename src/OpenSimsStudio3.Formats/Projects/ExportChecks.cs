using System.Globalization;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>How bad a check result is. Errors stop the export; warnings don't.</summary>
public enum CheckSeverity { Ok, Warning, Error }

/// <summary>
/// What a check's Show link jumps to. The first group are files and fields of the export window; the rest are places in
/// the project editor (<see cref="IsEditor" />): the item properties, the mesh section, one LOD, one mesh group, the
/// morphs and one recolour.
/// </summary>
public enum CheckTargetKind { None, Project, Package, Resource, OutputPath, TestProfile, Properties, Mesh, Lod, MeshGroup, Morphs, Recolour }

/// <summary>
/// Where a check applies: a project folder, its package, a resource key in it, the output path or the test profile, or
/// a place in the editor of the project at <see cref="Path" /> (<see cref="Lod" />, <see cref="Group" /> and
/// <see cref="Recolour" /> say which; -1 when not used). <see cref="Path" /> is the file or folder to show (empty for
/// <see cref="CheckTargetKind.None" />).
/// </summary>
public sealed record CheckTarget(CheckTargetKind Kind, string Path = "", ResourceKey? Key = null)
{
    public static readonly CheckTarget None = new(CheckTargetKind.None);

    /// <summary>The VPXY LOD ID (EA numbers them from 1), for <see cref="CheckTargetKind.Lod" /> and <see cref="CheckTargetKind.MeshGroup" />.</summary>
    public int Lod { get; init; } = -1;
    /// <summary>The group's index in its LOD, for <see cref="CheckTargetKind.MeshGroup" />.</summary>
    public int Group { get; init; } = -1;
    /// <summary>The recolour's index (0-based), for <see cref="CheckTargetKind.Recolour" />; -1 for the recolour block itself.</summary>
    public int Recolour { get; init; } = -1;

    public bool IsEditor => Kind >= CheckTargetKind.Properties;
}

/// <summary>
/// The part of a project a check is about. The export window adds one "Ok" line per area with no problem
/// (<see cref="ExportChecks.AreaLabel" />); <see cref="General" /> covers projects, packages, keys and output.
/// </summary>
public enum CheckArea { General, Flags, Mesh, Bones, Morphs, Lods, Uvs, Textures }

/// <summary>One line of the export window's Checks list (and, for a project's own problems, of the editor's Warnings).</summary>
public sealed record ExportCheck(CheckSeverity Severity, string Label, CheckTarget Target)
{
    public static ExportCheck Ok(string label) => new(CheckSeverity.Ok, label, CheckTarget.None);
    public bool IsProblem => Severity != CheckSeverity.Ok;
    public CheckArea Area { get; init; } = CheckArea.General;
    /// <summary>A longer explanation for the line's tooltip (DESIGN.md, Wording), or null.</summary>
    public string? Tip { get; init; }
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

    /// <summary>The areas of a project's own content that <see cref="Content" /> checks, in the Checks list's order.</summary>
    public static readonly CheckArea[] PartAreas =
        [CheckArea.Flags, CheckArea.Mesh, CheckArea.Bones, CheckArea.Morphs, CheckArea.Lods, CheckArea.Uvs, CheckArea.Textures];

    /// <summary>The "Ok" line of an area with no problem (the mockup's labels).</summary>
    public static string AreaLabel(CheckArea area) => area switch
    {
        CheckArea.Flags => "Item flags",
        CheckArea.Mesh => "Mesh",
        CheckArea.Bones => "Bones and weights",
        CheckArea.Morphs => "Morphs",
        CheckArea.Lods => "LODs",
        CheckArea.Uvs => "UVs",
        CheckArea.Textures => "Recolours and textures",
        _ => "",
    };

    /// <summary>
    /// The checks of the projects' own content (<see cref="PartChecks" />): flags, mesh, bones, morphs, LODs, UVs and
    /// textures, read through <paramref name="game" /> for what the projects lack (null: no install; a texture the
    /// project lacks is then a warning). They don't depend on the output or format choices, so the export window runs
    /// them once, not on every change. A project that can't be opened is left to <see cref="Run" />, which reports it,
    /// except a CASP that is in the package but can't be parsed, which only this check sees. With several projects each
    /// label starts with the project's name. One "Ok" line per area follows when every project was checked and had no
    /// problem there.
    /// </summary>
    public static IReadOnlyList<ExportCheck> Content(IReadOnlyList<string> projectFolders, IResourceSource? game)
    {
        var problems = new List<ExportCheck>();
        var opened = 0;
        foreach (var folder in projectFolders)
        {
            ProjectSession session;
            try { session = ProjectSession.Open(folder, game ?? NoResources.Instance); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
                                              or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException
                                              or System.Reflection.TargetInvocationException)
            {
                if (UnparsedCasp(folder, error) is { } check) problems.Add(check);
                continue;
            }
            opened++;
            var prefix = projectFolders.Count > 1 ? session.Project.Name + ": " : "";
            problems.AddRange(PartChecks.Run(session, game != null).Select(c => prefix.Length == 0 ? c : c with { Label = prefix + c.Label }));
        }
        if (opened == 0 || opened != projectFolders.Count) return problems;
        var oks = PartAreas.Where(a => problems.All(p => p.Area != a)).Select(a => ExportCheck.Ok(AreaLabel(a)) with { Area = a });
        return [.. oks, .. problems];
    }

    /// <summary>A warning when the project and package read fine and hold the named CASP, yet the session couldn't open it.</summary>
    static ExportCheck? UnparsedCasp(string folder, Exception error)
    {
        try
        {
            var project = Project.Read(folder);
            var package = Path.Combine(folder, project.Package);
            if (TryParseKey(project.Casp) is not { } casp || !File.Exists(package) || !ReadKeys(package).Contains(casp)) return null;
            return new ExportCheck(CheckSeverity.Warning, $"{project.Name}: CAS part can't be read ({error.Message})",
                new CheckTarget(CheckTargetKind.Package, package, casp)) { Area = CheckArea.Flags };
        }
        catch (Exception again) when (again is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
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
