using System.Numerics;
using JoltPhysicsSharp;
using XREngine.Data.Colors;

namespace XREngine.Scene.Physics.Jolt;

/// <summary>
/// Bridges Jolt's native debug renderer to the engine's transient debug primitives.
/// Jolt performs shape/constraint tessellation and invokes these two callbacks.
/// </summary>
internal sealed class JoltEngineDebugRenderer : DebugRenderer
{
    protected override void DrawLine(Vector3 from, Vector3 to, JoltColor color)
    {
        Vector4 rgba = color.ToVector4();
        RuntimePhysicsServices.Current.RenderLine(from, to, new ColorF4(rgba.X, rgba.Y, rgba.Z, rgba.W));
    }

    protected override void DrawText3D(Vector3 position, string? text, JoltColor color, float height)
    {
        Vector4 rgba = color.ToVector4();
        RuntimePhysicsServices.Current.RenderSphere(
            position,
            MathF.Max(height * 0.04f, 0.005f),
            false,
            new ColorF4(rgba.X, rgba.Y, rgba.Z, rgba.W));
    }
}
