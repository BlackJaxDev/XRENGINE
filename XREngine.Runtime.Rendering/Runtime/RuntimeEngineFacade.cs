using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Valve.VR;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models;
using XREngine.Rendering.Pipelines;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Timers;

namespace XREngine;

internal static partial class Engine
{
    public static float Delta => Time.Timer.Update.Delta;
    public static long ElapsedTicks => RuntimeRenderingHostServices.Current.ElapsedTicks;
    public static float ElapsedTime => RuntimeRenderingHostServices.Current.ElapsedTime;
    public static bool IsEditor => false;
    public static bool IsRenderThread => RuntimeRenderingHostServices.Current.IsRenderThread;
    public static bool IsDispatchingRenderFrame { get; set; }
    public static bool StartupPresentationEnabled { get; set; }
    public static ColorF4 StartupPresentationClearColor { get; set; } = ColorF4.Black;

    public static RuntimeTime Time { get; } = new();
    public static RuntimePlayMode PlayMode { get; } = new();
    public static RuntimeGameSettings GameSettings { get; } = new();
    public static RuntimeEditorPreferences EditorPreferences { get; } = new();
    public static RuntimeEffectiveSettings EffectiveSettings { get; } = new();
    public static UserSettings UserSettings { get; } = new();
    public static RuntimeAssetFacade Assets { get; } = new();
    public static RuntimeProfilerFacade Profiler { get; } = new();
    public static JobManager Jobs { get; } = new();
    public static RuntimeVrState VRState { get; } = new();
    public static IEnumerable<XRWindow> Windows => [];

    public static IEnumerable<XRViewport> EnumerateActiveViewports()
        => RuntimeRenderingHostServices.Current.EnumerateActiveViewports().OfType<XRViewport>();

    public static IEnumerable<(XRWindow Window, XRViewport Viewport)> EnumerateActiveWindowViewports()
    {
        yield break;
    }

    public static void ProcessMainThreadTasks()
        => RuntimeRenderingHostServices.Current.ProcessRenderThreadTasks();

    public static void EnqueueMainThreadTask(Action action, string? name = null)
        => RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(action, name ?? "main-thread facade task");

    public static void EnqueueRenderThreadTask(Action action, string? name = null)
        => RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(action, name ?? "render-thread facade task");

    public static void EnqueueAppThreadTask(Action action, string? name = null)
        => RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(action, name ?? "app-thread facade task");

    public static bool InvokeOnMainThread(Action action, string? name = null, bool forceSynchronous = false, bool executeNowIfAlreadyMainThread = false)
    {
        if (IsRenderThread)
        {
            if (executeNowIfAlreadyMainThread)
                action();
            return false;
        }

        if (forceSynchronous)
        {
            action();
            return false;
        }

        EnqueueMainThreadTask(action, name);
        return true;
    }

    public static void AddMainThreadCoroutine(Func<bool> step, string? name = null)
        => EnqueueMainThreadTask(() => step(), name);

    public static void AddRenderThreadCoroutine(Func<bool> step, string? name = null)
        => EnqueueRenderThreadTask(() => step(), name);

    public static string GetStackTrace() => Environment.StackTrace;
    public static void LogWarning(string message, EOutputVerbosity verbosity = EOutputVerbosity.Normal, ELogCategory category = ELogCategory.General)
        => Debug.Out(message);

    public static RuntimeEngineState State { get; } = new();

    public static partial class Rendering
    {
        private static readonly Stack<XRRenderPipelineInstance> PipelineStack = new();
        private static readonly Stack<XRRenderPipelineInstance> PipelineOverrideStack = new();

        public static RuntimeRenderSettings Settings { get; } = new();
        private static RuntimeRenderingState StateData { get; } = new();
        public static RuntimeBvhStats BvhStats { get; } = new();
        public static event Action? SettingsChanged;
        public static event Action? AntiAliasingSettingsChanged;

        public static string VulkanUpscaleBridgeEnvVar => "XRE_ENABLE_VULKAN_UPSCALE_BRIDGE";
        public static bool VulkanUpscaleBridgeRequested => IsEnvFlagEnabled(VulkanUpscaleBridgeEnvVar, defaultValue: true);
        public static VulkanUpscaleBridgeCapabilitySnapshot VulkanUpscaleBridgeSnapshot { get; } = new();
        public static bool VulkanUpscaleBridgeDx12InteropEnabled => false;
        public static bool VulkanUpscaleBridgeImplicitModeEnabled => false;

        private static bool IsEnvFlagEnabled(string name, bool defaultValue)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

            raw = raw.Trim();

            if (string.Equals(raw, "1", StringComparison.Ordinal))
                return true;

            if (string.Equals(raw, "0", StringComparison.Ordinal))
                return false;

            return bool.TryParse(raw, out bool enabled)
                ? enabled
                : defaultValue;
        }

        public static class Constants
        {
            public const string ShadowExponentBaseUniform = "ShadowBase";
            public const string ShadowExponentUniform = "ShadowMult";
            public const string ShadowBiasMinUniform = "ShadowBiasMin";
            public const string ShadowBiasMaxUniform = "ShadowBiasMax";
            public const string BoneTransformsName = "Transforms";
            public const string MorphWeightsName = "MorphWeights";
            public const string LightsStructName = "LightData";
            public const string EngineFontsCommonFolderName = "Fonts";
            public const string ShadowSamples = "ShadowSamples";
            public const string ShadowBlockerSamples = "ShadowBlockerSamples";
            public const string ShadowFilterSamples = "ShadowFilterSamples";
            public const string ShadowVogelTapCount = "ShadowVogelTapCount";
            public const string ShadowFilterRadius = "ShadowFilterRadius";
            public const string ShadowBlockerSearchRadius = "ShadowBlockerSearchRadius";
            public const string ShadowMinPenumbra = "ShadowMinPenumbra";
            public const string ShadowMaxPenumbra = "ShadowMaxPenumbra";
            public const string SoftShadowMode = "SoftShadowMode";
            public const string LightSourceRadius = "LightSourceRadius";
            public const string EnableCascadedShadows = "EnableCascadedShadows";
            public const string EnableContactShadows = "EnableContactShadows";
            public const string ContactShadowDistance = "ContactShadowDistance";
            public const string ContactShadowSamples = "ContactShadowSamples";
            public const string ContactShadowThickness = "ContactShadowThickness";
            public const string ContactShadowFadeStart = "ContactShadowFadeStart";
            public const string ContactShadowFadeEnd = "ContactShadowFadeEnd";
            public const string ContactShadowNormalOffset = "ContactShadowNormalOffset";
            public const string ContactShadowJitterStrength = "ContactShadowJitterStrength";
        }

        public static RenderPipeline NewRenderPipeline(bool stereo = false)
            => new DefaultRenderPipeline(stereo);

        public static IDisposable? PushRenderingPipeline(XRRenderPipelineInstance pipeline)
        {
            PipelineStack.Push(pipeline);
            return new DisposableAction(() => PipelineStack.Pop());
        }

        public static IDisposable? PushRenderingPipelineOverride(XRRenderPipelineInstance pipeline)
        {
            PipelineOverrideStack.Push(pipeline);
            return new DisposableAction(() => PipelineOverrideStack.Pop());
        }

        public static XRCamera.EDepthMode ResolveSceneCameraDepthModePreference()
            => RuntimeRenderingHostServices.Current.ResolveSceneCameraDepthModePreference();

        public static void PrepareVulkanUpscaleBridgeForFrame(VulkanRenderer renderer, XRViewport? viewport, int width, int height)
        {
        }

        public static void ReleaseVulkanUpscaleBridge()
        {
        }

        public static void ReleaseVulkanUpscaleBridge(XRViewport viewport, string? reason = null)
        {
        }

        public static VulkanUpscaleBridge? GetVulkanUpscaleBridge(XRViewport viewport)
            => null;

        public static string DescribeVulkanUpscaleBridgeUnavailability(XRViewport viewport, bool outputHdr)
            => "Vulkan upscale bridge is not configured.";

        public static EVulkanUpscaleBridgeQueueModel VulkanUpscaleBridgeQueueModel => EVulkanUpscaleBridgeQueueModel.Graphics;
        public static Func<XRWindow, BoundingRectangle?>? ScenePanelRenderRegionProvider { get; set; }

        public static void RefreshVulkanUpscaleBridgeCapabilitySnapshot()
        {
        }

        public static void RefreshVulkanUpscaleBridgeCapabilitySnapshot(object? renderer)
        {
        }

        public static bool IsVulkanRendererActive()
            => RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan;

        public static void ApplyGpuRenderDispatchToPipeline(object? pipeline, bool enabled)
        {
        }

        public static bool ResolveGpuRenderDispatchPreference(bool requested)
            => VulkanFeatureProfile.ResolveGpuRenderDispatchPreference(requested);

        internal static void RaiseSettingsChanged() => SettingsChanged?.Invoke();
        internal static void RaiseAntiAliasingSettingsChanged() => AntiAliasingSettingsChanged?.Invoke();

        public static class Debug
        {
            public static void RenderLine(Vector3 start, Vector3 end, ColorF4 color)
            {
            }

            public static void RenderSphere(Vector3 center, float radius, bool solid, ColorF4 color) { }
            public static void RenderCone(Vector3 center, Vector3 up, float radius, float height, bool solid, ColorF4 color) { }
            public static void RenderAABB(Vector3 halfExtents, Vector3 center, bool solid, ColorF4 color) { }
            public static void RenderBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color) { }
            public static void RenderQuad(Vector3 center, Quaternion rotation, Vector2 extents, bool solid, ColorF4 color) { }
            public static void RenderQuad(Vector3 center, object rotation, Vector2 extents, bool solid, ColorF4 color) { }
            public static void RenderPoint(Vector3 position, ColorF4 color) { }
            public static void RenderText(Vector3 position, string text, ColorF4 color) { }
            public static void RenderShapes() { }
        }

        public static class Stats
        {
            private static long _trackedVramBytes;
            public static bool EnableTracking { get; set; }
            public static int DrawCalls { get; private set; }
            public static int GpuCpuFallbackEvents { get; private set; }
            public static int GpuCpuFallbackRecoveredCommands { get; private set; }
            public static int GpuTransparencyOpaqueOrOtherVisible { get; private set; }
            public static int GpuTransparencyMaskedVisible { get; private set; }
            public static int GpuTransparencyApproximateVisible { get; private set; }
            public static int GpuTransparencyExactVisible { get; private set; }
            public static long VulkanRequestedDraws { get; private set; }
            public static long VulkanCulledDraws { get; private set; }
            public static long VulkanEmittedIndirectDraws { get; private set; }
            public static long VulkanConsumedDraws { get; private set; }
            public static int VulkanQueueOwnershipTransfers { get; private set; }
            public static int VulkanBarrierStageFlushes { get; private set; }
            public static int VulkanDescriptorBindSkips { get; private set; }
            public static int VulkanDescriptorFallbacksCurrentFrame { get; private set; }
            public static int VulkanDescriptorBindingFailuresCurrentFrame { get; private set; }
            public static int VulkanOomFallbackCount { get; private set; }
            public static int VulkanValidationMessageCountCurrentFrame { get; private set; }
            public static int VulkanValidationErrorCountCurrentFrame { get; private set; }

            public enum EVulkanGpuDrivenStageTiming
            {
                Reset,
                Cull,
                Occlusion,
                Indirect,
                Draw,
            }

            public enum EVulkanAllocationTelemetryClass
            {
                DeviceLocal,
                Upload,
                Readback,
            }

            public static void IncrementDrawCalls(int count = 1) => DrawCalls += count;
            public static void IncrementMultiDrawCalls(int count = 1) { }
            public static void AddTrianglesRendered(int count) { }
            public static void AddBufferAllocation(long bytes) => _trackedVramBytes += bytes;
            public static void RemoveBufferAllocation(long bytes) => _trackedVramBytes = Math.Max(0L, _trackedVramBytes - bytes);
            public static void AddTextureAllocation(long bytes) => _trackedVramBytes += bytes;
            public static void RemoveTextureAllocation(long bytes) => _trackedVramBytes = Math.Max(0L, _trackedVramBytes - bytes);
            public static void AddRenderBufferAllocation(long bytes) => _trackedVramBytes += bytes;
            public static void RemoveRenderBufferAllocation(long bytes) => _trackedVramBytes = Math.Max(0L, _trackedVramBytes - bytes);
            public static bool CanAllocateVram(long requestedBytes, long currentAllocationBytes, out long projectedBytes, out long budgetBytes)
            {
                budgetBytes = RuntimeRenderingHostServices.Current.TrackedVramBudgetBytes;
                projectedBytes = Math.Max(0L, _trackedVramBytes - currentAllocationBytes + requestedBytes);
                return projectedBytes <= budgetBytes;
            }

            public static void RecordGpuBufferMapped() { }
            public static void RecordGpuReadbackBytes(long bytes) { }
            public static void RecordGpuCpuFallback(int events, int recoveredCommands)
            {
                GpuCpuFallbackEvents += events;
                GpuCpuFallbackRecoveredCommands += recoveredCommands;
            }
            public static void RecordForbiddenGpuFallback(int events) { }
            public static void RecordGpuTransparencyDomainCounts(int opaqueOrOther, int masked, int approximate, int exact)
            {
                GpuTransparencyOpaqueOrOtherVisible = opaqueOrOther;
                GpuTransparencyMaskedVisible = masked;
                GpuTransparencyApproximateVisible = approximate;
                GpuTransparencyExactVisible = exact;
            }
            public static void RecordGpuTransparencyDomainCounts(uint opaqueOrOther, uint masked, uint approximate, uint exact)
                => RecordGpuTransparencyDomainCounts((int)opaqueOrOther, (int)masked, (int)approximate, (int)exact);
            public static void RecordOctreeCollect(int visibleRenderables, int emittedCommands) { }
            public static void RecordRtxIoCopyIndirect(long bytes, TimeSpan elapsed) { }
            public static void RecordRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan elapsed) { }
            public static void RecordSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded) { }
            public static void RecordSkinnedBoundsRefreshDeferredScheduled() { }
            public static void RecordSkinnedBoundsRefreshGpuCompleted(long gpuTicks, long applyTicks) { }
            public static void RecordVrCommandBuildTimes(params object?[] values) { }
            public static void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws) { }
            public static void RecordVrPerViewVisibleCounts(uint leftVisible, uint rightVisible) { }
            public static void RecordVrRenderSubmitTime(TimeSpan elapsed) { }
            public static void RecordVulkanAdhocBarrier(int emittedCount = 0, int redundantCount = 0) { }
            public static void RecordVulkanAllocation(EVulkanAllocationTelemetryClass allocationClass, long bytes) { }
            public static void RecordVulkanBarrierPlannerPass(int imageBarrierCount = 0, int bufferBarrierCount = 0, int queueOwnershipTransfers = 0, int stageFlushes = 0)
            {
                VulkanQueueOwnershipTransfers += queueOwnershipTransfers;
                VulkanBarrierStageFlushes += stageFlushes;
            }
            public static void RecordVulkanBindChurn(
                int pipelineBinds = 0,
                int descriptorBinds = 0,
                int pushConstantWrites = 0,
                int vertexBufferBinds = 0,
                int indexBufferBinds = 0,
                int pipelineBindSkips = 0,
                int descriptorBindSkips = 0,
                int vertexBufferBindSkips = 0,
                int indexBufferBindSkips = 0)
            {
                VulkanDescriptorBindSkips += descriptorBindSkips;
            }
            public static void RecordVulkanDescriptorBindingFailure(string programName = "", string setName = "", string bindingName = "", long set = 0, long binding = 0, bool skippedDraw = false, bool skippedDispatch = false, string? reason = null)
                => VulkanDescriptorBindingFailuresCurrentFrame++;
            public static void RecordVulkanDescriptorFallback(params object?[] values) => VulkanDescriptorFallbacksCurrentFrame++;
            public static void RecordVulkanDescriptorPoolCreate() { }
            public static void RecordVulkanDescriptorPoolDestroy() { }
            public static void RecordVulkanDescriptorPoolReset() { }
            public static void RecordVulkanDynamicUniformAllocation(long bytes) { }
            public static void RecordVulkanDynamicUniformExhaustion() { }
            public static void RecordVulkanFrameDiagnostics(params object?[] values) { }
            public static void RecordVulkanFrameGpuCommandBufferTime(TimeSpan elapsed) { }
            public static void RecordVulkanFrameLifecycleTiming(params object?[] values) { }
            public static void RecordVulkanGpuDrivenStageTiming(EVulkanGpuDrivenStageTiming stage, TimeSpan elapsed) { }
            public static void RecordVulkanIndirectBatchMerge(int requestedBatches, int mergedBatches) { }
            public static void RecordVulkanIndirectEffectiveness(uint requestedDraws, uint culledDraws, uint emittedIndirectDraws, uint consumedDraws, uint overflowCount = 0u)
            {
                VulkanRequestedDraws = requestedDraws;
                VulkanCulledDraws = culledDraws;
                VulkanEmittedIndirectDraws = emittedIndirectDraws;
                VulkanConsumedDraws = consumedDraws;
            }
            public static void RecordVulkanIndirectRecordingMode(bool usedSecondary = false, bool usedParallel = false, int opCount = 0) { }
            public static void RecordVulkanIndirectSubmission(bool usedCountPath = false, bool usedLoopFallback = false, int apiCalls = 0, uint submittedDraws = 0u) { }
            public static void RecordVulkanOomFallback() => VulkanOomFallbackCount++;
            public static void RecordVulkanPipelineCacheLookup(bool cacheHit) { }
            public static void RecordVulkanPipelineCacheMiss(string summary) { }
            public static void RecordVulkanQueueOverlapWindow(params object?[] values) { }
            public static void RecordVulkanQueueSubmit() { }
            public static void RecordVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount) { }
            public static void RecordVulkanValidationMessage(bool isError, string message)
            {
                VulkanValidationMessageCountCurrentFrame++;
                if (isError)
                    VulkanValidationErrorCountCurrentFrame++;
            }
        }

        public static class State
        {
            public static ulong RenderFrameId => StateData.RenderFrameId;
            public static IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState => StateData.ActiveRenderCommandExecutionState;
            public static int CurrentRenderGraphPassIndex => StateData.CurrentRenderGraphPassIndex;
            public static uint CurrentTransformId => StateData.CurrentTransformId;
            public static XRRenderPipelineInstance? CurrentRenderingPipeline => StateData.CurrentRenderingPipeline;
            public static RenderResourceRegistry? CurrentResourceRegistry => CurrentRenderingPipeline?.Resources;
            public static XRRenderPipelineInstance.RenderingState? RenderingPipelineState => StateData.RenderingPipelineState;
            public static XRViewport? RenderingViewport => StateData.RenderingViewport;
            public static IRuntimeRenderWorld? RenderingWorld => StateData.RenderingWorld;
            public static VisualScene? RenderingScene => RenderingPipelineState?.Scene;
            public static XRCamera? RenderingCamera => StateData.RenderingCamera;
            public static XRCamera? RenderingStereoRightEyeCamera => RenderingPipelineState?.StereoRightEyeCamera;
            public static XRFrameBuffer? RenderingTargetOutputFBO => RenderingPipelineState?.OutputFBO;
            public static XRMaterial? OverrideMaterial => RenderingPipelineState?.OverrideMaterial;
            public static XRCamera? RenderingCameraOverride
            {
                get => StateData.RenderingCameraOverride;
                set => StateData.RenderingCameraOverride = value;
            }
            public static BoundingRectangle RenderArea => StateData.RenderArea;
            public static float DefaultDepthClearValue => StateData.DefaultDepthClearValue;
            public static bool IsShadowPass => StateData.IsShadowPass;
            public static bool IsStereoPass => RenderingPipelineState?.StereoPass ?? false;
            public static bool IsSceneCapturePass
            {
                get => StateData.IsSceneCapturePass;
                set => StateData.IsSceneCapturePass = value;
            }
            public static bool IsLightProbePass
            {
                get => StateData.IsLightProbePass;
                set => StateData.IsLightProbePass = value;
            }
            public static int MirrorPassIndex => StateData.MirrorPassIndex;
            public static bool IsMirrorPass => StateData.IsMirrorPass;
            public static bool IsReflectedMirrorPass => (MirrorPassIndex & 1) == 1;
            public static bool IsMainPass => !IsMirrorPass && !IsSceneCapturePass && !IsLightProbePass;
            public static bool ReverseWinding { get; internal set; }
            public static bool ReverseCulling { get; internal set; }
            public static bool HasOvrMultiViewExtension { get; internal set; }
            public static bool HasVulkanMultiView { get; internal set; }
            public static bool HasAnyMultiViewExtension => HasOvrMultiViewExtension || HasVulkanMultiView;
            public static bool DebugInstanceRenderingAvailable { get; internal set; } = true;
            public static bool IsNVIDIA { get; internal set; }
            public static bool IsIntel { get; internal set; }
            public static bool IsVulkan { get; internal set; }
            public static bool HasNvRayTracing { get; internal set; }
            public static bool HasVulkanRayTracing { get; internal set; }
            public static bool HasVulkanMemoryDecompression { get; internal set; }
            public static bool HasVulkanCopyMemoryIndirect { get; internal set; }
            public static bool HasVulkanRtxIo { get; internal set; }
            public static bool HasParallelShaderCompile { get; internal set; }
            public static string[] OpenGLExtensions { get; internal set; } = [];
            public static string? OpenGLVendor { get; internal set; }
            public static string? OpenGLRendererName { get; internal set; }
            public static string? VulkanDeviceName { get; internal set; }
            public static uint VulkanVendorId { get; internal set; }
            public static uint VulkanDeviceId { get; internal set; }
            public static XRDataBuffer? ForwardPlusLocalLightsBuffer { get; internal set; }
            public static XRDataBuffer? ForwardPlusVisibleIndicesBuffer { get; internal set; }
            public static Vector2 ForwardPlusScreenSize { get; internal set; }
            public static int ForwardPlusTileSize { get; internal set; }
            public static int ForwardPlusTileCountX { get; internal set; }
            public static int ForwardPlusTileCountY { get; internal set; }
            public static int ForwardPlusMaxLightsPerTile { get; internal set; }
            public static int ForwardPlusLocalLightCount { get; internal set; }
            public static bool ForwardPlusEnabled => ForwardPlusLocalLightsBuffer is not null && ForwardPlusVisibleIndicesBuffer is not null && ForwardPlusLocalLightCount > 0;

            public static IDisposable PushRenderGraphPassIndex(int passIndex) => StateData.PushRenderGraphPassIndex(passIndex);
            public static IDisposable PushTransformId(uint transformId) => StateData.PushTransformId(transformId);
            public static IDisposable? PushRenderingPipeline(XRRenderPipelineInstance pipeline) => Rendering.PushRenderingPipeline(pipeline);
            public static IDisposable? PushRenderingPipelineOverride(XRRenderPipelineInstance? pipeline)
                => pipeline is null ? DisposableAction.Empty : Rendering.PushRenderingPipelineOverride(pipeline);
            public static void PushMirrorPass() => StateData.PushMirrorPass();
            public static void PopMirrorPass() => StateData.PopMirrorPass();
            internal static void BeginRenderFrame() => StateData.BeginRenderFrame();

            public static void ClearColor(ColorF4 color) => AbstractRenderer.Current?.ClearColor(color);
            public static void ClearStencil(int value) => AbstractRenderer.Current?.ClearStencil(value);
            public static void ClearDepth(float value) => AbstractRenderer.Current?.ClearDepth(value);
            public static void Clear(bool color, bool depth, bool stencil) => AbstractRenderer.Current?.Clear(color, depth, stencil);
            public static void ClearByBoundFBO(bool color = true, bool depth = true, bool stencil = true)
            {
                if (depth)
                    ClearDepth(GetDefaultDepthClearValue());

                var boundFBO = XRFrameBuffer.BoundForWriting;
                if (boundFBO is null)
                {
                    Clear(color, depth, stencil);
                    return;
                }

                var textureTypes = boundFBO.TextureTypes;
                Clear(
                    textureTypes.HasFlag(EFrameBufferTextureTypeFlags.Color) && color,
                    textureTypes.HasFlag(EFrameBufferTextureTypeFlags.Depth) && depth,
                    textureTypes.HasFlag(EFrameBufferTextureTypeFlags.Stencil) && stencil);
            }
            public static void UnbindFrameBuffers(EFramebufferTarget target) => AbstractRenderer.Current?.BindFrameBuffer(target, null);
            public static void SetReadBuffer(EReadBufferMode mode) => AbstractRenderer.Current?.SetReadBuffer(mode);
            public static void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode) => AbstractRenderer.Current?.SetReadBuffer(fbo, mode);
            public static float GetDepth(int x, int y) => AbstractRenderer.Current?.GetDepth(x, y) ?? 0.0f;
            public static Task<float> GetDepthAsync(XRFrameBuffer fbo, int x, int y)
            {
                var completion = new TaskCompletionSource<float>();
                AbstractRenderer.Current?.GetDepthAsync(fbo, x, y, completion.SetResult);
                return completion.Task;
            }
            public static byte GetStencilIndex(float x, float y) => AbstractRenderer.Current?.GetStencilIndex(x, y) ?? 0;
            public static void EnableDepthTest(bool enable) => AbstractRenderer.Current?.EnableDepthTest(enable);
            public static void StencilMask(uint mask) => AbstractRenderer.Current?.StencilMask(mask);
            public static void EnableStencilTest(bool enable) => AbstractRenderer.Current?.EnableStencilTest(enable);
            public static void StencilFunc(EComparison function, int reference, uint mask) => AbstractRenderer.Current?.StencilFunc(function, reference, mask);
            public static void StencilOp(EStencilOp sfail, EStencilOp dpfail, EStencilOp dppass) => AbstractRenderer.Current?.StencilOp(sfail, dpfail, dppass);
            public static void EnableBlend(bool enable) => AbstractRenderer.Current?.EnableBlend(enable);
            public static void BlendFunc(EBlendingFactor source, EBlendingFactor destination) => AbstractRenderer.Current?.BlendFunc(source, destination);
            public static void BlendFuncSeparate(EBlendingFactor srcRgb, EBlendingFactor dstRgb, EBlendingFactor srcAlpha, EBlendingFactor dstAlpha) => AbstractRenderer.Current?.BlendFuncSeparate(srcRgb, dstRgb, srcAlpha, dstAlpha);
            public static void BlendEquation(EBlendEquationMode mode) => AbstractRenderer.Current?.BlendEquation(mode);
            public static void BlendEquationSeparate(EBlendEquationMode modeRgb, EBlendEquationMode modeAlpha) => AbstractRenderer.Current?.BlendEquationSeparate(modeRgb, modeAlpha);
            public static void EnableSampleShading(float minValue) => AbstractRenderer.Current?.EnableSampleShading(minValue);
            public static void DisableSampleShading() => AbstractRenderer.Current?.DisableSampleShading();
            public static void AllowDepthWrite(bool allow) => AbstractRenderer.Current?.AllowDepthWrite(allow);
            public static void DepthFunc(EComparison comparison) => AbstractRenderer.Current?.DepthFunc(MapDepthComparison(comparison));
            public static void ColorMask(bool red, bool green, bool blue, bool alpha) => AbstractRenderer.Current?.ColorMask(red, green, blue, alpha);
            public static XRCamera.EDepthMode GetDepthMode() => RenderingPipelineState?.SceneCamera?.DepthMode ?? XRCamera.EDepthMode.Normal;
            public static float GetDefaultDepthClearValue() => RenderingPipelineState?.SceneCamera?.GetDepthClearValue() ?? 1.0f;
            public static EComparison MapDepthComparison(EComparison comparison)
            {
                if (GetDepthMode() != XRCamera.EDepthMode.Reversed)
                    return comparison;

                return comparison switch
                {
                    EComparison.Less => EComparison.Greater,
                    EComparison.Lequal => EComparison.Gequal,
                    EComparison.Greater => EComparison.Less,
                    EComparison.Gequal => EComparison.Lequal,
                    _ => comparison
                };
            }
            public static void CalculateDotLuminanceAsync(XRTexture2D texture, bool generateMipmapsNow, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, Settings.DefaultLuminance, generateMipmapsNow);
            public static void CalculateDotLuminanceAsync(XRTexture2DArray texture, bool generateMipmapsNow, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, Settings.DefaultLuminance, generateMipmapsNow);
            public static void CalculateDotLuminanceAsync(XRTexture2D texture, bool generateMipmapsNow, Vector3 luminance, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, luminance, generateMipmapsNow);
            public static void CalculateDotLuminanceAsync(XRTexture2DArray texture, bool generateMipmapsNow, Vector3 luminance, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, luminance, generateMipmapsNow);
            public static void CalculateFrontBufferDotLuminanceAsync(BoundingRectangle region, bool withTransparency, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceFrontAsync(region, withTransparency, callback);
            public static void CalculateFrontBufferDotLuminanceAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
                => AbstractRenderer.Current?.CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);
        }

        public sealed class RuntimeRenderingState
        {
            private readonly Stack<int> _renderGraphPasses = new();
            private readonly Stack<XRCamera?> _cameraOverrides = new();
            private readonly Stack<uint> _transformIds = new();
            private readonly Stack<int> _mirrorPasses = new();
            private bool _isNvidia;
            private bool _isIntel;
            private bool _isVulkan;

                public ulong RenderFrameId { get; private set; }
                public IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState => RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState;
            public XRRenderPipelineInstance? CurrentRenderingPipeline
                => PipelineOverrideStack.Count != 0
                    ? PipelineOverrideStack.Peek()
                    : RuntimeRenderingHostServices.Current.CurrentRenderPipelineContext as XRRenderPipelineInstance ?? (PipelineStack.Count != 0 ? PipelineStack.Peek() : null);
                public XRRenderPipelineInstance.RenderingState? RenderingPipelineState => CurrentRenderingPipeline?.RenderState;
                public XRViewport? RenderingViewport => CurrentRenderingPipeline?.RenderState.WindowViewport ?? CurrentRenderingPipeline?.LastWindowViewport;
                public IRuntimeRenderWorld? RenderingWorld => RenderingViewport?.World;
                public XRCamera? RenderingCamera => RenderingCameraOverride ?? RenderingPipelineState?.RenderingCamera;
            public XRCamera? RenderingCameraOverride
            {
                get => _cameraOverrides.Count == 0 ? null : _cameraOverrides.Peek();
                set
                {
                    if (value is null)
                    {
                        if (_cameraOverrides.Count != 0)
                            _cameraOverrides.Pop();
                    }
                    else
                    {
                        _cameraOverrides.Push(value);
                    }
                }
            }
            public BoundingRectangle RenderArea => RenderingPipelineState?.CurrentRenderRegion ?? BoundingRectangle.Empty;
            public float DefaultDepthClearValue => 1.0f;
            public bool IsShadowPass => CurrentRenderingPipeline?.RenderState.ShadowPass ?? RuntimeRenderingHostServices.Current.IsShadowPass;
            public bool IsSceneCapturePass { get; set; }
            public bool IsLightProbePass { get; set; }
            public bool IsNVIDIA
            {
                get => RuntimeRenderingHostServices.Current.IsNvidia || _isNvidia;
                internal set => _isNvidia = value;
            }
            public bool IsIntel
            {
                get => _isIntel;
                internal set => _isIntel = value;
            }
            public bool IsVulkan
            {
                get => RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan || _isVulkan;
                internal set => _isVulkan = value;
            }
            public bool IsMirrorPass => MirrorPassIndex > 0;
            public int MirrorPassIndex => _mirrorPasses.Count == 0 ? 0 : _mirrorPasses.Peek();
            public int CurrentRenderGraphPassIndex => _renderGraphPasses.Count == 0 ? int.MinValue : _renderGraphPasses.Peek();
            public uint CurrentTransformId => _transformIds.Count == 0 ? 0u : _transformIds.Peek();

            public void BeginRenderFrame()
            {
                unchecked
                {
                    RenderFrameId++;
                }
            }

            public IDisposable PushRenderGraphPassIndex(int passIndex)
            {
                _renderGraphPasses.Push(passIndex);
                return new DisposableAction(() => _renderGraphPasses.Pop());
            }

            public IDisposable PushTransformId(uint transformId)
            {
                _transformIds.Push(transformId);
                return new DisposableAction(() => _transformIds.Pop());
            }

            public IDisposable PushMirrorPass(int mirrorPassIndex)
            {
                _mirrorPasses.Push(mirrorPassIndex);
                return new DisposableAction(() => _mirrorPasses.Pop());
            }

            public void PushMirrorPass()
                => _mirrorPasses.Push(MirrorPassIndex + 1);

            public void PopMirrorPass()
            {
                if (_mirrorPasses.Count != 0)
                    _mirrorPasses.Pop();
            }
        }
    }
}

internal sealed class RuntimeRenderSettings
{
    public bool AllowBinaryProgramCaching { get; set; } = true;
    public bool AllowBlendshapes { get; set; } = true;
    public bool AllowShaderPipelines { get; set; } = true;
    public bool AllowSkinning { get; set; } = true;
    public bool AsyncProgramBinaryUpload { get; set; } = true;
    public bool AsyncProgramCompilation { get; set; } = true;
    public bool CacheGpuHiZOcclusionOncePerFrame { get; set; } = true;
    public bool CalculateBlendshapesInComputeShader { get; set; }
    public bool CalculateSkinnedBoundsInComputeShader { get; set; }
    public bool CalculateSkinningInComputeShader { get; set; }
    public bool CullShadowCollectionByCameraFrusta { get; set; } = true;
    public Vector3 DefaultLuminance { get; set; } = new(0.299f, 0.587f, 0.114f);
    public float DlssCustomScale { get; set; } = 1.0f;
    public EDlssQualityMode DlssQuality { get; set; } = EDlssQualityMode.Quality;
    public float DlssSharpness { get; set; }
    public bool EnableIntelXessFrameGeneration { get; set; }
    public bool EnableNvidiaDlss { get; set; }
    public EGpuSortDomainPolicy GpuSortDomainPolicy { get; set; } = EGpuSortDomainPolicy.OpaqueFrontToBackTransparentBackToFront;
    public uint LightProbeResolution { get; set; } = 128u;
    public bool LightProbesCaptureDepth { get; set; } = true;
    public bool LogMaterialTextureBindings { get; set; }
    public bool LogMissingShaderSamplers { get; set; }
    public int MaxAsyncShaderProgramsPerFrame { get; set; } = 4;
    public long MaxShadowAtlasMemoryBytes { get; set; }
    public int MaxShadowAtlasPages { get; set; } = 4;
    public uint MaxShadowAtlasTileResolution { get; set; } = 4096u;
    public float MaxShadowRenderMilliseconds { get; set; } = 2.0f;
    public int MaxShadowTilesRenderedPerFrame { get; set; } = 16;
    public uint MinShadowAtlasTileResolution { get; set; } = 128u;
    public bool OpenXrCullWithFrustum { get; set; } = true;
    public bool OpenXrDebugClearOnly { get; set; }
    public bool OpenXrDebugGl { get; set; }
    public bool OpenXrDebugLifecycle { get; set; }
    public bool OpenXrDebugRenderRightThenLeft { get; set; }
    public bool OptimizeSkinningTo4Weights { get; set; } = true;
    public bool OptimizeSkinningWeightsIfPossible { get; set; } = true;
    public bool OutputHDR { get; set; } = true;
    public bool PreferNVStereo { get; set; }
    public bool ProcessMeshImportsAsynchronously { get; set; } = true;
    public bool RenderVRSinglePassStereo { get; set; } = true;
    public int ShaderConfigVersion { get; set; }
    public uint ShadowAtlasPageSize { get; set; } = 2048u;
    public float TsrRenderScale { get; set; } = 1.0f;
    public bool UseAbsoluteBlendshapePositions { get; set; }
    public bool UseDetailPreservingComputeMipmaps { get; set; } = true;
    public bool UseDirectionalShadowAtlas { get; set; } = true;
    public bool UseGlobalBlendshapeWeightsBufferForComputeSkinning { get; set; } = true;
    public bool UseGlobalBoneMatricesBufferForComputeSkinning { get; set; } = true;
    public bool UseIntegerUniformsInShaders { get; set; } = true;
    public bool UseSkinnedBvhRefitOptimize { get; set; } = true;
    public bool UseSpotShadowAtlas { get; set; } = true;
    public RuntimeVulkanRobustnessSettings VulkanRobustnessSettings { get; } = new();
    public float XessCustomScale { get; set; } = 1.0f;
    public float XessSharpness { get; set; }
}

internal sealed class RuntimeEffectiveSettings
{
    private RuntimeRenderSettings Settings => Engine.Rendering.Settings;

    public bool AllowInitialSkinnedBoundsBuildWhenNever { get; set; } = true;
    public EAntiAliasingMode AntiAliasingMode { get; set; } = EAntiAliasingMode.None;
    public EDlssQualityMode DlssQuality => Settings.DlssQuality;
    public bool EnableGpuBvhTimingQueries { get; set; }
    public bool EnableGpuIndirectCpuFallback { get; set; } = true;
    public bool EnableGpuIndirectDebugLogging { get; set; }
    public bool EnableGpuIndirectValidationLogging { get; set; }
    public bool EnableIntelXess { get; set; }
    public bool EnableNvidiaDlss => Settings.EnableNvidiaDlss;
    public bool EnableVulkanBindlessMaterialTable { get; set; }
    public bool EnableVulkanDescriptorIndexing { get; set; }
    public bool EnableZeroReadbackMaterialScatter { get; set; }
    public EGpuCullingDataLayout GpuCullingDataLayout { get; set; } = EGpuCullingDataLayout.AoSHot;
    public EOcclusionCullingMode GpuOcclusionCullingMode { get; set; } = EOcclusionCullingMode.Disabled;
    public bool GPURenderDispatch { get; set; }
    public uint MsaaSampleCount { get; set; } = 1u;
    public ESkinnedBoundsRecomputePolicy SkinnedBoundsRecomputePolicy { get; set; } = ESkinnedBoundsRecomputePolicy.Selective;
    public bool UseGpuBvh { get; set; }
    public bool ValidateVulkanDescriptorContracts { get; set; }
    public EVulkanGeometryFetchMode VulkanGeometryFetchMode { get; set; } = EVulkanGeometryFetchMode.Atlas;
    public EVulkanGpuDrivenProfile VulkanGpuDrivenProfile { get; set; } = EVulkanGpuDrivenProfile.Auto;
    public EVulkanQueueOverlapMode VulkanQueueOverlapMode { get; set; } = EVulkanQueueOverlapMode.Auto;
    public EXessQualityMode XessQuality { get; set; } = EXessQualityMode.Quality;
}

internal sealed class RuntimeVulkanRobustnessSettings
{
    public EVulkanAllocatorBackend AllocatorBackend { get; set; } = EVulkanAllocatorBackend.Legacy;
    public EVulkanSynchronizationBackend SynchronizationBackend { get; set; } = EVulkanSynchronizationBackend.Legacy;
    public EVulkanSynchronizationBackend SyncBackend
    {
        get => SynchronizationBackend;
        set => SynchronizationBackend = value;
    }
    public EVulkanDescriptorUpdateBackend DescriptorUpdateBackend { get; set; } = EVulkanDescriptorUpdateBackend.Legacy;
    public bool DynamicUniformBufferEnabled { get; set; } = true;
    public bool EnableDebugNames { get; set; }
    public bool EnableValidationLayers { get; set; }
}

internal sealed class RuntimeBvhStats
{
    public void Publish(object? packet)
    {
    }
}

internal sealed class RuntimeTime
{
    public RuntimeEngineTimer Timer { get; } = new();
}

internal sealed class RuntimeEngineTimer
{
    public RuntimeTimerFrame Update { get; } = new();
    public RuntimeTimerFrame Render { get; } = new();
    public event Action? UpdateFrame;
    public event Action? CollectVisible;
    public event Action? SwapBuffers;

    public void RaiseUpdateFrame() => UpdateFrame?.Invoke();
    public void RaiseCollectVisible() => CollectVisible?.Invoke();
    public void RaiseSwapBuffers() => SwapBuffers?.Invoke();
}

internal sealed class RuntimeTimerFrame
{
    public float Delta => (float)RuntimeRenderingHostServices.Current.RenderDeltaSeconds;
    public long LastTimestampTicks => RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
}

internal sealed class RuntimePlayMode
{
    public event Action? PostEnterPlay;
    public event Action? PreExitPlay;
    public bool IsTransitioning { get; set; }
    public EPlayModeState State { get; set; } = EPlayModeState.Edit;

    public void RaisePostEnterPlay() => PostEnterPlay?.Invoke();
    public void RaisePreExitPlay() => PreExitPlay?.Invoke();
}

internal sealed class RuntimeGameSettings : IVRGameStartupSettings
{
    public uint MaxMirrorRecursionCount { get; set; } = 4u;
    public RuntimeBuildSettings BuildSettings { get; } = new();
    public OpenVR.NET.Manifest.VrManifest? VRManifest { get; set; }
    public OpenVR.NET.Manifest.IActionManifest? ActionManifest { get; }
    public EVRRuntime VRRuntime { get; set; } = EVRRuntime.Auto;
    public bool EnableOpenXrVulkanParallelRendering { get; set; }
    public string GameName { get; set; } = string.Empty;
    public (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; } = [];
}

internal sealed class RuntimeBuildSettings
{
    public EBuildConfiguration Configuration { get; set; } = EBuildConfiguration.Development;
}

internal sealed class RuntimeEngineState
{
    public IPawnController GetOrCreateLocalPlayer(ELocalPlayerIndex playerIndex)
        => GetLocalPlayer(playerIndex) ?? new NullPawnController(playerIndex);

    public IPawnController? GetLocalPlayer(ELocalPlayerIndex playerIndex)
        => RuntimeRenderingHostServices.Current.EnumerateLocalPlayers().FirstOrDefault(player => player.LocalPlayerIndex == playerIndex);
}

internal sealed class NullPawnController(ELocalPlayerIndex localPlayerIndex) : IPawnController
{
    public bool IsLocal => true;
    public Players.PlayerInfo? PlayerInfo => null;
    public object? InputDevice => null;
    public object? Viewport { get; set; }
    public object? FocusedInteractable { get; set; }
    public XRComponent? ControlledPawnComponent { get; set; }
    public ELocalPlayerIndex? LocalPlayerIndex => localPlayerIndex;
    public void TickPawnInput(float delta, bool isUIInputCaptured) { }
    public void OnPawnCameraChanged() { }
    public void EnqueuePossession(XRComponent pawn) => ControlledPawnComponent = pawn;
    public void ApplyNetworkTransform(Networking.PlayerTransformUpdate update) { }
}

internal sealed class RuntimeEditorPreferences
{
    public EViewportPresentationMode ViewportPresentationMode { get; set; } = EViewportPresentationMode.NativeWindow;
    public int ScenePanelResizeDebounceMs { get; set; } = 100;
    public ColorF4 HoverOutlineColor { get; set; } = ColorF4.Yellow;
    public ColorF4 SelectionOutlineColor { get; set; } = ColorF4.White;
    public RuntimeDebugPreferences Debug { get; } = new();
    public RuntimeThemePreferences Theme { get; } = new();

    public enum EViewportPresentationMode
    {
        NativeWindow,
        UseViewportPanel
    }
}

internal sealed class RuntimeDebugPreferences
{
    private bool? _forwardDepthPrePassEnabled;
    private bool? _forwardPrePassSharesGBufferTargets;

    public bool ForwardDepthPrePassEnabled
    {
        get => _forwardDepthPrePassEnabled ?? RuntimeRenderingHostServices.Current.ForwardDepthPrePassEnabled;
        set => _forwardDepthPrePassEnabled = value;
    }

    public bool ForwardPrePassSharesGBufferTargets
    {
        get => _forwardPrePassSharesGBufferTargets ?? RuntimeRenderingHostServices.Current.ForwardPrePassSharesGBufferTargets;
        set => _forwardPrePassSharesGBufferTargets = value;
    }

    public bool EnableGpuRenderPipelineProfiling { get; set; }
    public bool EnableExactTransparencyTechniques { get; set; }
    public int DepthPeelingMaxLayers { get; set; } = 4;
    public int DepthPeelingPreviewLayer { get; set; }
    public bool VisualizeTransformId { get; set; }
    public bool VisualizeTransparencyAccumulation { get; set; }
    public bool VisualizeTransparencyRevealage { get; set; }
    public bool VisualizeTransparencyOverdrawHeatmap { get; set; }
    public bool VisualizePerPixelLinkedListFragments { get; set; }
    public bool VisualizeDepthPeelingLayer { get; set; }
    public bool RenderLightProbeTetrahedra { get; set; }
    public bool VisualizeDirectionalLightVolumes { get; set; }
    public bool RenderMesh3DBounds { get; set; }
    public bool AllowGpuCpuFallback { get; set; } = true;
    public bool VisualizeTransparencyModeOverlay { get; set; }
    public bool VisualizeTransparencyClassificationOverlay { get; set; }
    public bool EnableZeroReadbackMaterialScatter { get; set; }
    public bool ForceGpuPassthroughCulling { get; set; }
}

internal sealed class RuntimeThemePreferences
{
    public ColorF4 MeshBoundsContainedColor { get; set; } = ColorF4.Green;
    public ColorF4 MeshBoundsIntersectedColor { get; set; } = ColorF4.Yellow;
    public ColorF4 MeshBoundsDisjointColor { get; set; } = ColorF4.Red;
    public ColorF4 Bounds3DColor { get; set; } = ColorF4.Cyan;
    public ColorF4 HoverOutlineColor { get; set; } = ColorF4.Yellow;
    public ColorF4 SelectionOutlineColor { get; set; } = ColorF4.White;
}

internal sealed class RuntimeAssetFacade
{
    public string EngineAssetsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Assets");
    public string GameAssetsPath { get; set; } = Path.Combine(Environment.CurrentDirectory, "Assets");

    public T? LoadEngineAsset<T>(JobPriority priority, params string[] pathParts)
        where T : class
        => default;
}

internal sealed class RuntimeProfilerFacade
{
    public IDisposable Start(string? label = null) => DisposableAction.Empty;
}

internal sealed class RuntimeVrState
{
    public bool IsInVR { get; set; }
    public bool IsOpenXRActive { get; set; }
    public XRViewport? LeftEyeViewport { get; set; }
    public XRViewport? RightEyeViewport { get; set; }
    public OpenXRAPI? OpenXRApi { get; set; }
    public RuntimeOpenVrApi OpenVRApi { get; } = new();
    public (XRCamera? LeftEyeCamera, XRCamera? RightEyeCamera, IRuntimeRenderWorld? World, SceneNode? HMDNode) ViewInformation { get; set; }
    public void InvokeRecalcMatrixOnDraw()
    {
    }
}

internal sealed class RuntimeOpenVrApi
{
    public bool IsHeadsetPresent => CVR is not null;
    public CVRSystem? CVR { get; set; }
}

internal sealed class DisposableAction : IDisposable
{
    public static readonly IDisposable Empty = new DisposableAction(null);
    private readonly Action? _dispose;

    public DisposableAction(Action? dispose)
    {
        _dispose = dispose;
    }

    public void Dispose()
        => _dispose?.Invoke();
}
