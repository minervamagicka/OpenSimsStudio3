using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.Tests;

public class ExportChecksTests
{
    static readonly ResourceKey Casp = new(ExportChecks.CaspType, 0, 1), Geom = new(0x015A1849, 0, 2), Other = new(0x00B2D882, 0, 3);

    static string MakeProject(TempFolder temp, string name, string casp, params (ResourceKey, byte[])[] resources)
    {
        var folder = Directory.CreateDirectory(temp[name]).FullName;
        new Project { Name = name, Package = name + ".package", Casp = casp }.Write(folder);
        TestPackages.Write(Path.Combine(folder, name + ".package"), resources);
        return folder;
    }

    static ExportCheck Only(ExportPlan plan) => Assert.Single(plan.Problems);

    [Fact]
    public void A_good_project_passes_every_check()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", Casp.ToString(), (Casp, [1]), (Geom, [2]));
        var plan = ExportChecks.Run([one], temp["out/one.package"]);
        Assert.True(plan.CanExport);
        Assert.Empty(plan.Problems);
        Assert.Equal(["Project", "Package", "CAS part", "Output path"], plan.Checks.Select(c => c.Label));
        Assert.Equal(Path.Combine(one, "One.package"), Assert.Single(plan.Sources).Package);
    }

    [Fact]
    public void Two_good_projects_also_report_unique_keys()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", "", (Casp, [1]));
        var two = MakeProject(temp, "Two", "", (new ResourceKey(ExportChecks.CaspType, 0, 9), [1]));
        Assert.Contains("Keys unique", ExportChecks.Run([one, two], temp["merged.package"]).Checks.Select(c => c.Label));
    }

    [Fact]
    public void Unreadable_project_is_an_error_on_the_project()
    {
        using var temp = new TempFolder();
        var empty = Directory.CreateDirectory(temp["Empty"]).FullName;
        var check = Only(ExportChecks.Run([empty], temp["out.package"]));
        Assert.Equal(CheckSeverity.Error, check.Severity);
        Assert.Equal(new CheckTarget(CheckTargetKind.Project, empty), check.Target);
    }

    [Fact]
    public void Missing_package_is_an_error()
    {
        using var temp = new TempFolder();
        var folder = Directory.CreateDirectory(temp["NoPackage"]).FullName;
        new Project { Name = "NoPackage", Package = "NoPackage.package" }.Write(folder);
        var plan = ExportChecks.Run([folder], temp["out.package"]);
        Assert.False(plan.CanExport);
        Assert.Equal(CheckTargetKind.Project, Only(plan).Target.Kind);
        Assert.Empty(plan.Sources);
    }

    [Fact]
    public void Own_package_as_output_is_an_error_on_the_output_path()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", "", (Casp, [1]));
        var plan = ExportChecks.Run([one], Path.Combine(one, "One.package"));
        var check = Only(plan);
        Assert.Equal(CheckSeverity.Error, check.Severity);
        Assert.Equal(CheckTargetKind.OutputPath, check.Target.Kind);
        Assert.DoesNotContain("Output path", plan.Checks.Select(c => c.Label));
    }

    [Fact]
    public void No_output_path_is_an_error()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", "", (Casp, [1]));
        var check = Only(ExportChecks.Run([one], ""));
        Assert.Equal((CheckSeverity.Error, CheckTargetKind.OutputPath), (check.Severity, check.Target.Kind));
    }

    [Fact]
    public void No_project_is_an_error()
    {
        using var temp = new TempFolder();
        Assert.False(ExportChecks.Run([], temp["out.package"]).CanExport);
    }

    [Fact]
    public void Collision_is_an_error_on_the_resource()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", "", (Casp, [1]));
        var two = MakeProject(temp, "Two", "", (Casp, [2]));
        var plan = ExportChecks.Run([one, two], temp["merged.package"]);
        var check = Only(plan);
        Assert.Equal(CheckSeverity.Error, check.Severity);
        Assert.Equal(new CheckTarget(CheckTargetKind.Resource, Path.Combine(two, "Two.package"), Casp), check.Target);
        Assert.Contains("\"One\" and \"Two\"", check.Label);
    }

    [Fact]
    public void Empty_package_is_a_warning_and_does_not_block()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", "");
        var plan = ExportChecks.Run([one], temp["out.package"]);
        var check = Only(plan);
        Assert.Equal((CheckSeverity.Warning, CheckTargetKind.Package), (check.Severity, check.Target.Kind));
        Assert.True(plan.CanExport);
    }

    [Fact]
    public void Package_without_a_casp_is_a_warning()
    {
        using var temp = new TempFolder();
        var one = MakeProject(temp, "One", "", (Geom, [1]));
        var check = Only(ExportChecks.Run([one], temp["out.package"]));
        Assert.Equal(CheckSeverity.Warning, check.Severity);
        Assert.Contains("no CAS part", check.Label);
    }

    [Fact]
    public void Package_without_the_projects_own_casp_is_a_warning_on_that_key()
    {
        using var temp = new TempFolder();
        var named = new ResourceKey(ExportChecks.CaspType, 0, 0x77);
        var one = MakeProject(temp, "One", named.ToString(), (Casp, [1]), (Other, [2]));
        var check = Only(ExportChecks.Run([one], temp["out.package"]));
        Assert.Equal(CheckSeverity.Warning, check.Severity);
        Assert.Equal(named, check.Target.Key);
    }

    [Fact]
    public void Keys_parse_back_from_their_text()
    {
        var key = new ResourceKey(0x034AEECB, 0x80000000, 0x0123456789ABCDEF);
        Assert.Equal(key, ExportChecks.TryParseKey(key.ToString()));
        Assert.Null(ExportChecks.TryParseKey(""));
        Assert.Null(ExportChecks.TryParseKey("nope:0:0"));
    }

    [Fact]
    public void Export_writes_despite_warnings_and_refuses_errors()
    {
        using var temp = new TempFolder();
        var noCasp = MakeProject(temp, "One", "", (Geom, [7]));
        var result = ProjectExport.Export(ExportChecks.Run([noCasp], temp["out.package"]));
        Assert.True(result.Succeeded);
        Assert.Equal(new byte[] { 7 }, TestPackages.ReadAll(temp["out.package"])[Geom]);

        var refused = ProjectExport.Export(ExportChecks.Run([noCasp], ""));
        Assert.False(refused.Succeeded);
    }
}
