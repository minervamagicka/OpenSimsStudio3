using System.Numerics;
using OpenSimsStudio3.Formats.Compositing;
using OpenSimsStudio3.Formats.Meshes;
using OpenSimsStudio3.Formats.Thumbnails;

namespace OpenSimsStudio3.Rendering;

/// <summary>
/// Item-browser thumbnails (the part's mesh from a slight three-quarter front view, filling a square) and rendered CAS
/// thumbnails for a recolour (framing in Formats/Thumbnails/ThumbnailFraming, tested there).
/// </summary>
public static class Thumbnails
{
    static readonly Vector3 Light = Vector3.Normalize(new Vector3(0.35f, 0.75f, 0.85f));

    /// <summary>Renders <paramref name="mesh" /> into size x size BGRA pixels (top row first).</summary>
    public static void Draw(OffscreenMeshRenderer renderer, GeomMesh mesh, int size, Span<byte> destination)
    {
        renderer.SetMesh(mesh);
        var (min, max) = mesh.Bounds();
        // TS3 meshes face +Z; a small yaw shows depth without hiding the front.
        var camera = new OrbitCamera { Yaw = 0.35f, Pitch = 0.08f };
        camera.Frame(min, max);
        camera.Distance *= 0.97f;
        renderer.Render(size, size, camera.ViewProjection(1f), Light, destination);
    }

    /// <summary>Side of the square a recolour thumbnail is rendered at, before it is shrunk to each CAS size.</summary>
    public const int RecolourRenderSize = 512;

    /// <summary>
    /// Renders <paramref name="mesh" /> with the composited recolour <paramref name="texture" /> (premultiplied RGBA,
    /// or null for untextured grey) on a transparent background, framed like a CAS thumbnail. Returns straight RGBA
    /// pixels, top row first. Changes the renderer's mesh, texture, clear colour and cutout setting.
    /// </summary>
    public static DdsImage RenderRecolour(OffscreenMeshRenderer renderer, GeomMesh mesh, CompositedTexture? texture, int size = RecolourRenderSize)
    {
        renderer.SetMesh(mesh);
        if (texture != null) renderer.SetTexture(texture.Width, texture.Height, texture.Rgba);
        else renderer.ClearTexture();
        renderer.AlphaCutout = true;
        renderer.ClearColor = Vector4.Zero;
        var (min, max) = mesh.Bounds();
        var camera = ThumbnailFraming.Frame(min, max);
        var bgra = new byte[size * size * 4];
        renderer.Render(size, size, camera.ViewProjection, Light, bgra);
        // BGRA to RGBA; the mesh is drawn opaque, so colour needs no un-premultiplying.
        for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return new DdsImage(size, size, bgra);
    }
}
