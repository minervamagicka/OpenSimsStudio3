using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using OpenSimsStudio3.App.Viewport;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Rendering;
using s3pi.Interfaces;

namespace OpenSimsStudio3.App;

/// <summary>
/// Viewport prototype window: steps through the drawable GEOMs of one package.
/// Not the product UI; it exists to prove the Avalonia + Vulkan viewport.
/// </summary>
public sealed class MainWindow : Window
{
    readonly OffscreenMeshRenderer _renderer = new();
    readonly MeshViewport _viewport;
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
    readonly TextBlock _meshLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    readonly IPackage _package;
    readonly List<GeomLoader.GeomEntry> _geoms;
    int _index = -1;
    GeomMesh? _mesh;

    public MainWindow(string packagePath)
    {
        Title = "OpenSimsStudio³ — viewport prototype";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OpenSimsStudio3/Assets/oss3_icon.png")));
        Width = 1100;
        Height = 800;
        (_package, _geoms) = GeomLoader.Open(packagePath);
        _viewport = new MeshViewport(_renderer);
        _viewport.FrameRendered += UpdateStatus;

        var previous = new Button { Content = "◀ Previous" };
        var next = new Button { Content = "Next ▶" };
        previous.Click += (_, _) => Step(-1);
        next.Click += (_, _) => Step(+1);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8), Children = { previous, next, _meshLabel } };

        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(new Border { Height = 28, Child = _status });
        DockPanel.SetDock(layout.Children[^1], Dock.Bottom);
        layout.Children.Add(_viewport);
        Content = layout;

        Opened += (_, _) => Step(+1);
        Closed += (_, _) => _renderer.Dispose();
    }

    /// <summary>Moves to the next or previous drawable GEOM (morph GEOMs, shader 0, carry deltas and are skipped).</summary>
    void Step(int direction)
    {
        for (var tries = 0; tries < _geoms.Count; tries++)
        {
            _index = (_index + direction + _geoms.Count) % _geoms.Count;
            var mesh = GeomLoader.Load(_package, _geoms[_index]);
            if (mesh.Shader == 0) continue;
            _mesh = mesh;
            _meshLabel.Text = $"{_index + 1} / {_geoms.Count}   {mesh.Name}";
            _viewport.ShowMesh(mesh);
            return;
        }
    }

    void UpdateStatus()
    {
        var mesh = _mesh == null ? "" : $"{_mesh.Positions.Length:N0} vertices, {_mesh.TriangleCount:N0} triangles, {_mesh.BoneCount} bones   ·   ";
        _status.Text = $"{mesh}{_renderer.DeviceName} (Vulkan)   ·   frame {_viewport.LastFrameMs:F1} ms";
    }
}
