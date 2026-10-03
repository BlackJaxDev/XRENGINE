using System.Numerics;
using XREngine.Components.Physics;
using XREngine.Data.Colors;
using XREngine.Scene.Physics;

namespace XREngine.UnitTests.Physics.Contracts;

/// <summary>Runs small backend contract scenes synchronously on their owning test thread.</summary>
internal sealed class PhysicsContractRuntimeServices(IRuntimePhysicsServices inner) : IRuntimePhysicsServices
{
    public float FixedDeltaSeconds => 1.0f / 60.0f;
    public bool IsPhysicsThread => true;
    public bool IsShuttingDown => false;
    public long ElapsedTicks => inner.ElapsedTicks;
    public PhysicsVisualizeSettings VisualizeSettings => inner.VisualizeSettings;
    public bool JoltDebugRenderDiagnostics => false;

    public void RenderPoint(Vector3 position, ColorF4 color) => inner.RenderPoint(position, color);
    public void RenderLine(Vector3 start, Vector3 end, ColorF4 color) => inner.RenderLine(start, end, color);
    public void RenderSphere(Vector3 center, float radius, bool solid, ColorF4 color)
        => inner.RenderSphere(center, radius, solid, color);
    public void RenderCapsule(Vector3 start, Vector3 end, float radius, bool solid, ColorF4 color)
        => inner.RenderCapsule(start, end, radius, solid, color);
}
