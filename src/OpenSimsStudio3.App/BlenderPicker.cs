using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.App;

/// <summary>
/// Chooses the Blender that Edit in Blender opens: "Automatic" (the Blender Launcher favourite with GEOM Tools, else the
/// newest with GEOM Tools), any Blender found, or a blender.exe picked with Browse…. Saved as settings BlenderPath.
/// </summary>
public sealed class BlenderPicker : StackPanel
{
    readonly ComboBox _box = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 36, FontSize = 13 };
    readonly List<string> _paths = [];
    bool _filling;

    public BlenderPicker()
    {
        Spacing = 6;
        var browse = new Button { Content = "Browse…", Padding = new Thickness(12, 4), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += async (_, _) => await BrowseAsync();
        Children.Add(new DockPanel { Children = { browse, _box }, LastChildFill = true });
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(6, 0, 0, 0);
        _box.SelectionChanged += (_, _) =>
        {
            if (_filling || _box.SelectedIndex < 0) return;
            AppSettings.Current.BlenderPath = _paths[_box.SelectedIndex];
            AppSettings.Current.Save();
        };
        Fill();
    }

    void Fill()
    {
        _filling = true;
        _paths.Clear();
        var labels = new List<string>();
        var automatic = ToolInstalls.PreferredBlender(null);
        _paths.Add("");
        labels.Add(automatic == null ? "Automatic (no Blender with GEOM Tools found)" : $"Automatic: Blender {automatic.Version}");
        foreach (var b in ToolInstalls.FindBlender())
        {
            _paths.Add(b.Executable);
            labels.Add($"Blender {b.Version}{(b.HasGeomTools ? " · GEOM Tools" : " · no GEOM Tools")}{(b.LauncherFavorite ? " · Launcher favourite" : "")}");
        }
        var chosen = AppSettings.Current.BlenderPath;
        if (chosen.Length > 0 && !_paths.Contains(chosen, StringComparer.OrdinalIgnoreCase))
        {
            _paths.Add(chosen);
            labels.Add($"{chosen}");
        }
        _box.ItemsSource = labels;
        _box.SelectedIndex = Math.Max(0, _paths.FindIndex(p => string.Equals(p, chosen, StringComparison.OrdinalIgnoreCase)));
        ToolTip.SetTip(_box, "Edit in Blender opens this Blender. GEOM Tools must be installed in it.");
        _filling = false;
    }

    async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose blender.exe", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Blender") { Patterns = ["blender.exe"] }],
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        AppSettings.Current.BlenderPath = path;
        AppSettings.Current.Save();
        Fill();
    }
}
