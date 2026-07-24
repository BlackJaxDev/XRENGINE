using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
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
    // Keep ordinary tracked-head motion inside the temporal domain while still
    // detecting discontinuous teleports and snap camera changes.
    private const float CameraCutTranslationThreshold = 2.0f;
    private const float CameraCutRotationThresholdDegrees = 55.0f;
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
        public TemporalEyeState LeftEye { get; } = new();
        public TemporalEyeState RightEye { get; } = new();
        public bool HistoryReady;
        public bool HistoryExposureReady;
        public bool PendingHistoryReady;
        public TemporalHistoryCoverage PendingHistoryCoverage;
        public TemporalHistoryGenerationTracker HistoryGeneration { get; } = new();
        public StateObject? ActiveJitterHandle;
        public StateObject? ActiveRightEyeJitterHandle;
        public EVrTemporalHistoryPolicy HistoryIsolationPolicy = EVrTemporalHistoryPolicy.HeadsetShared;
        public string HistoryIsolationReason = "mono/shared temporal history";
        public uint LastInternalWidth;
        public uint LastInternalHeight;
        public uint LastFullWidth;
        public uint LastFullHeight;
        public EAntiAliasingMode LastAntiAliasingMode = EAntiAliasingMode.None;
        public ulong LastFrameCount = 0;
        public EOpenXrSmokeTemporalResetReason ResetReasonThisFrame;
        public uint CameraCutLayerMaskThisFrame;

        public Vector2 CurrentJitter { get => LeftEye.CurrentJitter; set => LeftEye.CurrentJitter = value; }
        public Vector2 PreviousJitter { get => LeftEye.PreviousJitter; set => LeftEye.PreviousJitter = value; }
        public Matrix4x4 CurrProjection { get => LeftEye.CurrProjection; set => LeftEye.CurrProjection = value; }
        public Matrix4x4 CurrInverseProjection { get => LeftEye.CurrInverseProjection; set => LeftEye.CurrInverseProjection = value; }
        public Matrix4x4 CurrViewProjection { get => LeftEye.CurrViewProjection; set => LeftEye.CurrViewProjection = value; }
        public Matrix4x4 CurrViewProjectionUnjittered { get => LeftEye.CurrViewProjectionUnjittered; set => LeftEye.CurrViewProjectionUnjittered = value; }
        public Matrix4x4 CurrInverseViewProjection { get => LeftEye.CurrInverseViewProjection; set => LeftEye.CurrInverseViewProjection = value; }
        public Matrix4x4 PrevViewProjection { get => LeftEye.PrevViewProjection; set => LeftEye.PrevViewProjection = value; }
        public Matrix4x4 PrevViewProjectionUnjittered { get => LeftEye.PrevViewProjectionUnjittered; set => LeftEye.PrevViewProjectionUnjittered = value; }
        public Matrix4x4 PrevInverseViewProjection { get => LeftEye.PrevInverseViewProjection; set => LeftEye.PrevInverseViewProjection = value; }
    }

    private sealed class TemporalEyeState
    {
        public Vector2 CurrentJitter = Vector2.Zero;
        public Vector2 PreviousJitter = Vector2.Zero;
        public Matrix4x4 CurrViewMatrix = Matrix4x4.Identity;
        public Matrix4x4 CurrProjection = Matrix4x4.Identity;
        public Matrix4x4 CurrInverseProjection = Matrix4x4.Identity;
        public Matrix4x4 CurrViewProjection = Matrix4x4.Identity;
        public Matrix4x4 CurrViewProjectionUnjittered = Matrix4x4.Identity;
        public Matrix4x4 CurrInverseViewProjection = Matrix4x4.Identity;
        public Matrix4x4 PrevViewMatrix = Matrix4x4.Identity;
        public Matrix4x4 PrevProjection = Matrix4x4.Identity;
        public Matrix4x4 PrevViewProjection = Matrix4x4.Identity;
        public Matrix4x4 PrevViewProjectionUnjittered = Matrix4x4.Identity;
        public Matrix4x4 PrevInverseViewProjection = Matrix4x4.Identity;
        public Vector3 CurrentCameraPosition = Vector3.Zero;
        public Vector3 PreviousCameraPosition = Vector3.Zero;
        public Vector3 CurrentCameraForward = Vector3.UnitZ;
        public Vector3 PreviousCameraForward = Vector3.UnitZ;

        public void ResetCurrent()
        {
            CurrentJitter = Vector2.Zero;
            CurrViewMatrix = Matrix4x4.Identity;
            CurrProjection = Matrix4x4.Identity;
            CurrInverseProjection = Matrix4x4.Identity;
            CurrViewProjection = Matrix4x4.Identity;
            CurrViewProjectionUnjittered = Matrix4x4.Identity;
            CurrInverseViewProjection = Matrix4x4.Identity;
            CurrentCameraPosition = Vector3.Zero;
            CurrentCameraForward = Vector3.UnitZ;
        }

        public void ResetHistory()
        {
            CurrentJitter = Vector2.Zero;
            PreviousJitter = Vector2.Zero;
            PrevViewMatrix = Matrix4x4.Identity;
            PrevProjection = Matrix4x4.Identity;
            PrevViewProjection = Matrix4x4.Identity;
            PrevViewProjectionUnjittered = Matrix4x4.Identity;
            PrevInverseViewProjection = Matrix4x4.Identity;
            PreviousCameraPosition = Vector3.Zero;
            PreviousCameraForward = Vector3.UnitZ;
        }

        public void CommitCurrentToPrevious()
        {
            PreviousJitter = CurrentJitter;
            PrevViewMatrix = CurrViewMatrix;
            PrevProjection = CurrProjection;
            PrevViewProjection = CurrViewProjection;
            PrevViewProjectionUnjittered = CurrViewProjectionUnjittered;
            PrevInverseViewProjection = CurrInverseViewProjection;
            PreviousCameraPosition = CurrentCameraPosition;
            PreviousCameraForward = CurrentCameraForward;
        }
    }

    internal struct TemporalHistoryCoverage
    {
        public uint ExpectedLayerMask { get; private set; }
        public uint ColorLayerMask { get; private set; }
        public uint DepthLayerMask { get; private set; }
        public uint TsrColorLayerMask { get; private set; }
        public bool RequiresTsrColor { get; private set; }

        public readonly bool IsComplete
            => ExpectedLayerMask != 0u
                && (ColorLayerMask & ExpectedLayerMask) == ExpectedLayerMask
                && (DepthLayerMask & ExpectedLayerMask) == ExpectedLayerMask
                && (!RequiresTsrColor || (TsrColorLayerMask & ExpectedLayerMask) == ExpectedLayerMask);

        public readonly uint CompleteLayerMask
            => ColorLayerMask
                & DepthLayerMask
                & (RequiresTsrColor ? TsrColorLayerMask : uint.MaxValue)
                & ExpectedLayerMask;

        public void Begin(uint expectedLayerCount, bool requiresTsrColor)
        {
            uint clampedLayerCount = Math.Clamp(expectedLayerCount, 1u, 32u);
            ExpectedLayerMask = clampedLayerCount == 32u
                ? uint.MaxValue
                : (1u << (int)clampedLayerCount) - 1u;
            ColorLayerMask = 0u;
            DepthLayerMask = 0u;
            TsrColorLayerMask = 0u;
            RequiresTsrColor = requiresTsrColor;
        }

        public void RecordColorAndDepth(uint colorLayerMask, uint depthLayerMask)
        {
            ColorLayerMask |= colorLayerMask;
            DepthLayerMask |= depthLayerMask;
        }

        public void RecordTsrColor(uint layerMask)
            => TsrColorLayerMask |= layerMask;

        public void Clear()
            => this = default;

        public override readonly string ToString()
            => $"expected=0x{ExpectedLayerMask:X} color=0x{ColorLayerMask:X} depth=0x{DepthLayerMask:X} tsr=0x{TsrColorLayerMask:X} requireTsr={RequiresTsrColor}";
    }

    internal readonly record struct TemporalHistoryProfile(
        uint InternalWidth,
        uint InternalHeight,
        uint FullWidth,
        uint FullHeight,
        EAntiAliasingMode AntiAliasingMode,
        EVrTemporalHistoryPolicy IsolationPolicy);

    /// <summary>
    /// Tracks temporal compatibility and seeding independently for each eye.
    /// OpenXR swapchain image identity is intentionally absent from the profile:
    /// rotating to another acquired image does not invalidate engine-owned history.
    /// </summary>
    internal sealed class TemporalHistoryGenerationTracker
    {
        private TemporalHistoryProfile _profile;
        private bool _hasProfile;
        private ulong _leftEyeResetGeneration;
        private ulong _rightEyeResetGeneration;
        private ulong _leftEyeSeededGeneration;
        private ulong _rightEyeSeededGeneration;

        public ulong ProfileGeneration { get; private set; }
        public uint ExpectedLayerMask { get; private set; }
        public uint CurrentMatrixLayerMask { get; private set; }
        public ulong LeftEyeResetGeneration => _leftEyeResetGeneration;
        public ulong RightEyeResetGeneration => _rightEyeResetGeneration;
        public ulong LeftEyeSeededGeneration => _leftEyeSeededGeneration;
        public ulong RightEyeSeededGeneration => _rightEyeSeededGeneration;
        public bool LeftEyeHistoryReady => IsLayerReady(0);
        public bool RightEyeHistoryReady => IsLayerReady(1);
        public bool HistoryReady
            => ExpectedLayerMask != 0u
                && (!RequiresLayer(0) || LeftEyeHistoryReady)
                && (!RequiresLayer(1) || RightEyeHistoryReady);
        public bool CurrentMatricesComplete
            => ExpectedLayerMask != 0u
                && (CurrentMatrixLayerMask & ExpectedLayerMask) == ExpectedLayerMask;

        public bool BeginFrame(in TemporalHistoryProfile profile, uint expectedLayerCount)
        {
            ExpectedLayerMask = BuildLayerMask(Math.Clamp(expectedLayerCount, 1u, 2u));
            CurrentMatrixLayerMask = 0u;

            if (_hasProfile && _profile == profile)
                return false;

            _profile = profile;
            _hasProfile = true;
            ProfileGeneration = NextGeneration(ProfileGeneration);
            InvalidateLayers(0b11u);
            return true;
        }

        public void RecordCurrentMatrices(uint layerMask)
            => CurrentMatrixLayerMask |= layerMask & ExpectedLayerMask;

        public void RejectCurrentMatrices(uint layerMask)
        {
            uint rejectedMask = layerMask & ExpectedLayerMask;
            CurrentMatrixLayerMask &= ~rejectedMask;
            InvalidateReadyLayers(rejectedMask);
        }

        public void InvalidateLayers(uint layerMask)
        {
            if ((layerMask & 0b01u) != 0u)
            {
                _leftEyeResetGeneration = NextGeneration(_leftEyeResetGeneration);
                _leftEyeSeededGeneration = 0u;
            }

            if ((layerMask & 0b10u) != 0u)
            {
                _rightEyeResetGeneration = NextGeneration(_rightEyeResetGeneration);
                _rightEyeSeededGeneration = 0u;
            }
        }

        public uint CommitFrame(in TemporalHistoryCoverage coverage)
        {
            uint completedLayers = coverage.CompleteLayerMask
                & CurrentMatrixLayerMask
                & ExpectedLayerMask;
            uint incompleteLayers = ExpectedLayerMask & ~completedLayers;
            InvalidateReadyLayers(incompleteLayers);

            if ((completedLayers & 0b01u) != 0u)
                _leftEyeSeededGeneration = _leftEyeResetGeneration;
            if ((completedLayers & 0b10u) != 0u)
                _rightEyeSeededGeneration = _rightEyeResetGeneration;

            return completedLayers;
        }

        public bool IsLayerReady(int layerIndex)
            => layerIndex switch
            {
                0 => _leftEyeResetGeneration != 0u
                    && _leftEyeSeededGeneration == _leftEyeResetGeneration,
                1 => _rightEyeResetGeneration != 0u
                    && _rightEyeSeededGeneration == _rightEyeResetGeneration,
                _ => false,
            };

        private bool RequiresLayer(int layerIndex)
            => (ExpectedLayerMask & (1u << layerIndex)) != 0u;

        private void InvalidateReadyLayers(uint layerMask)
        {
            uint readyMask = 0u;
            if ((layerMask & 0b01u) != 0u && LeftEyeHistoryReady)
                readyMask |= 0b01u;
            if ((layerMask & 0b10u) != 0u && RightEyeHistoryReady)
                readyMask |= 0b10u;
            if (readyMask != 0u)
                InvalidateLayers(readyMask);
        }

        private static uint BuildLayerMask(uint layerCount)
            => (1u << (int)layerCount) - 1u;

        private static ulong NextGeneration(ulong generation)
            => generation == ulong.MaxValue ? 1u : generation + 1u;
    }

    internal readonly struct TemporalUniformData
    {
        public bool HistoryReady { get; init; }
        public bool LeftEyeHistoryReady { get; init; }
        public bool RightEyeHistoryReady { get; init; }
        public ulong ProfileGeneration { get; init; }
        public ulong LeftEyeResetGeneration { get; init; }
        public ulong RightEyeResetGeneration { get; init; }
        public ulong LeftEyeSeededGeneration { get; init; }
        public ulong RightEyeSeededGeneration { get; init; }
        public TemporalViewKey TemporalKey { get; init; }
        public EVrTemporalHistoryPolicy HistoryIsolationPolicy { get; init; }
        public string HistoryIsolationReason { get; init; }
        public Matrix4x4 PrevViewMatrix { get; init; }
        public Matrix4x4 PrevProjection { get; init; }
        public Matrix4x4 PrevViewProjection { get; init; }
        public Matrix4x4 PrevViewProjectionUnjittered { get; init; }
        public Matrix4x4 PrevInverseViewProjection { get; init; }
        public Matrix4x4 CurrViewMatrix { get; init; }
        public Matrix4x4 CurrProjection { get; init; }
        public Matrix4x4 CurrInverseProjection { get; init; }
        public Matrix4x4 CurrInverseViewProjection { get; init; }
        public Matrix4x4 CurrViewProjection { get; init; }
        public Matrix4x4 CurrViewProjectionUnjittered { get; init; }
        public Matrix4x4 RightEyePrevViewMatrix { get; init; }
        public Matrix4x4 RightEyePrevProjection { get; init; }
        public Matrix4x4 RightEyePrevViewProjection { get; init; }
        public Matrix4x4 RightEyePrevViewProjectionUnjittered { get; init; }
        public Matrix4x4 RightEyePrevInverseViewProjection { get; init; }
        public Matrix4x4 RightEyeCurrViewMatrix { get; init; }
        public Matrix4x4 RightEyeCurrProjection { get; init; }
        public Matrix4x4 RightEyeCurrInverseProjection { get; init; }
        public Matrix4x4 RightEyeCurrInverseViewProjection { get; init; }
        public Matrix4x4 RightEyeCurrViewProjection { get; init; }
        public Matrix4x4 RightEyeCurrViewProjectionUnjittered { get; init; }
        public Vector2 CurrentJitter { get; init; }
        public Vector2 PreviousJitter { get; init; }
        public Vector2 RightEyeCurrentJitter { get; init; }
        public Vector2 RightEyePreviousJitter { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }
        public bool HistoryExposureReady { get; init; }
    }

    internal readonly record struct TemporalViewKey(
        int PipelineInstanceId,
        int ViewportId,
        int CameraId,
        int StereoEyeIndex,
        int OpenXrViewIndex,
        int RenderTargetProfile);

    private static readonly object TemporalStatesLock = new();
    private static readonly Dictionary<TemporalViewKey, TemporalState> TemporalStates = [];
    private static readonly Dictionary<int, TemporalViewKey> TemporalKeysByPipelineInstance = [];

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
        /// <summary>
        /// Records that the full-resolution TSR history color copy was scheduled
        /// for every required array layer.
        /// </summary>
        MarkTsrHistoryColor,
        Commit
    }

    public EPhase Phase { get; set; } = EPhase.Accumulate;

    public string ForwardFBOName { get; set; } = DefaultRenderPipeline.ForwardPassFBOName;
    public string TemporalInputFBOName { get; set; } = DefaultRenderPipeline.TemporalInputFBOName;
    public string TemporalAccumulationFBOName { get; set; } = DefaultRenderPipeline.TemporalAccumulationFBOName;
    public string HistoryColorFBOName { get; set; } = DefaultRenderPipeline.HistoryCaptureFBOName;
    public string HistoryExposureFBOName { get; set; } = DefaultRenderPipeline.HistoryExposureFBOName;
    public string TsrSourceFBOName { get; set; } = DefaultRenderPipeline.TsrUpscaleFBOName;
    public string TsrHistoryColorFBOName { get; set; } = DefaultRenderPipeline.TsrHistoryColorFBOName;

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

    public VPRC_TemporalAccumulationPass ConfigureTsrHistoryTargets(
        string sourceFboName,
        string historyColorFboName)
    {
        TsrSourceFBOName = sourceFboName;
        TsrHistoryColorFBOName = historyColorFboName;
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
            case EPhase.MarkTsrHistoryColor:
                MarkTsrHistoryColorCaptured();
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
        EVrTemporalHistoryPolicy historyPolicy = ResolveHistoryIsolationPolicy(out _);
        if (IsHistoryIsolationPolicyDisabled(historyPolicy))
        {
            SetHistoryExposureReady(false);
            ResetHistory(ActivePipelineInstance);
            return;
        }

        XRFrameBuffer historyColorSourceFbo = forwardFBO;

        if (ShouldPopulateTemporalInput(antiAliasingMode))
        {
            var temporalInputFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(TemporalInputFBOName);
            if (temporalInputFBO is null)
            {
                Debug.Rendering("[Temporal] Accumulate skipped: missing temporal input FBO.");
                SetHistoryExposureReady(false);
                return;
            }

            // TemporalColorInput is the canonical internal-resolution current-frame
            // color for every temporal AA mode. TSR performs its resolve separately,
            // but still requires this resource to contain real current-frame data.
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
        }

        if (shouldAccumulate)
        {
            var accumulationFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(TemporalAccumulationFBOName);
            var historyExposureFBO = ActivePipelineInstance.GetFBO<XRFrameBuffer>(HistoryExposureFBOName);
            if (accumulationFBO is null || historyExposureFBO is null)
            {
                Debug.Rendering("[Temporal] Accumulate skipped: missing accumulation FBO(s)." +
                    $" accumulation={(accumulationFBO!=null)} historyExposure={(historyExposureFBO!=null)}");
                SetHistoryExposureReady(false);
                return;
            }

            SetHistoryExposureReady(true);

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

            historyColorSourceFbo = accumulationFBO;
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

        // History becomes usable only when color and depth cover every layer
        // represented by this temporal view key. TSR records its separate
        // full-resolution history color later in the command chain.
        RecordTemporalHistoryCaptured(historyColorSourceFbo, forwardFBO, historyColorFBO);
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

        if (!TryUseHistoryBasedVrEffects(out _, out _))
            return EAntiAliasingMode.None;

        return XREngine.Rendering.RenderPipeline.ResolveEffectiveAntiAliasingModeForFrame();
    }

    private static bool ShouldDisableHistoryBasedVrAntiAliasing()
        => !TryUseHistoryBasedVrEffects(out _, out _);

    internal static bool TryUseHistoryBasedVrEffects(
        out EVrTemporalHistoryPolicy policy,
        out string reason)
    {
        policy = ResolveHistoryIsolationPolicy(out reason);
        return !IsHistoryIsolationPolicyDisabled(policy);
    }

    internal static EVrTemporalHistoryPolicy ResolveHistoryIsolationPolicy(out string reason)
    {
        if (IsLightProbePass)
        {
            reason = "light probe pass";
            return EVrTemporalHistoryPolicy.Disabled;
        }

        if (RuntimeEngine.Rendering.State.IsSceneCapturePass)
        {
            reason = "scene capture pass";
            return EVrTemporalHistoryPolicy.Disabled;
        }

        bool externalSwapchainTarget = IsRenderingExternalSwapchainTarget();

        bool openXrVulkanRuntimeSelected =
            RuntimeEngine.Rendering.State.IsVulkan &&
            (RuntimeRenderingHostServices.Presentation.IsOpenXrRuntimeRequested ||
             RuntimeEngine.GameSettings?.VRRuntime == EVRRuntime.OpenXR ||
             RuntimeEngine.VRState.OpenXRApi is not null ||
             RuntimeEngine.VRState.IsOpenXRActive);

        if (!externalSwapchainTarget && !RuntimeEngine.Rendering.State.IsStereoPass)
        {
            reason = "mono/shared temporal history";
            return EVrTemporalHistoryPolicy.HeadsetShared;
        }

        if (openXrVulkanRuntimeSelected &&
            externalSwapchainTarget &&
            !RuntimeEngine.Rendering.State.IsStereoPass)
        {
            reason = "external OpenXR Vulkan swapchain target";
            return EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain;
        }

        if (!RuntimeEngine.VRState.IsInVR && !RuntimeEngine.Rendering.State.IsStereoPass)
        {
            reason = "mono/shared temporal history";
            return EVrTemporalHistoryPolicy.HeadsetShared;
        }

        ERenderLibrary backend = RuntimeEngine.Rendering.State.IsVulkan
            ? ERenderLibrary.Vulkan
            : ERenderLibrary.OpenGL;
        bool trueSinglePassStereoAvailable =
            RuntimeEngine.Rendering.Settings.VrViewRenderMode == EVrViewRenderMode.SinglePassStereo &&
            (RuntimeEngine.VRState.IsOpenXRActive
                ? RuntimeEngine.VRState.OpenXRApi?.CanUseTrueSinglePassStereo == true
                : RuntimeEngine.Rendering.State.IsStereoPass &&
                  (RuntimeEngine.Rendering.State.HasVulkanMultiView ||
                   RuntimeEngine.Rendering.State.HasOvrMultiViewExtension));

        VrViewRenderModeResolution resolution = VrViewRenderModeResolver.Resolve(
            backend,
            RuntimeEngine.Rendering.Settings.VrViewRenderMode,
            RuntimeRenderingHostServices.Presentation.EnableOpenXrVulkanParallelRendering,
            trueSinglePassStereoAvailable,
            externalSwapchainTarget);

        reason = resolution.TemporalHistoryPolicy switch
        {
            EVrTemporalHistoryPolicy.StereoArrayLayer => "true single-pass stereo array-layer history",
            EVrTemporalHistoryPolicy.PerEye => "per-eye temporal history",
            EVrTemporalHistoryPolicy.HeadsetShared => "headset-shared temporal history",
            EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain => "external per-eye swapchain targets",
            EVrTemporalHistoryPolicy.DisabledPerEyeSwapchain => "per-eye swapchain rendering",
            _ => resolution.Diagnostic ?? "temporal history disabled",
        };
        return resolution.TemporalHistoryPolicy;
    }

    private static bool IsHistoryIsolationPolicyDisabled(EVrTemporalHistoryPolicy policy)
        => policy is EVrTemporalHistoryPolicy.Disabled
            or EVrTemporalHistoryPolicy.DisabledPerEyeSwapchain
            or EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain;

    private static bool ShouldUseTemporalJitter(EAntiAliasingMode mode)
        => mode == EAntiAliasingMode.Taa
        || mode == EAntiAliasingMode.Tsr
        || mode == EAntiAliasingMode.Dlaa;

    private static bool ShouldRunInternalAccumulation(EAntiAliasingMode mode)
        => mode == EAntiAliasingMode.Taa;

    internal static bool ShouldPopulateTemporalInput(EAntiAliasingMode mode)
        => mode is EAntiAliasingMode.Taa
            or EAntiAliasingMode.Tsr
            or EAntiAliasingMode.Dlaa;

    private static void SetHistoryExposureReady(bool ready)
    {
        if (!TryGetActiveState(out _, out var state))
            return;

        state.HistoryExposureReady = ready;
    }

    internal static bool TryGetTemporalUniformData([NotNullWhen(true)] out TemporalUniformData data)
    {
        if (!TryResolveActiveTemporalKey(out _, out TemporalViewKey key))
        {
            data = default;
            return false;
        }

        lock (TemporalStatesLock)
        {
            if (TemporalStates.TryGetValue(key, out TemporalState? state))
            {
                data = CreateTemporalUniformData(key, state);
                return true;
            }
        }

        data = default;
        return false;
    }

    /// <summary>
    /// Resolves the temporal snapshot owned by a specific pipeline instance.
    /// Deferred Vulkan recording uses this overload so another output's ambient
    /// render state cannot redirect velocity bindings to a different view family.
    /// </summary>
    internal static bool TryGetTemporalUniformData(
        XRRenderPipelineInstance instance,
        [NotNullWhen(true)] out TemporalUniformData data)
    {
        lock (TemporalStatesLock)
        {
            if (TemporalKeysByPipelineInstance.TryGetValue(instance.InstanceId, out TemporalViewKey key) &&
                TemporalStates.TryGetValue(key, out TemporalState? state))
            {
                data = CreateTemporalUniformData(key, state);
                return true;
            }
        }

        data = default;
        return false;
    }

    /// <summary>
    /// Resolves unjittered current-frame matrices for consumers that could not
    /// obtain the immutable temporal snapshot. Stereo eyes are resolved
    /// independently; a missing right-eye camera is never substituted with the
    /// left-eye matrix.
    /// </summary>
    internal static uint ResolveCurrentFrameViewProjectionFallback(
        XRRenderPipelineInstance? instance,
        bool stereo,
        out Matrix4x4 leftViewProjection,
        out Matrix4x4 rightViewProjection)
    {
        leftViewProjection = Matrix4x4.Identity;
        rightViewProjection = Matrix4x4.Identity;
        uint validLayerMask = 0u;

        TryResolveTemporalCamera(instance, out XRCamera? leftCamera);
        if (leftCamera is null && (instance is null || ReferenceEquals(instance, CurrentRenderingPipeline)))
            leftCamera = RuntimeEngine.Rendering.State.RenderingCamera;

        if (leftCamera is not null && TryCreateCurrentFrameViewProjection(leftCamera, out leftViewProjection))
            validLayerMask |= 0b01u;

        if (!stereo)
        {
            rightViewProjection = leftViewProjection;
            return validLayerMask;
        }

        XRCamera? rightCamera = instance?.RenderState.StereoRightEyeCamera;
        if (rightCamera is null && ReferenceEquals(instance, CurrentRenderingPipeline))
            rightCamera = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;

        if (rightCamera is not null &&
            !ReferenceEquals(rightCamera, leftCamera) &&
            TryCreateCurrentFrameViewProjection(rightCamera, out rightViewProjection))
            validLayerMask |= 0b10u;

        return validLayerMask;
    }

    /// <summary>
    /// Records a zero-tolerance temporal snapshot miss. If a state exists for
    /// the exact resolved key, invalidate it before the consumer renders its
    /// current-frame fallback so stale history cannot become visible later.
    /// </summary>
    internal static void ReportMissingTemporalSnapshot(
        XRRenderPipelineInstance? instance,
        string consumer,
        uint currentMatrixLayerMask,
        uint expectedLayerMask)
    {
        bool keyResolved = TryResolveTemporalKey(instance, out TemporalViewKey temporalKey);
        bool invalidatedExistingState = false;
        uint effectiveExpectedLayerMask = expectedLayerMask;
        if (keyResolved)
        {
            lock (TemporalStatesLock)
            {
                if (TemporalStates.TryGetValue(temporalKey, out TemporalState? state))
                {
                    TemporalHistoryGenerationTracker generation = state.HistoryGeneration;
                    if (generation.ExpectedLayerMask != 0u)
                        effectiveExpectedLayerMask = generation.ExpectedLayerMask;
                    bool hasTemporalData =
                        (generation.CurrentMatrixLayerMask & effectiveExpectedLayerMask) != 0u ||
                        generation.LeftEyeHistoryReady ||
                        generation.RightEyeHistoryReady ||
                        state.HistoryReady ||
                        state.HistoryExposureReady ||
                        state.PendingHistoryReady ||
                        state.PendingHistoryCoverage.ColorLayerMask != 0u ||
                        state.PendingHistoryCoverage.DepthLayerMask != 0u ||
                        state.PendingHistoryCoverage.TsrColorLayerMask != 0u;
                    if (hasTemporalData)
                    {
                        generation.InvalidateLayers(effectiveExpectedLayerMask);
                        generation.RejectCurrentMatrices(effectiveExpectedLayerMask);
                        ResetHistoryStorage(state);
                        state.ResetReasonThisFrame |= EOpenXrSmokeTemporalResetReason.MissingSnapshot;
                        invalidatedExistingState = true;
                    }
                }
            }
        }

        string temporalKeyText = keyResolved ? temporalKey.ToString() : "<unresolved>";
        int instanceId = instance?.InstanceId ?? 0;
        Debug.RenderingWarningEvery(
            $"Temporal.MissingSnapshot.{instanceId}.{consumer}.{temporalKeyText}",
            TimeSpan.FromSeconds(1),
            "[Temporal] Required immutable snapshot unavailable; history was rejected and the consumer is rendering current-frame data. Pipeline={0} Consumer={1} Key={2} MatrixMask=0x{3:X} ExpectedMask=0x{4:X} ExistingStateInvalidated={5}",
            instance?.ProfilerKey ?? "<none>",
            consumer,
            temporalKeyText,
            currentMatrixLayerMask,
            effectiveExpectedLayerMask,
            invalidatedExistingState);
    }

    private static TemporalUniformData CreateTemporalUniformData(
        in TemporalViewKey key,
        TemporalState state)
    {
        TemporalEyeState leftEye = state.LeftEye;
        TemporalEyeState rightEye = state.RightEye;
        TemporalHistoryGenerationTracker generation = state.HistoryGeneration;
        return new TemporalUniformData
        {
            HistoryReady = state.HistoryReady,
            LeftEyeHistoryReady = generation.LeftEyeHistoryReady,
            RightEyeHistoryReady = generation.RightEyeHistoryReady,
            ProfileGeneration = generation.ProfileGeneration,
            LeftEyeResetGeneration = generation.LeftEyeResetGeneration,
            RightEyeResetGeneration = generation.RightEyeResetGeneration,
            LeftEyeSeededGeneration = generation.LeftEyeSeededGeneration,
            RightEyeSeededGeneration = generation.RightEyeSeededGeneration,
            TemporalKey = key,
            HistoryExposureReady = state.HistoryExposureReady,
            HistoryIsolationPolicy = state.HistoryIsolationPolicy,
            HistoryIsolationReason = state.HistoryIsolationReason,
            PrevViewMatrix = leftEye.PrevViewMatrix,
            PrevProjection = leftEye.PrevProjection,
            PrevViewProjection = leftEye.PrevViewProjection,
            PrevViewProjectionUnjittered = leftEye.PrevViewProjectionUnjittered,
            PrevInverseViewProjection = leftEye.PrevInverseViewProjection,
            CurrViewMatrix = leftEye.CurrViewMatrix,
            CurrProjection = leftEye.CurrProjection,
            CurrInverseProjection = leftEye.CurrInverseProjection,
            CurrInverseViewProjection = leftEye.CurrInverseViewProjection,
            CurrViewProjection = leftEye.CurrViewProjection,
            CurrViewProjectionUnjittered = leftEye.CurrViewProjectionUnjittered,
            RightEyePrevViewMatrix = rightEye.PrevViewMatrix,
            RightEyePrevProjection = rightEye.PrevProjection,
            RightEyePrevViewProjection = rightEye.PrevViewProjection,
            RightEyePrevViewProjectionUnjittered = rightEye.PrevViewProjectionUnjittered,
            RightEyePrevInverseViewProjection = rightEye.PrevInverseViewProjection,
            RightEyeCurrViewMatrix = rightEye.CurrViewMatrix,
            RightEyeCurrProjection = rightEye.CurrProjection,
            RightEyeCurrInverseProjection = rightEye.CurrInverseProjection,
            RightEyeCurrInverseViewProjection = rightEye.CurrInverseViewProjection,
            RightEyeCurrViewProjection = rightEye.CurrViewProjection,
            RightEyeCurrViewProjectionUnjittered = rightEye.CurrViewProjectionUnjittered,
            CurrentJitter = leftEye.CurrentJitter,
            PreviousJitter = leftEye.PreviousJitter,
            RightEyeCurrentJitter = rightEye.CurrentJitter,
            RightEyePreviousJitter = rightEye.PreviousJitter,
            Width = state.LastInternalWidth,
            Height = state.LastInternalHeight
        };
    }

    private static bool TryCreateCurrentFrameViewProjection(
        XRCamera camera,
        out Matrix4x4 viewProjection)
    {
        viewProjection = camera.Transform.InverseRenderMatrix * camera.ProjectionMatrixUnjittered;
        if (IsTemporalMatrixFinite(viewProjection))
            return true;

        viewProjection = Matrix4x4.Identity;
        return false;
    }

    internal static void ResetHistory(XRRenderPipelineInstance? instance)
    {
        if (!TryResolveTemporalKey(instance, out TemporalViewKey key))
            return;

        lock (TemporalStatesLock)
        {
            if (!TemporalStates.TryGetValue(key, out TemporalState? state))
                return;

            state.ResetReasonThisFrame |= EOpenXrSmokeTemporalResetReason.ExplicitReset;
            ResetHistory(state);
        }
    }

    private static bool TryGetActiveState([NotNullWhen(true)] out XRRenderPipelineInstance? instance, [NotNullWhen(true)] out TemporalState? state)
    {
        if (!TryResolveActiveTemporalKey(out instance, out TemporalViewKey key))
        {
            state = null;
            return false;
        }

        lock (TemporalStatesLock)
        {
            if (!TemporalStates.TryGetValue(key, out state))
            {
                state = new TemporalState();
                TemporalStates.Add(key, state);
            }

            TemporalKeysByPipelineInstance[instance.InstanceId] = key;
        }

        return true;
    }

    private static void ResetHistory(TemporalState state)
    {
        state.HistoryGeneration.InvalidateLayers(0b11u);
        ResetHistoryStorage(state);
    }

    private static void ResetHistoryStorage(TemporalState state)
    {
        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;
        state.ActiveRightEyeJitterHandle?.Dispose();
        state.ActiveRightEyeJitterHandle = null;
        state.HistoryReady = false;
        state.HistoryExposureReady = false;
        state.PendingHistoryReady = false;
        state.PendingHistoryCoverage.Clear();
        state.LeftEye.ResetHistory();
        state.RightEye.ResetHistory();
        state.HaltonIndex = 1;
    }

    private static void ResetEyeHistory(TemporalState state, int eyeIndex)
    {
        uint layerMask = 1u << eyeIndex;
        state.HistoryGeneration.InvalidateLayers(layerMask);
        if (eyeIndex == 0)
            state.LeftEye.ResetHistory();
        else
            state.RightEye.ResetHistory();
        state.HistoryReady = state.HistoryGeneration.HistoryReady;
    }

    private static bool TryResolveActiveTemporalKey(
        [NotNullWhen(true)] out XRRenderPipelineInstance? instance,
        out TemporalViewKey key)
    {
        instance = CurrentRenderingPipeline;
        return TryResolveTemporalKey(instance, out key);
    }

    private static bool TryResolveTemporalKey(
        XRRenderPipelineInstance? instance,
        out TemporalViewKey key)
    {
        key = default;
        if (instance is null || !TryResolveTemporalCamera(instance, out XRCamera? camera))
            return false;

        var viewport = instance.RenderState.WindowViewport;
        EVrTemporalHistoryPolicy policy = ResolveHistoryIsolationPolicy(out _);
        int stereoEyeIndex = policy == EVrTemporalHistoryPolicy.PerEye
            ? ResolveCurrentStereoEyeIndex(camera)
            : -1;

        key = new TemporalViewKey(
            RuntimeHelpers.GetHashCode(instance),
            viewport is null ? 0 : RuntimeHelpers.GetHashCode(viewport),
            RuntimeHelpers.GetHashCode(camera),
            stereoEyeIndex,
            stereoEyeIndex,
            // Extent, AA mode, and isolation policy belong to the generation
            // profile, not the state identity. Keeping the identity stable lets
            // an incompatible profile advance and invalidate the same state.
            RenderTargetProfile: 0);
        return true;
    }

    private static int ResolveCurrentStereoEyeIndex(XRCamera camera)
    {
        XRCamera? rightEyeCamera = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera
            ?? CurrentRenderingPipeline?.RenderState.StereoRightEyeCamera;
        return ReferenceEquals(camera, rightEyeCamera) ? 1 : 0;
    }

    private static bool IsRenderingExternalSwapchainTarget()
    {
        AbstractRenderer? renderer = RuntimeRenderingHostServices.FrameTiming.CurrentRenderer as AbstractRenderer
            ?? AbstractRenderer.Current;
        return renderer?.IsRenderingExternalSwapchainTarget == true;
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
        state.PendingHistoryCoverage.Clear();
        state.ResetReasonThisFrame = EOpenXrSmokeTemporalResetReason.None;
        state.CameraCutLayerMaskThisFrame = 0u;

        var viewport = instance.RenderState.WindowViewport;
        uint width = viewport is null ? 0u : (uint)viewport.InternalWidth;
        uint height = viewport is null ? 0u : (uint)viewport.InternalHeight;
        uint fullWidth = viewport is null ? 0u : (uint)viewport.Width;
        uint fullHeight = viewport is null ? 0u : (uint)viewport.Height;
        EAntiAliasingMode antiAliasingMode = ResolveAntiAliasingMode();
        EVrTemporalHistoryPolicy historyPolicy = ResolveHistoryIsolationPolicy(out string historyPolicyReason);
        state.HistoryIsolationPolicy = historyPolicy;
        state.HistoryIsolationReason = historyPolicyReason;

        uint expectedHistoryLayers = historyPolicy == EVrTemporalHistoryPolicy.StereoArrayLayer ? 2u : 1u;
        TemporalHistoryProfile profile = new(
            width,
            height,
            fullWidth,
            fullHeight,
            antiAliasingMode,
            historyPolicy);
        bool profileReset = state.HistoryGeneration.BeginFrame(profile, expectedHistoryLayers);
        if (profileReset)
        {
            // The tracker has already advanced both eye generations. Clear only
            // the stored matrices/history so the reset is not counted twice.
            ResetHistoryStorage(state);
            state.ResetReasonThisFrame |= EOpenXrSmokeTemporalResetReason.ProfileChanged;
        }

        state.LastInternalWidth = width;
        state.LastInternalHeight = height;
        state.LastFullWidth = fullWidth;
        state.LastFullHeight = fullHeight;
        state.LastAntiAliasingMode = antiAliasingMode;
        state.PendingHistoryCoverage.Begin(
            expectedHistoryLayers,
            requiresTsrColor: antiAliasingMode == EAntiAliasingMode.Tsr && !IsHistoryIsolationPolicyDisabled(historyPolicy));

        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;
        state.ActiveRightEyeJitterHandle?.Dispose();
        state.ActiveRightEyeJitterHandle = null;

        if (!TryResolveTemporalCamera(instance, out XRCamera? camera))
        {
            state.LeftEye.ResetCurrent();
            state.RightEye.ResetCurrent();
            state.HistoryGeneration.RejectCurrentMatrices(state.HistoryGeneration.ExpectedLayerMask);
            state.HistoryReady = false;
            state.ResetReasonThisFrame |= EOpenXrSmokeTemporalResetReason.MissingCamera;
            LogTemporalReseedPending(instance, state, "missing primary camera");
            return;
        }

        XRCamera? rightEyeCamera = instance.RenderState.StereoRightEyeCamera
            ?? RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
        if (state.HistoryGeneration.LeftEyeHistoryReady && IsCameraCut(camera, state.LeftEye))
        {
            ResetEyeHistory(state, 0);
            state.CameraCutLayerMaskThisFrame |= 0b01u;
            state.ResetReasonThisFrame |= EOpenXrSmokeTemporalResetReason.CameraCut;
        }
        if (state.HistoryGeneration.RightEyeHistoryReady &&
            rightEyeCamera is not null &&
            !ReferenceEquals(rightEyeCamera, camera) &&
            IsCameraCut(rightEyeCamera, state.RightEye))
        {
            ResetEyeHistory(state, 1);
            state.CameraCutLayerMaskThisFrame |= 0b10u;
            state.ResetReasonThisFrame |= EOpenXrSmokeTemporalResetReason.CameraCut;
        }

        bool jitterEnabled = ShouldUseTemporalJitter(antiAliasingMode);
        Vector2 jitter = jitterEnabled ? GenerateJitter(state, antiAliasingMode) : Vector2.Zero;
        state.LeftEye.CurrentJitter = jitter;
        state.RightEye.CurrentJitter = jitter;
        //Debug.Out($"[Temporal] JitterEnabled={jitterEnabled} Jitter=({jitter.X:F6},{jitter.Y:F6}) HaltonIndex={state.HaltonIndex}");

        // Exposure history is only reliable when we are actively running temporal accumulation.
        if (!jitterEnabled)
            state.HistoryExposureReady = false;

        if (jitterEnabled)
        {
            state.ActiveJitterHandle = instance.RenderState.RequestCameraProjectionJitter(jitter);
            if (rightEyeCamera is not null && !ReferenceEquals(rightEyeCamera, camera))
            {
                Vector2 resolution = new(Math.Max(1u, width), Math.Max(1u, height));
                state.ActiveRightEyeJitterHandle = rightEyeCamera.PushProjectionJitter(
                    ProjectionJitterRequest.TexelSpace(jitter, resolution));
            }
        }

        bool leftMatricesValid = CaptureEyeTemporalState(
            camera,
            state.LeftEye,
            state.HistoryGeneration.LeftEyeHistoryReady);
        if (leftMatricesValid)
            state.HistoryGeneration.RecordCurrentMatrices(0b01u);
        else
            state.HistoryGeneration.RejectCurrentMatrices(0b01u);

        bool requiresRightEye = expectedHistoryLayers == 2u;
        bool hasDistinctRightEye = rightEyeCamera is not null && !ReferenceEquals(rightEyeCamera, camera);
        bool rightMatricesValid;
        if (hasDistinctRightEye)
        {
            rightMatricesValid = CaptureEyeTemporalState(
                rightEyeCamera!,
                state.RightEye,
                state.HistoryGeneration.RightEyeHistoryReady);
        }
        else if (!requiresRightEye)
        {
            // Keep compatibility fields populated for mono consumers without
            // pretending that a missing stereo layer was captured.
            rightMatricesValid = CaptureEyeTemporalState(
                camera,
                state.RightEye,
                state.HistoryGeneration.LeftEyeHistoryReady);
        }
        else
        {
            state.RightEye.ResetCurrent();
            rightMatricesValid = false;
        }

        if (requiresRightEye)
        {
            if (rightMatricesValid)
                state.HistoryGeneration.RecordCurrentMatrices(0b10u);
            else
                state.HistoryGeneration.RejectCurrentMatrices(0b10u);
        }

        state.HistoryReady = state.HistoryGeneration.HistoryReady;
        if (!state.HistoryReady)
        {
            string reason = profileReset
                ? "incompatible extent/profile generation"
                : !leftMatricesValid
                    ? "invalid left-eye temporal matrices"
                    : requiresRightEye && !rightMatricesValid
                        ? "invalid or missing right-eye temporal matrices"
                        : "history generation awaiting layer reseed";
            LogTemporalReseedPending(instance, state, reason);
        }
    }

    private static bool CaptureEyeTemporalState(XRCamera camera, TemporalEyeState eyeState, bool historyReady)
    {
        Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
        Matrix4x4 baseProjection = camera.ProjectionMatrixUnjittered;
        Matrix4x4 baseViewProjection = viewMatrix * baseProjection;

        if (historyReady && IsMatrixApproximatelyEqual(baseViewProjection, eyeState.PrevViewProjectionUnjittered))
            baseViewProjection = eyeState.PrevViewProjectionUnjittered;

        Matrix4x4 jitteredProjection = camera.ProjectionMatrix;
        Matrix4x4 jitteredViewProjection = viewMatrix * jitteredProjection;
        bool invertible = Matrix4x4.Invert(jitteredViewProjection, out Matrix4x4 inverseViewProjection);
        if (!invertible)
            inverseViewProjection = Matrix4x4.Identity;

        eyeState.CurrViewMatrix = viewMatrix;
        eyeState.CurrentCameraPosition = camera.Transform.RenderTranslation;
        eyeState.CurrentCameraForward = NormalizeOrForward(camera.Transform.RenderForward);
        eyeState.CurrViewProjectionUnjittered = baseViewProjection;
        eyeState.CurrProjection = jitteredProjection;
        eyeState.CurrInverseProjection = camera.InverseProjectionMatrix;
        eyeState.CurrViewProjection = jitteredViewProjection;
        eyeState.CurrInverseViewProjection = inverseViewProjection;
        return invertible
            && IsTemporalMatrixFinite(viewMatrix)
            && IsTemporalMatrixFinite(baseProjection)
            && IsTemporalMatrixFinite(baseViewProjection)
            && IsTemporalMatrixFinite(jitteredProjection)
            && IsTemporalMatrixFinite(camera.InverseProjectionMatrix)
            && IsTemporalMatrixFinite(jitteredViewProjection)
            && IsTemporalMatrixFinite(inverseViewProjection);
    }

    private static void LogTemporalReseedPending(
        XRRenderPipelineInstance instance,
        TemporalState state,
        string reason)
    {
        TryResolveTemporalKey(instance, out TemporalViewKey temporalKey);
        TemporalHistoryGenerationTracker generation = state.HistoryGeneration;
        Debug.RenderingWarningEvery(
            $"Temporal.ReseedPending.{instance.InstanceId}.{temporalKey}",
            TimeSpan.FromSeconds(1),
            "[Temporal] History unavailable; rendering current-frame data until every required layer is reseeded. Pipeline={0} Key={1} Reason={2} ProfileGeneration={3} LeftGeneration={4}/{5} RightGeneration={6}/{7} MatrixMask=0x{8:X} ExpectedMask=0x{9:X}",
            instance.ProfilerKey,
            temporalKey,
            reason,
            generation.ProfileGeneration,
            generation.LeftEyeSeededGeneration,
            generation.LeftEyeResetGeneration,
            generation.RightEyeSeededGeneration,
            generation.RightEyeResetGeneration,
            generation.CurrentMatrixLayerMask,
            generation.ExpectedLayerMask);
    }

    private static void RecordTemporalHistoryCaptured(
        XRFrameBuffer colorSourceFbo,
        XRFrameBuffer depthSourceFbo,
        XRFrameBuffer historyDestinationFbo)
    {
        if (!TryGetActiveState(out var instance, out var state))
        {
            Debug.Rendering("[Temporal] History coverage skipped: no active state.");
            return;
        }

        if (!TryResolveTemporalCamera(instance, out _))
        {
            Debug.Rendering("[Temporal] History coverage skipped: no camera.");
            return;
        }

        uint colorSourceMask = ResolveAttachmentLayerMask(colorSourceFbo, color: true);
        uint depthSourceMask = ResolveAttachmentLayerMask(depthSourceFbo, color: false);
        uint colorDestinationMask = ResolveAttachmentLayerMask(historyDestinationFbo, color: true);
        uint depthDestinationMask = ResolveAttachmentLayerMask(historyDestinationFbo, color: false);
        state.PendingHistoryCoverage.RecordColorAndDepth(
            colorSourceMask & colorDestinationMask,
            depthSourceMask & depthDestinationMask);
        state.PendingHistoryReady = state.PendingHistoryCoverage.IsComplete
            && state.HistoryGeneration.CurrentMatricesComplete;
    }

    private void MarkTsrHistoryColorCaptured()
    {
        if (!TryGetActiveState(out XRRenderPipelineInstance? instance, out TemporalState? state))
            return;

        XRFrameBuffer? source = instance.GetFBO<XRFrameBuffer>(TsrSourceFBOName);
        XRFrameBuffer? destination = instance.GetFBO<XRFrameBuffer>(TsrHistoryColorFBOName);
        if (source is null || destination is null)
        {
            state.PendingHistoryReady = false;
            Debug.RenderingWarningEvery(
                $"Temporal.TsrHistoryCoverage.MissingFbo.{instance.InstanceId}",
                TimeSpan.FromSeconds(1),
                "[Temporal] TSR history coverage incomplete. Pipeline={0} Source={1} Destination={2}",
                instance.ProfilerKey,
                source is null ? "missing" : "ready",
                destination is null ? "missing" : "ready");
            return;
        }

        uint sourceMask = ResolveAttachmentLayerMask(source, color: true);
        uint destinationMask = ResolveAttachmentLayerMask(destination, color: true);
        state.PendingHistoryCoverage.RecordTsrColor(sourceMask & destinationMask);
        state.PendingHistoryReady = state.PendingHistoryCoverage.IsComplete
            && state.HistoryGeneration.CurrentMatricesComplete;
    }

    private static uint ResolveAttachmentLayerMask(XRFrameBuffer frameBuffer, bool color)
    {
        if (frameBuffer.Targets is not { Length: > 0 } targets)
            return 0u;

        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, _, layerIndex) = targets[i];
            bool matches = color
                ? IsColorAttachment(attachment)
                : attachment is EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment;
            if (!matches)
                continue;

            uint baseLayer = target is XRTextureViewBase viewTarget
                ? Math.Min(viewTarget.MinLayer, 31u)
                : 0u;
            if (layerIndex >= 0)
            {
                uint selectedLayer = Math.Min(baseLayer + (uint)layerIndex, 31u);
                return 1u << (int)selectedLayer;
            }

            uint layerCount = target switch
            {
                XRTextureViewBase view => Math.Max(view.NumLayers, 1u),
                XRTexture2DArray array => Math.Max(array.Depth, 1u),
                _ => 1u,
            };
            uint clampedLayerCount = Math.Min(layerCount, 32u - baseLayer);
            uint contiguousMask = clampedLayerCount == 32u
                ? uint.MaxValue
                : (1u << (int)clampedLayerCount) - 1u;
            return contiguousMask << (int)baseLayer;
        }

        return 0u;
    }

    private static bool IsColorAttachment(EFrameBufferAttachment attachment)
        => attachment >= EFrameBufferAttachment.ColorAttachment0
            && attachment <= EFrameBufferAttachment.ColorAttachment31;

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
        state.ActiveRightEyeJitterHandle?.Dispose();
        state.ActiveRightEyeJitterHandle = null;
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
        state.ActiveRightEyeJitterHandle?.Dispose();
        state.ActiveRightEyeJitterHandle = null;

        if (IsHistoryIsolationPolicyDisabled(state.HistoryIsolationPolicy))
        {
            state.PendingHistoryReady = false;
            state.HistoryReady = false;
            state.PendingHistoryCoverage.Clear();
            return;
        }

        TemporalHistoryGenerationTracker generation = state.HistoryGeneration;
        bool historyReadyBeforeCommit = state.HistoryReady;
        bool leftReadyBeforeCommit = generation.LeftEyeHistoryReady;
        bool rightReadyBeforeCommit = generation.RightEyeHistoryReady;
        ulong leftPreviousFingerprint = ComputeMatrixFingerprint(state.LeftEye.PrevViewProjectionUnjittered);
        ulong rightPreviousFingerprint = ComputeMatrixFingerprint(state.RightEye.PrevViewProjectionUnjittered);
        ulong leftCurrentFingerprint = ComputeMatrixFingerprint(state.LeftEye.CurrViewProjectionUnjittered);
        ulong rightCurrentFingerprint = ComputeMatrixFingerprint(state.RightEye.CurrViewProjectionUnjittered);
        uint committedLayerMask = generation.CommitFrame(state.PendingHistoryCoverage);
        if ((committedLayerMask & 0b01u) != 0u)
            state.LeftEye.CommitCurrentToPrevious();
        if ((committedLayerMask & 0b10u) != 0u)
            state.RightEye.CommitCurrentToPrevious();

        state.PendingHistoryReady = false;
        state.HistoryReady = generation.HistoryReady;
        RecordTemporalStateEvidence(
            instance,
            state,
            generation,
            committedLayerMask,
            historyReadyBeforeCommit,
            leftReadyBeforeCommit,
            rightReadyBeforeCommit,
            leftPreviousFingerprint,
            rightPreviousFingerprint,
            leftCurrentFingerprint,
            rightCurrentFingerprint);
        if (!state.HistoryReady)
        {
            TryResolveTemporalKey(instance, out TemporalViewKey temporalKey);
            Debug.RenderingWarningEvery(
                $"Temporal.HistoryCoverageIncomplete.{instance.InstanceId}.{temporalKey}",
                TimeSpan.FromSeconds(1),
                "[Temporal] History invalidated because the frame did not populate valid matrices, color, depth, and TSR color for every required layer. Pipeline={0} Policy={1} Key={2} ProfileGeneration={3} LeftGeneration={4}/{5} RightGeneration={6}/{7} MatrixMask=0x{8:X} CommittedMask=0x{9:X} Coverage={10}",
                instance.ProfilerKey,
                state.HistoryIsolationPolicy,
                temporalKey,
                state.HistoryGeneration.ProfileGeneration,
                state.HistoryGeneration.LeftEyeSeededGeneration,
                state.HistoryGeneration.LeftEyeResetGeneration,
                state.HistoryGeneration.RightEyeSeededGeneration,
                state.HistoryGeneration.RightEyeResetGeneration,
                state.HistoryGeneration.CurrentMatrixLayerMask,
                committedLayerMask,
                state.PendingHistoryCoverage);
            state.PendingHistoryCoverage.Clear();
            return;
        }

        state.PendingHistoryCoverage.Clear();
        //Debug.Out("[Temporal] Commit completed: history stored.");
    }

    private static void RecordTemporalStateEvidence(
        XRRenderPipelineInstance instance,
        TemporalState state,
        TemporalHistoryGenerationTracker generation,
        uint committedLayerMask,
        bool historyReadyBeforeCommit,
        bool leftReadyBeforeCommit,
        bool rightReadyBeforeCommit,
        ulong leftPreviousFingerprint,
        ulong rightPreviousFingerprint,
        ulong leftCurrentFingerprint,
        ulong rightCurrentFingerprint)
    {
        if (!Phase524bTemporalStateDiagnostics.Enabled)
            return;

        ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        RecordTemporalEyeStateEvidence(
            renderFrameId,
            instance.InstanceId,
            state,
            generation,
            eyeIndex: 0,
            committedLayerMask,
            historyReadyBeforeCommit,
            leftReadyBeforeCommit,
            generation.LeftEyeHistoryReady,
            leftPreviousFingerprint,
            leftCurrentFingerprint);

        if ((generation.ExpectedLayerMask & 0b10u) != 0u)
        {
            RecordTemporalEyeStateEvidence(
                renderFrameId,
                instance.InstanceId,
                state,
                generation,
                eyeIndex: 1,
                committedLayerMask,
                historyReadyBeforeCommit,
                rightReadyBeforeCommit,
                generation.RightEyeHistoryReady,
                rightPreviousFingerprint,
                rightCurrentFingerprint);
        }
    }

    private static void RecordTemporalEyeStateEvidence(
        ulong renderFrameId,
        int pipelineInstanceId,
        TemporalState state,
        TemporalHistoryGenerationTracker generation,
        int eyeIndex,
        uint committedLayerMask,
        bool historyReadyBeforeCommit,
        bool eyeReadyBeforeCommit,
        bool eyeReadyAfterCommit,
        ulong previousFingerprint,
        ulong currentFingerprint)
    {
        uint eyeMask = 1u << eyeIndex;
        TemporalEyeState eye = eyeIndex == 0 ? state.LeftEye : state.RightEye;
        ulong resetGeneration = eyeIndex == 0
            ? generation.LeftEyeResetGeneration
            : generation.RightEyeResetGeneration;
        ulong seededGeneration = eyeIndex == 0
            ? generation.LeftEyeSeededGeneration
            : generation.RightEyeSeededGeneration;
        var entry = new OpenXrSmokeTemporalStateLedgerEntry(
            renderFrameId,
            pipelineInstanceId,
            eyeIndex,
            (int)state.HistoryIsolationPolicy,
            generation.ProfileGeneration,
            generation.ExpectedLayerMask,
            generation.CurrentMatrixLayerMask,
            state.PendingHistoryCoverage.ColorLayerMask,
            state.PendingHistoryCoverage.DepthLayerMask,
            state.PendingHistoryCoverage.TsrColorLayerMask,
            committedLayerMask,
            historyReadyBeforeCommit,
            state.HistoryReady,
            eyeReadyBeforeCommit,
            eyeReadyAfterCommit,
            resetGeneration,
            seededGeneration,
            previousFingerprint,
            currentFingerprint,
            eye.PreviousJitter.X,
            eye.PreviousJitter.Y,
            eye.CurrentJitter.X,
            eye.CurrentJitter.Y,
            state.ResetReasonThisFrame,
            (state.CameraCutLayerMaskThisFrame & eyeMask) != 0u,
            (committedLayerMask & eyeMask) != 0u);
        Phase524bTemporalStateDiagnostics.Record(entry);
    }

    private static ulong ComputeMatrixFingerprint(in Matrix4x4 matrix)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        Hash(matrix.M11); Hash(matrix.M12); Hash(matrix.M13); Hash(matrix.M14);
        Hash(matrix.M21); Hash(matrix.M22); Hash(matrix.M23); Hash(matrix.M24);
        Hash(matrix.M31); Hash(matrix.M32); Hash(matrix.M33); Hash(matrix.M34);
        Hash(matrix.M41); Hash(matrix.M42); Hash(matrix.M43); Hash(matrix.M44);
        return hash;

        void Hash(float value)
        {
            hash ^= unchecked((uint)BitConverter.SingleToInt32Bits(value));
            hash *= prime;
        }
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

    private static bool IsCameraCut(XRCamera camera, TemporalEyeState eyeState)
        => IsCameraDiscontinuity(
            camera.Transform.RenderTranslation,
            camera.Transform.RenderForward,
            eyeState.PreviousCameraPosition,
            eyeState.PreviousCameraForward,
            CameraCutTranslationThreshold,
            CameraCutRotationThresholdDegrees);

    internal static bool IsCameraDiscontinuity(
        Vector3 currentPosition,
        Vector3 currentForward,
        Vector3 previousPosition,
        Vector3 previousForward,
        float translationThreshold,
        float rotationThresholdDegrees)
    {
        float translationDeltaSq = Vector3.DistanceSquared(
            currentPosition,
            previousPosition);
        float clampedTranslationThreshold = Math.Max(translationThreshold, 0.0f);
        if (translationDeltaSq > clampedTranslationThreshold * clampedTranslationThreshold)
            return true;

        float directionDot = Vector3.Dot(
            NormalizeOrForward(currentForward),
            NormalizeOrForward(previousForward));
        float clampedRotationDegrees = Math.Clamp(rotationThresholdDegrees, 0.0f, 180.0f);
        float rotationThresholdDot = MathF.Cos(clampedRotationDegrees * MathF.PI / 180.0f);
        return directionDot < rotationThresholdDot;
    }

    internal static bool IsTemporalMatrixFinite(in Matrix4x4 matrix)
        => float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14)
            && float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24)
            && float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34)
            && float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

    private static Vector3 NormalizeOrForward(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-8f
            ? value / MathF.Sqrt(lengthSquared)
            : Vector3.UnitZ;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        if (Phase != EPhase.Accumulate)
            return;

        EAntiAliasingMode antiAliasingMode = ResolveAntiAliasingMode();
        if (ShouldPopulateTemporalInput(antiAliasingMode))
            DescribeTemporalInputCopy(context);

        if (ShouldRunInternalAccumulation(antiAliasingMode))
            DescribeTaaAccumulation(context);
        else
            DescribeHistoryPassthrough(context);
    }

    private void DescribeTemporalInputCopy(RenderGraphDescribeContext context)
    {
        context.GetOrCreateSyntheticPass(TemporalInputCopyPassName, ERenderGraphPassStage.Transfer)
            .UseTransferSource(MakeFboColorResource(ForwardFBOName))
            .UseTransferDestination(MakeFboColorResource(TemporalInputFBOName));
    }

    private void DescribeTaaAccumulation(RenderGraphDescribeContext context)
    {
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
