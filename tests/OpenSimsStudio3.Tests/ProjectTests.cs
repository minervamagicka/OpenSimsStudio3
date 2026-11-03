using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.Tests;

public class ProjectTests
{
    [Fact]
    public void Write_then_read_keeps_every_field()
    {
        using var temp = new TempFolder();
        var project = new Project
        {
            Id = "abc", Name = "Test top", Target = "Both", Kind = "CasDefaultReplacement", SourceKey = "034AEECB:00000000:0000000000000001",
            SourceName = "src", SourcePackage = "cc.package", Casp = "034AEECB:00000000:0000000000000002", Package = "Test top.package",
            SourceProduct = 3, Created = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), Modified = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        project.Write(temp.Path);
        Assert.Equal(project, Project.Read(temp.Path));
    }

    [Fact]
    public void Newer_format_is_refused()
    {
        using var temp = new TempFolder();
        new Project { Format = Project.CurrentFormat + 1 }.Write(temp.Path);
        Assert.Throws<InvalidDataException>(() => Project.Read(temp.Path));
    }

    [Fact]
    public void Missing_fields_take_defaults()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp[Project.FileName], "{\"Name\":\"Old\"}");
        var project = Project.Read(temp.Path);
        Assert.Equal(("Old", "Retail", "CasClone", Project.CurrentFormat), (project.Name, project.Target, project.Kind, project.Format));
    }
}
