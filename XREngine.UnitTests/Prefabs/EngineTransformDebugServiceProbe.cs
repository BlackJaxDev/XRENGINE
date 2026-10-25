using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Prefabs;

/// <summary>Uses the real debug-handle factory with fixed test-only transform settings.</summary>
internal sealed class EngineTransformDebugServiceProbe : IRuntimeTransformServices
{
    private readonly IRuntimeTransformServices _factory;

    internal EngineTransformDebugServiceProbe()
    {
        Type type = typeof(XREngine.Engine).Assembly.GetType(
            "XREngine.EngineRuntimeTransformServices", throwOnError: true)!;
        _factory = (IRuntimeTransformServices)Activator.CreateInstance(type, nonPublic: true)!;
    }

    public IRuntimeTransformDebugHandle? CreateDebugHandle(object transform,
        Action renderCallback) => _factory.CreateDebugHandle(transform, renderCallback);
    public bool RenderTransformDebugInfo => false;
    public bool RenderTransformLines => false;
    public bool RenderTransformPoints => false;
    public bool RenderTransformCapsules => false;
    public bool TransformCullingIsAxisAligned => true;
    public bool IsShadowPass => false;
    public bool IsRenderThread => true;
    public bool IsEditing => true;
    public ELoopType ChildRecalculationLoopType => ELoopType.Sequential;
    public ERenderMatrixUpdateMode RenderMatrixUpdateMode => ERenderMatrixUpdateMode.Default;
    public float UpdateDeltaSeconds => 0;
    public float DilatedUpdateDeltaSeconds => 0;
    public long UpdateDeltaTicks => 0;
    public long FixedUpdateDeltaTicks => 0;
    public float UndilatedUpdateDeltaSeconds => 0;
    public float SmoothedDilatedUpdateDeltaSeconds => 0;
    public float TargetUpdateFrequency => 60;
    public float TransformReplicationKeyframeIntervalSeconds => 1;
    public ColorF4 TransformLineColor => default;
    public ColorF4 TransformPointColor => default;
    public ColorF4 TransformCapsuleColor => default;
    public void RecordRenderMatrixChange(Delegate? listeners) { }
    public void RenderLine(Vector3 start, Vector3 end, ColorF4 color) { }
    public void RenderPoint(Vector3 position, ColorF4 color) { }
    public void RenderCapsule(Capsule capsule, ColorF4 color) { }
    public void LogRendering(string message) { }
    public void LogWarning(string message) { }
    public void LogException(Exception ex, string context) { }
}
