using System.Numerics;
using XREngine.Data.Colors;

namespace XREngine.Scene.Physics;

/// <summary>
/// Supplies host timing and transient debug drawing required by physics backends.
/// </summary>
public interface IRuntimePhysicsServices
{
    float FixedDeltaSeconds { get; }
    bool IsPhysicsThread => true;
    bool IsShuttingDown => false;
    long ElapsedTicks => 0L;
    PhysicsVisualizeSettings VisualizeSettings { get; }
    bool JoltDebugRenderDiagnostics { get; }

    void RenderLine(Vector3 start, Vector3 end, ColorF4 color);
    void RenderSphere(Vector3 center, float radius, bool solid, ColorF4 color);
    void RenderCapsule(Vector3 start, Vector3 end, float radius, bool solid, ColorF4 color);
}

/// <summary>
/// Process-wide physics host services. Application composition replaces the no-op default.
/// </summary>
public static class RuntimePhysicsServices
{
    private static IRuntimePhysicsServices _current = new DefaultRuntimePhysicsServices();

    public static IRuntimePhysicsServices Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    private sealed class DefaultRuntimePhysicsServices : IRuntimePhysicsServices
    {
        private readonly PhysicsVisualizeSettings _visualizeSettings = new();

        public float FixedDeltaSeconds => 1.0f / 60.0f;
        public PhysicsVisualizeSettings VisualizeSettings => _visualizeSettings;
        public bool JoltDebugRenderDiagnostics => false;

        public void RenderLine(Vector3 start, Vector3 end, ColorF4 color) { }
        public void RenderSphere(Vector3 center, float radius, bool solid, ColorF4 color) { }
        public void RenderCapsule(Vector3 start, Vector3 end, float radius, bool solid, ColorF4 color) { }
    }
}
