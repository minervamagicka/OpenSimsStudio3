using System.Numerics;
using OpenSimsStudio3.Formats.Meshes;

namespace OpenSimsStudio3.Rendering;

/// <summary>Item-browser thumbnails: the part's mesh from a slight three-quarter front view, filling a square.</summary>
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
}
