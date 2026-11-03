using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenSimsStudio3.Formats.Meshes;
using Vector2 = System.Numerics.Vector2;

namespace OpenSimsStudio3.App;

/// <summary>Shows one mesh group's UV layout in the 0–1 square, problem triangles highlighted. View only: OSS3 doesn't repack UVs.</summary>
public sealed class UvWindow : Window
{
    public UvWindow(string title, GeomMesh mesh, UvReport report)
    {
        Title = $"UVs · {title}";
        Width = 640;
        Height = 720;
        Background = Brush("Ink");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var view = new UvView(mesh, report);
        var legend = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        var channels = new ComboBox { IsVisible = mesh.UvChannels.Count > 1, MinWidth = 120 };
        channels.ItemsSource = Enumerable.Range(1, Math.Max(1, mesh.UvChannels.Count)).Select(c => $"UV{c}").ToList();
        channels.SelectedIndex = 0;
        channels.SelectionChanged += (_, _) => { view.Channel = Math.Max(0, channels.SelectedIndex); FillLegend(); };
        FillLegend();

        void FillLegend()
        {
            legend.Children.Clear();
            var issues = report.Issues.Where(i => i.Channel == view.Channel).ToList();
            if (issues.Count == 0) legend.Children.Add(new TextBlock { Text = "No UV problems", FontSize = 12, Foreground = Brush("Muted") });
            foreach (var issue in issues)
            {
                var line = new TextBlock
                {
                    Text = $"■ {UvWording.Label(issue with { Channel = 0 })} · {issue.Count:N0}",
                    FontSize = 12,
                    Foreground = Brush(UvWording.IsDanger(issue.Kind) ? "Danger" : "Warn"),
                };
                ToolTip.SetTip(line, UvWording.Tip(issue));
                legend.Children.Add(line);
            }
        }

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(channels);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(legend, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(legend);
        root.Children.Add(view);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    internal static ISolidColorBrush Brush(string key) => (ISolidColorBrush)Application.Current!.FindResource(key)!;
}

/// <summary>
/// The UV wireframe of one UV set, drawn with V downwards so it lines up with the texture image (DDS rows run top to
/// bottom). The 0–1 square is outlined; the view widens to show UVs outside it, up to -1..2.
/// </summary>
public sealed class UvView : Control
{
    readonly GeomMesh _mesh;
    readonly UvReport _report;
    int _channel;
    Size _builtFor;
    StreamGeometry? _edges, _warn, _danger;
    Rect _square;

    public UvView(GeomMesh mesh, UvReport report)
    {
        _mesh = mesh;
        _report = report;
        ClipToBounds = true;
    }

    public int Channel
    {
        get => _channel;
        set { _channel = value; _builtFor = default; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 8 || size.Height < 8) return;
        context.FillRectangle(UvWindow.Brush("Rail"), new Rect(size));
        if (size != _builtFor) Build(size);
        context.FillRectangle(UvWindow.Brush("Surface"), _square);
        context.DrawRectangle(new Pen(UvWindow.Brush("EdgeHover"), 1), _square);
        if (_edges != null) context.DrawGeometry(null, new Pen(UvWindow.Brush("Muted"), 0.6), _edges);
        if (_warn != null) context.DrawGeometry(Tint("Warn"), new Pen(UvWindow.Brush("Warn"), 1), _warn);
        if (_danger != null) context.DrawGeometry(Tint("Danger"), new Pen(UvWindow.Brush("Danger"), 1), _danger);
    }

    static IBrush Tint(string key) => new SolidColorBrush(UvWindow.Brush(key).Color, 0.35);

    void Build(Size size)
    {
        _builtFor = size;
        _edges = _warn = _danger = null;
        var uvs = _channel < _mesh.UvChannels.Count ? _mesh.UvChannels[_channel] : [];

        // View bounds: the 0–1 square plus whatever UVs lie outside it, capped at -1..2.
        var lo = Vector2.Zero;
        var hi = Vector2.One;
        foreach (var uv in uvs)
            if (float.IsFinite(uv.X) && float.IsFinite(uv.Y)) { lo = Vector2.Min(lo, uv); hi = Vector2.Max(hi, uv); }
        lo = Vector2.Max(lo, new Vector2(-1));
        hi = Vector2.Min(hi, new Vector2(2));
        var span = Math.Max(hi.X - lo.X, hi.Y - lo.Y);
        const double pad = 12;
        var scale = (Math.Min(size.Width, size.Height) - 2 * pad) / span;
        var origin = new Point((size.Width - span * scale) / 2 - lo.X * scale, (size.Height - span * scale) / 2 - lo.Y * scale);
        Point At(Vector2 uv) => new(origin.X + uv.X * scale, origin.Y + uv.Y * scale);
        _square = new Rect(At(Vector2.Zero), At(Vector2.One));
        if (uvs.Length != _mesh.Positions.Length) return;

        var warn = new HashSet<int>();
        var danger = new HashSet<int>();
        foreach (var issue in _report.Issues.Where(i => i.Channel == _channel))
            (UvWording.IsDanger(issue.Kind) ? danger : warn).UnionWith(issue.Triangles);
        warn.ExceptWith(danger);

        _edges = Triangles(Enumerable.Range(0, _mesh.TriangleCount).Where(f => !warn.Contains(f) && !danger.Contains(f)), false);
        if (warn.Count > 0) _warn = Triangles(warn, true);
        if (danger.Count > 0) _danger = Triangles(danger, true);

        StreamGeometry Triangles(IEnumerable<int> faces, bool filled)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            foreach (var f in faces)
            {
                var (a, b, c) = (uvs[_mesh.Indices[3 * f]], uvs[_mesh.Indices[3 * f + 1]], uvs[_mesh.Indices[3 * f + 2]]);
                if (!Finite(a) || !Finite(b) || !Finite(c)) continue;
                g.BeginFigure(At(a), filled);
                g.LineTo(At(b));
                g.LineTo(At(c));
                g.EndFigure(true);
            }
            return geometry;
        }
    }

    static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
}
