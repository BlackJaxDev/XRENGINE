using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Handles all temporal accumulation stages (begin, accumulate, commit) without relying on manual commands.
/// </summary>
public sealed class VPRC_TemporalAccumulationPass : ViewportRenderCommand
{
    private const float TaaJitterScaleInTexels = 0.35f;
    private const float TsrJitterScaleInTexels = 0.20f;
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
        if (ParentPipeline is not DefaultRenderPipeline pipeline)
            return;

        switch (Phase)
        {
            case EPhase.Begin:
                //Debug.Out("[Temporal] Begin phase");
                BeginTemporalFrame();
                break;
            case EPhase.Accumulate:
                //Debug.Out("[Temporal] Accumulate phase");
                ExecuteAccumulation(pipeline);
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

    private void ExecuteAccumulation(DefaultRenderPipeline pipeline)
    {
        var renderer = AbstractRenderer.Current;
        if (renderer is null)
        {
            Debug.Out("[Temporal] Accumulate skipped: renderer unavailable.");
            return;
        }

        var forwardFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(ForwardFBOName);
        var temporalInputFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(TemporalInputFBOName);
        var accumulationFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(TemporalAccumulationFBOName);
        var historyColorFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(HistoryColorFBOName);
        var historyExposureFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(HistoryExposureFBOName);

        if (forwardFBO is null || temporalInputFBO is null || accumulationFBO is null || historyColorFBO is null || historyExposureFBO is null)
        {
            Debug.Out("[Temporal] Accumulate skipped: missing FBO(s)." +
                $" forward={(forwardFBO!=null)} temporalInput={(temporalInputFBO!=null)} accumulation={(accumulationFBO!=null)}" +
                $" historyColor={(historyColorFBO!=null)} historyExposure={(historyExposureFBO!=null)}");
            return;
        }

        // Always keep the history buffers in sync with the latest color/depth, even if temporal AA is disabled.
        renderer.BlitFBOToFBO(
            forwardFBO,
            temporalInputFBO,
            EReadBufferMode.ColorAttachment0,
            true,
            false,
            false,
            false);

        EAntiAliasingMode antiAliasingMode = ResolveAntiAliasingMode();
        bool shouldAccumulate = ShouldRunInternalAccumulation(antiAliasingMode);
        //Debug.Out($"[Temporal] shouldAccumulate={shouldAccumulate}");
        SetHistoryExposureReady(shouldAccumulate);
        if (shouldAccumulate)
        {
            //Debug.Out("[Temporal] Rendering accumulation FBO.");
            accumulationFBO.Render(accumulationFBO);
        }

        renderer.BlitFBOToFBO(
            forwardFBO,
            historyColorFBO,
            EReadBufferMode.ColorAttachment0,
            true,
            true,
            false,
            false);

        if (shouldAccumulate)
        {
            renderer.BlitFBOToFBO(
                accumulationFBO,
                historyExposureFBO,
                EReadBufferMode.ColorAttachment1,
                true,
                false,
                false,
                false);
        }

        // Always flag history as captured - motion blur depends on view-projection tracking
        // even when TAA/jitter is disabled.
        //Debug.Out("[Temporal] Flagging history captured.");
        FlagTemporalHistoryCaptured();
    }

    private static bool ShouldUseTemporalJitter()
        => ShouldUseTemporalJitter(ResolveAntiAliasingMode());

    private static EAntiAliasingMode ResolveAntiAliasingMode()
    {
        if (Engine.VRState.IsInVR && !Engine.Rendering.Settings.RenderVRSinglePassStereo)
            return EAntiAliasingMode.None;

        var camera = Engine.Rendering.State.RenderingCamera;
        return camera?.AntiAliasingModeOverride ?? Engine.EffectiveSettings.AntiAliasingMode;
    }

    private static bool ShouldUseTemporalJitter(EAntiAliasingMode mode)
        => mode == EAntiAliasingMode.Taa || mode == EAntiAliasingMode.Tsr;

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
        if (CurrentRenderingPipeline is not { } instance || instance.RenderState.SceneCamera is not { } camera)
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

    private static bool TryGetActiveState([NotNullWhen(true)] out XRRenderPipelineInstance? instance, [NotNullWhen(true)] out TemporalState? state)
    {
        instance = ActivePipelineInstance;
        if (instance is null || instance.RenderState.SceneCamera is not { } camera)
        {
            state = null;
            return false;
        }

        state = TemporalStates.GetValue(camera, _ => new TemporalState());
        return true;
    }

    private static void BeginTemporalFrame()
    {
        if (!TryGetActiveState(out var instance, out var state))
        {
            Debug.Out("[Temporal] Begin skipped: no active state.");
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

        var camera = instance.RenderState.SceneCamera;
        if (camera is null)
        {
            state.CurrentJitter = Vector2.Zero;
            state.CurrViewProjection = Matrix4x4.Identity;
            state.CurrViewProjectionUnjittered = Matrix4x4.Identity;
            state.CurrInverseViewProjection = Matrix4x4.Identity;
            state.HistoryReady = false;
            Debug.Out("[Temporal] Begin: no camera; resetting state.");
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
        state.CurrViewProjection = jitteredViewProjection;
        state.CurrInverseViewProjection = inverseViewProjection;
        //Debug.Out("[Temporal] Begin completed: VP/Jitter prepared.");
    }

    private static void FlagTemporalHistoryCaptured()
    {
        if (!TryGetActiveState(out var instance, out var state))
        {
            Debug.Out("[Temporal] FlagHistoryCaptured skipped: no active state.");
            return;
        }

        if (instance.RenderState.SceneCamera is null)
        {
            Debug.Out("[Temporal] FlagHistoryCaptured skipped: no camera.");
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
            Debug.Out("[Temporal] Commit skipped: no active state.");
            return;
        }

        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;

        // Always commit view-projection history for motion blur, even if TAA accumulation was skipped.
        // Only skip if no history was captured this frame at all.
        if (!state.PendingHistoryReady)
        {
            Debug.Out("[Temporal] Commit skipped: no pending history.");
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

        var builder = context.GetOrCreateSyntheticPass($"{nameof(VPRC_TemporalAccumulationPass)}_{Phase}", ERenderGraphPassStage.Graphics);

        builder.SampleTexture(MakeFboColorResource(ForwardFBOName));

        builder.UseColorAttachment(
            MakeFboColorResource(TemporalInputFBOName),
            ERenderGraphAccess.Write,
            ERenderPassLoadOp.DontCare,
            ERenderPassStoreOp.Store);
        builder.SampleTexture(MakeFboColorResource(TemporalInputFBOName));

        builder.UseColorAttachment(MakeFboColorResource(TemporalAccumulationFBOName));
        builder.SampleTexture(MakeFboColorResource(TemporalAccumulationFBOName));

        builder.SampleTexture(MakeFboColorResource(HistoryColorFBOName));
        builder.UseColorAttachment(
            MakeFboColorResource(HistoryColorFBOName),
            ERenderGraphAccess.Write,
            ERenderPassLoadOp.DontCare,
            ERenderPassStoreOp.Store);
        builder.UseDepthAttachment(
            MakeFboDepthResource(HistoryColorFBOName),
            ERenderGraphAccess.Write,
            ERenderPassLoadOp.DontCare,
            ERenderPassStoreOp.Store);

        builder.SampleTexture(MakeFboColorResource(HistoryExposureFBOName));
        builder.UseColorAttachment(
            MakeFboColorResource(HistoryExposureFBOName),
            ERenderGraphAccess.Write,
            ERenderPassLoadOp.DontCare,
            ERenderPassStoreOp.Store);
    }
}
