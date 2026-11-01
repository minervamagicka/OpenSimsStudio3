using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace OpenSimsStudio3.App;

/// <summary>
/// The start screen's Settings page: the default Blender for Edit in Blender, the game folder, and where new projects
/// go. Everything saves to settings.json through <see cref="AppSettings.Current" /> as it changes.
/// </summary>
public sealed class SettingsView : ScrollViewer
{
    readonly Func<Task> _chooseGameFolder;
    readonly TextBlock _projects = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _game = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };

    /// <param name="chooseGameFolder">The start window's game-folder picker (it re-runs discovery afterwards).</param>
    public SettingsView(Func<Task> chooseGameFolder, string? gameRoot, Action back)
    {
        _chooseGameFolder = chooseGameFolder;
        var settings = AppSettings.Current;
        _game.Text = gameRoot ?? "Not found";
        _projects.Text = settings.EffectiveProjectsRoot;

        var changeGame = new Button { Content = "Change…", MinHeight = 38, Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        changeGame.Click += async (_, _) => await _chooseGameFolder();
        var changeProjects = new Button { Content = "Change…", MinHeight = 38, Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        changeProjects.Click += async (_, _) => await ChooseProjectsFolderAsync();

        var backButton = new Button { Content = "← Back", MinHeight = 40, Padding = new Thickness(14, 8), HorizontalAlignment = HorizontalAlignment.Left };
        backButton.Click += (_, _) => back();
        Content = new StackPanel
        {
            Margin = new Thickness(34, 0, 42, 32), Spacing = 22, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                backButton,
                Section("BLENDER", "The Blender that Edit in Blender opens. GEOM Tools must be installed in it.", new BlenderPicker { Width = 560, HorizontalAlignment = HorizontalAlignment.Left }),
                Section("GAME FOLDER", "Where The Sims 3 is installed. OSS3 only reads from it.", _game, changeGame),
                Section("PROJECTS FOLDER", "Where new projects are created.", _projects, changeProjects),
            },
        };
    }

    async Task ChooseProjectsFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose where new projects go", AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        AppSettings.Current.ProjectsRoot = path;
        AppSettings.Current.Save();
        _projects.Text = AppSettings.Current.EffectiveProjectsRoot;
    }

    static StackPanel Section(string title, string description, params Control[] content)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, Classes = { "eyebrow" } });
        panel.Children.Add(new TextBlock { Text = description, FontSize = 13, Foreground = (IBrush)Application.Current!.FindResource("Muted")!, TextWrapping = TextWrapping.Wrap });
        foreach (var c in content) panel.Children.Add(c);
        return panel;
    }
}
