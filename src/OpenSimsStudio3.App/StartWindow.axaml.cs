using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.App;

/// <summary>
/// The start screen: recent projects in the rail, the three ways to start, and what OSS3 found on this PC.
/// Discovery and the loading-screen read run on a worker thread so the window shows at once. EA images (loading
/// screens, pack icons) are decoded from the player's install into memory and never written anywhere.
/// </summary>
public partial class StartWindow : Window
{
    readonly AppSettings _settings;
    Discovery? _found;

    public StartWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Current;
        var logo = new Bitmap(AssetLoader.Open(new Uri("avares://OpenSimsStudio3/Assets/oss3_icon.png")));
        Icon = new WindowIcon(logo);
        Logo.Source = logo;
        HeroFallback.Source = logo;
        ShowRecents();
        _ = RefreshAsync();
    }

    Task<ResourceIndex>? _indexTask;

    /// <summary>Recent projects in the rail: name and source part, newest first; folders that are gone are skipped.</summary>
    void ShowRecents()
    {
        Recents.Children.Clear();
        foreach (var folder in _settings.RecentProjects)
        {
            Formats.Projects.Project project;
            try { project = Formats.Projects.Project.Read(folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { continue; }
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = project.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            row.Children.Add(new TextBlock { Text = $"{project.SourceName} · {project.ParsedTarget.DisplayName()}", FontSize = 12, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Padding = new Thickness(10, 6), MinHeight = 0 };
            ToolTip.SetTip(button, folder);
            var open = folder;
            button.Click += async (_, _) => await OpenProjectAsync(open);
            Recents.Children.Add(button);
        }
        NoRecents.IsVisible = Recents.Children.Count == 0;
    }

    async Task RefreshAsync()
    {
        var found = await Task.Run(() => Discover(_settings.GameRoot));
        Dispatcher.UIThread.Post(() => Show(found));
    }

    sealed record Discovery(GameInstall? Game, Bitmap? Hero, IReadOnlyList<(GameProduct Product, Bitmap? Icon)> Products,
        BlenderInstall? Blender, string? OpenTS3);

    static Discovery Discover(string chosenRoot)
    {
        var game = GameInstall.Find(chosenRoot);
        Bitmap? hero = null;
        var products = new List<(GameProduct, Bitmap?)>();
        if (game != null)
        {
            if (LoadingScreens.Open(game).RandomBackground(Random.Shared) is { } png) hero = Decode(png);
            foreach (var product in new[] { game.BaseGame }.Concat(game.Packs))
                products.Add((product, product.IconPath is { } path ? Decode(File.ReadAllBytes(path)) : null));
        }
        var blender = ToolInstalls.PreferredBlender(AppSettings.Current.BlenderPath) ?? ToolInstalls.FindBlender().FirstOrDefault();
        var openTs3 = ToolInstalls.FindOpenTS3();
        Console.Error.WriteLine($"OSS3: game {game?.Root ?? "not found"} ({game?.Packs.Count ?? 0} packs), loading screen {(hero != null ? "yes" : "no")}, " +
                                $"Blender {blender?.Version ?? "not found"}, OpenTS3 {openTs3 ?? "not found"}");
        return new Discovery(game, hero, products, blender, openTs3);
    }

    /// <summary>Decodes a PNG or ICO from memory; null when the codec cannot read it (the chip then shows text only).</summary>
    static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"OSS3: an image from the game install could not be decoded ({error.Message}).");
            return null;
        }
    }

    void Show(Discovery found)
    {
        _found = found;
        // Test hook: OSS3_TEST_START=browse opens "Clone a game item" at once (screenshot checks without clicking).
        if (Environment.GetEnvironmentVariable("OSS3_TEST_START") == "browse" && BrowseHost.Content == null)
            Dispatcher.UIThread.Post(() => CloneGameItem(null, new RoutedEventArgs()));
        var heroImage = HeroImage;
        heroImage.Source = found.Hero;
        HeroBackdrop.Source = found.Hero;
        HeroFallback.IsVisible = found.Hero == null;

        var strip = Found;
        while (strip.Children.Count > 1) strip.Children.RemoveAt(1);

        if (found.Game is { } game)
        {
            var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(4, 0, 0, 0) };
            foreach (var (pack, icon) in found.Products.Skip(1))
                icons.Children.Add(icon != null
                    ? new Image { Source = icon, Width = 18, Height = 18, [ToolTip.TipProperty] = pack.Name }
                    : new TextBlock { Text = pack.Code, FontSize = 10, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, [ToolTip.TipProperty] = pack.Name });
            strip.Children.Add(Chip(found.Products[0].Icon, $"The Sims 3 {game.Version.Split('.').Take(2).Aggregate((a, b) => a + "." + b)}", icons,
                $"{game.Root}\n{game.Packs.Count} packs installed"));
        }
        else
            strip.Children.Add(Chip(null, "The Sims 3 not found", null, "Choose the game folder with Change…"));

        if (found.OpenTS3 != null) strip.Children.Add(Chip(null, "OpenTS3", null, found.OpenTS3));
        strip.Children.Add(found.Blender is { } blender
            ? Chip(null, $"Blender {blender.Version}{(blender.HasGeomTools ? " · GEOM Tools" : " · no GEOM Tools")}", null, blender.Executable)
            : Chip(null, "Blender not found", null, null));

        var change = new Button { Content = "Change…", MinHeight = 30, Padding = new Thickness(10, 4), FontSize = 12, BorderThickness = new Thickness(0), Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Left };
        change.Click += async (_, _) => await ChooseGameFolderAsync();
        strip.Children.Add(change);
    }

    static IBrush Muted => (IBrush)Application.Current!.FindResource("Muted")!;

    static Border Chip(Bitmap? icon, string text, Control? trailing, string? tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (icon != null) row.Children.Add(new Image { Source = icon, Width = 22, Height = 22 });
        else row.Children.Add(new Border { Width = 4 });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap });
        if (trailing != null) row.Children.Add(trailing);
        var chip = new Border { Classes = { "chip" }, Child = row, VerticalAlignment = VerticalAlignment.Center };
        if (tip != null) ToolTip.SetTip(chip, tip);
        return chip;
    }

    async Task ChooseGameFolderAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose your The Sims 3 folder", AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        if (GameInstall.TryOpen(path) == null)
        {
            Console.Error.WriteLine($"OSS3: {path} is not a The Sims 3 install (needs Game\\Bin and GameData\\Shared\\Packages\\FullBuild0.package).");
            return;
        }
        _settings.GameRoot = path;
        _settings.Save();
        await RefreshAsync();
    }

    void CloneGameItem(object? sender, RoutedEventArgs e)
    {
        if (_found?.Game is not { } game) return;
        _indexTask ??= Task.Run(() => ResourceIndex.Build(game));
        OpenBrowse(game, "Clone a game item", _indexTask.ContinueWith(t => (IResourceSource)t.Result), null);
    }

    /// <summary>"Clone an existing package": pick a .package, list its CAS parts, clone through it over the game.</summary>
    async void CloneExistingPackage(object? sender, RoutedEventArgs e)
    {
        if (_found?.Game is not { } game) return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a package to clone from", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Sims 3 package") { Patterns = ["*.package"] }],
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        _indexTask ??= Task.Run(() => ResourceIndex.Build(game));
        var index = _indexTask;
        var source = Task.Run(async () => (IResourceSource)new PackageOverGame(path, await index));
        OpenBrowse(game, $"Clone from {Path.GetFileName(path)}", source, () => Formats.Catalog.CasCatalog.FromPackage(path));
    }

    void DefaultReplacement(object? sender, RoutedEventArgs e)
    {
        if (_found?.Game is not { } game) return;
        _indexTask ??= Task.Run(() => ResourceIndex.Build(game));
        OpenBrowse(game, "Make a default replacement", _indexTask.ContinueWith(t => (IResourceSource)t.Result), null, defaultReplacement: true);
    }

    void OpenBrowse(GameInstall game, string title, Task<IResourceSource> source, Func<Formats.Catalog.CasCatalog>? catalog, bool defaultReplacement = false)
    {
        var icons = _found!.Products.ToDictionary(p => p.Product.Major, p => p.Icon);
        var browse = new BrowseView(game, icons, source, catalog);
        browse.BackRequested += () =>
        {
            ShowHome();
            if (catalog != null) _ = source.ContinueWith(t => (t.Result as IDisposable)?.Dispose());
        };
        browse.CreateRequested += (item, target, name) => CreateProject(source, item, target, name, defaultReplacement);
        BrowseHost.Content = browse;
        BrowseHost.IsVisible = true;
        HomePage.IsVisible = false;
        PageTitle.Text = title;
    }

    void ShowSettings(object? sender, RoutedEventArgs e)
    {
        BrowseHost.Content = new SettingsView(ChooseGameFolderAsync, _found?.Game?.Root, ShowHome);
        BrowseHost.IsVisible = true;
        HomePage.IsVisible = false;
        PageTitle.Text = "Settings";
    }

    void ShowAbout(object? sender, RoutedEventArgs e)
    {
        BrowseHost.Content = new AboutView(ShowHome);
        BrowseHost.IsVisible = true;
        HomePage.IsVisible = false;
        PageTitle.Text = "About";
    }

    /// <summary>Opens a new GitHub issue in the browser with the version and OS filled in; the user writes and sends it.</summary>
    async void ReportBug(object? sender, RoutedEventArgs e) =>
        await AboutView.Open(this, Formats.Support.AppInfo.BugReportUrl(AboutView.Version, Formats.Support.AppInfo.OperatingSystem));

    void ShowHome()
    {
        BrowseHost.Content = null;
        BrowseHost.IsVisible = false;
        HomePage.IsVisible = true;
        PageTitle.Text = "Start";
    }

    async void CreateProject(Task<IResourceSource> sourceTask, Formats.Catalog.CasItem item, ProjectTarget target, string name, bool defaultReplacement)
    {
        if (BrowseHost.Content is not BrowseView browse) return;
        browse.ShowCreating("Cloning…");
        var root = _settings.EffectiveProjectsRoot;
        try
        {
            var index = await sourceTask;
            var folder = await Task.Run(() =>
            {
                Directory.CreateDirectory(root);
                return Formats.Projects.Project.CreateCasClone(root, name, target, index,
                    new ResourceKey(0x034AEECB, item.Group, item.Instance), item.Name, item.ProductMajor,
                    (index as PackageOverGame)?.PackagePath ?? "", defaultReplacement);
            });
            Console.Error.WriteLine($"OSS3: created project \"{name}\" from {item.Key} at {folder}");
            _settings.AddRecent(folder);
            _settings.Save();
            ShowRecents();
            browse.ShowCreated($"Created in {folder}");
            await OpenProjectAsync(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"OSS3: creating project \"{name}\" from {item.Key} failed: {error}");
            browse.ShowCreated($"Could not create the project: {error.Message}");
        }
    }

    /// <summary>
    /// Opens a project in the editor and hides the start screen until it closes. A project cloned from a CC package
    /// reads through that package if it is still there, otherwise through the game alone.
    /// </summary>
    async Task OpenProjectAsync(string folder)
    {
        if (_found?.Game is not { } game) return;
        try
        {
            _indexTask ??= Task.Run(() => ResourceIndex.Build(game));
            var index = await _indexTask;
            var session = await Task.Run(() =>
            {
                var project = Formats.Projects.Project.Read(folder);
                IResourceSource source = project.SourcePackage.Length > 0 && File.Exists(project.SourcePackage)
                    ? new PackageOverGame(project.SourcePackage, index) : index;
                return Formats.Projects.ProjectSession.Open(folder, source);
            });
            _settings.AddRecent(folder);
            _settings.Save();
            var editor = new EditorWindow(session);
            editor.ProjectClosed += () =>
            {
                if (session.Game is PackageOverGame cc) cc.Dispose();
                ShowRecents();
                ShowHome();
                Show();
                Activate();
            };
            editor.Show();
            Hide();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"OSS3: opening {folder} failed: {error}");
            await ConfirmDialog.Ask(this, "Could not open the project", $"{folder}: {error.Message}", "OK");
        }
    }

    async void OpenProject(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a project folder", AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        if (!File.Exists(Path.Combine(path, Formats.Projects.Project.FileName)))
        {
            await ConfirmDialog.Ask(this, "Not a project folder", $"{path} has no {Formats.Projects.Project.FileName}.", "OK");
            return;
        }
        await OpenProjectAsync(path);
    }
}
