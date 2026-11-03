using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.Tests;

public class ProjectExportTests
{
    static readonly ResourceKey A = new(0x034AEECB, 0, 1), B = new(0x00B2D882, 0, 2), C = new(0x015A1849, 0, 3);

    static string MakeProject(TempFolder temp, string name, params (ResourceKey, byte[])[] resources)
    {
        var folder = Directory.CreateDirectory(temp[name]).FullName;
        new Project { Name = name, Package = name + ".package" }.Write(folder);
        TestPackages.Write(Path.Combine(folder, name + ".package"), resources);
        return folder;
    }

    [Fact]
    public void Merges_every_resource_of_every_project()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", (A, [1, 2, 3]), (B, [4]));
        var two = MakeProject(temp, "Two", (C, [5, 6]));
        var result = ProjectExport.Export([one, two], temp["out/merged.package"]);
        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Resources);
        Assert.Equal(["One", "Two"], result.Projects);
        var written = TestPackages.ReadAll(temp["out/merged.package"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, written[A]);
        Assert.Equal(new byte[] { 4 }, written[B]);
        Assert.Equal(new byte[] { 5, 6 }, written[C]);
    }

    [Fact]
    public void Same_key_in_two_projects_writes_nothing()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", (A, [1]));
        var two = MakeProject(temp, "Two", (A, [2]));
        var result = ProjectExport.Export([one, two], temp["merged.package"]);
        Assert.False(result.Succeeded);
        Assert.Contains("\"One\"", Assert.Single(result.Problems));
        Assert.False(File.Exists(temp["merged.package"]));
    }

    [Fact]
    public void Exporting_over_a_projects_own_package_is_refused()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", (A, [1]));
        var own = Path.Combine(one, "One.package");
        var before = File.ReadAllBytes(own);
        var result = ProjectExport.Export([one], own);
        Assert.False(result.Succeeded);
        Assert.Equal(before, File.ReadAllBytes(own));
    }

    [Fact]
    public void Unreadable_project_is_reported()
    {
        using var temp = new TempFolder();
        var result = ProjectExport.Export([Directory.CreateDirectory(temp["Empty"]).FullName], temp["merged.package"]);
        Assert.False(result.Succeeded);
        Assert.Single(result.Problems);
    }
}
