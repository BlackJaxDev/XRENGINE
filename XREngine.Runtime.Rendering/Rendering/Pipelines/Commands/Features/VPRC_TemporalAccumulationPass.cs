using System;
using System.Collections.Generic;
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
    private const float CameraCutTranslationThreshold = 2.0f;
    private const float CameraCutRotationDotThreshold = 0.94f;
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

    internal readonly struct TemporalUniformData
    {
        public bool HistoryReady { get; init; }
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
            (RuntimeRenderingHostServices.Current.IsOpenXrRuntimeRequested ||
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
            RuntimeRenderingHostServices.Current.EnableOpenXrVulkanParallelRendering,
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
                TemporalEyeState leftEye = state.LeftEye;
                TemporalEyeState rightEye = state.RightEye;
                data = new TemporalUniformData
                {
                    HistoryReady = state.HistoryReady,
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
                    Width = state.LastInternalWidth,
                    Height = state.LastInternalHeight
                };
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
                TemporalEyeState leftEye = state.LeftEye;
                TemporalEyeState rightEye = state.RightEye;
                data = new TemporalUniformData
                {
                    HistoryReady = state.HistoryReady,
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
                    Width = state.LastInternalWidth,
                    Height = state.LastInternalHeight
                };
                return true;
            }
        }

        data = default;
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
        uint width = viewport is null ? 0u : (uint)viewport.InternalWidth;
        uint height = viewport is null ? 0u : (uint)viewport.InternalHeight;
        uint fullWidth = viewport is null ? 0u : (uint)viewport.Width;
        uint fullHeight = viewport is null ? 0u : (uint)viewport.Height;
        EVrTemporalHistoryPolicy policy = ResolveHistoryIsolationPolicy(out _);
        int stereoEyeIndex = policy == EVrTemporalHistoryPolicy.PerEye
            ? ResolveCurrentStereoEyeIndex(camera)
            : -1;
        int renderTargetProfile = HashCode.Combine(
            width,
            height,
            fullWidth,
            fullHeight,
            (int)policy,
            IsRenderingExternalSwapchainTarget());

        key = new TemporalViewKey(
            RuntimeHelpers.GetHashCode(instance),
            viewport is null ? 0 : RuntimeHelpers.GetHashCode(viewport),
            RuntimeHelpers.GetHashCode(camera),
            stereoEyeIndex,
            stereoEyeIndex,
            renderTargetProfile);
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
        AbstractRenderer? renderer = RuntimeRenderingHostServices.Current.CurrentRenderer as AbstractRenderer
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

        var viewport = instance.RenderState.WindowViewport;
        uint width = viewport is null ? 0u : (uint)viewport.InternalWidth;
        uint height = viewport is null ? 0u : (uint)viewport.InternalHeight;
        uint fullWidth = viewport is null ? 0u : (uint)viewport.Width;
        uint fullHeight = viewport is null ? 0u : (uint)viewport.Height;
        EAntiAliasingMode antiAliasingMode = ResolveAntiAliasingMode();
        EVrTemporalHistoryPolicy historyPolicy = ResolveHistoryIsolationPolicy(out string historyPolicyReason);
        state.HistoryIsolationPolicy = historyPolicy;
        state.HistoryIsolationReason = historyPolicyReason;

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
            ResetHistory(state);
            //Debug.Out($"[Temporal] Resolution change detected. New={width}x{height}; history reset.");
        }

        state.ActiveJitterHandle?.Dispose();
        state.ActiveJitterHandle = null;
        state.ActiveRightEyeJitterHandle?.Dispose();
        state.ActiveRightEyeJitterHandle = null;

        if (!TryResolveTemporalCamera(instance, out XRCamera? camera))
        {
            state.LeftEye.ResetCurrent();
            state.RightEye.ResetCurrent();
            state.HistoryReady = false;
            Debug.Rendering("[Temporal] Begin: no camera; resetting state.");
            return;
        }

        XRCamera? rightEyeCamera = instance.RenderState.StereoRightEyeCamera
            ?? RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
        if (state.HistoryReady &&
            (IsCameraCut(camera, state.LeftEye) ||
             (rightEyeCamera is not null &&
              !ReferenceEquals(rightEyeCamera, camera) &&
              IsCameraCut(rightEyeCamera, state.RightEye))))
        {
            ResetHistory(state);
        }

        uint expectedHistoryLayers = historyPolicy == EVrTemporalHistoryPolicy.StereoArrayLayer ? 2u : 1u;
        state.PendingHistoryCoverage.Begin(
            expectedHistoryLayers,
            requiresTsrColor: antiAliasingMode == EAntiAliasingMode.Tsr && !IsHistoryIsolationPolicyDisabled(historyPolicy));

        bool jitterEnabled = ShouldUseTemporalJitter(antiAliasingMode);
        Vector2 jitter = jitterEnabled ? GenerateJitter(state, antiAliasingMode) : Vector2.Zero;
        state.LeftEye.CurrentJitter = jitter;
        state.RightEye.CurrentJitter = jitter;
        //Debug.Out($"[Temporal] JitterEnabled={jitterEnabled} Jitter=({jitter.X:F6},{jitter.Y:F6}) HaltonIndex={state.HaltonIndex}");

        // Exposure history is only reliable when we are actively running temporal accumulation.
        if (!jitterEnabled)
            state.HistoryExposureReady = false;

        Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
        state.LeftEye.CurrViewMatrix = viewMatrix;
        state.LeftEye.CurrentCameraPosition = camera.Transform.RenderTranslation;
        state.LeftEye.CurrentCameraForward = NormalizeOrForward(camera.Transform.RenderForward);
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
        {
            state.ActiveJitterHandle = instance.RenderState.RequestCameraProjectionJitter(jitter);
            if (rightEyeCamera is not null && !ReferenceEquals(rightEyeCamera, camera))
            {
                Vector2 resolution = new(Math.Max(1u, width), Math.Max(1u, height));
                state.ActiveRightEyeJitterHandle = rightEyeCamera.PushProjectionJitter(
                    ProjectionJitterRequest.TexelSpace(jitter, resolution));
            }
        }

        Matrix4x4 jitteredProjection = camera.ProjectionMatrix;
        Matrix4x4 jitteredViewProjection = viewMatrix * jitteredProjection;
        if (!Matrix4x4.Invert(jitteredViewProjection, out Matrix4x4 inverseViewProjection))
            inverseViewProjection = Matrix4x4.Identity;

        state.CurrViewProjectionUnjittered = baseViewProjection;
        state.CurrProjection = jitteredProjection;
        state.CurrInverseProjection = camera.InverseProjectionMatrix;
        state.CurrViewProjection = jitteredViewProjection;
        state.CurrInverseViewProjection = inverseViewProjection;
        CaptureEyeTemporalState(rightEyeCamera ?? camera, state.RightEye, state.HistoryReady);
        //Debug.Out("[Temporal] Begin completed: VP/Jitter prepared.");
    }

    private static void CaptureEyeTemporalState(XRCamera camera, TemporalEyeState eyeState, bool historyReady)
    {
        Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
        Matrix4x4 baseProjection = camera.ProjectionMatrixUnjittered;
        Matrix4x4 baseViewProjection = viewMatrix * baseProjection;

        if (historyReady && IsMatrixApproximatelyEqual(baseViewProjection, eyeState.PrevViewProjectionUnjittered))
            baseViewProjection = eyeState.PrevViewProjectionUnjittered;

        Matrix4x4 jitteredProjection = camera.ProjectionMatrix;
        Matrix4x4 jitteredViewProjection = viewMatrix * jitteredProjection;
        if (!Matrix4x4.Invert(jitteredViewProjection, out Matrix4x4 inverseViewProjection))
            inverseViewProjection = Matrix4x4.Identity;

        eyeState.CurrViewMatrix = viewMatrix;
        eyeState.CurrentCameraPosition = camera.Transform.RenderTranslation;
        eyeState.CurrentCameraForward = NormalizeOrForward(camera.Transform.RenderForward);
        eyeState.CurrViewProjectionUnjittered = baseViewProjection;
        eyeState.CurrProjection = jitteredProjection;
        eyeState.CurrInverseProjection = camera.InverseProjectionMatrix;
        eyeState.CurrViewProjection = jitteredViewProjection;
        eyeState.CurrInverseViewProjection = inverseViewProjection;
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
        state.PendingHistoryReady = state.PendingHistoryCoverage.IsComplete;
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
        state.PendingHistoryReady = state.PendingHistoryCoverage.IsComplete;
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

            uint layerCount = layerIndex >= 0
                ? 1u
                : target switch
                {
                    XRTextureViewBase view => Math.Max(view.NumLayers, 1u),
                    XRTexture2DArray array => Math.Max(array.Depth, 1u),
                    _ => 1u,
                };
            uint clampedLayerCount = Math.Clamp(layerCount, 1u, 32u);
            return clampedLayerCount == 32u
                ? uint.MaxValue
                : (1u << (int)clampedLayerCount) - 1u;
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

        if (!state.PendingHistoryCoverage.IsComplete)
        {
            state.PendingHistoryReady = false;
            state.HistoryReady = false;
            TryResolveTemporalKey(instance, out TemporalViewKey temporalKey);
            Debug.RenderingWarningEvery(
                $"Temporal.HistoryCoverageIncomplete.{instance.InstanceId}",
                TimeSpan.FromSeconds(1),
                "[Temporal] History invalidated because the frame did not populate every required layer. Pipeline={0} Policy={1} Key={2} Coverage={3}",
                instance.ProfilerKey,
                state.HistoryIsolationPolicy,
                temporalKey,
                state.PendingHistoryCoverage);
            state.PendingHistoryCoverage.Clear();
            return;
        }

        state.PendingHistoryReady = false;
        state.LeftEye.CommitCurrentToPrevious();
        state.RightEye.CommitCurrentToPrevious();
        state.HistoryReady = true;
        state.PendingHistoryCoverage.Clear();
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

    private static bool IsCameraCut(XRCamera camera, TemporalEyeState eyeState)
    {
        float translationDeltaSq = Vector3.DistanceSquared(
            camera.Transform.RenderTranslation,
            eyeState.PreviousCameraPosition);
        if (translationDeltaSq > CameraCutTranslationThreshold * CameraCutTranslationThreshold)
            return true;

        float directionDot = Vector3.Dot(
            NormalizeOrForward(camera.Transform.RenderForward),
            NormalizeOrForward(eyeState.PreviousCameraForward));
        return directionDot < CameraCutRotationDotThreshold;
    }

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
