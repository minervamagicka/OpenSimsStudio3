namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>Short UI wording for <see cref="UvIssue" />s: one-line labels for the warnings list, details for tooltips.</summary>
public static class UvWording
{
    public static string Label(UvIssue issue)
    {
        var text = issue.Kind switch
        {
            UvIssueKind.MissingUvs => "No UVs",
            UvIssueKind.OutOfRange => "UVs outside 0–1",
            UvIssueKind.Degenerate => "Degenerate UVs",
            UvIssueKind.Flipped => "Flipped UVs",
            UvIssueKind.Overlap => "UV overlap",
            UvIssueKind.MirroredOverlap => "Mirrored UV overlap",
            _ => issue.Kind.ToString(),
        };
        return issue.Channel == 0 ? text : $"{text} (UV{issue.Channel + 1})";
    }

    public static string Tip(UvIssue issue) => issue.Kind switch
    {
        UvIssueKind.MissingUvs => "This mesh group has no UV set, so its texture can't be placed.",
        UvIssueKind.OutOfRange => $"{issue.Vertices:N0} vertices on {issue.Count:N0} triangles lie outside the 0–1 UV square.",
        UvIssueKind.Degenerate => $"{issue.Count:N0} triangles have almost no area in UV space, which smears the texture.",
        UvIssueKind.Flipped => $"{issue.Count:N0} triangles wind the other way in UV space from the rest of the group.",
        UvIssueKind.Overlap => $"{issue.Count:N0} triangles share UV space with other triangles of this group.",
        UvIssueKind.MirroredOverlap => $"{issue.Count:N0} triangles share UV space with mirrored triangles of this group.",
        _ => "",
    };

    /// <summary>Overlaps are drawn in Danger, the other problems in Warn.</summary>
    public static bool IsDanger(UvIssueKind kind) => kind is UvIssueKind.Overlap or UvIssueKind.MirroredOverlap or UvIssueKind.MissingUvs;
}
