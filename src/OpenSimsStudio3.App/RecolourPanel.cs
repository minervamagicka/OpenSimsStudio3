using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;
using OpenSimsStudio3.Formats.Textures;
using OpenSimsStudio3.Formats.Thumbnails;
using OpenSimsStudio3.Rendering;

namespace OpenSimsStudio3.App;

/// <summary>
/// The editor's recolour block (mockup Editor.dc.html, right column): pick a recolour, duplicate or delete it, edit its
/// colours (top-level "… Color" values such as hair's Root/Diffuse/Highlight/Tip, and solid-colour pattern channels),
/// and see its textures with their size and whether every recolour shares them, and its CAS thumbnail (Render, Replace…;
/// <see cref="CasThumbnails" />). Edits go straight into the CASP's
/// preset XML through <see cref="PresetModel" />; <see cref="Changed" /> tells the editor to refresh its save state.
/// </summary>
public sealed class RecolourPanel : Border
{
    readonly ProjectSession _session;
    readonly ComboBox _picker = new() { MinWidth = 130, MinHeight = 32, FontSize = 13 };
    readonly StackPanel _body = new() { Spacing = 8 };
    int _index;
    bool _filling;

    public event Action? Changed;

    /// <summary>Raised when another recolour is shown (picker, outline, duplicate, delete).</summary>
    public event Action<int>? Shown;

    readonly Image _preview = new() { Width = 120, Height = 120, Stretch = Stretch.Uniform };
    readonly TextBlock _previewNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _previewRow;

    // Thumbnail block (mockup: a 72 px tile with Render and Replace…). Built once; ShowThumbnail refreshes it.
    readonly Image _thumbnail = new() { Width = 70, Height = 70, Stretch = Stretch.Uniform };
    readonly Border _thumbnailFrame;
    readonly TextBlock _thumbnailStatus = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _render = new() { Content = "Render", Padding = new Thickness(14, 4) };
    readonly Button _replace = new() { Content = "Replace…", Padding = new Thickness(14, 4) };

    /// <summary>Shows the composed texture of the current recolour (or why there is none).</summary>
    public void SetPreview(Avalonia.Media.Imaging.Bitmap? image, string note)
    {
        _preview.Source = image;
        _previewNote.Text = note;
    }

    public RecolourPanel(ProjectSession session)
    {
        _session = session;
        Background = new SolidColorBrush(Color.Parse("#1C2430"));
        BorderBrush = new SolidColorBrush(Color.Parse("#33404E"));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12, 10);

        // Built once: Build() clears and refills the body, and the image keeps its source between rebuilds.
        _previewNote.Foreground = Muted;
        _previewNote.MaxWidth = 190;
        var previewFrame = new Border { Width = 124, Height = 124, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#0C1016")), BorderBrush = new SolidColorBrush(Color.Parse("#33404E")), BorderThickness = new Thickness(1), Child = _preview };
        _previewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { previewFrame, _previewNote } };

        _thumbnailFrame = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#0C1016")), BorderBrush = (IBrush)Application.Current!.FindResource("Line")!, BorderThickness = new Thickness(1), Child = _thumbnail };
        ToolTip.SetTip(_render, "Render this recolour on its mesh as the CAS thumbnail");
        ToolTip.SetTip(_replace, "Use a PNG as the CAS thumbnail");
        _render.Click += async (_, _) => await RenderThumbnail();
        _replace.Click += async (_, _) => await ReplaceThumbnail();
        var thumbnailRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                _thumbnailFrame,
                new StackPanel
                {
                    Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Thumbnail", FontSize = 12, Foreground = Muted }, _thumbnailStatus } },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _render, _replace } },
                    },
                },
            },
        };

        var duplicate = SmallButton("Duplicate", "Add a recolour copied from this one");
        var delete = SmallButton("Delete", "Remove this recolour");
        duplicate.Click += (_, _) => { Select(_session.DuplicatePreset(_index)); Changed?.Invoke(); };
        delete.Click += (_, _) =>
        {
            var presetId = _session.Part.Presets[_index].Unknown1;
            if (!_session.DeletePreset(_index)) return;
            CasThumbnails.Remove(_session, presetId);
            Select(Math.Min(_index, _session.Part.Presets.Count - 1));
            Changed?.Invoke();
        };
        _picker.SelectionChanged += (_, _) => { if (!_filling && _picker.SelectedIndex >= 0) Select(_picker.SelectedIndex); };

        var header = new DockPanel { LastChildFill = true };
        var title = new TextBlock { Text = "Recolour", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);
        _picker.HorizontalAlignment = HorizontalAlignment.Stretch;
        header.Children.Add(_picker);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { duplicate, delete } };
        Child = new StackPanel { Spacing = 10, Children = { header, actions, thumbnailRow, _body } };
        Select(0);
    }

    public int SelectedIndex => _index;

    /// <summary>Shows a recolour (also used by the outline).</summary>
    public void Select(int index)
    {
        _index = Math.Clamp(index, 0, Math.Max(0, _session.Part.Presets.Count - 1));
        _filling = true;
        _picker.ItemsSource = Enumerable.Range(1, _session.Part.Presets.Count).Select(i => $"Recolour {i} of {_session.Part.Presets.Count}").ToList();
        _picker.SelectedIndex = _index;
        _filling = false;
        Build();
        ShowThumbnail();
        Shown?.Invoke(_index);
    }

    // ---- Thumbnail ----

    /// <summary>Shows the recolour's large thumbnail from the project (a game one for a default replacement), or none.</summary>
    void ShowThumbnail(string? status = null, bool problem = false)
    {
        _thumbnailStatus.Text = status ?? "";
        _thumbnailStatus.Foreground = problem ? Warn : Muted;
        ToolTip.SetTip(_thumbnailStatus, null);
        var old = _thumbnail.Source as IDisposable;
        _thumbnail.Source = null;
        old?.Dispose();
        var bytes = _session.Part.Presets.Count == 0 ? null : CasThumbnails.ReadBytes(_session, _index);
        if (bytes != null)
            try { _thumbnail.Source = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes)); }
            catch (Exception) { _thumbnailStatus.Text = "unreadable"; _thumbnailStatus.Foreground = Warn; }
        var hasMesh = _session.Lods.Count > 0;
        _render.IsEnabled = hasMesh && _session.Part.Presets.Count > 0;
        ToolTip.SetTip(_render, hasMesh ? "Render this recolour on its mesh as the CAS thumbnail" : "This part has no mesh to render");
        _replace.IsEnabled = _session.Part.Presets.Count > 0;
    }

    void ShowThumbnailProblem(string label, string detail)
    {
        ShowThumbnail(label, problem: true);
        ToolTip.SetTip(_thumbnailStatus, detail);
    }

    void SetThumbnailBusy(bool busy)
    {
        _render.IsEnabled = !busy && _session.Lods.Count > 0;
        _replace.IsEnabled = !busy;
        if (busy) _thumbnailStatus.Text = "…";
        else if (_thumbnailStatus.Text == "…") _thumbnailStatus.Text = "";
    }

    /// <summary>
    /// Composes the recolour (as the editor's preview does), renders it on LOD 0 with a fresh offscreen renderer on a
    /// worker thread, crops to what shows, and stores every CAS size. A recolour that can't be composed renders grey.
    /// </summary>
    async Task RenderThumbnail()
    {
        if (_session.Lods.Count == 0 || _session.Part.Presets.Count == 0) return;
        var index = _index;
        var xml = _session.Part.Presets[index].XmlFile.ReadToEnd();
        var mesh = _session.Mesh(_session.Lods[0]);
        if (mesh == null) { ShowThumbnailProblem("no mesh", "LOD 0 could not be read."); return; }
        SetThumbnailBusy(true);
        try
        {
            var (image, note) = await Task.Run(() =>
            {
                Formats.Compositing.CompositedTexture? texture = null;
                string? reason;
                try { Formats.Compositing.CasCompositor.TryComposeDiffuse(_session, xml, out texture, out reason, 1024); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or System.Xml.XmlException or IndexOutOfRangeException)
                {
                    reason = error.Message;
                }
                using var renderer = new OffscreenMeshRenderer();
                var rendered = Thumbnails.RenderRecolour(renderer, mesh, texture);
                return (ImageResize.CropToContent(rendered, ThumbnailFraming.Margin), texture == null ? reason ?? "the recolour could not be composed" : null);
            });
            if (index != _index || index >= _session.Part.Presets.Count) return;
            CasThumbnails.Set(_session, index, image);
            Changed?.Invoke();
            if (note == null) ShowThumbnail();
            else ShowThumbnailProblem("untextured", "Rendered without the recolour's texture: " + note);
        }
        catch (Exception error)
        {
            // Vulkan setup and readback throw their own types; any of them means no render, and the old thumbnail stays.
            ShowThumbnailProblem("render failed", error.Message);
        }
        finally { SetThumbnailBusy(false); }
    }

    /// <summary>Uses a PNG (any size; fitted to each square CAS size on a transparent background) as the thumbnail.</summary>
    async Task ReplaceThumbnail()
    {
        if (TopLevel.GetTopLevel(this) is not { } top || _session.Part.Presets.Count == 0) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Replace thumbnail",
            FileTypeFilter = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        var index = _index;
        SetThumbnailBusy(true);
        try
        {
            var image = await Task.Run(() => PngFiles.Load(path));
            if (index != _index || index >= _session.Part.Presets.Count) return;
            CasThumbnails.Set(_session, index, image);
            Changed?.Invoke();
            ShowThumbnail();
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            ShowThumbnailProblem("not imported", error.Message);
        }
        finally { SetThumbnailBusy(false); }
    }

    void Build()
    {
        _body.Children.Clear();
        if (_session.Part.Presets.Count == 0) { _body.Children.Add(Note("This part has no recolours.")); return; }
        PresetModel model;
        try { model = _session.Preset(_index); }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidDataException)
        {
            _body.Children.Add(Note("This recolour's preset XML could not be read: " + error.Message));
            return;
        }

        var colours = model.Colors.Select(c => (Label: c.Name, Rgba: c.Rgba, Channel: (string?)null, Enabled: (bool?)null))
            .Concat(model.Channels.Where(c => c.Color != null)
                .Select(c => (Label: $"{c.Variable[8..]} · {c.PatternName}", Rgba: c.Color!, Channel: (string?)c.Variable, Enabled: (bool?)c.Enabled)))
            .ToList();

        // Preview strip: the recolour's colours side by side (a rendered thumbnail needs the texture compositor).
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Height = 22 };
        foreach (var c in colours.Where(c => c.Rgba != null && c.Enabled != false))
            strip.Children.Add(new Border { Width = 34, Background = new SolidColorBrush(ToColor(c.Rgba)) });
        _body.Children.Add(new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = strip, HorizontalAlignment = HorizontalAlignment.Left });
        _body.Children.Add(_previewRow);

        _body.Children.Add(Eyebrow("COLOURS"));
        if (colours.Count == 0) _body.Children.Add(Note("No editable colours in this recolour."));
        foreach (var c in colours) _body.Children.Add(ColourRow(model, c.Label, c.Rgba, c.Channel, c.Enabled));

        _body.Children.Add(Eyebrow("TEXTURES"));
        foreach (var t in model.Textures.DistinctBy(t => t.Name).Where(t => model.IsTextureUsed(t.Name)))
        {
            var empty = RecolourTextures.IsEmpty(t.Key);
            var info = empty ? null : TextureInfo.Read(_session.Read(t.Key));
            var row = new DockPanel { LastChildFill = true };
            var size = new TextBlock { Text = empty ? "" : info == null ? "missing" : $"{info.Width} × {info.Height} · {info.Format}", FontSize = 12,
                Foreground = !empty && info == null ? Warn : Muted, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            if (RecolourTextures.IsShared(_session, t.Name))
            {
                var link = LinkIcon();
                DockPanel.SetDock(link, Dock.Right);
                row.Children.Add(link);
            }
            row.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var slot = new Button
            {
                Content = row, MinHeight = 32, Padding = new Thickness(4, 4), BorderThickness = new Thickness(0, 1, 0, 0),
                CornerRadius = new CornerRadius(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = (IBrush)Application.Current!.FindResource("Selected")!,
            };
            ToolTip.SetTip(slot, t.Key.ToString());
            var name = t.Name;
            slot.Click += async (_, _) => await OpenTexture(name);
            _body.Children.Add(slot);
        }
    }

    async Task OpenTexture(string slot)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var window = new TextureWindow(_session, _index, slot);
        await window.ShowDialog(owner);
        if (!window.Edited) return;
        Changed?.Invoke();
        Build();
    }

    /// <summary>The shared-slot mark from the mockup: a link glyph in a coral-edged tile.</summary>
    static Control LinkIcon()
    {
        Avalonia.Controls.Shapes.Path Stroke(string data) => new()
        {
            Data = Geometry.Parse(data), Stroke = new SolidColorBrush(Color.Parse("#FFC2AF")), StrokeThickness = 2.2,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        var glyph = new Viewbox
        {
            Width = 14, Height = 14,
            Child = new Canvas { Width = 24, Height = 24, Children = { Stroke("M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1.5 1.5"), Stroke("M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1.5-1.5") } },
        };
        var tile = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = (IBrush)Application.Current!.FindResource("CoralTint")!, BorderBrush = (IBrush)Application.Current!.FindResource("Coral")!,
            BorderThickness = new Thickness(1), Child = glyph,
        };
        ToolTip.SetTip(tile, "Shared by all recolours");
        return tile;
    }

    Control ColourRow(PresetModel model, string label, float[] rgba, string? channel, bool? enabled)
    {
        var swatch = new Button { Width = 30, Height = 26, MinHeight = 26, Padding = new Thickness(0), Background = new SolidColorBrush(ToColor(rgba)),
            BorderBrush = new SolidColorBrush(Color.Parse("#4B5D72")), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
        var hex = new TextBox { Text = Hex(rgba), Width = 92, MinHeight = 30, FontSize = 12, Padding = new Thickness(8, 4), FontFamily = new FontFamily("Consolas") };
        var view = new ColorView { Color = ToColor(rgba), IsAlphaEnabled = false, IsColorPaletteVisible = false };
        var flyout = new Flyout { Content = view, Placement = PlacementMode.Left };
        swatch.Flyout = flyout;

        void Apply(Color c)
        {
            var value = new[] { c.R / 255f, c.G / 255f, c.B / 255f, rgba.Length > 3 ? rgba[3] : 1f };
            var current = _session.Preset(_index);
            var ok = channel == null ? Try(() => current.SetColor(label, value)) : current.SetChannelColor(channel, value);
            if (!ok) return;
            _session.SetPreset(_index, current);
            swatch.Background = new SolidColorBrush(c);
            Changed?.Invoke();
        }
        flyout.Closed += (_, _) => { Apply(view.Color); hex.Text = Hex(new[] { view.Color.R / 255f, view.Color.G / 255f, view.Color.B / 255f }); Build(); };
        hex.LostFocus += (_, _) => { if (Color.TryParse(hex.Text?.StartsWith('#') == true ? hex.Text : "#" + hex.Text, out var c)) { Apply(c); Build(); } };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (channel != null)
        {
            var box = new CheckBox { IsChecked = enabled == true, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(box, "Channel enabled");
            box.IsCheckedChanged += (_, _) =>
            {
                var current = _session.Preset(_index);
                current.SetChannelEnabled(channel, box.IsChecked == true);
                _session.SetPreset(_index, current);
                Changed?.Invoke();
                Build();
            };
            row.Children.Add(box);
        }
        row.Children.Add(swatch);
        row.Children.Add(hex);
        row.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    static bool Try(Action a) { try { a(); return true; } catch (KeyNotFoundException) { return false; } }

    static Color ToColor(float[] rgba) =>
        Color.FromRgb((byte)Math.Round(Math.Clamp(rgba[0], 0, 1) * 255), (byte)Math.Round(Math.Clamp(rgba[1], 0, 1) * 255), (byte)Math.Round(Math.Clamp(rgba[2], 0, 1) * 255));

    static string Hex(float[] rgba) { var c = ToColor(rgba); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }

    static Button SmallButton(string text, string tip)
    {
        var b = new Button { Content = text, MinHeight = 32, Padding = new Thickness(10, 4), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(b, tip);
        return b;
    }

    static TextBlock Eyebrow(string text) => new() { Text = text, Classes = { "eyebrow" }, Margin = new Thickness(0, 6, 0, 0) };
    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    static IBrush Muted => (IBrush)Application.Current!.FindResource("Muted")!;
    static IBrush Warn => (IBrush)Application.Current!.FindResource("Warn")!;
}
