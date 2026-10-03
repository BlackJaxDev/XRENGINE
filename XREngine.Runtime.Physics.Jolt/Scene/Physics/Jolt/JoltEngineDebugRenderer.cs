using System.Numerics;
using JoltPhysicsSharp;
using XREngine.Data.Colors;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Scene.Physics.Jolt;

/// <summary>
/// Bridges Jolt's native debug renderer to the engine's transient debug primitives.
/// Jolt performs shape/constraint tessellation and invokes these two callbacks.
/// </summary>
internal sealed class JoltEngineDebugRenderer : DebugRenderer
{
    private PhysicsDebugFrameWriter? _writer;

    public void BeginFrame(PhysicsDebugFrameWriter writer)
        => _writer = writer;

    public void EndFrame()
        => _writer = null;

    protected override void DrawLine(Vector3 from, Vector3 to, JoltColor color)
    {
        if (_writer is not { } writer)
            return;

        Vector4 rgba = color.ToVector4();
        uint packed = PhysicsDebugColor.Pack(new ColorF4(rgba.X, rgba.Y, rgba.Z, rgba.W));
        writer.AddLine(new PhysicsDebugLine(from, to, packed));
    }

    protected override void DrawText3D(Vector3 position, string? text, JoltColor color, float height)
    {
        if (_writer is not { } writer)
            return;

        Vector4 rgba = color.ToVector4();
        PhysicsDebugGeometryWriter.AddSphere(
            writer,
            position,
            MathF.Max(height * 0.04f, 0.005f),
            PhysicsDebugColor.Pack(new ColorF4(rgba.X, rgba.Y, rgba.Z, rgba.W)));
    }
}
