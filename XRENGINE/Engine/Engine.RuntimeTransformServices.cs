using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine;

internal sealed class EngineRuntimeTransformServices : IRuntimeTransformServices
{
    public IRuntimeTransformDebugHandle? CreateDebugHandle(object transform, Action renderCallback)
        => transform is TransformBase transformBase
            ? new TransformDebugHandle(transformBase, renderCallback)
            : null;

    public bool RenderTransformDebugInfo => Engine.EditorPreferences.Debug.RenderTransformDebugInfo;
    public bool RenderTransformLines => Engine.EditorPreferences.Debug.RenderTransformLines;
    public bool RenderTransformPoints => Engine.EditorPreferences.Debug.RenderTransformPoints;
    public bool RenderTransformCapsules => Engine.EditorPreferences.Debug.RenderTransformCapsules;
    public bool TransformCullingIsAxisAligned => Engine.Rendering.Settings.TransformCullingIsAxisAligned;
    public bool IsShadowPass => Engine.Rendering.State.IsShadowPass;
    public bool IsRenderThread => Engine.IsRenderThread;
    public ELoopType ChildRecalculationLoopType => Engine.Rendering.Settings.RecalcChildMatricesLoopType;
    public float UpdateDeltaSeconds => Engine.Time.Timer.Update.Delta;
    public float SmoothedDilatedUpdateDeltaSeconds => Engine.Time.Timer.Update.SmoothedDilatedDelta;
    public float TargetUpdateFrequency => Engine.Time.Timer.TargetUpdateFrequency;
    public float TransformReplicationKeyframeIntervalSeconds => Engine.EffectiveSettings.TransformReplicationKeyframeIntervalSec;
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

    public void LogRendering(string message)
        => Debug.Rendering(message);

    public void LogWarning(string message)
        => Debug.LogWarning(message);

    public void LogException(Exception ex, string context)
        => Debug.LogException(ex, context);

    private sealed class TransformDebugHandle : IRuntimeTransformDebugHandle
    {
        private readonly TransformDebugRenderable _owner;

        public TransformDebugHandle(TransformBase transform, Action renderCallback)
        {
            _owner = new TransformDebugRenderable(transform);
            RenderInfo3D renderInfo = RenderInfo3D.New(
                _owner,
                new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, () => renderCallback()));
            renderInfo.Layer = XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex;
            _owner.RenderInfo = renderInfo;
            UpdateWorld(transform.World);
        }

        public bool IsVisible
        {
            get => _owner.RenderInfo?.IsVisible ?? false;
            set
            {
                if (_owner.RenderInfo is not null)
                    _owner.RenderInfo.IsVisible = value;
            }
        }

        public void UpdateWorld(IRuntimeWorldContext? worldContext)
        {
            if (_owner.RenderInfo is not null)
                _owner.RenderInfo.WorldInstance = worldContext as XRWorldInstance;
        }

        public void UpdateBounds(AABB localCullingVolume, Matrix4x4 cullingOffsetMatrix)
        {
            if (_owner.RenderInfo is null)
                return;

            _owner.RenderInfo.LocalCullingVolume = localCullingVolume;
            _owner.RenderInfo.CullingOffsetMatrix = cullingOffsetMatrix;
        }

        public void Dispose()
        {
            if (_owner.RenderInfo is null)
                return;

            _owner.RenderInfo.IsVisible = false;
            _owner.RenderInfo.WorldInstance = null;
        }
    }

    private sealed class TransformDebugRenderable(TransformBase transform) : XRBase, IRenderable
    {
        public TransformBase Transform { get; } = transform;
        public RenderInfo3D? RenderInfo { get; set; }
        public RenderInfo[] RenderedObjects => RenderInfo is null ? [] : [RenderInfo];
        float IRenderableBase.TransformDepth => Transform.Depth;
    }
}
