using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Rendering;

namespace OpenSimsStudio3.App.Viewport;

/// <summary>Camera presets. The Sim faces +Z, so Front looks down -Z and Side looks at the Sim's left.</summary>
public enum CameraView { Free, Front, Side, CloseUp }

/// <summary>
/// The 3D viewport: renders the current mesh with the Vulkan offscreen renderer and draws the result.
/// Left-drag orbits (and switches to the Free view), the wheel zooms. Renders only when something changes (no idle frame loop).
/// A new mesh keeps the current camera, so switching LOD doesn't move the view; <see cref="ResetCamera" /> re-frames.
/// </summary>
public sealed class MeshViewport : Control
{
    static readonly Vector3 LightDirection = new(0.4f, 0.8f, 0.6f);

    readonly OffscreenMeshRenderer _renderer;
    readonly OrbitCamera _camera = new();
    WriteableBitmap? _bitmap;
    byte[] _pixels = [];
    Point? _dragFrom;
    bool _hasMesh;
    (Vector3 Min, Vector3 Max) _bounds;
    CameraView _view;

    public MeshViewport(OffscreenMeshRenderer renderer)
    {
        _renderer = renderer;
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Milliseconds the last frame took (render plus copy), for the status bar.</summary>
    public double LastFrameMs { get; private set; }
    public event Action? FrameRendered;

    /// <summary>Raised when the user's drag leaves a preset for the Free view.</summary>
    public event Action<CameraView>? ViewChanged;

    public CameraView View
    {
        get => _view;
        set { _view = value; ResetCamera(); }
    }

    /// <summary>Shows the part, with the rest of the Sim around it when given. The camera frames the part alone.</summary>
    public void ShowMesh(GeomMesh mesh, GeomMesh? body = null)
    {
        _renderer.SetMesh(mesh, body);
        _bounds = mesh.Bounds();
        if (!_hasMesh) { _hasMesh = true; ResetCamera(); }
        else RenderFrame();
    }

    /// <summary>Frames the mesh from the current view's angle.</summary>
    public void ResetCamera()
    {
        if (!_hasMesh) return;
        (_camera.Yaw, _camera.Pitch) = _view switch
        {
            CameraView.Front => (0f, 0.05f),
            CameraView.Side => (MathF.PI / 2, 0.05f),
            CameraView.CloseUp => (0.3f, 0.1f),
            _ => (0.5f, 0.15f),
        };
        // Front and Side frame the box as seen from that axis: the depth extent (the arm span, from the side) doesn't count.
        var (min, max) = _bounds;
        var centre = (min + max) / 2;
        if (_view == CameraView.Front) (min.Z, max.Z) = (centre.Z, centre.Z);
        if (_view == CameraView.Side) (min.X, max.X) = (centre.X, centre.X);
        _camera.Frame(min, max);
        if (_view == CameraView.CloseUp) _camera.Distance *= 0.5f;
        RenderFrame();
    }

    /// <summary>Draws the mesh with this RGBA8 texture (a composited recolour) as its albedo.</summary>
    public void SetTexture(int width, int height, byte[] rgba)
    {
        _renderer.SetTexture(width, height, rgba);
        RenderFrame();
    }

    /// <summary>Back to the untextured grey.</summary>
    public void ClearTexture()
    {
        _renderer.ClearTexture();
        RenderFrame();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RenderFrame();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var to = e.GetPosition(this);
        _camera.Orbit(-(float)(to.X - from.X) * 0.01f, (float)(to.Y - from.Y) * 0.01f);
        _dragFrom = to;
        if (_view != CameraView.Free) { _view = CameraView.Free; ViewChanged?.Invoke(_view); }
        RenderFrame();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _camera.Zoom(MathF.Pow(0.9f, (float)e.Delta.Y));
        RenderFrame();
    }

    void RenderFrame()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = (int)(Bounds.Width * scale);
        var height = (int)(Bounds.Height * scale);
        if (width < 1 || height < 1) return;

        var timer = Stopwatch.StartNew();
        if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96 * scale, 96 * scale), PixelFormats.Bgra8888, AlphaFormat.Opaque);
            _pixels = new byte[width * height * 4];
        }
        if (_hasMesh) _renderer.Render(width, height, _camera.ViewProjection((float)width / height), LightDirection, _pixels);

        using (var frame = _bitmap.Lock())
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(_pixels, y * rowBytes, frame.Address + y * frame.RowBytes, rowBytes);
        }
        LastFrameMs = timer.Elapsed.TotalMilliseconds;
        InvalidateVisual();
        FrameRendered?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(43, 46, 51)), new Rect(Bounds.Size));
        if (_bitmap != null) context.DrawImage(_bitmap, new Rect(Bounds.Size));
    }
}
