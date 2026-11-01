using System.Text.Json;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.Formats.Projects;

/// <summary>
/// An OSS3 project: a folder holding project.json (this record) and the working package with the cloned resources.
/// Format version 1 (provisional, 2026-10-29). The package only ever holds the project's own copies; game resources
/// are referenced by key, never copied unless changed.
/// </summary>
public sealed record Project
{
    public const string FileName = "project.json";
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Retail, Both or OpenTS3Only.</summary>
    public string Target { get; init; } = "Retail";
    /// <summary>CasClone (a new item) or CasDefaultReplacement (overrides the game's part under its own keys).</summary>
    public string Kind { get; init; } = "CasClone";
    public string SourceKey { get; init; } = "";
    public string SourceName { get; init; } = "";
    /// <summary>The custom-content package the part was cloned from; empty for a game part.</summary>
    public string SourcePackage { get; init; } = "";
    public string Casp { get; init; } = "";
    public string Package { get; init; } = "";
    /// <summary>The product (skuversion major) the source part came from; the clone needs it installed.</summary>
    public int SourceProduct { get; init; }
    public DateTime Created { get; init; }
    public DateTime Modified { get; init; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Project Read(string folder)
    {
        var project = JsonSerializer.Deserialize<Project>(File.ReadAllText(Path.Combine(folder, FileName)))
                      ?? throw new InvalidDataException($"{folder}: empty project file");
        if (project.Format > CurrentFormat) throw new InvalidDataException($"{folder}: project format {project.Format} is newer than this OSS3 ({CurrentFormat})");
        return project;
    }

    public void Write(string folder) => File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Json));

    /// <summary>
    /// Creates a project folder under <paramref name="projectsRoot" /> for a clone of one game CAS part and writes its
    /// package. The folder name is the project name made file-safe, numbered when taken. Returns the folder.
    /// </summary>
    public static string CreateCasClone(string projectsRoot, string name, string target, IResourceSource game,
        ResourceKey sourceCasp, string sourceName, int sourceProduct, string sourcePackage = "", bool defaultReplacement = false)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "Project";
        var folder = Path.Combine(projectsRoot, safe);
        for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(projectsRoot, $"{safe} ({n})");
        Directory.CreateDirectory(folder);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var package = safe + ".package";
            var result = CasClone.Clone(game, sourceCasp, id, Path.Combine(folder, package), keepKeys: defaultReplacement);
            var now = DateTime.UtcNow;
            new Project
            {
                Id = id, Name = name, Kind = defaultReplacement ? "CasDefaultReplacement" : "CasClone", Target = target, SourceKey = sourceCasp.ToString(), SourceName = sourceName, SourcePackage = sourcePackage,
                Casp = result.NewCasp.ToString(), Package = package, SourceProduct = sourceProduct, Created = now, Modified = now,
            }.Write(folder);
            return folder;
        }
        catch
        {
            // Leave nothing half-made: the folder was created by this call and holds only this call's files.
            Directory.Delete(folder, true);
            throw;
        }
    }
}
