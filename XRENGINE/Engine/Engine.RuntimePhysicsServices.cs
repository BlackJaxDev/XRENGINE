using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Physics;

namespace XREngine;

internal sealed class EngineRuntimePhysicsServices : IRuntimePhysicsServices
{
    public float FixedDeltaSeconds => Engine.FixedDelta;
    public bool IsPhysicsThread => Engine.IsPhysicsThread;
    public bool IsShuttingDown => Engine.ShuttingDown;
    public long ElapsedTicks => Engine.ElapsedTicks;
    public PhysicsVisualizeSettings VisualizeSettings => Engine.Rendering.Settings.PhysicsVisualizeSettings;
    public bool JoltDebugRenderDiagnostics => Engine.EditorPreferences.Diagnostics.General.JoltDebugRenderDiagnostics;

    public void RenderPoint(Vector3 position, ColorF4 color)
        => Engine.Rendering.Debug.RenderPoint(position, color);

    public void RenderLine(Vector3 start, Vector3 end, ColorF4 color)
        => Engine.Rendering.Debug.RenderLine(start, end, color);

    public void RenderSphere(Vector3 center, float radius, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderSphere(center, radius, solid, color);

    public void RenderCapsule(Vector3 start, Vector3 end, float radius, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderCapsule(start, end, radius, solid, color);
}
