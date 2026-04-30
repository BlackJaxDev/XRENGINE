using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Rendering.RenderGraph;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Tracks the per-camera matrix history used by the separated volumetric fog temporal pass.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_VolumetricFogHistoryPass : ViewportRenderCommand
{
    private const float CameraCutTranslationThreshold = 2.0f;
    private const float CameraCutRotationDotThreshold = 0.94f;

    private sealed class VolumetricFogTemporalState
    {
        public Matrix4x4 CurrentViewProjection = Matrix4x4.Identity;
        public Matrix4x4 PreviousViewProjection = Matrix4x4.Identity;
        public Vector3 CurrentCameraPosition;
        public Vector3 PreviousCameraPosition;
        public Vector3 CurrentCameraForward = Vector3.UnitZ;
        public Vector3 PreviousCameraForward = Vector3.UnitZ;
        public uint LastHalfWidth;
        public uint LastHalfHeight;
        public bool HistoryReady;
        public bool ForceHistoryReset;
        public bool PreparedCurrentFrame;
    }

    internal readonly struct VolumetricFogTemporalUniformData
    {
        public bool HistoryReady { get; init; }
        public Matrix4x4 PreviousViewProjection { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
    }

    public enum EPhase
    {
        Begin,
        Commit
    }

    private static readonly ConditionalWeakTable<XRCamera, VolumetricFogTemporalState> TemporalStates = new();

    public EPhase Phase { get; set; }

    protected override void Execute()
    {
        switch (Phase)
        {
            case EPhase.Begin:
                BeginTemporalFrame();
                break;
            case EPhase.Commit:
                CommitTemporalFrame();
                break;
        }
    }

    internal static bool TryGetTemporalUniformData(out VolumetricFogTemporalUniformData data)
    {
        if (CurrentRenderingPipeline is not { } instance || instance.RenderState.SceneCamera is not { } camera)
        {
            data = default;
            return false;
        }

        if (!TemporalStates.TryGetValue(camera, out VolumetricFogTemporalState? state))
        {
            data = default;
            return false;
        }

        data = new VolumetricFogTemporalUniformData
        {
            HistoryReady = state.HistoryReady && !state.ForceHistoryReset,
            PreviousViewProjection = state.PreviousViewProjection,
            Width = state.LastHalfWidth,
            Height = state.LastHalfHeight,
        };
        return true;
    }

    internal static void ResetHistory(XRRenderPipelineInstance? instance)
    {
        XRCamera? camera = instance?.RenderState.SceneCamera
            ?? instance?.RenderState.RenderingCamera
            ?? instance?.LastSceneCamera
            ?? instance?.LastRenderingCamera;
        ResetHistory(camera);
    }

    internal static void ResetHistory(XRCamera? camera)
    {
        if (camera is null || !TemporalStates.TryGetValue(camera, out VolumetricFogTemporalState? state))
            return;

        ResetState(state);
    }

    private static void BeginTemporalFrame()
    {
        if (!TryGetActiveState(out XRRenderPipelineInstance? instance, out VolumetricFogTemporalState? state))
            return;

        XRCamera camera = instance.RenderState.SceneCamera;
        uint halfWidth = 1u;
        uint halfHeight = 1u;
        var viewport = instance.RenderState.WindowViewport;
        if (viewport is not null)
        {
            halfWidth = Math.Max(1u, (uint)viewport.InternalWidth / 2u);
            halfHeight = Math.Max(1u, (uint)viewport.InternalHeight / 2u);
        }

        bool sizeChanged = halfWidth != state.LastHalfWidth || halfHeight != state.LastHalfHeight;
        if (sizeChanged)
        {
            state.LastHalfWidth = halfWidth;
            state.LastHalfHeight = halfHeight;
            state.HistoryReady = false;
        }

        state.CurrentViewProjection = camera.ViewProjectionMatrixUnjittered;
        state.CurrentCameraPosition = camera.Transform.RenderTranslation;
        state.CurrentCameraForward = NormalizeOrForward(camera.Transform.RenderForward);
        state.ForceHistoryReset = sizeChanged || !state.HistoryReady || IsCameraCut(state);
        state.PreparedCurrentFrame = true;
    }

    private static void CommitTemporalFrame()
    {
        if (!TryGetActiveState(out _, out VolumetricFogTemporalState? state) || !state.PreparedCurrentFrame)
            return;

        state.PreparedCurrentFrame = false;
        state.PreviousViewProjection = state.CurrentViewProjection;
        state.PreviousCameraPosition = state.CurrentCameraPosition;
        state.PreviousCameraForward = state.CurrentCameraForward;
        state.HistoryReady = true;
        state.ForceHistoryReset = false;
    }

    private static bool TryGetActiveState(out XRRenderPipelineInstance? instance, out VolumetricFogTemporalState? state)
    {
        instance = ActivePipelineInstance;
        if (instance?.RenderState.SceneCamera is not { } camera)
        {
            state = null;
            return false;
        }

        state = TemporalStates.GetValue(camera, _ => new VolumetricFogTemporalState());
        return true;
    }

    private static bool IsCameraCut(VolumetricFogTemporalState state)
    {
        if (!state.HistoryReady)
            return true;

        float translationDeltaSq = Vector3.DistanceSquared(state.CurrentCameraPosition, state.PreviousCameraPosition);
        if (translationDeltaSq > CameraCutTranslationThreshold * CameraCutTranslationThreshold)
            return true;

        float directionDot = Vector3.Dot(
            NormalizeOrForward(state.CurrentCameraForward),
            NormalizeOrForward(state.PreviousCameraForward));
        return directionDot < CameraCutRotationDotThreshold;
    }

    private static Vector3 NormalizeOrForward(Vector3 value)
    {
        float lengthSq = value.LengthSquared();
        return lengthSq > 1e-8f ? value / MathF.Sqrt(lengthSq) : Vector3.UnitZ;
    }

    private static void ResetState(VolumetricFogTemporalState state)
    {
        state.HistoryReady = false;
        state.ForceHistoryReset = true;
        state.PreparedCurrentFrame = false;
        state.CurrentViewProjection = Matrix4x4.Identity;
        state.PreviousViewProjection = Matrix4x4.Identity;
        state.CurrentCameraPosition = Vector3.Zero;
        state.PreviousCameraPosition = Vector3.Zero;
        state.CurrentCameraForward = Vector3.UnitZ;
        state.PreviousCameraForward = Vector3.UnitZ;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
    }
}