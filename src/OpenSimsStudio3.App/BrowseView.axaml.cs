using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CASPartResource;
using OpenSimsStudio3.Formats.Catalog;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.App;

/// <summary>One tile in the item browser; its thumbnail arrives later from <see cref="ThumbnailService" />.</summary>
public sealed class BrowseTile(CasItem item, Bitmap? productIcon) : System.ComponentModel.INotifyPropertyChanged
{
    Bitmap? _thumbnail;
    public CasItem Item { get; } = item;
    public string Name => Item.Name;
    public Bitmap? ProductIcon { get; } = productIcon;
    public bool ThumbnailRequested { get; set; }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new(nameof(HasThumbnail)));
        }
    }

    public bool HasThumbnail => Thumbnail != null;
    public string Glyph => BrowseView.GlyphOf(Item.Category);
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Project targets (BRIEF.md "Project target"): what the export is allowed to contain.</summary>
public enum ProjectTarget { Retail, Both, OpenTS3Only }

/// <summary>
/// "Clone a game item": the player's CAS parts by category and species, searchable, with details of the selected part.
/// The catalog is read from the install on a worker thread when the view opens (about 2 s for the full game).
/// </summary>
public partial class BrowseView : UserControl
{
    static readonly (CasGroup Group, string Label)[] Groups =
    {
        (CasGroup.Tops, "Tops"), (CasGroup.Bottoms, "Bottoms"), (CasGroup.FullBody, "Full body"), (CasGroup.Shoes, "Shoes"),
        (CasGroup.Accessories, "Accessories"), (CasGroup.Hair, "Hair"), (CasGroup.MakeupAndOverlays, "Makeup and overlays"),
        (CasGroup.PetParts, "Pet parts"),
    };

    static readonly (string Label, SpeciesType[] Species)[] SpeciesChoices =
    {
        ("Humans", [SpeciesType.Human, SpeciesType.None]), ("Horses", [SpeciesType.Horse]), ("Cats", [SpeciesType.Cat]),
        ("Dogs", [SpeciesType.Dog]), ("Small dogs", [SpeciesType.LittleDog]),
    };

    readonly GameInstall _install;
    readonly Dictionary<int, Bitmap?> _productIcons;
    readonly Task<IResourceSource>? _game;
    readonly Func<CasCatalog>? _loadCatalog;
    ThumbnailService? _thumbnails;
    CasCatalog? _catalog;
    List<BrowseTile> _tiles = [];
    ProjectTarget _target = ProjectTarget.Retail;

    /// <summary>Raised by Create project with the chosen part, target and name.</summary>
    public event Action<CasItem, ProjectTarget, string>? CreateRequested;

    public event Action? BackRequested;

    public BrowseView() : this(null!, new(), null) { }

    /// <param name="game">Where meshes and clone sources are read (the game, or a package over it).</param>
    /// <param name="loadCatalog">The parts to list; the whole install when null.</param>
    public BrowseView(GameInstall install, Dictionary<int, Bitmap?> productIcons, Task<IResourceSource>? game, Func<CasCatalog>? loadCatalog = null)
    {
        _game = game;
        _loadCatalog = loadCatalog;
        DetachedFromVisualTree += (_, _) => _thumbnails?.Dispose();
        InitializeComponent();
        _install = install;
        _productIcons = productIcons;
        BackButton.Click += (_, _) => BackRequested?.Invoke();
        SpeciesBox.ItemsSource = SpeciesChoices.Select(s => s.Label).ToArray();
        SpeciesBox.SelectedIndex = 0;
        SpeciesBox.SelectionChanged += (_, _) => Refilter();
        SearchBox.TextChanged += (_, _) => Refilter();
        Categories.SelectionChanged += (_, _) => Refilter();
        Items.SelectionChanged += (_, _) => ShowDetails(Items.SelectedItem as BrowseTile);
        CreateButton.Click += (_, _) =>
        {
            if (Items.SelectedItem is BrowseTile tile && ProjectName.Text is { Length: > 0 } name) CreateRequested?.Invoke(tile.Item, _target, name.Trim());
        };
        ProjectName.TextChanged += (_, _) => UpdateCreate();
        BuildTargets();
        ShowDetails(null);
        if (install != null) _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        Status.Text = _loadCatalog == null ? "Reading CAS parts from the game…" : "Reading the package…";
        var catalog = await Task.Run(() => _loadCatalog?.Invoke() ?? CasCatalog.Load(_install));
        _catalog = catalog;
        if (_game != null) _thumbnails = new ThumbnailService(await _game);
        _tiles = catalog.Items.Where(i => i.Category != CasGroup.Other)
            .Select(i => new BrowseTile(i, _productIcons.GetValueOrDefault(i.ProductMajor))).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            Categories.ItemsSource = Groups.Select(g => $"{g.Label}").ToArray();
            // Open on the first group that has parts (a package may hold only hair, say).
            var first = Array.FindIndex(Groups, g => _tiles.Any(t => t.Item.Category == g.Group));
            Categories.SelectedIndex = Math.Max(0, first);
            Refilter();
            if (Environment.GetEnvironmentVariable("OSS3_TEST_START") == "browse") Items.SelectedIndex = 0;
        });
    }

    void Refilter()
    {
        if (_catalog == null || Categories.SelectedIndex < 0) return;
        var group = Groups[Categories.SelectedIndex].Group;
        var species = SpeciesChoices[Math.Max(0, SpeciesBox.SelectedIndex)].Species;
        var search = SearchBox.Text?.Trim() ?? "";
        // Pet parts with "Humans" chosen show every species' pet parts; otherwise the species filter applies everywhere.
        var anySpecies = group == CasGroup.PetParts && species.Contains(SpeciesType.Human);
        var shown = _tiles.Where(t => t.Item.Category == group && (anySpecies || species.Contains(t.Item.Species)) &&
                                      (search.Length == 0 || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Items.ItemsSource = shown;
        // Thumbnails for this view, in display order; anything still queued for the previous filter is dropped.
        _thumbnails?.Request(shown.Where(t => !t.ThumbnailRequested).Select(t =>
        {
            return (t.Item, (Action<Bitmap?>)(b =>
            {
                t.ThumbnailRequested = true;
                t.Thumbnail = b;
                if (Items.SelectedItem == t) ShowDetails(t);
            }));
        }));
        Status.Text = $"{shown.Count} of {_catalog.Items.Count} parts";
        ShowDetails(null);
    }

    void ShowDetails(BrowseTile? tile)
    {
        DetailImage.Source = tile?.Thumbnail;
        DetailGlyph.Text = tile == null || tile.HasThumbnail ? "" : tile.Glyph;
        DetailName.Text = tile?.Name ?? "Pick an item to start from";
        DetailKey.Text = tile?.Item.Key ?? "";
        DetailFacts.Text = tile == null ? "" : Facts(tile.Item);
        if (tile != null && string.IsNullOrWhiteSpace(ProjectName.Text)) ProjectName.Text = tile.Name;
        UpdateCreate();
    }

    bool _creating;

    /// <summary>Shows that the project is being made; Create stays off until <see cref="ShowCreated" />.</summary>
    public void ShowCreating(string text)
    {
        _creating = true;
        CreateButton.IsEnabled = false;
        CreateStatus.Text = text;
    }

    public void ShowCreated(string text)
    {
        _creating = false;
        UpdateCreate();
        CreateStatus.Text = text;
    }

    void UpdateCreate()
    {
        if (_creating) return;
        var ready = Items.SelectedItem is BrowseTile && !string.IsNullOrWhiteSpace(ProjectName.Text);
        CreateButton.IsEnabled = ready;
        CreateStatus.Text = ready ? "" : "Pick an item and name the project";
    }

    static string Facts(CasItem item)
    {
        var ages = Enum.GetValues<AgeFlags>().Where(a => item.Ages.HasFlag(a)).Select(a => a switch
        {
            AgeFlags.YoungAdult => "young adult", AgeFlags.Toddler => "toddler", _ => a.ToString().ToLowerInvariant(),
        });
        var genders = Enum.GetValues<GenderFlags>().Where(g => item.Genders.HasFlag(g)).Select(g => g.ToString().ToLowerInvariant());
        var categories = Enum.GetValues<ClothingCategoryFlags>().Where(c => (uint)c is > 0 and <= 0x80000 && (item.OutfitCategories & (uint)c) != 0 &&
                                                                         System.Numerics.BitOperations.IsPow2((uint)c))
            .Select(c => c.ToString());
        return string.Join("\n", new[]
        {
            $"{item.Part} · {GameProducts.Name(item.ProductMajor) ?? GameProducts.Code(item.ProductMajor)}",
            string.Join(", ", ages) + " · " + string.Join(", ", genders),
            string.Join(", ", categories),
            $"{item.Presets} recolour{(item.Presets == 1 ? "" : "s")}",
        }.Where(s => s.Trim(' ', '·').Length > 0));
    }

    void BuildTargets()
    {
        foreach (var (target, label) in new[] { (ProjectTarget.Retail, "Retail"), (ProjectTarget.Both, "Both"), (ProjectTarget.OpenTS3Only, "OpenTS3 only") })
        {
            var button = new Button { Content = label, MinHeight = 40, Padding = new Avalonia.Thickness(14, 8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            if (target == _target) button.Classes.Add("selected");
            button.Click += (_, _) =>
            {
                _target = target;
                foreach (var b in Targets.Children.OfType<Button>()) b.Classes.Remove("selected");
                button.Classes.Add("selected");
            };
            Targets.Children.Add(button);
        }
    }

    internal static string GlyphOf(CasGroup group) => group switch
    {
        CasGroup.Tops => "T", CasGroup.Bottoms => "B", CasGroup.FullBody => "F", CasGroup.Shoes => "S",
        CasGroup.Accessories => "A", CasGroup.Hair => "H", CasGroup.MakeupAndOverlays => "M", CasGroup.PetParts => "P", _ => "?",
    };
}
