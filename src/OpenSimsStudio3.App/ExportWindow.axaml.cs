using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Sims3Pack;

namespace OpenSimsStudio3.App;

/// <summary>
/// The export window (DESIGN.md, Export). Exports the given projects (one, from the editor, today; the core merges
/// several) to &lt;folder&gt;\&lt;name&gt;.package and/or .sims3pack, optionally copies the package to a test profile and
/// clears its CAS caches after asking. The checks re-run whenever a choice changes; an error disables Export. The checks
/// of the projects' content (ExportChecks.Content: flags, mesh, bones, morphs, LODs, UVs, textures) don't depend on the
/// choices and read every mesh, so they run once, on a worker thread, when the window opens; Export waits for them.
/// A Show link on a content check closes the window and goes to that place in the editor.
/// </summary>
public partial class ExportWindow : Window
{
    readonly IReadOnlyList<string> _projects;
    readonly Action<CheckTarget>? _showInEditor;
    IReadOnlyList<ExportCheck>? _content;
    readonly string _name = "";
    readonly string _displayName = "";
    string _lastFolder = "";
    bool _busy;

    public ExportWindow() : this([]) { }

    /// <param name="game">Where resources the projects lack are looked up (textures a clone takes from the game); null when unknown.</param>
    /// <param name="showInEditor">Goes to a check's place in the editor (<see cref="CheckTarget.IsEditor" />); null when no editor is open.</param>
    public ExportWindow(IReadOnlyList<string> projectFolders, IResourceSource? game = null, Action<CheckTarget>? showInEditor = null)
    {
        InitializeComponent();
        _projects = projectFolders;
        _showInEditor = showInEditor;
        if (projectFolders.Count == 0) return;

        var first = TryRead(projectFolders[0]);
        _displayName = projectFolders.Count == 1 ? first?.Name ?? Path.GetFileName(projectFolders[0]) : $"{projectFolders.Count} projects";
        _name = projectFolders.Count == 1 && first != null ? Path.GetFileNameWithoutExtension(first.Package) : "Merged";
        Title = Heading.Text = $"Export · {_displayName}";
        TargetText.Text = first == null ? "" : "Target: " + (first.Target == "OpenTS3Only" ? "OpenTS3 only" : first.Target);

        var settings = AppSettings.Current;
        PackageBox.IsChecked = settings.ExportPackage;
        Sims3PackBox.IsChecked = settings.ExportSims3Pack;
        CopyBox.IsChecked = settings.CopyToTestProfile;
        ClearBox.IsChecked = settings.ClearCasCaches;
        OutputBox.Text = settings.EffectiveExportFolder;
        ProfileBox.Text = settings.EffectiveTestProfileFolder;

        foreach (var box in new[] { PackageBox, Sims3PackBox, CopyBox, ClearBox }) box.IsCheckedChanged += (_, _) => Refresh();
        OutputBox.TextChanged += (_, _) => Refresh();
        ProfileBox.TextChanged += (_, _) => Refresh();
        BrowseOutput.Click += async (_, _) => await PickFolder(OutputBox, "Save to folder");
        BrowseProfile.Click += async (_, _) => await PickFolder(ProfileBox, "Test profile");
        CancelButton.Click += (_, _) => Close();
        ExportButton.Click += async (_, _) => await ExportAsync();
        ShowFolderButton.Click += (_, _) => Reveal(_lastFolder);
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && !_busy) Close(); };
        Refresh();
        _ = CheckContentAsync(game);
    }

    async Task CheckContentAsync(IResourceSource? game)
    {
        try { _content = await Task.Run(() => ExportChecks.Content(_projects, game)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine($"OSS3: content checks failed: {error}");
            _content = [new ExportCheck(CheckSeverity.Warning, "Content checks failed: " + error.Message, CheckTarget.None)];
        }
        Refresh();
    }

    static Project? TryRead(string folder)
    {
        try { return Project.Read(folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { return null; }
    }

    bool WantPackage => PackageBox.IsChecked == true;
    bool WantSims3Pack => Sims3PackBox.IsChecked == true;
    bool WantCopy => CopyBox.IsChecked == true;
    bool WantClear => WantCopy && ClearBox.IsChecked == true;
    string Folder => (OutputBox.Text ?? "").Trim();
    string Profile => (ProfileBox.Text ?? "").Trim();
    string PackagePath => Folder.Length == 0 ? "" : Path.Combine(Folder, _name + ".package");
    string Sims3PackPath => Path.Combine(Folder, _name + ".sims3pack");

    /// <summary>Every check for the current choices: the projects and output, the formats, and the test profile.</summary>
    (ExportPlan Plan, TestProfilePlan? Profile, List<ExportCheck> Checks) RunChecks()
    {
        var plan = ExportChecks.Run(_projects, PackagePath);
        var checks = plan.Checks.ToList();
        checks.AddRange(_content ?? []);
        if (!WantPackage && !WantSims3Pack) checks.Add(new ExportCheck(CheckSeverity.Error, "No format chosen", CheckTarget.None));
        TestProfilePlan? profile = null;
        if (WantCopy)
        {
            profile = TestProfile.Plan(Profile, PackagePath, WantClear);
            checks.AddRange(profile.Checks);
        }
        return (plan, profile, checks);
    }

    void Refresh()
    {
        ClearBox.IsEnabled = ProfileBox.IsEnabled = BrowseProfile.IsEnabled = WantCopy;
        var (_, _, checks) = RunChecks();
        CheckList.Children.Clear();
        // Problems first, then the passed checks, as one-line labels.
        foreach (var check in checks.OrderByDescending(c => c.Severity)) CheckList.Children.Add(Row(check));
        if (_content == null) CheckList.Children.Add(new TextBlock { Text = "Checking the project…", FontSize = 13, Foreground = Brush("Muted"), Margin = new Thickness(20, 10, 0, 0) });
        var errors = checks.Count(c => c.Severity == CheckSeverity.Error);
        var warnings = checks.Count(c => c.Severity == CheckSeverity.Warning);
        Summary.Text = errors > 0 ? Count(errors, "error") + (warnings > 0 ? ", " + Count(warnings, "warning") : "")
            : warnings > 0 ? Count(warnings, "warning") : "All checks passed";
        Summary.Foreground = Brush(errors > 0 ? "Danger" : warnings > 0 ? "Warn" : "Success");
        ExportButton.IsEnabled = errors == 0 && !_busy && _content != null;
    }

    static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    Control Row(ExportCheck check)
    {
        var (mark, colour) = check.Severity switch
        {
            CheckSeverity.Ok => ("✓", "Success"),
            CheckSeverity.Warning => ("!", "Warn"),
            _ => ("✕", "Danger"),
        };
        var row = new DockPanel { MinHeight = 40 };
        row.Children.Add(new TextBlock
        {
            Text = mark, Width = 20, FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"), Foreground = Brush(colour),
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left,
        });
        if (check.IsProblem && check.Target.Kind != CheckTargetKind.None && (!check.Target.IsEditor || _showInEditor != null || check.Target.Path.Length > 0))
        {
            var show = new Button { Content = "Show", Padding = new Thickness(14, 4), Margin = new Thickness(8, 4, 0, 4), [DockPanel.DockProperty] = Dock.Right };
            show.Click += (_, _) => Show(check.Target);
            row.Children.Add(show);
        }
        var label = new TextBlock { Text = check.Label, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (check.Tip != null) ToolTip.SetTip(label, check.Tip);
        row.Children.Add(label);
        return new Border { BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 4), Child = row };
    }

    /// <summary>
    /// The Show link: focuses the output or profile box, or shows the project, package or resource's package in the file
    /// manager. An editor place closes this window and goes there in the editor; without an editor it shows the project folder.
    /// </summary>
    void Show(CheckTarget target)
    {
        if (target.IsEditor)
        {
            if (_showInEditor == null) { Reveal(target.Path); return; }
            Close();
            _showInEditor(target);
            return;
        }
        switch (target.Kind)
        {
            case CheckTargetKind.OutputPath: OutputBox.Focus(); OutputBox.SelectAll(); break;
            case CheckTargetKind.TestProfile: ProfileBox.Focus(); ProfileBox.SelectAll(); break;
            case CheckTargetKind.Project or CheckTargetKind.Package or CheckTargetKind.Resource: Reveal(target.Path); break;
        }
    }

    /// <summary>Opens the file manager on a folder, or on a file's folder with the file selected (Windows).</summary>
    static void Reveal(string path)
    {
        if (path.Length == 0) return;
        try
        {
            if (File.Exists(path) && OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false });
            else if (Directory.Exists(path) || Directory.Exists(Path.GetDirectoryName(path)))
                Process.Start(new ProcessStartInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"OSS3: could not show {path}: {error.Message}");
        }
    }

    async Task PickFolder(TextBox box, string title)
    {
        var start = Directory.Exists(box.Text) ? await StorageProvider.TryGetFolderFromPathAsync(box.Text!) : null;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = start });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) box.Text = path;
    }

    async Task ExportAsync()
    {
        var (plan, profile, checks) = RunChecks();
        if (checks.Any(c => c.Severity == CheckSeverity.Error)) { Refresh(); return; }
        if (profile is { CachesToDelete.Count: > 0 })
        {
            var names = string.Join("\n", profile.CachesToDelete.Select(Path.GetFileName));
            if (await ConfirmDialog.Ask(this, "Clear CAS caches?", $"{profile.UserFolder}\n\n{names}", "Delete", "Cancel") != 0) return;
        }
        SaveChoices();

        _busy = true;
        ExportButton.IsEnabled = false;
        ErrorText.IsVisible = DonePanel.IsVisible = false;
        var (package, sims3Pack, copy, folder) = (WantPackage, WantSims3Pack, WantCopy, Folder);
        var sims3PackPath = Sims3PackPath;
        try
        {
            var (written, result) = await Task.Run(() =>
            {
                // A .sims3pack-only export still builds the package first, under its real name in a temp folder.
                var packagePath = plan.OutPath;
                var temp = package ? null : Directory.CreateTempSubdirectory("oss3-export-").FullName;
                var source = temp == null ? packagePath : Path.Combine(temp, _name + ".package");
                var exported = ProjectExport.Export(temp == null ? plan : ExportChecks.Run(_projects, source));
                if (!exported.Succeeded) { if (temp != null) Directory.Delete(temp, true); return (new List<string>(), exported); }
                try
                {
                    var files = new List<string>();
                    if (package) files.Add(packagePath);
                    if (sims3Pack)
                    {
                        Sims3PackFormat.WriteFromPackage(source, sims3PackPath, _displayName);
                        files.Add(sims3PackPath);
                    }
                    if (copy && profile != null)
                    {
                        var copied = TestProfile.Apply(profile, source);
                        files.Add(copied.Copied);
                        files.AddRange(copied.Deleted.Select(d => "✕ " + Path.GetFileName(d)));
                    }
                    return (files, exported);
                }
                finally { if (temp != null) Directory.Delete(temp, true); }
            });
            if (!result.Succeeded)
            {
                ErrorText.Text = string.Join("\n", result.Problems);
                ErrorText.IsVisible = true;
            }
            else
            {
                _lastFolder = folder;
                DoneTitle.Text = $"✓ Exported {Count(result.Resources, "resource")}";
                DoneFiles.Text = string.Join("\n", written.Select(f => f.StartsWith('✕') ? f : Path.GetFileName(f)));
                DonePanel.IsVisible = true;
                ExportButton.Content = "Export again";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            ErrorText.Text = "Export failed: " + error.Message;
            ErrorText.IsVisible = true;
            Console.Error.WriteLine($"OSS3: export failed: {error}");
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    void SaveChoices()
    {
        var settings = AppSettings.Current;
        settings.ExportPackage = WantPackage;
        settings.ExportSims3Pack = WantSims3Pack;
        settings.CopyToTestProfile = WantCopy;
        settings.ClearCasCaches = ClearBox.IsChecked == true;
        settings.ExportFolder = Folder;
        if (WantCopy) settings.TestProfileFolder = Profile;
        try { settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"OSS3: settings could not be saved ({error.Message}).");
        }
    }
}
