using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine;

internal sealed class EngineRuntimeTransformServices : IRuntimeTransformServices
{
    public bool RenderTransformDebugInfo => Engine.EditorPreferences.Debug.RenderTransformDebugInfo;
    public bool RenderTransformLines => Engine.EditorPreferences.Debug.RenderTransformLines;
    public bool RenderTransformPoints => Engine.EditorPreferences.Debug.RenderTransformPoints;
    public bool RenderTransformCapsules => Engine.EditorPreferences.Debug.RenderTransformCapsules;
    public bool TransformCullingIsAxisAligned => Engine.Rendering.Settings.TransformCullingIsAxisAligned;
    public bool IsShadowPass => Engine.Rendering.State.IsShadowPass;
    public ELoopType ChildRecalculationLoopType => Engine.Rendering.Settings.RecalcChildMatricesLoopType;
    public ColorF4 TransformLineColor => Engine.EditorPreferences.Theme.TransformLineColor;
    public ColorF4 TransformPointColor => Engine.EditorPreferences.Theme.TransformPointColor;
    public ColorF4 TransformCapsuleColor => Engine.EditorPreferences.Theme.TransformCapsuleColor;

    public void RecordRenderMatrixChange(Delegate? listeners)
        => Engine.Rendering.Stats.RecordRenderMatrixChange(listeners);

    public void RenderLine(Vector3 start, Vector3 end, ColorF4 color)
        => Engine.Rendering.Debug.RenderLine(start, end, color);

    public void RenderPoint(Vector3 position, ColorF4 color)
        => Engine.Rendering.Debug.RenderPoint(position, color);

    public void RenderCapsule(Capsule capsule, ColorF4 color)
        => Engine.Rendering.Debug.RenderCapsule(capsule, color);

    public void LogWarning(string message)
        => Debug.LogWarning(message);

    public void LogException(Exception ex, string context)
        => Debug.LogException(ex, context);
}
