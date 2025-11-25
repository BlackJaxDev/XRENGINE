using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Handles all temporal accumulation stages (begin, accumulate, commit) without relying on manual commands.
/// </summary>
public sealed class VPRC_TemporalAccumulationPass : ViewportRenderCommand
{
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
        public bool PendingHistoryReady;
        public StateObject? ActiveJitterHandle;
        public uint LastInternalWidth;
        public uint LastInternalHeight;
    }

    internal readonly struct TemporalUniformData
    {
        public bool HistoryReady { get; init; }
        public Matrix4x4 PrevViewProjection { get; init; }
        public Matrix4x4 PrevViewProjectionUnjittered { get; init; }
        public Matrix4x4 CurrInverseViewProjection { get; init; }
        public Matrix4x4 CurrViewProjection { get; init; }
        public Matrix4x4 CurrViewProjectionUnjittered { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
    }

    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, TemporalState> TemporalStates = new();

    public enum EPhase
    {
        Begin,
        Accumulate,
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
                BeginTemporalFrame();
                break;
            case EPhase.Accumulate:
                ExecuteAccumulation(pipeline);
                break;
            case EPhase.Commit:
                CommitTemporalFrame();
                break;
        }
    }

    private void ExecuteAccumulation(DefaultRenderPipeline pipeline)
    {
        var renderer = AbstractRenderer.Current;
        if (renderer is null)
            return;

        var forwardFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(ForwardFBOName);
        var temporalInputFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(TemporalInputFBOName);
        var accumulationFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(TemporalAccumulationFBOName);
        var historyColorFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(HistoryColorFBOName);
        var historyExposureFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(HistoryExposureFBOName);

        if (forwardFBO is null || temporalInputFBO is null || accumulationFBO is null || historyColorFBO is null || historyExposureFBO is null)
            return;

        // Always keep the history buffers in sync with the latest color/depth, even if temporal AA is disabled.
        renderer.BlitFBOToFBO(
            forwardFBO,
            temporalInputFBO,
            EReadBufferMode.ColorAttachment0,
            true,
            false,
            false,
            false);

        bool shouldAccumulate = ShouldUseTemporalJitter();
        if (shouldAccumulate)
            accumulationFBO.Render();

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
        FlagTemporalHistoryCaptured();
    }

    private static bool ShouldUseTemporalJitter()
    {
        var mode = Engine.Rendering.Settings.AntiAliasingMode;
        return mode == Engine.Rendering.EAntiAliasingMode.Taa
            || mode == Engine.Rendering.EAntiAliasingMode.Tsr;
    }

    internal static bool TryGetTemporalUniformData([NotNullWhen(true)] out TemporalUniformData data)
    {
        if (CurrentRenderingPipeline is not { } instance)
        {
            data = default;
            return false;
        }

        if (TemporalStates.TryGetValue(instance, out TemporalState? state))
        {
            data = new TemporalUniformData
            {
                HistoryReady = state.HistoryReady,
                PrevViewProjection = state.PrevViewProjection,
                PrevViewProjectionUnjittered = state.PrevViewProjectionUnjittered,
                CurrInverseViewProjection = state.CurrInverseViewProjection,
                CurrViewProjection = state.CurrViewProjection,
                CurrViewProjectionUnjittered = state.CurrViewProjectionUnjittered,
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
        if (instance is null)
        {
            state = null;
            return false;
        }

        state = TemporalStates.GetValue(instance, _ => new TemporalState());
        return true;
    }

    private static void BeginTemporalFrame()
    {
        if (!TryGetActiveState(out var instance, out var state))
            return;

        state.PendingHistoryReady = false;

        var viewport = instance.RenderState.WindowViewport;
        uint width = viewport is null ? 0u : (uint)viewport.InternalWidth;
        uint height = viewport is null ? 0u : (uint)viewport.InternalHeight;

        if (width != state.LastInternalWidth || height != state.LastInternalHeight)
        {
            state.LastInternalWidth = width;
            state.LastInternalHeight = height;
            state.HistoryReady = false;
            state.PendingHistoryReady = false;
            state.HaltonIndex = 1;
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
            return;
        }

        bool jitterEnabled = ShouldUseTemporalJitter();
        Vector2 jitter = jitterEnabled ? GenerateHaltonJitter(state) : Vector2.Zero;
        state.CurrentJitter = jitter;

        Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
        Matrix4x4 baseProjection = camera.ProjectionMatrix;
        Matrix4x4 baseViewProjection = baseProjection * viewMatrix;

        if (jitterEnabled)
            state.ActiveJitterHandle = instance.RenderState.RequestCameraProjectionJitter(jitter);

        Matrix4x4 jitteredProjection = camera.ProjectionMatrix;
        Matrix4x4 jitteredViewProjection = jitteredProjection * viewMatrix;
        if (!Matrix4x4.Invert(jitteredViewProjection, out Matrix4x4 inverseViewProjection))
            inverseViewProjection = Matrix4x4.Identity;

        state.CurrViewProjectionUnjittered = baseViewProjection;
        state.CurrViewProjection = jitteredViewProjection;
        state.CurrInverseViewProjection = inverseViewProjection;
    }

    private static void FlagTemporalHistoryCaptured()
    {
        if (!TryGetActiveState(out var instance, out var state))
            return;

        if (instance.RenderState.SceneCamera is null)
            return;

        state.PendingHistoryReady = true;
    }

    private static void CommitTemporalFrame()
    {
        if (!TryGetActiveState(out var instance, out var state))
            return;

        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;

        // Always commit view-projection history for motion blur, even if TAA accumulation was skipped.
        // Only skip if no history was captured this frame at all.
        if (!state.PendingHistoryReady)
            return;

        state.PendingHistoryReady = false;
        state.PreviousJitter = state.CurrentJitter;
        state.PrevViewProjection = state.CurrViewProjection;
        state.PrevViewProjectionUnjittered = state.CurrViewProjectionUnjittered;
        state.PrevInverseViewProjection = state.CurrInverseViewProjection;
        state.HistoryReady = true;
    }

    private static Vector2 GenerateHaltonJitter(TemporalState state)
    {
        state.HaltonIndex = (state.HaltonIndex % 8192u) + 1u;
        float x = Halton(state.HaltonIndex, 2u) - 0.5f;
        float y = Halton(state.HaltonIndex, 3u) - 0.5f;
        return new Vector2(x, y);
    }

    private static float Halton(uint index, uint @base)
    {
        float result = 0.0f;
        float f = 1.0f / @base;
        uint i = index;
        while (i > 0)
        {
            result += f * (i % @base);
            i /= @base;
            f /= @base;
        }
        return result;
    }
}
