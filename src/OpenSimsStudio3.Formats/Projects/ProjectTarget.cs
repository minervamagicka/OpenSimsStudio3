namespace OpenSimsStudio3.Formats.Projects;

/// <summary>
/// Which game a project is made for (docs/ui/DESIGN.md, "Project target"). The order is the strictness order: a later
/// target allows more (OpenTS3-only data), so a merge of several projects takes the latest of theirs.
/// </summary>
public enum ProjectTarget { Retail, Both, OpenTS3Only }

/// <summary>Parsing, display names and strictness for <see cref="ProjectTarget" />.</summary>
public static class ProjectTargets
{
    /// <summary>Every target, in strictness order (the order the pickers show them).</summary>
    public static IReadOnlyList<ProjectTarget> All { get; } = [ProjectTarget.Retail, ProjectTarget.Both, ProjectTarget.OpenTS3Only];

    /// <summary>The target's value in project.json ("Retail", "Both", "OpenTS3Only"; unchanged since format 1).</summary>
    public static string Id(this ProjectTarget target) => target switch
    {
        ProjectTarget.Retail => "Retail",
        ProjectTarget.Both => "Both",
        ProjectTarget.OpenTS3Only => "OpenTS3Only",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>The label the UI shows.</summary>
    public static string DisplayName(this ProjectTarget target) => target switch
    {
        ProjectTarget.Retail => "Retail",
        ProjectTarget.Both => "Both",
        ProjectTarget.OpenTS3Only => "OpenTS3 only",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>Reads a project.json value, ignoring case. Numbers and unknown names are refused.</summary>
    public static bool TryParse(string? value, out ProjectTarget target)
    {
        foreach (var t in All)
            if (string.Equals(value?.Trim(), t.Id(), StringComparison.OrdinalIgnoreCase))
            {
                target = t;
                return true;
            }
        target = ProjectTarget.Retail;
        return false;
    }

    /// <summary>Reads a project.json value; a missing or unknown value is Retail, project.json's default.</summary>
    public static ProjectTarget Parse(string? value) => TryParse(value, out var target) ? target : ProjectTarget.Retail;

    /// <summary>True when <paramref name="target" /> allows more than <paramref name="other" />.</summary>
    public static bool IsStricterThan(this ProjectTarget target, ProjectTarget other) => target > other;

    /// <summary>The strictest of several targets (a merged export's target); Retail when there are none.</summary>
    public static ProjectTarget Strictest(IEnumerable<ProjectTarget> targets) => targets.DefaultIfEmpty(ProjectTarget.Retail).Max();

    /// <summary>
    /// The settings a change from <paramref name="from" /> to <paramref name="to" /> removes: the project's
    /// OpenTS3-only settings when it leaves OpenTS3 only, otherwise none.
    /// </summary>
    public static IReadOnlyList<string> RemovedSettings(ProjectTarget from, ProjectTarget to, IReadOnlyList<string> openTs3OnlySettings) =>
        from == ProjectTarget.OpenTS3Only && to != ProjectTarget.OpenTS3Only ? openTs3OnlySettings : [];
}
