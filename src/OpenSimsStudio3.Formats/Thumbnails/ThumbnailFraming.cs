using System.Numerics;

namespace OpenSimsStudio3.Formats.Thumbnails;

/// <summary>A square thumbnail's camera, with its view-projection in the renderer's convention.</summary>
public sealed record ThumbnailCamera(Vector3 Eye, Vector3 Target, float FieldOfView, float Near, float Far)
{
    /// <summary>
    /// Right-handed view, perspective with depth 0..1 and Y flipped (Vulkan clip space points Y down), row-vector
    /// memory order: the same convention as the viewport's OrbitCamera.
    /// </summary>
    public Matrix4x4 ViewProjection
    {
        get
        {
            var view = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, 1f, Near, Far);
            projection.M22 = -projection.M22;
            return view * projection;
        }
    }

    /// <summary>A point in normalised device coordinates (x right, y down, -1..1).</summary>
    public Vector2 Project(Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), ViewProjection);
        return new Vector2(clip.X / clip.W, clip.Y / clip.W);
    }
}

/// <summary>
/// Framing for rendered CAS thumbnails, pure maths so it is tested without a GPU. The item is seen from the front
/// (TS3 meshes face +Z) and a little above, through a narrow lens so it reads almost flat like a catalogue shot, and
/// is fitted so its bounding box's projection fills the square less <see cref="Margin" /> on its longer side, centred.
/// The look is OSS3's choice (README.md), not measured from EA's thumbnails.
/// </summary>
public static class ThumbnailFraming
{
    public const float Yaw = 0f;
    public const float Pitch = 0.08f;
    public const float FieldOfView = MathF.PI / 9;  // 20°
    /// <summary>Space left on each side, as a fraction of the half-width (0.08 = 4% of the side).</summary>
    public const float Margin = 0.08f;

    public static ThumbnailCamera Frame(Vector3 min, Vector3 max, float yaw = Yaw, float pitch = Pitch, float fieldOfView = FieldOfView, float margin = Margin)
    {
        var corners = Corners(min, max);
        var toEye = new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toEye));
        var up = Vector3.Cross(toEye, right);
        var limit = MathF.Tan(fieldOfView / 2) * (1 - margin);
        var target = (min + max) / 2;
        var radius = Math.Max(Vector3.Distance(min, max) / 2, 1e-3f);

        // The nearest distance at which every corner fits: |x| <= limit · depth, depth = distance - z (z toward the eye).
        // Then shift the target to centre the projected box and fit again; a few rounds settle it.
        float distance = 0;
        for (var round = 0; round < 12; round++)
        {
            distance = 0;
            foreach (var c in corners)
            {
                var d = c - target;
                var z = Vector3.Dot(d, toEye);
                distance = Math.Max(distance, z + Math.Max(MathF.Abs(Vector3.Dot(d, right)), MathF.Abs(Vector3.Dot(d, up))) / limit);
            }
            distance = Math.Max(distance, radius * 0.05f);
            // Centre: the midpoint of the projected extents, in tangent units, moved back to world units at the target.
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var c in corners)
            {
                var d = c - target;
                var depth = distance - Vector3.Dot(d, toEye);
                var x = Vector3.Dot(d, right) / depth;
                var y = Vector3.Dot(d, up) / depth;
                (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
            }
            var shift = right * ((minX + maxX) / 2 * distance) + up * ((minY + maxY) / 2 * distance);
            if (shift.Length() < radius * 1e-5f || round == 11) break;
            target += shift;
        }

        var near = Math.Max((distance - radius) * 0.5f, distance * 0.01f);
        return new ThumbnailCamera(target + toEye * distance, target, fieldOfView, near, distance + radius * 2);
    }

    /// <summary>The projected bounding box of the box's corners, in normalised device coordinates.</summary>
    public static (Vector2 Min, Vector2 Max) ProjectedBounds(ThumbnailCamera camera, Vector3 min, Vector3 max)
    {
        var points = Corners(min, max).Select(camera.Project).ToList();
        return (new Vector2(points.Min(p => p.X), points.Min(p => p.Y)), new Vector2(points.Max(p => p.X), points.Max(p => p.Y)));
    }

    static Vector3[] Corners(Vector3 min, Vector3 max) =>
    [
        new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
        new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z),
    ];
}
