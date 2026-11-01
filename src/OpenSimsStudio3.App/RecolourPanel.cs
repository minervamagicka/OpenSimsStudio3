using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using OpenSimsStudio3.Formats.Cas;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.App;

/// <summary>
/// The editor's recolour block (mockup Editor.dc.html, right column): pick a recolour, duplicate or delete it, edit its
/// colours (top-level "… Color" values such as hair's Root/Diffuse/Highlight/Tip, and solid-colour pattern channels),
/// and see its textures with their size and whether every recolour shares them. Edits go straight into the CASP's
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

        var duplicate = SmallButton("Duplicate", "Add a recolour copied from this one");
        var delete = SmallButton("Delete", "Remove this recolour");
        duplicate.Click += (_, _) => { Select(_session.DuplicatePreset(_index)); Changed?.Invoke(); };
        delete.Click += (_, _) =>
        {
            if (!_session.DeletePreset(_index)) return;
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
        Child = new StackPanel { Spacing = 10, Children = { header, actions, _body } };
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
        Shown?.Invoke(_index);
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
        var shared = SharedTextures();
        foreach (var t in model.Textures.DistinctBy(t => t.Name).Where(t => model.IsTextureUsed(t.Name)))
        {
            var info = TextureInfo.Read(_session.Read(t.Key));
            var row = new DockPanel { LastChildFill = true };
            var size = new TextBlock { Text = info == null ? "missing" : $"{info.Width} × {info.Height} · {info.Format}", FontSize = 12,
                Foreground = info == null ? Warn : Muted, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            if (shared.Contains(t.Name))
            {
                var link = new TextBlock { Text = "🔗", FontSize = 11, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(link, "Shared by all recolours");
                DockPanel.SetDock(link, Dock.Right);
                row.Children.Add(link);
            }
            var name = new TextBlock { Text = t.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(name, t.Key.ToString());
            row.Children.Add(name);
            _body.Children.Add(row);
        }
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

    /// <summary>Texture slots whose key is the same in every recolour.</summary>
    HashSet<string> SharedTextures()
    {
        var models = Enumerable.Range(0, _session.Part.Presets.Count).Select(i => { try { return _session.Preset(i); } catch { return null; } })
            .Where(m => m != null).Select(m => m!.Textures.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First().Key)).ToList();
        if (models.Count < 2) return [];
        return models[0].Where(kv => models.All(m => m.TryGetValue(kv.Key, out var k) && k == kv.Value)).Select(kv => kv.Key).ToHashSet();
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
