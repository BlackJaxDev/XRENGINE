using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Handles all temporal accumulation stages (begin, accumulate, commit) without relying on manual commands.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_TemporalAccumulationPass : ViewportRenderCommand
{
    private const float TaaJitterScaleInTexels = 0.35f;
    private const float TsrJitterScaleInTexels = 0.20f;
    private const string TemporalInputCopyScopeName = "Temporal Copy Forward->Input";
    private const string TemporalAccumulationScopeName = "Temporal Accumulation shader=TemporalAccumulation.fs";
    private const string TemporalHistoryColorScopeName = "Temporal History Color";
    private const string TemporalHistoryDepthScopeName = "Temporal History Depth";
    private const string TemporalHistoryExposureScopeName = "Temporal History Exposure";
    private const string TemporalHistoryPassthroughScopeName = "Temporal History Passthrough Color+Depth";
    private const string TemporalInputCopyPassName = "Temporal_InputCopy";
    private const string TemporalAccumulationResolvePassName = "Temporal_AccumulationResolve";
    private const string TemporalHistoryColorCopyPassName = "Temporal_HistoryColorCopy";
    private const string TemporalHistoryDepthCopyPassName = "Temporal_HistoryDepthCopy";
    private const string TemporalHistoryExposureCopyPassName = "Temporal_HistoryExposureCopy";
    private const string TemporalHistoryPassthroughPassName = "Temporal_HistoryPassthrough";

    private static readonly Vector2[] TemporalJitterSequence =
    [
        new(0.125f, -0.375f),
        new(-0.125f, 0.375f),
        new(0.625f, 0.125f),
        new(0.375f, -0.625f),
        new(-0.625f, 0.625f),
        new(-0.875f, -0.125f),
        new(0.375f, 0.875f),
        new(0.875f, -0.875f)
    ];

    private sealed class TemporalState
    {
        public uint HaltonIndex = 1;
        public Vector2 CurrentJitter = Vector2.Zero;
        public Vector2 PreviousJitter = Vector2.Zero;
        public Matrix4x4 CurrProjection = Matrix4x4.Identity;
        public Matrix4x4 CurrInverseProjection = Matrix4x4.Identity;
        public Matrix4x4 CurrViewProjection = Matrix4x4.Identity;
        public Matrix4x4 CurrViewProjectionUnjittered = Matrix4x4.Identity;
        public Matrix4x4 CurrInverseViewProjection = Matrix4x4.Identity;
        public Matrix4x4 PrevViewProjection = Matrix4x4.Identity;
        public Matrix4x4 PrevViewProjectionUnjittered = Matrix4x4.Identity;
        public Matrix4x4 PrevInverseViewProjection = Matrix4x4.Identity;
        public bool HistoryReady;
        public bool HistoryExposureReady;
        public bool PendingHistoryReady;
        public StateObject? ActiveJitterHandle;
        public uint LastInternalWidth;
        public uint LastInternalHeight;
        public uint LastFullWidth;
        public uint LastFullHeight;
        public EAntiAliasingMode LastAntiAliasingMode = EAntiAliasingMode.None;
        public ulong LastFrameCount = 0;
    }

    internal readonly struct TemporalUniformData
    {
        public bool HistoryReady { get; init; }
        public Matrix4x4 PrevViewProjection { get; init; }
        public Matrix4x4 PrevViewProjectionUnjittered { get; init; }
        public Matrix4x4 CurrProjection { get; init; }
        public Matrix4x4 CurrInverseProjection { get; init; }
        public Matrix4x4 CurrInverseViewProjection { get; init; }
        public Matrix4x4 CurrViewProjection { get; init; }
        public Matrix4x4 CurrViewProjectionUnjittered { get; init; }
        public Vector2 CurrentJitter { get; init; }
        public Vector2 PreviousJitter { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
        public bool HistoryExposureReady { get; init; }
    }

    // Key by XRCamera instead of PipelineInstance to support multi-view/stereo rendering correctly
    private static readonly ConditionalWeakTable<XRCamera, TemporalState> TemporalStates = new();

    public enum EPhase
    {
        Begin,
        Accumulate,
        /// <summary>
        /// Disposes the active jitter handle so that subsequent rendering
        /// (e.g. transparent / masked forward passes) uses an unjittered projection.
        /// The jitter values are preserved in the temporal state for the Commit phase.
        /// </summary>
        PopJitter,
        Commit
    }

    public EPhase Phase { get; set; } = EPhase.Accumulate;

    public string ForwardFBOName { get; set; } = DefaultRenderPipeline.ForwardPassFBOName;
    public string TemporalInputFBOName { get; set; } = DefaultRenderPipeline.TemporalInputFBOName;
    public string TemporalAccumulationFBOName { get; set; } = DefaultRenderPipeline.TemporalAccumulationFBOName;
    public string HistoryColorFBOName { get; set; } = DefaultRenderPipeline.HistoryCaptureFBOName;
    public string HistoryExposureFBOName { get; set; } = DefaultRenderPipeline.HistoryExposureFBOName;

    public override string GpuProfilingName
        => $"{base.GpuProfilingName}[{Phase}]";

    public VPRC_TemporalAccumulationPass ConfigureAccumulationTargets(
        string forwardFboName,
        string temporalInputFboName,
        string accumulationFboName,
        string historyColorFboName,
        string historyExposureFboName)
    {
        ForwardFBOName = forwardFboName;
        TemporalInputFBOName = temporalInputFboName;
        TemporalAccumulationFBOName = accumulationFboName;
        HistoryColorFBOName = historyColorFboName;
        HistoryExposureFBOName = historyExposureFboName;
        return this;
    }

    protected override void Execute()
    {
        if (ParentPipeline is null)
            return;

        switch (Phase)
        {
            case EPhase.Begin:
                //Debug.Out("[Temporal] Begin phase");
                BeginTemporalFrame();
                break;
            case EPhase.Accumulate:
                //Debug.Out("[Temporal] Accumulate phase");
                ExecuteAccumulation();
                break;
            case EPhase.PopJitter:
                PopActiveJitter();
                break;
            case EPhase.Commit:
                //Debug.Out("[Temporal] Commit phase");
                CommitTemporalFrame();
                break;
        }
    }

    private void ExecuteAccumulation()
    {
        var renderer = AbstractRenderer.Current;
        if (renderer is null)
        {
            Debug.Rendering("[Temporal] Accumulate skipped: renderer unavailable.");
            return;
        }

        var forwardFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(ForwardFBOName);
        var historyColorFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(HistoryColorFBOName);

        if (forwardFBO is null || historyColorFBO is null)
        {
            Debug.Rendering("[Temporal] Accumulate skipped: missing FBO(s)." +
                $" forward={(forwardFBO!=null)} historyColor={(historyColorFBO!=null)}");
            SetHistoryExposureReady(false);
            return;
        }

        EAntiAliasingMode antiAliasingMode = ResolveAntiAliasingMode();
        bool shouldAccumulate = ShouldRunInternalAccumulation(antiAliasingMode);
        if (shouldAccumulate)
        {
            var temporalInputFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(TemporalInputFBOName);
            var accumulationFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(TemporalAccumulationFBOName);
            var historyExposureFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(HistoryExposureFBOName);
            if (temporalInputFBO is null || accumulationFBO is null || historyExposureFBO is null)
            {
                Debug.Rendering("[Temporal] Accumulate skipped: missing accumulation FBO(s)." +
                    $" temporalInput={(temporalInputFBO!=null)} accumulation={(accumulationFBO!=null)} historyExposure={(historyExposureFBO!=null)}");
                SetHistoryExposureReady(false);
                return;
            }

            SetHistoryExposureReady(true);

            using (RenderPipelineGpuProfiler.Instance.StartScope(TemporalInputCopyScopeName))
            {
                using IDisposable? passScope = PushRenderGraphPass(TemporalInputCopyPassName);
                renderer.BlitFBOToFBO(
                    forwardFBO,
                    temporalInputFBO,
                    EReadBufferMode.ColorAttachment0,
                    true,
                    false,
                    false,
                    false);
            }

            //Debug.Out("[Temporal] Rendering accumulation FBO.");
            using (RenderPipelineGpuProfiler.Instance.StartScope(TemporalAccumulationScopeName))
            using (IDisposable? passScope = PushRenderGraphPass(TemporalAccumulationResolvePassName))
            {
                accumulationFBO.Render(accumulationFBO);
            }

            // TAA history must store the resolved color, not the raw forward pass,
            // otherwise accumulation never becomes recursive and tuning the temporal
            // weights has only a weak one-frame effect.
            using (RenderPipelineGpuProfiler.Instance.StartScope(TemporalHistoryColorScopeName))
            {
                using IDisposable? passScope = PushRenderGraphPass(TemporalHistoryColorCopyPassName);
                renderer.BlitFBOToFBO(
                    accumulationFBO,
                    historyColorFBO,
                    EReadBufferMode.ColorAttachment0,
                    true,
                    false,
                    false,
                    false);
            }

            // Preserve the latest scene depth from the forward pass for depth-based
            // reprojection rejection next frame.
            using (RenderPipelineGpuProfiler.Instance.StartScope(TemporalHistoryDepthScopeName))
            {
                using IDisposable? passScope = PushRenderGraphPass(TemporalHistoryDepthCopyPassName);
                renderer.BlitFBOToFBO(
                    forwardFBO,
                    historyColorFBO,
                    EReadBufferMode.ColorAttachment0,
                    false,
                    true,
                    false,
                    false);
            }

            using (RenderPipelineGpuProfiler.Instance.StartScope(TemporalHistoryExposureScopeName))
            {
                using IDisposable? passScope = PushRenderGraphPass(TemporalHistoryExposureCopyPassName);
                renderer.BlitFBOToFBO(
                    accumulationFBO,
                    historyExposureFBO,
                    EReadBufferMode.ColorAttachment1,
                    true,
                    false,
                    false,
                    false);
            }
        }
        else
        {
            SetHistoryExposureReady(false);

            using (RenderPipelineGpuProfiler.Instance.StartScope(TemporalHistoryPassthroughScopeName))
            {
                using IDisposable? passScope = PushRenderGraphPass(TemporalHistoryPassthroughPassName);
                renderer.BlitFBOToFBO(
                    forwardFBO,
                    historyColorFBO,
                    EReadBufferMode.ColorAttachment0,
                    true,
                    true,
                    false,
                    false);
            }
        }

        // Always flag history as captured - motion blur depends on view-projection tracking
        // even when TAA/jitter is disabled.
        //Debug.Out("[Temporal] Flagging history captured.");
        FlagTemporalHistoryCaptured();
    }

    private IDisposable? PushRenderGraphPass(string passName)
    {
        int passIndex = ResolvePassIndex(passName);
        return passIndex != int.MinValue
            ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
            : null;
    }

    private int ResolvePassIndex(string passName)
    {
        var metadata = ParentPipeline?.PassMetadata;
        if (metadata is null)
            return int.MinValue;

        foreach (RenderPassMetadata pass in metadata)
        {
            if (string.Equals(pass.Name, passName, StringComparison.OrdinalIgnoreCase))
                return pass.PassIndex;
        }

        return int.MinValue;
    }

    private static bool ShouldUseTemporalJitter()
        => ShouldUseTemporalJitter(ResolveAntiAliasingMode());

    private static EAntiAliasingMode ResolveAntiAliasingMode()
    {
        if (IsLightProbePass || RuntimeEngine.Rendering.State.IsSceneCapturePass)
            return EAntiAliasingMode.None;

        if (RuntimeEngine.VRState.IsInVR && !RuntimeEngine.Rendering.Settings.RenderVRSinglePassStereo)
            return EAntiAliasingMode.None;

        return XREngine.Rendering.RenderPipeline.ResolveEffectiveAntiAliasingModeForFrame();
    }

    private static bool ShouldUseTemporalJitter(EAntiAliasingMode mode)
        => mode == EAntiAliasingMode.Taa
        || mode == EAntiAliasingMode.Tsr
        || mode == EAntiAliasingMode.Dlaa;

    private static bool ShouldRunInternalAccumulation(EAntiAliasingMode mode)
        => mode == EAntiAliasingMode.Taa;

    private static void SetHistoryExposureReady(bool ready)
    {
        if (!TryGetActiveState(out _, out var state))
            return;

        state.HistoryExposureReady = ready;
    }

    internal static bool TryGetTemporalUniformData([NotNullWhen(true)] out TemporalUniformData data)
    {
        if (CurrentRenderingPipeline is not { } instance ||
            !TryResolveTemporalCamera(instance, out XRCamera? camera))
        {
            data = default;
            return false;
        }

        if (TemporalStates.TryGetValue(camera, out TemporalState? state))
        {
            data = new TemporalUniformData
            {
                HistoryReady = state.HistoryReady,
                HistoryExposureReady = state.HistoryExposureReady,
                PrevViewProjection = state.PrevViewProjection,
                PrevViewProjectionUnjittered = state.PrevViewProjectionUnjittered,
                CurrProjection = state.CurrProjection,
                CurrInverseProjection = state.CurrInverseProjection,
                CurrInverseViewProjection = state.CurrInverseViewProjection,
                CurrViewProjection = state.CurrViewProjection,
                CurrViewProjectionUnjittered = state.CurrViewProjectionUnjittered,
                CurrentJitter = state.CurrentJitter,
                PreviousJitter = state.PreviousJitter,
                Width = state.LastInternalWidth,
                Height = state.LastInternalHeight
            };
            return true;
        }

        data = default;
        return false;
    }

    internal static void ResetHistory(XRRenderPipelineInstance? instance)
    {
        if (!TryResolveTemporalCamera(instance, out XRCamera? camera) ||
            !TemporalStates.TryGetValue(camera, out TemporalState? state))
            return;

        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;
        state.HistoryReady = false;
        state.HistoryExposureReady = false;
        state.PendingHistoryReady = false;
        state.CurrentJitter = Vector2.Zero;
        state.PreviousJitter = Vector2.Zero;
        state.HaltonIndex = 1;
    }

    private static bool TryGetActiveState([NotNullWhen(true)] out XRRenderPipelineInstance? instance, [NotNullWhen(true)] out TemporalState? state)
    {
        instance = CurrentRenderingPipeline;
        if (instance is null || !TryResolveTemporalCamera(instance, out XRCamera? camera))
        {
            state = null;
            return false;
        }

        state = TemporalStates.GetValue(camera, _ => new TemporalState());
        return true;
    }

    private static bool TryResolveTemporalCamera(
        XRRenderPipelineInstance? instance,
        [NotNullWhen(true)] out XRCamera? camera)
    {
        camera = instance?.RenderState.SceneCamera
            ?? instance?.RenderState.RenderingCamera
            ?? instance?.LastSceneCamera
            ?? instance?.LastRenderingCamera;
        return camera is not null;
    }

    private static void BeginTemporalFrame()
    {
        if (!TryGetActiveState(out var instance, out var state))
        {
            Debug.Rendering("[Temporal] Begin skipped: no active state.");
            return;
        }

        state.PendingHistoryReady = false;
        state.HistoryExposureReady = false;

        var viewport = instance.RenderState.WindowViewport;
        uint width = viewport is null ? 0u : (uint)viewport.InternalWidth;
        uint height = viewport is null ? 0u : (uint)viewport.InternalHeight;
        uint fullWidth = viewport is null ? 0u : (uint)viewport.Width;
        uint fullHeight = viewport is null ? 0u : (uint)viewport.Height;
        EAntiAliasingMode antiAliasingMode = ResolveAntiAliasingMode();

        if (width != state.LastInternalWidth
            || height != state.LastInternalHeight
            || fullWidth != state.LastFullWidth
            || fullHeight != state.LastFullHeight
            || antiAliasingMode != state.LastAntiAliasingMode)
        {
            state.LastInternalWidth = width;
            state.LastInternalHeight = height;
            state.LastFullWidth = fullWidth;
            state.LastFullHeight = fullHeight;
            state.LastAntiAliasingMode = antiAliasingMode;
            state.HistoryReady = false;
            state.PendingHistoryReady = false;
            state.HaltonIndex = 1;
            state.HistoryExposureReady = false;
            //Debug.Out($"[Temporal] Resolution change detected. New={width}x{height}; history reset.");
        }

        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;

        if (!TryResolveTemporalCamera(instance, out XRCamera? camera))
        {
            state.CurrentJitter = Vector2.Zero;
            state.CurrProjection = Matrix4x4.Identity;
            state.CurrInverseProjection = Matrix4x4.Identity;
            state.CurrViewProjection = Matrix4x4.Identity;
            state.CurrViewProjectionUnjittered = Matrix4x4.Identity;
            state.CurrInverseViewProjection = Matrix4x4.Identity;
            state.HistoryReady = false;
            Debug.Rendering("[Temporal] Begin: no camera; resetting state.");
            return;
        }

        bool jitterEnabled = ShouldUseTemporalJitter(antiAliasingMode);
        Vector2 jitter = jitterEnabled ? GenerateJitter(state, antiAliasingMode) : Vector2.Zero;
        state.CurrentJitter = jitter;
        //Debug.Out($"[Temporal] JitterEnabled={jitterEnabled} Jitter=({jitter.X:F6},{jitter.Y:F6}) HaltonIndex={state.HaltonIndex}");

        // Exposure history is only reliable when we are actively running temporal accumulation.
        if (!jitterEnabled)
            state.HistoryExposureReady = false;

        Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
        // Use Unjittered property to ensure we get a clean projection matrix, 
        // even if the jitter stack has leaked or is dirty from other passes.
        Matrix4x4 baseProjection = camera.ProjectionMatrixUnjittered;
        // System.Numerics is row-major / row-vector: combined VP = View * Projection.
        // When uploaded untransposed, GLSL sees (V*P)^T = P_gl * V_gl — correct order.
        Matrix4x4 baseViewProjection = viewMatrix * baseProjection;

        // Stabilize projection if it hasn't changed significantly to prevent micro-jitter/drift
        // which causes diagonal motion blur on static objects, especially at distance.
        if (state.HistoryReady && IsMatrixApproximatelyEqual(baseViewProjection, state.PrevViewProjectionUnjittered))
        {
            baseViewProjection = state.PrevViewProjectionUnjittered;
            //Debug.Out("[Temporal] Stabilized projection using previous unjittered VP.");
        }

        if (jitterEnabled)
            state.ActiveJitterHandle = instance.RenderState.RequestCameraProjectionJitter(jitter);

        Matrix4x4 jitteredProjection = camera.ProjectionMatrix;
        Matrix4x4 jitteredViewProjection = viewMatrix * jitteredProjection;
        if (!Matrix4x4.Invert(jitteredViewProjection, out Matrix4x4 inverseViewProjection))
            inverseViewProjection = Matrix4x4.Identity;

        state.CurrViewProjectionUnjittered = baseViewProjection;
        state.CurrProjection = jitteredProjection;
        state.CurrInverseProjection = camera.InverseProjectionMatrix;
        state.CurrViewProjection = jitteredViewProjection;
        state.CurrInverseViewProjection = inverseViewProjection;
        //Debug.Out("[Temporal] Begin completed: VP/Jitter prepared.");
    }

    private static void FlagTemporalHistoryCaptured()
    {
        if (!TryGetActiveState(out var instance, out var state))
        {
            Debug.Rendering("[Temporal] FlagHistoryCaptured skipped: no active state.");
            return;
        }

        if (!TryResolveTemporalCamera(instance, out _))
        {
            Debug.Rendering("[Temporal] FlagHistoryCaptured skipped: no camera.");
            return;
        }

        state.PendingHistoryReady = true;
    }

    /// <summary>
    /// Disposes the active jitter handle so transparent/masked passes render unjittered.
    /// Called between Accumulate and Commit phases.
    /// </summary>
    private static void PopActiveJitter()
    {
        if (!TryGetActiveState(out _, out var state))
            return;

        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;
    }

    private static void CommitTemporalFrame()
    {
        if (!TryGetActiveState(out var instance, out var state))
        {
            Debug.Rendering("[Temporal] Commit skipped: no active state.");
            return;
        }

        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;

        // Always commit view-projection history for motion blur, even if TAA accumulation was skipped.
        // Only skip if no history was captured this frame at all.
        if (!state.PendingHistoryReady)
        {
            Debug.Rendering("[Temporal] Commit skipped: no pending history.");
            return;
        }

        state.PendingHistoryReady = false;
        state.PreviousJitter = state.CurrentJitter;
        state.PrevViewProjection = state.CurrViewProjection;
        state.PrevViewProjectionUnjittered = state.CurrViewProjectionUnjittered;
        state.PrevInverseViewProjection = state.CurrInverseViewProjection;
        state.HistoryReady = true;
        //Debug.Out("[Temporal] Commit completed: history stored.");
    }

    private static Vector2 GenerateJitter(TemporalState state, EAntiAliasingMode antiAliasingMode)
    {
        state.HaltonIndex = (state.HaltonIndex + 1u) % (uint)TemporalJitterSequence.Length;
        Vector2 sample = TemporalJitterSequence[state.HaltonIndex];
        float scale = antiAliasingMode == EAntiAliasingMode.Tsr
            ? TsrJitterScaleInTexels
            : TaaJitterScaleInTexels;
        return sample * scale;
    }

    private static bool IsMatrixApproximatelyEqual(in Matrix4x4 a, in Matrix4x4 b, float epsilon = 1e-6f)
    {
        return MathF.Abs(a.M11 - b.M11) < epsilon && MathF.Abs(a.M12 - b.M12) < epsilon && MathF.Abs(a.M13 - b.M13) < epsilon && MathF.Abs(a.M14 - b.M14) < epsilon &&
               MathF.Abs(a.M21 - b.M21) < epsilon && MathF.Abs(a.M22 - b.M22) < epsilon && MathF.Abs(a.M23 - b.M23) < epsilon && MathF.Abs(a.M24 - b.M24) < epsilon &&
               MathF.Abs(a.M31 - b.M31) < epsilon && MathF.Abs(a.M32 - b.M32) < epsilon && MathF.Abs(a.M33 - b.M33) < epsilon && MathF.Abs(a.M34 - b.M34) < epsilon &&
               MathF.Abs(a.M41 - b.M41) < epsilon && MathF.Abs(a.M42 - b.M42) < epsilon && MathF.Abs(a.M43 - b.M43) < epsilon && MathF.Abs(a.M44 - b.M44) < epsilon;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        if (Phase != EPhase.Accumulate)
            return;

        EAntiAliasingMode antiAliasingMode = ResolveAntiAliasingMode();
        if (ShouldRunInternalAccumulation(antiAliasingMode))
            DescribeTaaAccumulation(context);
        else
            DescribeHistoryPassthrough(context);
    }

    private void DescribeTaaAccumulation(RenderGraphDescribeContext context)
    {
        context.GetOrCreateSyntheticPass(TemporalInputCopyPassName, ERenderGraphPassStage.Transfer)
            .UseTransferSource(MakeFboColorResource(ForwardFBOName))
            .UseTransferDestination(MakeFboColorResource(TemporalInputFBOName));

        context.GetOrCreateSyntheticPass(TemporalAccumulationResolvePassName, ERenderGraphPassStage.Graphics)
            .SampleTexture(MakeFboColorResource(TemporalInputFBOName))
            .SampleTexture(MakeFboColorResource(HistoryColorFBOName))
            .SampleTexture(MakeTextureResource(DefaultRenderPipeline.VelocityTextureName))
            .SampleTexture(MakeTextureResource(DefaultRenderPipeline.DepthViewTextureName))
            .SampleTexture(MakeTextureResource(DefaultRenderPipeline.HistoryDepthViewTextureName))
            .SampleTexture(MakeFboColorResource(HistoryExposureFBOName))
            .UseColorAttachment(
                MakeTextureResource(DefaultRenderPipeline.HDRSceneTextureName),
                ERenderGraphAccess.Write,
                ERenderPassLoadOp.DontCare,
                ERenderPassStoreOp.Store)
            .UseColorAttachment(
                MakeTextureResource(DefaultRenderPipeline.TemporalExposureVarianceTextureName),
                ERenderGraphAccess.Write,
                ERenderPassLoadOp.DontCare,
                ERenderPassStoreOp.Store);

        context.GetOrCreateSyntheticPass(TemporalHistoryColorCopyPassName, ERenderGraphPassStage.Transfer)
            .UseTransferSource(MakeTextureResource(DefaultRenderPipeline.HDRSceneTextureName))
            .UseTransferDestination(MakeFboColorResource(HistoryColorFBOName));

        context.GetOrCreateSyntheticPass(TemporalHistoryDepthCopyPassName, ERenderGraphPassStage.Transfer)
            .UseTransferSource(MakeFboDepthResource(ForwardFBOName))
            .UseTransferDestination(MakeFboDepthResource(HistoryColorFBOName));

        context.GetOrCreateSyntheticPass(TemporalHistoryExposureCopyPassName, ERenderGraphPassStage.Transfer)
            .UseTransferSource(MakeTextureResource(DefaultRenderPipeline.TemporalExposureVarianceTextureName))
            .UseTransferDestination(MakeFboColorResource(HistoryExposureFBOName));
    }

    private void DescribeHistoryPassthrough(RenderGraphDescribeContext context)
    {
        context.GetOrCreateSyntheticPass(TemporalHistoryPassthroughPassName, ERenderGraphPassStage.Transfer)
            .UseTransferSource(MakeFboColorResource(ForwardFBOName))
            .UseTransferSource(MakeFboDepthResource(ForwardFBOName))
            .UseTransferDestination(MakeFboColorResource(HistoryColorFBOName))
            .UseTransferDestination(MakeFboDepthResource(HistoryColorFBOName));
    }
}
