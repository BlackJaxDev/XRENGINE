using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;

namespace XREngine.Scene.Transforms;

public interface IRuntimeTransformServices
{
    IRuntimeTransformDebugHandle? CreateDebugHandle(object transform, Action renderCallback);
    bool RenderTransformDebugInfo { get; }
    bool RenderTransformLines { get; }
    bool RenderTransformPoints { get; }
    bool RenderTransformCapsules { get; }
    bool TransformCullingIsAxisAligned { get; }
    bool IsShadowPass { get; }
    bool IsRenderThread { get; }
    ELoopType ChildRecalculationLoopType { get; }
    float UpdateDeltaSeconds { get; }
    float SmoothedDilatedUpdateDeltaSeconds { get; }
    float TargetUpdateFrequency { get; }
    float TransformReplicationKeyframeIntervalSeconds { get; }
    ColorF4 TransformLineColor { get; }
    ColorF4 TransformPointColor { get; }
    ColorF4 TransformCapsuleColor { get; }
    void RecordRenderMatrixChange(Delegate? listeners);
    void RenderLine(Vector3 start, Vector3 end, ColorF4 color);
    void RenderPoint(Vector3 position, ColorF4 color);
    void RenderCapsule(Capsule capsule, ColorF4 color);
    void LogRendering(string message);
    void LogWarning(string message);
    void LogException(Exception ex, string context);
}

public interface IRuntimeTransformDebugHandle : IDisposable
{
    bool IsVisible { get; set; }
    void UpdateWorld(IRuntimeWorldContext? worldContext);
    void UpdateBounds(AABB localCullingVolume, Matrix4x4 cullingOffsetMatrix);
}

public static class RuntimeTransformServices
{
    public static IRuntimeTransformServices? Current { get; set; }
}
