using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.Tests;

public class TestProfileTests
{
    // A fake user folder: the two CAS caches, the three other caches, a save, options and a Mods folder.
    static readonly string[] OtherFiles =
    [
        "compositorCache.package", "scriptCache.package", "socialCache.package", "Options.ini",
        "Saves/Test.sims3/Meta.data", "Mods/Resource.cfg", "Mods/Packages/someone_else.package",
        "Thumbnails/CASThumbnails.package", "WorldCaches/Sunset.package",
    ];

    static string MakeUserFolder(TempFolder temp, bool resourceCfg = true)
    {
        var root = Directory.CreateDirectory(temp["The Sims 3"]).FullName;
        foreach (var file in TestProfile.CasCacheFiles.Concat(OtherFiles))
        {
            if (!resourceCfg && file == "Mods/Resource.cfg") continue;
            var path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        return root;
    }

    static string MakePackage(TempFolder temp)
    {
        var path = temp["export/Long skirt.package"];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TestPackages.Write(path, (new ResourceKey(ExportChecks.CaspType, 0, 1), [1, 2]));
        return path;
    }

    static List<string> AllFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order().ToList();

    [Fact]
    public void Cache_list_is_the_two_cas_caches()
    {
        Assert.Equal(["CASPartCache.package", "simCompositorCache.package"], TestProfile.CasCacheFiles);
    }

    [Fact]
    public void Copies_into_mods_packages_and_deletes_only_the_cas_caches()
    {
        using var temp = new TempFolder();
        var root = MakeUserFolder(temp);
        var package = MakePackage(temp);
        var outside = temp["CASPartCache.package"];
        File.WriteAllText(outside, "not in the user folder");

        var plan = TestProfile.Plan(root, package, clearCaches: true);
        Assert.True(plan.CanCopy);
        Assert.Equal("Test profile", Assert.Single(plan.Checks).Label);
        Assert.Equal(Path.Combine(root, "Mods", "Packages", "Long skirt.package"), plan.Destination);
        Assert.Equal(TestProfile.CasCacheFiles.Select(f => Path.Combine(root, f)), plan.CachesToDelete);

        var result = TestProfile.Apply(plan, package);
        Assert.Equal(plan.CachesToDelete, result.Deleted);
        Assert.Equal(File.ReadAllBytes(package), File.ReadAllBytes(plan.Destination));
        Assert.Equal(OtherFiles.Append("Mods/Packages/Long skirt.package").Order().ToList(), AllFiles(root));
        foreach (var file in OtherFiles) Assert.Equal(file, File.ReadAllText(Path.Combine(root, file)));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(package));
    }

    [Fact]
    public void Without_clearing_nothing_is_deleted()
    {
        using var temp = new TempFolder();
        var root = MakeUserFolder(temp);
        var before = AllFiles(root);
        var plan = TestProfile.Plan(root, MakePackage(temp), clearCaches: false);
        Assert.Empty(plan.CachesToDelete);
        Assert.Empty(TestProfile.Apply(plan, MakePackage(temp)).Deleted);
        Assert.Equal(before.Append("Mods/Packages/Long skirt.package").Order().ToList(), AllFiles(root));
    }

    [Fact]
    public void Missing_caches_are_skipped()
    {
        using var temp = new TempFolder();
        var root = MakeUserFolder(temp);
        File.Delete(Path.Combine(root, "simCompositorCache.package"));
        var plan = TestProfile.Plan(root, MakePackage(temp), clearCaches: true);
        Assert.Equal([Path.Combine(root, "CASPartCache.package")], plan.CachesToDelete);
    }

    [Fact]
    public void A_cache_name_that_is_a_folder_is_not_deleted()
    {
        using var temp = new TempFolder();
        var root = MakeUserFolder(temp);
        File.Delete(Path.Combine(root, "CASPartCache.package"));
        Directory.CreateDirectory(Path.Combine(root, "CASPartCache.package"));
        File.WriteAllText(Path.Combine(root, "CASPartCache.package", "keep.txt"), "keep");
        var plan = TestProfile.Plan(root, MakePackage(temp), clearCaches: true);
        TestProfile.Apply(plan, MakePackage(temp));
        Assert.True(File.Exists(Path.Combine(root, "CASPartCache.package", "keep.txt")));
    }

    [Fact]
    public void Apply_ignores_paths_added_to_the_plan_from_elsewhere()
    {
        using var temp = new TempFolder();
        var root = MakeUserFolder(temp);
        var package = MakePackage(temp);
        var outside = temp["CASPartCache.package"];
        File.WriteAllText(outside, "x");
        var plan = TestProfile.Plan(root, package, clearCaches: true);
        var tampered = plan with
        {
            CachesToDelete = [.. plan.CachesToDelete, outside, Path.Combine(root, "Options.ini"), Path.Combine(root, "Mods", "CASPartCache.package")],
        };
        File.WriteAllText(Path.Combine(root, "Mods", "CASPartCache.package"), "x");
        var result = TestProfile.Apply(tampered, package);
        Assert.Equal(plan.CachesToDelete, result.Deleted);
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(Path.Combine(root, "Options.ini")));
        Assert.True(File.Exists(Path.Combine(root, "Mods", "CASPartCache.package")));
    }

    [Fact]
    public void Missing_folder_is_an_error_and_apply_refuses()
    {
        using var temp = new TempFolder();
        var plan = TestProfile.Plan(temp["nowhere"], MakePackage(temp), clearCaches: true);
        var check = Assert.Single(plan.Checks);
        Assert.Equal((CheckSeverity.Error, CheckTargetKind.TestProfile), (check.Severity, check.Target.Kind));
        Assert.Throws<InvalidOperationException>(() => TestProfile.Apply(plan, MakePackage(temp)));
    }

    [Fact]
    public void A_folder_that_is_not_a_user_folder_is_an_error_and_untouched()
    {
        using var temp = new TempFolder();
        var root = Directory.CreateDirectory(temp["Random"]).FullName;
        File.WriteAllText(Path.Combine(root, "CASPartCache.txt"), "x");
        var plan = TestProfile.Plan(root, MakePackage(temp), clearCaches: true);
        Assert.False(plan.CanCopy);
        Assert.Contains(plan.Checks, c => c.Label == "Not a Sims 3 user folder");
        Assert.Throws<InvalidOperationException>(() => TestProfile.Apply(plan, MakePackage(temp)));
        Assert.Equal(["CASPartCache.txt"], AllFiles(root));
    }

    [Fact]
    public void A_drive_root_is_refused()
    {
        using var temp = new TempFolder();
        var plan = TestProfile.Plan(Path.GetPathRoot(temp.Path)!, MakePackage(temp), clearCaches: true);
        Assert.False(plan.CanCopy);
        Assert.Empty(plan.CachesToDelete);
    }

    [Fact]
    public void Missing_resource_cfg_is_a_warning()
    {
        using var temp = new TempFolder();
        var root = MakeUserFolder(temp, resourceCfg: false);
        var plan = TestProfile.Plan(root, MakePackage(temp), clearCaches: true);
        Assert.True(plan.CanCopy);
        Assert.Equal(CheckSeverity.Warning, Assert.Single(plan.Checks).Severity);
    }
}
