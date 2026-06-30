using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Scene.Environment;
using XREngine.Rendering.RenderGraph;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Tracks the per-camera matrix and active-atmosphere history used by atmospheric temporal reprojection.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_AtmosphereHistoryPass : ViewportRenderCommand
{
    private const float CameraCutTranslationThreshold = 2.0f;
    private const float CameraCutRotationDotThreshold = 0.94f;

    private sealed class AtmosphereTemporalState
    {
        public Matrix4x4 CurrentViewProjection = Matrix4x4.Identity;
        public Matrix4x4 PreviousViewProjection = Matrix4x4.Identity;
        public Vector3 CurrentCameraPosition;
        public Vector3 PreviousCameraPosition;
        public Vector3 CurrentCameraForward = Vector3.UnitZ;
        public Vector3 PreviousCameraForward = Vector3.UnitZ;
        public AtmosphericScatteringComponent? CurrentAtmosphere;
        public AtmosphericScatteringComponent? PreviousAtmosphere;
        public int CurrentAtmosphereRevision = -1;
        public int PreviousAtmosphereRevision = -1;
        public uint LastHalfWidth;
        public uint LastHalfHeight;
        public bool HistoryReady;
        public bool ForceHistoryReset;
        public bool PreparedCurrentFrame;
    }

    internal readonly struct AtmosphereTemporalUniformData
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

    private static readonly ConditionalWeakTable<XRCamera, AtmosphereTemporalState> TemporalStates = new();

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

    internal static bool TryGetTemporalUniformData(out AtmosphereTemporalUniformData data)
    {
        if (CurrentRenderingPipeline is not { } instance || instance.RenderState.SceneCamera is not { } camera)
        {
            data = default;
            return false;
        }

        if (!TemporalStates.TryGetValue(camera, out AtmosphereTemporalState? state))
        {
            data = default;
            return false;
        }

        data = new AtmosphereTemporalUniformData
        {
            HistoryReady = TryUseTemporalAtmosphereHistory(out _, out _)
                && state.HistoryReady
                && !state.ForceHistoryReset,
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
        if (camera is null || !TemporalStates.TryGetValue(camera, out AtmosphereTemporalState? state))
            return;

        ResetState(state);
    }

    private static void BeginTemporalFrame()
    {
        if (!TryGetActiveState(out XRRenderPipelineInstance? instance, out AtmosphereTemporalState? state))
            return;

        XRCamera? camera = instance.RenderState.SceneCamera;
        if (camera is null)
            return;

        uint halfWidth = 1u;
        uint halfHeight = 1u;
        var viewport = instance.RenderState.WindowViewport;
        if (viewport is not null)
        {
            halfWidth = Math.Max(1u, (uint)viewport.InternalWidth / 2u);
            halfHeight = Math.Max(1u, (uint)viewport.InternalHeight / 2u);
        }

        if (!TryUseTemporalAtmosphereHistory(out EVrTemporalHistoryPolicy policy, out string historyDisabledReason))
        {
            state.LastHalfWidth = halfWidth;
            state.LastHalfHeight = halfHeight;
            ResetState(state);
            Debug.RenderingEvery(
                $"AtmosphereHistory.VrTemporalDisabled.{policy}",
                TimeSpan.FromSeconds(5),
                "[AtmosphereHistory] VR atmosphere temporal history disabled policy={0}: {1}",
                policy,
                historyDisabledReason);
            return;
        }

        bool sizeChanged = halfWidth != state.LastHalfWidth || halfHeight != state.LastHalfHeight;
        if (sizeChanged)
        {
            state.LastHalfWidth = halfWidth;
            state.LastHalfHeight = halfHeight;
            state.HistoryReady = false;
        }

        AtmosphericScatteringSettings.TrySelectActiveAtmosphereForCurrentFrame(out AtmosphericScatteringComponent? activeAtmosphere);
        state.CurrentAtmosphere = activeAtmosphere;
        state.CurrentAtmosphereRevision = activeAtmosphere?.Revision ?? -1;

        state.CurrentViewProjection = VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData)
            ? temporalData.CurrViewProjection
            : camera.ViewProjectionMatrix;
        state.CurrentCameraPosition = camera.Transform.RenderTranslation;
        state.CurrentCameraForward = NormalizeOrForward(camera.Transform.RenderForward);
        state.ForceHistoryReset = sizeChanged
            || !state.HistoryReady
            || IsCameraCut(state)
            || ActiveAtmosphereChanged(state);
        state.PreparedCurrentFrame = true;
    }

    private static void CommitTemporalFrame()
    {
        if (!TryUseTemporalAtmosphereHistory(out _, out _))
            return;

        if (!TryGetActiveState(out _, out AtmosphereTemporalState? state) || !state.PreparedCurrentFrame)
            return;

        state.PreparedCurrentFrame = false;
        state.PreviousViewProjection = state.CurrentViewProjection;
        state.PreviousCameraPosition = state.CurrentCameraPosition;
        state.PreviousCameraForward = state.CurrentCameraForward;
        state.PreviousAtmosphere = state.CurrentAtmosphere;
        state.PreviousAtmosphereRevision = state.CurrentAtmosphereRevision;
        state.HistoryReady = true;
        state.ForceHistoryReset = false;
    }

    private static bool TryGetActiveState(
        [NotNullWhen(true)] out XRRenderPipelineInstance? instance,
        [NotNullWhen(true)] out AtmosphereTemporalState? state)
    {
        instance = ActivePipelineInstance;
        if (instance?.RenderState.SceneCamera is not { } camera)
        {
            state = null;
            return false;
        }

        state = TemporalStates.GetValue(camera, _ => new AtmosphereTemporalState());
        return true;
    }

    internal static bool TryUseTemporalAtmosphereHistory(
        out EVrTemporalHistoryPolicy policy,
        out string reason)
    {
        policy = VPRC_TemporalAccumulationPass.ResolveHistoryIsolationPolicy(out string temporalReason);
        if (IsTemporalHistoryPolicyDisabled(policy))
        {
            reason = temporalReason;
            return false;
        }

        if (!RuntimeEngine.VRState.IsInVR && !RuntimeEngine.Rendering.State.IsStereoPass)
        {
            reason = "mono atmosphere temporal history is keyed by camera.";
            return true;
        }

        reason = "VR atmosphere temporal history disabled until half-resolution atmosphere textures and shaders are stereo array-layered.";
        return false;
    }

    private static bool IsTemporalHistoryPolicyDisabled(EVrTemporalHistoryPolicy policy)
        => policy is EVrTemporalHistoryPolicy.Disabled
            or EVrTemporalHistoryPolicy.DisabledPerEyeSwapchain
            or EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain;

    private static bool IsCameraCut(AtmosphereTemporalState state)
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

    private static bool ActiveAtmosphereChanged(AtmosphereTemporalState state)
        => !ReferenceEquals(state.CurrentAtmosphere, state.PreviousAtmosphere)
        || state.CurrentAtmosphereRevision != state.PreviousAtmosphereRevision;

    private static Vector3 NormalizeOrForward(Vector3 value)
    {
        float lengthSq = value.LengthSquared();
        return lengthSq > 1e-8f ? value / MathF.Sqrt(lengthSq) : Vector3.UnitZ;
    }

    private static void ResetState(AtmosphereTemporalState state)
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
        state.CurrentAtmosphere = null;
        state.PreviousAtmosphere = null;
        state.CurrentAtmosphereRevision = -1;
        state.PreviousAtmosphereRevision = -1;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
    }
}
