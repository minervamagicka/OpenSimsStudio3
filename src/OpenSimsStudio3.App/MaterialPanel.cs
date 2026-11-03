using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OpenSimsStudio3.Formats.Install;
using OpenSimsStudio3.Formats.Materials;
using OpenSimsStudio3.Formats.Projects;

namespace OpenSimsStudio3.App;

/// <summary>
/// The group inspector's Material section (mockup Editor.dc.html): the shader dropdown, one row per MTNF parameter with
/// an editor for its kind, and Add…. Every commit reads the group's current GEOM from the session, applies the change
/// through <see cref="GeomMaterial" /> and puts the new bytes back with <see cref="ProjectSession.Replace" />, so the
/// panel holds no copy of its own. <see cref="Changed" /> carries whether the shader changed (the outline depends on it).
/// </summary>
public sealed class MaterialPanel : StackPanel
{
    const uint DdsType = 0x00B2D882;
    static readonly FontFamily Mono = new("JetBrains Mono, Consolas, monospace");
    static readonly HashSet<string> ColourNames = new(StringComparer.OrdinalIgnoreCase)
        { "Diffuse", "Specular", "Ambient", "Emission", "Reflective", "Transparent" };

    readonly ProjectSession _session;
    readonly ResourceKey _key;

    public event Action<bool>? Changed;

    public MaterialPanel(ProjectSession session, ResourceKey key)
    {
        _session = session;
        _key = key;
        Spacing = 6;
        Build();
    }

    GeomMaterial? Current()
    {
        try { return _session.Read(_key) is { } bytes ? GeomMaterial.Read(bytes) : null; }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or System.Reflection.TargetInvocationException) { return null; }
    }

    /// <summary>Applies one edit to the stored GEOM; returns false (and changes nothing) when the edit is refused.</summary>
    bool Commit(Action<GeomMaterial> edit, bool rebuild = false)
    {
        if (_session.Read(_key) is not { } bytes || GeomMaterial.Read(bytes) is not { } material) return false;
        var shaderBefore = material.Shader;
        try { edit(material); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
        var written = material.Write(bytes);
        if (written.AsSpan().SequenceEqual(bytes)) return true;
        _session.Replace(_key, written);
        Changed?.Invoke(material.Shader != shaderBefore);
        if (rebuild) Build();
        return true;
    }

    void Build()
    {
        Children.Clear();
        Children.Add(new Border { BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 2) });
        Children.Add(new TextBlock { Text = "MATERIAL", Classes = { "eyebrow" } });
        var material = Current();
        if (material == null)
        {
            Children.Add(new TextBlock { Text = "None", FontSize = 13, Foreground = Brush("Muted") });
            return;
        }

        var shaders = ShaderNames.Shaders.Select(s => (s.Hash, Label: s.Name)).ToList();
        if (ShaderNames.ShaderName(material.Shader) == null) shaders.Insert(0, (material.Shader, ShaderNames.ShaderLabel(material.Shader)));
        var shaderBox = new ComboBox
        {
            ItemsSource = shaders.Select(s => s.Label).ToList(), SelectedIndex = shaders.FindIndex(s => s.Hash == material.Shader),
            MinHeight = 34, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 420,
        };
        shaderBox.SelectionChanged += (_, _) =>
        {
            if (shaderBox.SelectedIndex < 0 || shaders[shaderBox.SelectedIndex].Hash == material.Shader) return;
            var hash = shaders[shaderBox.SelectedIndex].Hash;
            Commit(m => m.Shader = hash, rebuild: true);
        };
        Children.Add(Row("Shader", shaderBox, mono: false));

        foreach (var parameter in material.Parameters)
            Children.Add(ParameterRow(parameter, material));

        var add = new Button { Content = "Add…", Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        add.Flyout = AddFlyout(material);
        Children.Add(add);
    }

    Control ParameterRow(ShaderParameter p, GeomMaterial material)
    {
        var field = p.Field;
        Control editor = p.Kind switch
        {
            ShaderParameterKind.Int => NumberBox(p.IntValue.ToString(CultureInfo.InvariantCulture), text =>
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && Commit(m => m.Find(field)!.IntValue = v)),
            ShaderParameterKind.Texture or ShaderParameterKind.TextureKey => TextureEditor(p, material),
            _ => FloatEditor(p),
        };
        var remove = new Button { Content = "×", Width = 30, MinHeight = 30, Height = 30, Padding = new Thickness(0), FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(remove, "Remove");
        remove.Click += (_, _) => Commit(m => m.Remove(field), rebuild: true);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        grid.Children.Add(editor);
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);
        var row = Row(p.Label, grid, mono: true);
        ToolTip.SetTip(row, $"{p.Kind} · 0x{p.Field:X8}");
        return row;
    }

    Control FloatEditor(ShaderParameter p)
    {
        var field = p.Field;
        var values = p.Floats;
        var panel = new Grid { ColumnSpacing = 4 };
        var boxes = new List<TextBox>();
        for (var i = 0; i < values.Length; i++)
        {
            var index = i;
            panel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var box = NumberBox(Format(values[i]), text =>
                float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && Commit(m => m.Find(field)!.SetFloat(index, v)));
            Grid.SetColumn(box, i);
            panel.Children.Add(box);
            boxes.Add(box);
        }
        if (values.Length < 3 || !(ColourNames.Contains(p.Label) || p.Label.EndsWith("Color", StringComparison.OrdinalIgnoreCase))) return panel;

        // Colour-like vectors also get a swatch with a colour picker (RGB, 0–1; a fourth value is kept).
        var swatch = new Button { Width = 30, Height = 30, MinHeight = 30, Padding = new Thickness(0), Background = new SolidColorBrush(ToColor(values)),
            BorderBrush = Brush("Line"), BorderThickness = new Thickness(1) };
        ToolTip.SetTip(swatch, "Colour");
        var view = new ColorView { Color = ToColor(values), IsAlphaEnabled = false, IsColorPaletteVisible = false };
        var flyout = new Flyout { Content = view, Placement = PlacementMode.Left };
        swatch.Flyout = flyout;
        flyout.Closed += (_, _) =>
        {
            var c = view.Color;
            if (c == ToColor(values)) return;
            var rgb = new[] { c.R / 255f, c.G / 255f, c.B / 255f };
            if (Commit(m => { var param = m.Find(field)!; var v = param.Floats; Array.Copy(rgb, v, 3); param.Floats = v; }))
            {
                for (var i = 0; i < 3; i++) { values[i] = rgb[i]; boxes[i].Text = Format(rgb[i]); }
                swatch.Background = new SolidColorBrush(c);
            }
        };
        panel.ColumnDefinitions.Insert(0, new ColumnDefinition(GridLength.Auto));
        foreach (var b in boxes) Grid.SetColumn(b, Grid.GetColumn(b) + 1);
        panel.Children.Add(swatch);
        return panel;
    }

    Control TextureEditor(ShaderParameter p, GeomMaterial material)
    {
        var field = p.Field;
        var box = new TextBox
        {
            Text = p.Texture?.ToString() ?? "", PlaceholderText = p.Texture == null ? $"index {p.TextureIndex}" : null,
            FontFamily = Mono, FontSize = 11, MinHeight = 30, Padding = new Thickness(6, 4), TextWrapping = TextWrapping.Wrap,
        };
        void Apply(string? text)
        {
            if (TryParseKey(text, out var key) && Commit(m => m.Find(field)!.Texture = key)) { box.Text = key.ToString(); return; }
            box.Text = Current()?.Find(field)?.Texture?.ToString() ?? "";
        }
        CommitOn(box, Apply);

        // Pick: the project's textures, then keys this GEOM already references.
        var keys = _session.PackageKeys.Where(k => k.Type == DdsType).OrderBy(k => k.Instance)
            .Concat(material.Keys.Where(k => k.Type == DdsType)).Distinct().ToList();
        var pick = new Button { Content = "…", Width = 30, MinHeight = 30, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, IsEnabled = keys.Count > 0 };
        ToolTip.SetTip(pick, "Pick a texture");
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var key in keys)
        {
            var item = new MenuItem { Header = key.ToString() + (_session.PackageKeys.Contains(key) ? "" : "  (game)"), FontFamily = Mono, FontSize = 11 };
            item.Click += (_, _) => Apply(key.ToString());
            menu.Items.Add(item);
        }
        pick.Flyout = menu;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        grid.Children.Add(box);
        Grid.SetColumn(pick, 1);
        grid.Children.Add(pick);
        return grid;
    }

    Flyout AddFlyout(GeomMaterial material)
    {
        var present = material.Parameters.Select(p => p.Field).ToHashSet();
        var name = new AutoCompleteBox
        {
            ItemsSource = ShaderNames.KnownParameters.Where(k => !present.Contains(k.Hash)).Select(k => k.Name).ToList(),
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 0, Width = 230, MinHeight = 34, FontSize = 13,
            PlaceholderText = "Name or 0x hash",
        };
        var kinds = Enum.GetValues<ShaderParameterKind>();
        var kind = new ComboBox { ItemsSource = kinds.Select(k => k.ToString()).ToList(), SelectedIndex = 0, MinHeight = 34, FontSize = 13, Width = 120 };
        name.TextChanged += (_, _) =>
        {
            if (ShaderNames.ParseHashOrName(name.Text) is { } h && ShaderNames.ParameterKind(h) is { } k) kind.SelectedIndex = Array.IndexOf(kinds, k);
        };
        var confirm = new Button { Content = "Add", Classes = { "primary" }, Padding = new Thickness(18, 6) };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Left,
            Content = new StackPanel { Spacing = 8, Children = { name, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { kind, confirm } } } },
        };
        confirm.Click += (_, _) =>
        {
            if (ShaderNames.ParseHashOrName(name.Text) is not { } hash || present.Contains(hash)) return;
            var chosen = kinds[Math.Max(0, kind.SelectedIndex)];
            // A new texture parameter starts on the first texture the material already uses, else the project's first.
            ResourceKey? texture = chosen is ShaderParameterKind.Texture or ShaderParameterKind.TextureKey
                ? material.Parameters.Select(p => p.Texture).FirstOrDefault(t => t != null)
                  ?? _session.PackageKeys.Where(k => k.Type == DdsType).OrderBy(k => k.Instance).Select(k => (ResourceKey?)k).FirstOrDefault()
                : null;
            flyout.Hide();
            Commit(m => m.Add(hash, chosen, texture), rebuild: true);
        };
        return flyout;
    }

    // ---- helpers ----

    static Control Row(string key, Control field, bool mono)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), ColumnSpacing = 10 };
        var label = new TextBlock { Text = key, FontSize = mono ? 12 : 13, Foreground = Brush("Muted"), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        if (mono) label.FontFamily = Mono;
        ToolTip.SetTip(label, key);
        grid.Children.Add(label);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return grid;
    }

    /// <summary>A compact text box that commits on Enter or when focus leaves; a refused value goes back to the stored one.</summary>
    static TextBox NumberBox(string text, Func<string, bool> commit)
    {
        var box = new TextBox { Text = text, FontFamily = Mono, FontSize = 12, MinHeight = 30, MinWidth = 0, Padding = new Thickness(6, 4) };
        var last = text;
        CommitOn(box, value =>
        {
            if (value == last) return;
            if (value != null && commit(value)) last = value;
            else box.Text = last;
        });
        return box;
    }

    static void CommitOn(TextBox box, Action<string?> commit)
    {
        box.LostFocus += (_, _) => commit(box.Text?.Trim());
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(box.Text?.Trim()); e.Handled = true; } };
    }

    static bool TryParseKey(string? text, out ResourceKey key)
    {
        key = default;
        var parts = text?.Split(':', '-') ?? [];
        if (parts.Length != 3) return false;
        var style = NumberStyles.HexNumber;
        var culture = CultureInfo.InvariantCulture;
        static string Hex(string s) => s.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Trim()[2..] : s.Trim();
        if (!uint.TryParse(Hex(parts[0]), style, culture, out var type) || !uint.TryParse(Hex(parts[1]), style, culture, out var group)
            || !ulong.TryParse(Hex(parts[2]), style, culture, out var instance)) return false;
        key = new ResourceKey(type, group, instance);
        return true;
    }

    static string Format(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    static Color ToColor(float[] v) => Color.FromRgb(Byte(v[0]), Byte(v[1]), Byte(v[2]));

    static byte Byte(float f) => (byte)Math.Round(Math.Clamp(float.IsNaN(f) ? 0 : f, 0, 1) * 255);

    static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
}
