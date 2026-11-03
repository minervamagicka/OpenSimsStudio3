using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Textures;

namespace OpenSimsStudio3.App;

/// <summary>
/// One texture slot of one recolour (mockup Editor.dc.html, the slot dialog): a large preview with R/G/B/A view
/// toggles, Import… (PNG, encoded in the slot's format), Export… (PNG of the top mip), Bake (the Multiplier of a
/// patterned recolour only: see <see cref="TextureBake" />), Clear, and Same in all recolours. While that box is
/// ticked, Import, Bake and Clear change every recolour that has the slot; ticking it points
/// every recolour at this recolour's texture. <see cref="Edited" /> tells the caller to refresh.
/// </summary>
public sealed class TextureWindow : Window
{
    const int PreviewSize = 300;

    readonly ProjectSession _session;
    readonly int _preset;
    readonly string _slot;
    readonly Image _preview = new() { Width = PreviewSize, Height = PreviewSize, Stretch = Stretch.Uniform };
    readonly TextBlock _size = new() { FontSize = 13 };
    readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 };
    readonly Button _import = Action("Import…");
    readonly Button _export = Action("Export…");
    readonly Button _bake = Action("Bake");
    readonly Button _clear = Action("Clear");
    readonly CheckBox _shared = new() { Content = "Same in all recolours", FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };
    readonly Button[] _channels;
    readonly bool[] _shown = [true, true, true, true];
    DdsImage? _image;
    bool _filling;

    /// <summary>True once the project changed through this window.</summary>
    public bool Edited { get; private set; }

    public TextureWindow(ProjectSession session, int preset, string slot)
    {
        _session = session;
        _preset = preset;
        _slot = slot;
        Title = $"{slot} · Recolour {preset + 1}";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("Rail");

        _channels = new[] { "R", "G", "B", "A" }.Select(c => new Button
        {
            Content = c, Width = 36, MinHeight = 30, Height = 30, FontSize = 12, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            BorderBrush = Brush("Edge"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
        }).ToArray();
        for (var i = 0; i < 4; i++)
        {
            var channel = i;
            _channels[i].Click += (_, _) => { _shown[channel] = !_shown[channel]; ShowPreview(); };
        }

        _import.Click += async (_, _) => await Import();
        _export.Click += async (_, _) => await Export();
        _bake.Click += async (_, _) => await Bake();
        _bake.IsVisible = TextureBake.Applies(session, preset, slot);
        _clear.Click += (_, _) => Run(() => RecolourTextures.Clear(_session, _preset, _slot, _shared.IsChecked == true));
        _shared.IsCheckedChanged += (_, _) =>
        {
            // Unticking changes nothing yet: the next Import or Clear then applies to this recolour only.
            if (!_filling && _shared.IsChecked == true) Run(() => RecolourTextures.ShareAcross(_session, _preset, _slot));
        };
        _status.Foreground = Brush("Warn");

        var frame = new Border
        {
            Width = PreviewSize + 2, Height = PreviewSize + 2, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#0C1016")), BorderBrush = Brush("Edge"), BorderThickness = new Thickness(1), Child = _preview,
        };
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var t in _channels) toggles.Children.Add(t);
        var left = new StackPanel { Spacing = 8, Children = { frame, toggles } };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _import, _export, _bake, _clear } };
        var right = new StackPanel { Spacing = 10, Children = { _size, buttons, _shared, _status } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,*"), Margin = new Thickness(16) };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        Refresh();
    }

    /// <summary>Reads the slot again: size, preview, shared state and which actions apply.</summary>
    void Refresh()
    {
        var key = RecolourTextures.Slot(_session, _preset, _slot);
        var empty = key is not { } k || RecolourTextures.IsEmpty(k);
        var bytes = empty ? null : _session.Read(key!.Value);
        var info = TextureInfo.Read(bytes);
        _image = bytes == null ? null : DdsDecoder.TryDecode(bytes);
        _size.Text = empty ? "" : info == null ? "Missing" : $"{info.Width} × {info.Height} · {info.Format}";
        _size.Foreground = !empty && info == null ? Brush("Warn") : Brush("NavText");
        _export.IsEnabled = _image != null;
        _clear.IsEnabled = !empty;
        _bake.IsEnabled = key != null;
        _import.IsEnabled = key != null;
        _filling = true;
        _shared.IsChecked = RecolourTextures.IsShared(_session, _slot);
        _shared.IsEnabled = key != null && _session.Part.Presets.Count > 1;
        _filling = false;
        ShowPreview();
    }

    void Run(Action edit)
    {
        try
        {
            edit();
            Edited = true;
            _status.Text = "";
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            _status.Text = error.Message;
        }
        Refresh();
    }

    async Task Import()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import {_slot}", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        SetBusy(true);
        try
        {
            var existing = RecolourTextures.Slot(_session, _preset, _slot) is { } old && !RecolourTextures.IsEmpty(old) ? _session.Read(old) : null;
            var encoded = await Task.Run(() => TextureImport.Encode(PngFiles.Load(path), existing));
            Run(() => RecolourTextures.Replace(_session, _preset, _slot, encoded.Dds, _shared.IsChecked == true));
            _status.Text = string.Join("\n", encoded.Warnings);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
        }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// Composes the recolour on a worker thread, then stores the bake and its preset edits here. A bake the recolour
    /// doesn't compose back to (see <see cref="BakePlan.Matches" />) is kept, with a warning.
    /// </summary>
    async Task Bake()
    {
        SetBusy(true);
        try
        {
            var plan = await Task.Run(() => TextureBake.Plan(_session, _preset));
            Run(() => TextureBake.Apply(_session, plan, _shared.IsChecked == true));
            if (!plan.Matches && string.IsNullOrEmpty(_status.Text)) _status.Text = $"Preview differs from the bake by {plan.Difference:F1} levels";
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or NotSupportedException or ArgumentException
                                          or System.Xml.XmlException or IndexOutOfRangeException)
        {
            _status.Text = error.Message;
        }
        finally { SetBusy(false); }
    }

    async Task Export()
    {
        if (_image is not { } image) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {_slot}", SuggestedFileName = $"{_slot} {_preset + 1}.png", DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            await Task.Run(() => PngFiles.Save(image, path));
            _status.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
        }
    }

    void SetBusy(bool busy)
    {
        _import.IsEnabled = _export.IsEnabled = _bake.IsEnabled = _clear.IsEnabled = _shared.IsEnabled = !busy;
        if (!busy) Refresh();
    }

    /// <summary>
    /// The top mip through the channel toggles: one channel on shows it as grey; otherwise the ticked colour channels,
    /// with A (when on) as transparency over the dark frame.
    /// </summary>
    void ShowPreview()
    {
        var on = _shown;
        for (var i = 0; i < 4; i++)
        {
            _channels[i].Background = on[i] ? new SolidColorBrush(Color.Parse("#4A3036")) : Brushes.Transparent;
            _channels[i].Foreground = on[i] ? Brush("Text") : Brush("Muted");
        }
        if (_image is not { } image) { _preview.Source = null; return; }

        var single = on.Count(x => x) == 1 ? Array.IndexOf(on, true) : -1;
        var src = image.Rgba;
        var dst = new byte[src.Length];
        for (var p = 0; p < src.Length; p += 4)
        {
            if (single >= 0)
            {
                dst[p] = dst[p + 1] = dst[p + 2] = src[p + single];
                dst[p + 3] = 255;
                continue;
            }
            int a = on[3] ? src[p + 3] : 255;
            // Premultiplied for the bitmap below.
            for (var c = 0; c < 3; c++) dst[p + c] = on[c] ? (byte)((src[p + c] * a + 127) / 255) : (byte)0;
            dst[p + 3] = (byte)a;
        }
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var target = bitmap.Lock())
            for (var y = 0; y < image.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(dst, y * image.Width * 4, target.Address + y * target.RowBytes, image.Width * 4);
        _preview.Source = bitmap;
    }

    static Button Action(string text) => new() { Content = text, Padding = new Thickness(16, 6), Margin = new Thickness(0, 0, 8, 8) };

    static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
}
