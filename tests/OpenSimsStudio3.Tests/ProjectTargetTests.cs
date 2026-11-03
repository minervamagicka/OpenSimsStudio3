using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.Tests;

public class ProjectTargetTests
{
    [Theory]
    [InlineData("Retail", ProjectTarget.Retail)]
    [InlineData("Both", ProjectTarget.Both)]
    [InlineData("OpenTS3Only", ProjectTarget.OpenTS3Only)]
    [InlineData("opents3only", ProjectTarget.OpenTS3Only)]
    [InlineData(" both ", ProjectTarget.Both)]
    public void Known_values_parse(string value, ProjectTarget expected)
    {
        Assert.True(ProjectTargets.TryParse(value, out var target));
        Assert.Equal(expected, target);
        Assert.Equal(expected, ProjectTargets.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OpenTS3 only")]
    [InlineData("Sims4")]
    [InlineData("2")]
    public void Unknown_values_are_refused_and_read_as_retail(string? value)
    {
        Assert.False(ProjectTargets.TryParse(value, out _));
        Assert.Equal(ProjectTarget.Retail, ProjectTargets.Parse(value));
    }

    [Fact]
    public void Ids_round_trip_and_keep_the_format_1_values()
    {
        Assert.Equal(["Retail", "Both", "OpenTS3Only"], ProjectTargets.All.Select(t => t.Id()));
        foreach (var target in ProjectTargets.All) Assert.Equal(target, ProjectTargets.Parse(target.Id()));
    }

    [Fact]
    public void Display_names_are_the_ui_labels() =>
        Assert.Equal(["Retail", "Both", "OpenTS3 only"], ProjectTargets.All.Select(t => t.DisplayName()));

    [Fact]
    public void Strictness_runs_retail_both_opents3only()
    {
        Assert.True(ProjectTarget.Both.IsStricterThan(ProjectTarget.Retail));
        Assert.True(ProjectTarget.OpenTS3Only.IsStricterThan(ProjectTarget.Both));
        Assert.False(ProjectTarget.Retail.IsStricterThan(ProjectTarget.Retail));
        Assert.Equal(ProjectTarget.Both, ProjectTargets.Strictest([ProjectTarget.Retail, ProjectTarget.Both, ProjectTarget.Retail]));
        Assert.Equal(ProjectTarget.OpenTS3Only, ProjectTargets.Strictest([ProjectTarget.OpenTS3Only, ProjectTarget.Retail]));
        Assert.Equal(ProjectTarget.Retail, ProjectTargets.Strictest([]));
    }

    [Fact]
    public void Only_leaving_opents3only_removes_settings()
    {
        string[] settings = ["Setting A", "Setting B"];
        Assert.Equal(settings, ProjectTargets.RemovedSettings(ProjectTarget.OpenTS3Only, ProjectTarget.Retail, settings));
        Assert.Equal(settings, ProjectTargets.RemovedSettings(ProjectTarget.OpenTS3Only, ProjectTarget.Both, settings));
        Assert.Empty(ProjectTargets.RemovedSettings(ProjectTarget.OpenTS3Only, ProjectTarget.OpenTS3Only, settings));
        Assert.Empty(ProjectTargets.RemovedSettings(ProjectTarget.Retail, ProjectTarget.OpenTS3Only, settings));
        Assert.Empty(ProjectTargets.RemovedSettings(ProjectTarget.Both, ProjectTarget.Retail, settings));
        Assert.Empty(ProjectTargets.RemovedSettings(ProjectTarget.OpenTS3Only, ProjectTarget.Retail, []));
    }

    [Fact]
    public void Changing_the_target_round_trips_through_project_json()
    {
        using var temp = new TempFolder();
        var project = new Project { Id = "abc", Name = "Top", Target = "Retail", Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        project.Write(temp.Path);

        var changed = Project.Read(temp.Path).WriteTarget(temp.Path, ProjectTarget.OpenTS3Only);
        var read = Project.Read(temp.Path);
        Assert.Equal(changed, read);
        Assert.Equal(("OpenTS3Only", ProjectTarget.OpenTS3Only), (read.Target, read.ParsedTarget));
        Assert.True(read.Modified > project.Modified);
        Assert.Equal(project with { Target = "OpenTS3Only", Modified = read.Modified }, read);
        Assert.Contains("\"Target\": \"OpenTS3Only\"", File.ReadAllText(temp[Project.FileName]));
        Assert.DoesNotContain("ParsedTarget", File.ReadAllText(temp[Project.FileName]));
    }

    [Fact]
    public void An_unknown_target_in_project_json_reads_as_retail_and_is_kept()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp[Project.FileName], "{\"Name\":\"Future\",\"Target\":\"Sims5\"}");
        var project = Project.Read(temp.Path);
        Assert.Equal(("Sims5", ProjectTarget.Retail), (project.Target, project.ParsedTarget));
    }
}
