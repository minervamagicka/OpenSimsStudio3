using System.Numerics;

namespace OpenSimsStudio3.Rendering;

/// <summary>A turntable camera around a target point (Y-up, like the game's mesh data).</summary>
public sealed class OrbitCamera
{
    public Vector3 Target { get; set; }
    public float Distance { get; set; } = 2f;
    public float Yaw { get; set; } = 0.5f;     // radians around Y
    public float Pitch { get; set; } = 0.15f;  // radians above the horizon
    public float FieldOfView { get; set; } = MathF.PI / 5;

    /// <summary>Frames an axis-aligned box so it fills most of the view.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        Target = (min + max) / 2;
        var radius = Math.Max(Vector3.Distance(min, max) / 2, 0.01f);
        Distance = radius / MathF.Sin(FieldOfView / 2) * 1.1f;
    }

    public void Orbit(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.5f, 1.5f);
    }

    public void Zoom(float factor) => Distance = Math.Clamp(Distance * factor, 0.01f, 1000f);

    public Vector3 Eye => Target + Distance * new Vector3(
        MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));

    /// <summary>View-projection for Vulkan: depth 0..1, and Y flipped because Vulkan's clip space points Y down.</summary>
    public Matrix4x4 ViewProjection(float aspect)
    {
        var view = Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);
        var near = Math.Max(Distance * 0.01f, 0.001f);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, near, Distance * 10 + 10);
        projection.M22 = -projection.M22;
        return view * projection;
    }
}
