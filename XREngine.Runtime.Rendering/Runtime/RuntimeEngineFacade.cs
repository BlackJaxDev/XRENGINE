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
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
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

internal static partial class RuntimeEngine
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
        => RuntimeRenderingHostServices.Current.EnqueueAppThreadTask(action, name ?? "app-thread facade task");

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
        private static readonly Stack<XRRenderPipelineInstance?> PipelineOverrideStack = new();

        public static RuntimeRenderSettings Settings { get; } = new();
        private static RuntimeRenderingState StateData { get; } = new();
        public static RuntimeBvhStats BvhStats { get; } = new();
        private static event Action? SettingsChangedHandlers;
        private static event Action? AntiAliasingSettingsChangedHandlers;

        public static event Action? SettingsChanged
        {
            add
            {
                if (value is null)
                    return;

                SettingsChangedHandlers += value;
                RuntimeRenderingHostServices.Current.SubscribeRenderingSettingsChanged(value);
            }
            remove
            {
                if (value is null)
                    return;

                SettingsChangedHandlers -= value;
                RuntimeRenderingHostServices.Current.UnsubscribeRenderingSettingsChanged(value);
            }
        }

        public static event Action? AntiAliasingSettingsChanged
        {
            add
            {
                if (value is null)
                    return;

                AntiAliasingSettingsChangedHandlers += value;
                RuntimeRenderingHostServices.Current.SubscribeAntiAliasingSettingsChanged(value);
            }
            remove
            {
                if (value is null)
                    return;

                AntiAliasingSettingsChangedHandlers -= value;
                RuntimeRenderingHostServices.Current.UnsubscribeAntiAliasingSettingsChanged(value);
            }
        }

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
            return new DisposableAction(() =>
            {
                if (PipelineStack.Count != 0)
                    PipelineStack.Pop();
            });
        }

        public static IDisposable? PushRenderingPipelineOverride(XRRenderPipelineInstance? pipeline)
        {
            PipelineOverrideStack.Push(pipeline);
            return new DisposableAction(() =>
            {
                if (PipelineOverrideStack.Count != 0)
                    PipelineOverrideStack.Pop();
            });
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

        /// <summary>
        /// Strategy the user requested in <c>ForceMeshSubmissionStrategy</c> when the
        /// resolver had to downgrade it (typically a meshlet strategy on a backend that
        /// can't dispatch mesh tasks). Null when no downgrade is active.
        /// </summary>
        public static EMeshSubmissionStrategy? LastMeshletDowngradeRequested { get; private set; }
        /// <summary>Strategy the resolver substituted for the requested meshlet strategy.</summary>
        public static EMeshSubmissionStrategy? LastMeshletDowngradeResolved { get; private set; }
        /// <summary>Human-readable reason for the meshlet downgrade.</summary>
        public static string? LastMeshletDowngradeReason { get; private set; }
        /// <summary>Active render backend snapshotted by the last meshlet/strategy resolve.</summary>
        public static RuntimeGraphicsApiKind LastResolvedRendererBackend { get; private set; }
        /// <summary>Mesh-shader dialect (none/KHR/NV/Vulkan EXT) the active renderer reported.</summary>
        public static EMeshShaderDialect LastResolvedMeshShaderDialect { get; private set; }
        /// <summary>True when the active renderer reported a production meshlet dispatch path.</summary>
        public static bool LastResolvedSupportsMeshletDispatch { get; private set; }

        public static void ApplyGpuRenderDispatchToPipeline(object? pipeline, bool enabled)
        {
        }

        public static void ApplyMeshSubmissionStrategyToPipeline(object? pipeline, EMeshSubmissionStrategy strategy)
        {
        }

        public static bool ResolveGpuRenderDispatchPreference(bool requested)
            => VulkanFeatureProfile.ResolveGpuRenderDispatchPreference(requested);

        public static EMeshSubmissionStrategy ResolveMeshSubmissionStrategy(bool? requestedGpuDispatch = null)
        {
            AbstractRenderer? renderer = AbstractRenderer.Current;
            bool rendererKnown = renderer is not null;
            bool supportsIndirectCount = rendererKnown ? renderer!.SupportsIndirectCountDraw() : true;
            EMeshShaderDialect meshShaderDialect = renderer?.MeshShaderDialect ?? EMeshShaderDialect.None;
            bool supportsDirectMeshTaskDispatch = renderer?.SupportsDirectMeshTaskDispatch() ?? false;
            bool supportsIndirectCountMeshTaskDispatch = renderer?.SupportsIndirectCountMeshTaskDispatch() ?? false;
            bool supportsMeshletDispatch = renderer?.SupportsMeshletDispatch() ?? false;

            // Snapshot inputs the UI uses to explain meshlet availability without re-deriving them.
            LastResolvedRendererBackend = RuntimeRenderingHostServices.Current.CurrentRenderBackend;
            LastResolvedMeshShaderDialect = meshShaderDialect;
            LastResolvedSupportsMeshletDispatch = supportsMeshletDispatch;

            EMeshSubmissionStrategy? forced = RuntimeEngine.EffectiveSettings.ForceMeshSubmissionStrategy;
            if (forced.HasValue)
            {
                if (forced.Value.IsAnyMeshletStrategy())
                {
                    EMeshSubmissionStrategy resolved = ResolveForcedMeshletSubmissionStrategy(
                        forced.Value,
                        IsMeshletInstrumentationAllowed(),
                        supportsMeshletDispatch,
                        supportsIndirectCount);

                    if (resolved != forced.Value)
                    {
                        string reason = GetMeshletFallbackReason(
                            forced.Value,
                            supportsMeshletDispatch,
                            meshShaderDialect,
                            supportsDirectMeshTaskDispatch,
                            supportsIndirectCountMeshTaskDispatch);

                        LastMeshletDowngradeRequested = forced.Value;
                        LastMeshletDowngradeResolved = resolved;
                        LastMeshletDowngradeReason = reason;

                        XREngine.Debug.RenderingWarningEvery(
                            "RenderDispatch.MeshSubmissionStrategy.UnsupportedGpuMeshlet",
                            TimeSpan.FromSeconds(2),
                            "[RenderDispatch] Mesh submission strategy downgraded from {0} to {1}. Dialect={2}; DirectTaskDispatch={3}; IndirectCountTaskDispatch={4}; FallbackReason={5}.",
                            forced.Value,
                            resolved,
                            meshShaderDialect,
                            supportsDirectMeshTaskDispatch,
                            supportsIndirectCountMeshTaskDispatch,
                            reason);
                    }
                    else
                    {
                        LastMeshletDowngradeRequested = null;
                        LastMeshletDowngradeResolved = null;
                        LastMeshletDowngradeReason = null;
                    }

                    return resolved;
                }

                return forced.Value;
            }

            if (!(requestedGpuDispatch ?? RuntimeEngine.EffectiveSettings.GPURenderDispatch))
                return EMeshSubmissionStrategy.CpuDirect;

            bool diagnosticsProfile = VulkanFeatureProfile.IsActive &&
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics;
            bool shippingFastProfile = VulkanFeatureProfile.IsActive &&
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.ShippingFast;
            bool zeroReadbackRequested = shippingFastProfile
                || RuntimeEngine.EffectiveSettings.EnableZeroReadbackMaterialScatter
                || RuntimeEngine.EditorPreferences.Debug.EnableZeroReadbackMaterialScatter;
            bool instrumentationRequested = diagnosticsProfile
                || RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging
                || RuntimeEngine.EffectiveSettings.EnableGpuIndirectValidationLogging
                || RuntimeEngine.EffectiveSettings.EnableGpuIndirectCpuFallback;

            if (zeroReadbackRequested)
            {
                if (supportsIndirectCount)
                    return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

                return VulkanFeatureProfile.EnforceStrictNoFallbacks
                    ? EMeshSubmissionStrategy.CpuDirect
                    : EMeshSubmissionStrategy.GpuIndirectInstrumented;
            }

            if (instrumentationRequested)
                return EMeshSubmissionStrategy.GpuIndirectInstrumented;

            return supportsIndirectCount
                ? EMeshSubmissionStrategy.GpuIndirectInstrumented
                : EMeshSubmissionStrategy.CpuDirect;
        }

        private static EMeshSubmissionStrategy ResolveForcedMeshletSubmissionStrategy(
            EMeshSubmissionStrategy requestedStrategy,
            bool instrumentationAllowed,
            bool supportsMeshletDispatch,
            bool supportsIndirectCountDraw)
        {
            if (supportsMeshletDispatch)
            {
                if (requestedStrategy == EMeshSubmissionStrategy.GpuMeshletInstrumented && instrumentationAllowed)
                    return EMeshSubmissionStrategy.GpuMeshletInstrumented;

                return EMeshSubmissionStrategy.GpuMeshletZeroReadback;
            }

            if (supportsIndirectCountDraw)
                return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

            return VulkanFeatureProfile.EnforceStrictNoFallbacks
                ? EMeshSubmissionStrategy.CpuDirect
                : EMeshSubmissionStrategy.GpuIndirectInstrumented;
        }

        private static bool IsMeshletInstrumentationAllowed()
            => (VulkanFeatureProfile.IsActive &&
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics) ||
               RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging;

        private static string GetMeshletFallbackReason(
            EMeshSubmissionStrategy requestedStrategy,
            bool supportsMeshletDispatch,
            EMeshShaderDialect meshShaderDialect,
            bool supportsDirectMeshTaskDispatch,
            bool supportsIndirectCountMeshTaskDispatch)
        {
            if (supportsMeshletDispatch)
            {
                if (requestedStrategy == EMeshSubmissionStrategy.GpuMeshletInstrumented &&
                    !IsMeshletInstrumentationAllowed())
                {
                    return "meshlet instrumentation requires the Diagnostics Vulkan profile or EnableGpuIndirectDebugLogging";
                }

                return "production meshlet dispatch is available";
            }

            if (meshShaderDialect == EMeshShaderDialect.None)
                return "no mesh shader dialect is available";

            if (supportsDirectMeshTaskDispatch && !supportsIndirectCountMeshTaskDispatch)
                return "only diagnostic CPU-count mesh task dispatch is available";

            if (!supportsIndirectCountMeshTaskDispatch)
                return "production indirect-count mesh task dispatch is unavailable";

            return "production meshlet dispatch is unavailable";
        }

        internal static void RaiseSettingsChanged() => SettingsChangedHandlers?.Invoke();
        internal static void RaiseAntiAliasingSettingsChanged() => AntiAliasingSettingsChangedHandlers?.Invoke();

        public static class Debug
        {
            public static void RenderLine(Vector3 start, Vector3 end, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugLine(start, end, color);

            public static void RenderSphere(Vector3 center, float radius, bool solid, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugSphere(center, radius, solid, color);

            public static void RenderCone(Vector3 center, Vector3 up, float radius, float height, bool solid, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugCone(center, up, radius, height, solid, color);

            public static void RenderAABB(Vector3 halfExtents, Vector3 center, bool solid, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugAABB(halfExtents, center, solid, color);

            public static void RenderRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugRect2D(rectangle, solid, color);

            public static void RenderBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugBox(halfExtents, center, transform, solid, color);

            public static void RenderQuad(Vector3 center, Rotator rotation, Vector2 extents, bool solid, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugQuad(center, rotation, extents, solid, color);

            public static void RenderQuad(Vector3 center, Quaternion rotation, Vector2 extents, bool solid, ColorF4 color)
            {
                Rotator rotator = Rotator.FromQuaternion(rotation);
                RuntimeRenderingHostServices.Current.RenderDebugQuad(center, rotator, extents, solid, color);
            }

            public static void RenderQuad(Vector3 center, object rotation, Vector2 extents, bool solid, ColorF4 color)
            {
                if (rotation is Rotator rotator)
                    RuntimeRenderingHostServices.Current.RenderDebugQuad(center, rotator, extents, solid, color);
            }

            public static void RenderPoint(Vector3 position, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugPoint(position, color);

            public static void RenderText(Vector3 position, string text, ColorF4 color)
                => RuntimeRenderingHostServices.Current.RenderDebugText(position, text, color);

            public static void RenderShapes()
                => RuntimeRenderingHostServices.Current.RenderDebugShapes();
        }

        public static class Stats
        {
            private static long _trackedVramBytes;
            private static bool _enableTracking = RuntimeRenderingHostServiceDefaults.EnableRenderStatisticsTracking;
            public static bool EnableTracking
            {
                get => RuntimeRenderingHostServices.HasConcreteHost
                    ? RuntimeRenderingHostServices.Current.EnableRenderStatisticsTracking
                    : _enableTracking;
                set => _enableTracking = value;
            }
            public static int DrawCalls { get; private set; }
            public static int MultiDrawCalls { get; private set; }
            public static int TrianglesRendered { get; private set; }
            public static int GpuCpuFallbackEvents { get; private set; }
            public static int GpuCpuFallbackRecoveredCommands { get; private set; }
            public static int GpuTransparencyOpaqueOrOtherVisible { get; private set; }
            public static int GpuTransparencyMaskedVisible { get; private set; }
            public static int GpuTransparencyApproximateVisible { get; private set; }
            public static int GpuTransparencyExactVisible { get; private set; }
            public static int GpuMeshletRequestedFrames { get; private set; }
            public static int GpuMeshletProductionFrames { get; private set; }
            public static int GpuMeshletFallbackFrames { get; private set; }
            public static int GpuMeshletDispatchSkipped { get; private set; }
            public static long GpuMeshletTaskRecordsEmitted { get; private set; }
            public static long GpuMeshletTaskRecordsFrustumCulled { get; private set; }
            public static long GpuMeshletTaskRecordsConeCulled { get; private set; }
            public static long GpuMeshletTaskRecordsHiZCulled { get; private set; }
            public static long GpuMeshletExpansionOverflowCount { get; private set; }
            public static long GpuMeshletBufferBytesResident { get; private set; }
            public static long LastVisibleMeshletCount { get; private set; }
            public static long LastDispatchedMeshletCount { get; private set; }
            public static long LastTaskRecordOverflowCount { get; private set; }
            public static TimeSpan LastDispatchTime { get; private set; }
            public static long LastReadbackBytes { get; private set; }
            public static int GpuMeshletCacheHits { get; private set; }
            public static int GpuMeshletCacheMisses { get; private set; }
            public static int GpuMeshletCacheStale { get; private set; }
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

            private static bool HasHostStats => RuntimeRenderingHostServices.HasConcreteHost;

            public static void IncrementDrawCalls(int count = 1)
            {
                if (!EnableTracking || count <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.IncrementRenderDrawCalls(count);
                else
                    DrawCalls += count;
            }

            public static void IncrementMultiDrawCalls(int count = 1)
            {
                if (!EnableTracking || count <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.IncrementRenderMultiDrawCalls(count);
                else
                    MultiDrawCalls += count;
            }

            public static void AddTrianglesRendered(int count)
            {
                if (!EnableTracking || count <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.AddRenderTrianglesRendered(count);
                else
                    TrianglesRendered += count;
            }

            public static void AddBufferAllocation(long bytes)
            {
                if (bytes <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.AddRenderGpuBufferAllocation(bytes);
                else
                    _trackedVramBytes += bytes;
            }

            public static void RemoveBufferAllocation(long bytes)
            {
                if (bytes <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RemoveRenderGpuBufferAllocation(bytes);
                else
                    _trackedVramBytes = Math.Max(0L, _trackedVramBytes - bytes);
            }

            public static void AddTextureAllocation(long bytes)
            {
                if (bytes <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.AddRenderGpuTextureAllocation(bytes);
                else
                    _trackedVramBytes += bytes;
            }

            public static void RemoveTextureAllocation(long bytes)
            {
                if (bytes <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RemoveRenderGpuTextureAllocation(bytes);
                else
                    _trackedVramBytes = Math.Max(0L, _trackedVramBytes - bytes);
            }

            public static void AddRenderBufferAllocation(long bytes)
            {
                if (bytes <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.AddRenderGpuRenderBufferAllocation(bytes);
                else
                    _trackedVramBytes += bytes;
            }

            public static void RemoveRenderBufferAllocation(long bytes)
            {
                if (bytes <= 0)
                    return;

                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RemoveRenderGpuRenderBufferAllocation(bytes);
                else
                    _trackedVramBytes = Math.Max(0L, _trackedVramBytes - bytes);
            }

            public static bool CanAllocateVram(long requestedBytes, long currentAllocationBytes, out long projectedBytes, out long budgetBytes)
            {
                if (HasHostStats)
                    return RuntimeRenderingHostServices.Current.CanAllocateRenderVram(requestedBytes, currentAllocationBytes, out projectedBytes, out budgetBytes);

                budgetBytes = RuntimeRenderingHostServices.Current.TrackedVramBudgetBytes;
                projectedBytes = Math.Max(0L, _trackedVramBytes - Math.Max(0L, currentAllocationBytes)) + Math.Max(0L, requestedBytes);
                return projectedBytes <= budgetBytes;
            }

            public static void RecordGpuBufferMapped(int count = 1)
            {
                if (EnableTracking && count > 0 && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderGpuBufferMapped(count);
            }

            public static void RecordGpuReadbackBytes(long bytes)
            {
                if (EnableTracking && bytes > 0 && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderGpuReadbackBytes(bytes);
            }

            public static void RecordRendererStateCounter(ERendererProfilerCounter counter, long count = 1)
            {
                if (EnableTracking && count > 0 && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderRendererStateCounter(counter, count);
            }

            public static void RecordMemoryBarrier(EMemoryBarrierMask mask)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderMemoryBarrier(mask);
            }

            public static void RecordSceneAssetVisible(
                string? sourceAssetIdentity,
                string? cookedVariantIdentity,
                string? meshName,
                string? materialName,
                int materialSlots,
                int textureCount,
                long triangleCount,
                bool skinned,
                string? representation)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderSceneAssetVisible(
                        sourceAssetIdentity,
                        cookedVariantIdentity,
                        meshName,
                        materialName,
                        materialSlots,
                        textureCount,
                        triangleCount,
                        skinned,
                        representation);
            }

            public static void RecordTextureUpload(long bytes, TimeSpan elapsed)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderTextureUpload(bytes, elapsed);
            }

            public static void RecordSkinningUpload(
                long boneMatrixBytes,
                long blendshapeWeightBytes,
                int skinningDispatches = 0,
                int blendshapeDispatches = 0,
                long coreInfluenceBytes = 0,
                long spillHeaderBytes = 0,
                long spillEntryBytes = 0,
                long skinPaletteBytes = 0,
                int skippedSkinningDispatches = 0,
                int reusedSkinnedOutputBuffers = 0,
                int liveSkinningShaderPermutations = 0,
                long blendshapeActiveListUploadBytes = 0,
                long blendshapeDeltaBytes = 0,
                int blendshapeAuthoredShapeCount = 0,
                int blendshapeActiveShapeCount = 0,
                int blendshapeAffectedVertexCount = 0,
                int skippedBlendshapeDispatches = 0,
                int compactedActiveBlendshapeCount = 0,
                int liveBlendshapeShaderPermutations = 0)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderSkinningUpload(
                        boneMatrixBytes,
                        blendshapeWeightBytes,
                        skinningDispatches,
                        blendshapeDispatches,
                        coreInfluenceBytes,
                        spillHeaderBytes,
                        spillEntryBytes,
                        skinPaletteBytes,
                        skippedSkinningDispatches,
                        reusedSkinnedOutputBuffers,
                        liveSkinningShaderPermutations,
                        blendshapeActiveListUploadBytes,
                        blendshapeDeltaBytes,
                        blendshapeAuthoredShapeCount,
                        blendshapeActiveShapeCount,
                        blendshapeAffectedVertexCount,
                        skippedBlendshapeDispatches,
                        compactedActiveBlendshapeCount,
                        liveBlendshapeShaderPermutations);
            }

            public static void RecordShaderVariant(bool requested = false, bool warming = false, bool linked = false, bool failed = false, bool loadedFromDiskCache = false, bool generatedThisRun = false)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderShaderVariant(requested, warming, linked, failed, loadedFromDiskCache, generatedThisRun);
            }

            public static void RecordGpuDrivenBucketWork(int activeBuckets = 0, int emptyBucketSkips = 0, int fullBucketScans = 0, int materialScatterDispatches = 0)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderGpuDrivenBucketWork(activeBuckets, emptyBucketSkips, fullBucketScans, materialScatterDispatches);
            }

            public static void RecordGpuDrivenCommandCompaction(long culledCommands = 0, long delayedDrawCountValue = 0, long gpuCompactionOverflow = 0, long activeListOverflow = 0, long bucketOverflow = 0, long meshletOverflow = 0)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderGpuDrivenCommandCompaction(culledCommands, delayedDrawCountValue, gpuCompactionOverflow, activeListOverflow, bucketOverflow, meshletOverflow);
            }

            public static void RecordGpuDrivenStageTiming(TimeSpan indirectGeneration, TimeSpan gpuCull, TimeSpan sortCompact)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderGpuDrivenStageTiming(indirectGeneration, gpuCull, sortCompact);
            }

            public static void RecordGpuDrivenDelayedDiagnosticReadback(long bytes)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderGpuDrivenDelayedDiagnosticReadback(bytes);
            }

            public static void RecordGpuDrivenHiZMode(string? mode)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderGpuDrivenHiZMode(mode);
            }

            public static void RecordGpuDrivenHiZPhase(bool twoPhase, long phaseOneDraws, long phaseTwoDraws)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderGpuDrivenHiZPhase(twoPhase, phaseOneDraws, phaseTwoDraws);
            }

            public static void RecordVisibilityBuffer(int passDraws, long classifiedPixels, int activeMaterialTiles, int classificationOverflow, TimeSpan reconstruction, TimeSpan materialShading)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVisibilityBuffer(passDraws, classifiedPixels, activeMaterialTiles, classificationOverflow, reconstruction, materialShading);
            }

            public static void RecordGpuCpuFallback(int events, int recoveredCommands)
            {
                if (!EnableTracking || events <= 0)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuCpuFallback(events, recoveredCommands);
                    return;
                }

                GpuCpuFallbackEvents += events;
                if (recoveredCommands > 0)
                    GpuCpuFallbackRecoveredCommands += recoveredCommands;
            }

            public static void RecordForbiddenGpuFallback(int events)
            {
                if (EnableTracking && events > 0 && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderForbiddenGpuFallback(events);
            }

            public static void RecordGpuTransparencyDomainCounts(int opaqueOrOther, int masked, int approximate, int exact)
            {
                if (!EnableTracking)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuTransparencyDomainCounts(
                        (uint)Math.Max(0, opaqueOrOther),
                        (uint)Math.Max(0, masked),
                        (uint)Math.Max(0, approximate),
                        (uint)Math.Max(0, exact));
                    return;
                }

                GpuTransparencyOpaqueOrOtherVisible = opaqueOrOther;
                GpuTransparencyMaskedVisible = masked;
                GpuTransparencyApproximateVisible = approximate;
                GpuTransparencyExactVisible = exact;
            }
            public static void RecordGpuTransparencyDomainCounts(uint opaqueOrOther, uint masked, uint approximate, uint exact)
                => RecordGpuTransparencyDomainCounts((int)opaqueOrOther, (int)masked, (int)approximate, (int)exact);

            public static void RecordGpuMeshletStrategyRequested(
                int renderPass,
                EMeshSubmissionStrategy requestedStrategy,
                EMeshSubmissionStrategy selectedStrategy,
                EMeshShaderDialect dialect,
                uint commandCount,
                uint taskCapacity)
            {
                if (!EnableTracking)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletStrategyRequested();
                    return;
                }

                GpuMeshletRequestedFrames++;
            }

            public static void RecordGpuMeshletProductionFrame(int eventCount = 1)
            {
                if (!EnableTracking || eventCount <= 0)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletProductionFrame(eventCount);
                    return;
                }

                GpuMeshletProductionFrames += eventCount;
            }

            public static void RecordGpuMeshletFallback(int eventCount = 1)
            {
                if (!EnableTracking || eventCount <= 0)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletFallback(eventCount);
                    return;
                }

                GpuMeshletFallbackFrames += eventCount;
            }

            public static void RecordGpuMeshletDispatchSkipped(int eventCount = 1)
            {
                if (!EnableTracking || eventCount <= 0)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletDispatchSkipped(eventCount);
                    return;
                }

                GpuMeshletDispatchSkipped += eventCount;
            }

            public static void RecordGpuMeshletTaskStats(uint emitted, uint frustumCulled, uint coneCulled, uint hiZCulled)
            {
                if (!EnableTracking)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletTaskStats(emitted, frustumCulled, coneCulled, hiZCulled);
                    return;
                }

                GpuMeshletTaskRecordsEmitted += emitted;
                GpuMeshletTaskRecordsFrustumCulled += frustumCulled;
                GpuMeshletTaskRecordsConeCulled += coneCulled;
                GpuMeshletTaskRecordsHiZCulled += hiZCulled;
            }

            public static void RecordGpuMeshletExpansionOverflow(uint overflowCount)
            {
                if (!EnableTracking || overflowCount == 0u)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletExpansionOverflow(overflowCount);
                    return;
                }

                GpuMeshletExpansionOverflowCount += overflowCount;
            }

            public static void RecordGpuMeshletBufferBytesResident(ulong bytes)
            {
                if (!EnableTracking)
                    return;

                long saturated = bytes > long.MaxValue ? long.MaxValue : (long)bytes;
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletBufferBytesResident(saturated);
                    return;
                }

                GpuMeshletBufferBytesResident = Math.Max(GpuMeshletBufferBytesResident, saturated);
            }

            public static void RecordGpuMeshletInstrumentation(
                uint visibleMeshletCount,
                uint dispatchedMeshletCount,
                uint taskRecordOverflowCount,
                TimeSpan dispatchTime,
                uint readbackBytes)
            {
                if (!EnableTracking)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletInstrumentation(
                        visibleMeshletCount,
                        dispatchedMeshletCount,
                        taskRecordOverflowCount,
                        dispatchTime,
                        readbackBytes);
                    return;
                }

                LastVisibleMeshletCount = visibleMeshletCount;
                LastDispatchedMeshletCount = dispatchedMeshletCount;
                LastTaskRecordOverflowCount = taskRecordOverflowCount;
                LastDispatchTime = dispatchTime;
                LastReadbackBytes += readbackBytes;
            }

            public static void RecordGpuMeshletCacheHit(int eventCount = 1)
            {
                if (!EnableTracking || eventCount <= 0)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletCacheHit(eventCount);
                    return;
                }

                GpuMeshletCacheHits += eventCount;
            }

            public static void RecordGpuMeshletCacheMiss(int eventCount = 1)
            {
                if (!EnableTracking || eventCount <= 0)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletCacheMiss(eventCount);
                    return;
                }

                GpuMeshletCacheMisses += eventCount;
            }

            public static void RecordGpuMeshletCacheStale(int eventCount = 1)
            {
                if (!EnableTracking || eventCount <= 0)
                    return;

                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderGpuMeshletCacheStale(eventCount);
                    return;
                }

                GpuMeshletCacheStale += eventCount;
            }

            public static void RecordOctreeCollect(int visibleRenderables, int emittedCommands)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderOctreeCollect(visibleRenderables, emittedCommands);
            }

            public static void RecordCpuSpatialTreeStats(string mode, SpatialTreeOccupancyStats occupancy, long collectTicks)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderCpuSpatialTreeStats(mode, occupancy, collectTicks);
            }

            public static void RecordRtxIoCopyIndirect(long bytes, TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderRtxIoCopyIndirect(bytes, elapsed);
            }

            public static void RecordRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderRtxIoDecompression(compressedBytes, decompressedBytes, elapsed);
            }

            public static void RecordSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderSkinnedBoundsRefreshDeferredFinished(queueWaitTicks, cpuJobTicks, applyTicks, succeeded);
            }

            public static void RecordSkinnedBoundsRefreshDeferredScheduled()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderSkinnedBoundsRefreshDeferredScheduled();
            }

            public static void RecordSkinnedBoundsRefreshGpuCompleted(long gpuTicks, long applyTicks)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderSkinnedBoundsRefreshGpuCompleted(gpuTicks, applyTicks);
            }

            public static void RecordVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrCommandBuildTimes(leftBuildTime, rightBuildTime);
            }

            public static void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordVrPerViewDrawCounts(leftDraws, rightDraws);
            }

            public static void RecordVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrPerViewVisibleCounts(leftVisible, rightVisible);
            }

            public static void RecordVrRenderSubmitTime(TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrRenderSubmitTime(elapsed);
            }

            public static void RecordVrXrWaitFrameBlockTime(TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrWaitFrameBlockTime(elapsed);
            }

            public static void RecordVrXrEndFrameSubmitTime(TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrEndFrameSubmitTime(elapsed);
            }

            public static void RecordVrXrPredictedToLatePoseDelta(double millimeters, double degrees)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrPredictedToLatePoseDelta(millimeters, degrees);
            }

            public static void RecordVrXrPredictedDisplayLeadTime(double leadTimeMs)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrPredictedDisplayLeadTime(leadTimeMs);
            }

            public static void RecordVrXrMissedDeadlineFrame()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrMissedDeadlineFrame();
            }

            public static void RecordVrXrTrackingLossFrame()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrTrackingLossFrame();
            }

            public static void RecordVrXrRelocatePredictedTime(TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrRelocatePredictedTime(elapsed);
            }

            public static void RecordVrXrCollectFrustumExpansionDegrees(double degrees)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrCollectFrustumExpansionDegrees(degrees);
            }

            public static void RecordVrXrPacingThreadIdleTime(TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrPacingThreadIdleTime(elapsed);
            }

            public static void RecordVrXrPacingHandoffStall()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVrXrPacingHandoffStall();
            }

            public static void RecordVulkanAdhocBarrier(int emittedCount = 0, int redundantCount = 0)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanAdhocBarrier(emittedCount, redundantCount);
            }

            public static void RecordVulkanAllocation(EVulkanAllocationTelemetryClass allocationClass, long bytes)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanAllocation((int)allocationClass, bytes);
            }

            public static void RecordVulkanBarrierPlannerPass(int imageBarrierCount = 0, int bufferBarrierCount = 0, int queueOwnershipTransfers = 0, int stageFlushes = 0)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanBarrierPlannerPass(imageBarrierCount, bufferBarrierCount, queueOwnershipTransfers, stageFlushes);
                    return;
                }

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
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanBindChurn(
                        pipelineBinds,
                        descriptorBinds,
                        pushConstantWrites,
                        vertexBufferBinds,
                        indexBufferBinds,
                        pipelineBindSkips,
                        descriptorBindSkips,
                        vertexBufferBindSkips,
                        indexBufferBindSkips);
                    return;
                }

                VulkanDescriptorBindSkips += descriptorBindSkips;
            }

            public static void RecordVulkanDescriptorBindingFailure(
                string? programName = null,
                string? bindingClass = null,
                string? bindingName = null,
                uint set = 0,
                uint binding = 0,
                bool skippedDraw = false,
                bool skippedDispatch = false,
                string? reason = null)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanDescriptorBindingFailure(programName, bindingClass, bindingName, set, binding, skippedDraw, skippedDispatch, reason);
                else
                    VulkanDescriptorBindingFailuresCurrentFrame++;
            }

            public static void RecordVulkanDescriptorFallback(
                string? programName,
                string? bindingClass,
                string? bindingName,
                uint set,
                uint binding,
                int count = 1)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanDescriptorFallback(programName, bindingClass, bindingName, set, binding, count);
                else
                    VulkanDescriptorFallbacksCurrentFrame++;
            }

            public static void RecordVulkanDescriptorPoolCreate()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanDescriptorPoolCreate();
            }

            public static void RecordVulkanDescriptorPoolDestroy()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanDescriptorPoolDestroy();
            }

            public static void RecordVulkanDescriptorPoolReset()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanDescriptorPoolReset();
            }

            public static void RecordVulkanDynamicUniformAllocation(long bytes)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanDynamicUniformAllocation(bytes);
            }

            public static void RecordVulkanDynamicUniformExhaustion()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanDynamicUniformExhaustion();
            }

            public static void RecordVulkanFrameDiagnostics(
                int droppedFrameOps,
                int droppedDrawOps,
                int droppedComputeOps,
                int sceneSwapchainWriters,
                int overlaySwapchainWriters,
                int forcedDiagnosticSwapchainWriters,
                int fboOnlyDrawOps,
                int fboOnlyBlitOps,
                bool missingSceneSwapchainWriters,
                string? firstFailedOpType,
                int firstFailedPassIndex,
                int firstFailedPipelineIdentity,
                int firstFailedViewportIdentity,
                string? firstFailedTargetName,
                string? firstFailedMaterialName,
                string? firstFailedShaderName,
                string? firstFailedMessage,
                string? diagnosticSummary)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanFrameDiagnostics(
                        droppedFrameOps,
                        droppedDrawOps,
                        droppedComputeOps,
                        sceneSwapchainWriters,
                        overlaySwapchainWriters,
                        forcedDiagnosticSwapchainWriters,
                        fboOnlyDrawOps,
                        fboOnlyBlitOps,
                        missingSceneSwapchainWriters,
                        firstFailedOpType,
                        firstFailedPassIndex,
                        firstFailedPipelineIdentity,
                        firstFailedViewportIdentity,
                        firstFailedTargetName,
                        firstFailedMaterialName,
                        firstFailedShaderName,
                        firstFailedMessage,
                        diagnosticSummary);
                }
            }

            public static void RecordVulkanFrameGpuCommandBufferTime(TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanFrameGpuCommandBufferTime(elapsed);
            }

            public static void RecordVulkanFrameLifecycleTiming(
                TimeSpan waitFence,
                TimeSpan acquireImage,
                TimeSpan recordCommandBuffer,
                TimeSpan submit,
                TimeSpan trim,
                TimeSpan present,
                TimeSpan total)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanFrameLifecycleTiming(waitFence, acquireImage, recordCommandBuffer, submit, trim, present, total);
            }

            public static void RecordVulkanGpuDrivenStageTiming(EVulkanGpuDrivenStageTiming stage, TimeSpan elapsed)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanGpuDrivenStageTiming((int)stage, elapsed);
            }

            public static void RecordVulkanIndirectBatchMerge(int requestedBatches, int mergedBatches)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanIndirectBatchMerge(requestedBatches, mergedBatches);
            }

            public static void RecordVulkanIndirectEffectiveness(uint requestedDraws, uint culledDraws, uint emittedIndirectDraws, uint consumedDraws, uint overflowCount = 0u)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanIndirectEffectiveness(requestedDraws, culledDraws, emittedIndirectDraws, consumedDraws, overflowCount);
                    return;
                }

                VulkanRequestedDraws = requestedDraws;
                VulkanCulledDraws = culledDraws;
                VulkanEmittedIndirectDraws = emittedIndirectDraws;
                VulkanConsumedDraws = consumedDraws;
            }

            public static void RecordVulkanIndirectRecordingMode(bool usedSecondary = false, bool usedParallel = false, int opCount = 0)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanIndirectRecordingMode(usedSecondary, usedParallel, opCount);
            }

            public static void RecordVulkanIndirectSubmission(bool usedCountPath = false, bool usedLoopFallback = false, int apiCalls = 0, uint submittedDraws = 0u)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanIndirectSubmission(usedCountPath, usedLoopFallback, apiCalls, submittedDraws);
            }

            public static void RecordVulkanOomFallback()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanOomFallback();
                else
                    VulkanOomFallbackCount++;
            }

            public static void RecordVulkanPipelineCacheLookup(bool cacheHit)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanPipelineCacheLookup(cacheHit);
            }

            public static void RecordVulkanPipelineCacheMiss(string? summary)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanPipelineCacheMiss(summary);
            }

            public static void RecordVulkanQueueOverlapWindow(int overlapCandidatePasses, int transferCost, TimeSpan frameDelta, bool promotedMode, bool demotedMode)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanQueueOverlapWindow(overlapCandidatePasses, transferCost, frameDelta, promotedMode, demotedMode);
            }

            public static void RecordVulkanQueueSubmit()
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanQueueSubmit();
            }

            public static void RecordVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanRetiredResourcePlanReplacement(imageCount, bufferCount);
            }

            public static void RecordVulkanValidationMessage(bool isError, string message)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanValidationMessage(isError, message);
                    return;
                }

                VulkanValidationMessageCountCurrentFrame++;
                if (isError)
                    VulkanValidationErrorCountCurrentFrame++;
            }

            public static class Frame
            {
                public static int DrawCalls => Stats.DrawCalls;
                public static int MultiDrawCalls => Stats.MultiDrawCalls;
                public static int TrianglesRendered => Stats.TrianglesRendered;
                public static void IncrementDrawCalls(int count = 1) => Stats.IncrementDrawCalls(count);
                public static void IncrementMultiDrawCalls(int count = 1) => Stats.IncrementMultiDrawCalls(count);
                public static void AddTrianglesRendered(int count) => Stats.AddTrianglesRendered(count);
            }

            public static class Vram
            {
                public static void AddBufferAllocation(long bytes) => Stats.AddBufferAllocation(bytes);
                public static void RemoveBufferAllocation(long bytes) => Stats.RemoveBufferAllocation(bytes);
                public static void AddTextureAllocation(long bytes) => Stats.AddTextureAllocation(bytes);
                public static void RemoveTextureAllocation(long bytes) => Stats.RemoveTextureAllocation(bytes);
                public static void AddRenderBufferAllocation(long bytes) => Stats.AddRenderBufferAllocation(bytes);
                public static void RemoveRenderBufferAllocation(long bytes) => Stats.RemoveRenderBufferAllocation(bytes);
                public static bool CanAllocateVram(long requestedBytes, long currentAllocationBytes, out long projectedBytes, out long budgetBytes)
                    => Stats.CanAllocateVram(requestedBytes, currentAllocationBytes, out projectedBytes, out budgetBytes);
            }

            public static class GpuReadback
            {
                public static void RecordGpuBufferMapped(int count = 1) => Stats.RecordGpuBufferMapped(count);
                public static void RecordGpuReadbackBytes(long bytes) => Stats.RecordGpuReadbackBytes(bytes);
            }

            public static class GpuFallback
            {
                public static int GpuCpuFallbackEvents => Stats.GpuCpuFallbackEvents;
                public static int GpuCpuFallbackRecoveredCommands => Stats.GpuCpuFallbackRecoveredCommands;
                public static void RecordGpuCpuFallback(int events, int recoveredCommands) => Stats.RecordGpuCpuFallback(events, recoveredCommands);
                public static void RecordForbiddenGpuFallback(int events = 1) => Stats.RecordForbiddenGpuFallback(events);
            }

            public static class GpuTransparency
            {
                public static int GpuTransparencyOpaqueOrOtherVisible => Stats.GpuTransparencyOpaqueOrOtherVisible;
                public static int GpuTransparencyMaskedVisible => Stats.GpuTransparencyMaskedVisible;
                public static int GpuTransparencyApproximateVisible => Stats.GpuTransparencyApproximateVisible;
                public static int GpuTransparencyExactVisible => Stats.GpuTransparencyExactVisible;
                public static void RecordGpuTransparencyDomainCounts(int opaqueOrOther, int masked, int approximate, int exact)
                    => Stats.RecordGpuTransparencyDomainCounts(opaqueOrOther, masked, approximate, exact);
                public static void RecordGpuTransparencyDomainCounts(uint opaqueOrOther, uint masked, uint approximate, uint exact)
                    => Stats.RecordGpuTransparencyDomainCounts(opaqueOrOther, masked, approximate, exact);
            }

            public static class GpuMeshlets
            {
                public static int GpuMeshletRequestedFrames => Stats.GpuMeshletRequestedFrames;
                public static int GpuMeshletProductionFrames => Stats.GpuMeshletProductionFrames;
                public static int GpuMeshletFallbackFrames => Stats.GpuMeshletFallbackFrames;
                public static int GpuMeshletDispatchSkipped => Stats.GpuMeshletDispatchSkipped;
                public static long GpuMeshletTaskRecordsEmitted => Stats.GpuMeshletTaskRecordsEmitted;
                public static long GpuMeshletTaskRecordsFrustumCulled => Stats.GpuMeshletTaskRecordsFrustumCulled;
                public static long GpuMeshletTaskRecordsConeCulled => Stats.GpuMeshletTaskRecordsConeCulled;
                public static long GpuMeshletTaskRecordsHiZCulled => Stats.GpuMeshletTaskRecordsHiZCulled;
                public static long GpuMeshletExpansionOverflowCount => Stats.GpuMeshletExpansionOverflowCount;
                public static long GpuMeshletBufferBytesResident => Stats.GpuMeshletBufferBytesResident;
                public static long LastVisibleMeshletCount => Stats.LastVisibleMeshletCount;
                public static long LastDispatchedMeshletCount => Stats.LastDispatchedMeshletCount;
                public static long LastTaskRecordOverflowCount => Stats.LastTaskRecordOverflowCount;
                public static TimeSpan LastDispatchTime => Stats.LastDispatchTime;
                public static long LastReadbackBytes => Stats.LastReadbackBytes;
                public static int GpuMeshletCacheHits => Stats.GpuMeshletCacheHits;
                public static int GpuMeshletCacheMisses => Stats.GpuMeshletCacheMisses;
                public static int GpuMeshletCacheStale => Stats.GpuMeshletCacheStale;
                public static void RecordGpuMeshletStrategyRequested(
                    int renderPass,
                    EMeshSubmissionStrategy requestedStrategy,
                    EMeshSubmissionStrategy selectedStrategy,
                    EMeshShaderDialect dialect,
                    uint commandCount,
                    uint taskCapacity)
                    => Stats.RecordGpuMeshletStrategyRequested(renderPass, requestedStrategy, selectedStrategy, dialect, commandCount, taskCapacity);
                public static void RecordGpuMeshletProductionFrame(int eventCount = 1) => Stats.RecordGpuMeshletProductionFrame(eventCount);
                public static void RecordGpuMeshletFallback(int eventCount = 1) => Stats.RecordGpuMeshletFallback(eventCount);
                public static void RecordGpuMeshletDispatchSkipped(int eventCount = 1) => Stats.RecordGpuMeshletDispatchSkipped(eventCount);
                public static void RecordGpuMeshletTaskStats(uint emitted, uint frustumCulled, uint coneCulled, uint hiZCulled)
                    => Stats.RecordGpuMeshletTaskStats(emitted, frustumCulled, coneCulled, hiZCulled);
                public static void RecordGpuMeshletExpansionOverflow(uint overflowCount) => Stats.RecordGpuMeshletExpansionOverflow(overflowCount);
                public static void RecordGpuMeshletBufferBytesResident(ulong bytes) => Stats.RecordGpuMeshletBufferBytesResident(bytes);
                public static void RecordGpuMeshletInstrumentation(
                    uint visibleMeshletCount,
                    uint dispatchedMeshletCount,
                    uint taskRecordOverflowCount,
                    TimeSpan dispatchTime,
                    uint readbackBytes)
                    => Stats.RecordGpuMeshletInstrumentation(
                        visibleMeshletCount,
                        dispatchedMeshletCount,
                        taskRecordOverflowCount,
                        dispatchTime,
                        readbackBytes);
                public static void RecordGpuMeshletCacheHit(int eventCount = 1) => Stats.RecordGpuMeshletCacheHit(eventCount);
                public static void RecordGpuMeshletCacheMiss(int eventCount = 1) => Stats.RecordGpuMeshletCacheMiss(eventCount);
                public static void RecordGpuMeshletCacheStale(int eventCount = 1) => Stats.RecordGpuMeshletCacheStale(eventCount);
            }

            public static class Octree
            {
                public static void RecordOctreeCollect(int visibleRenderables, int emittedCommands)
                    => Stats.RecordOctreeCollect(visibleRenderables, emittedCommands);

                public static void RecordCpuSpatialTreeStats(string mode, SpatialTreeOccupancyStats occupancy, long collectTicks)
                    => Stats.RecordCpuSpatialTreeStats(mode, occupancy, collectTicks);
            }

            public static class RtxIo
            {
                public static void RecordRtxIoCopyIndirect(long bytes, TimeSpan elapsed) => Stats.RecordRtxIoCopyIndirect(bytes, elapsed);
                public static void RecordRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan elapsed)
                    => Stats.RecordRtxIoDecompression(compressedBytes, decompressedBytes, elapsed);
            }

            public static class SkinnedBounds
            {
                public static void RecordSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded)
                    => Stats.RecordSkinnedBoundsRefreshDeferredFinished(queueWaitTicks, cpuJobTicks, applyTicks, succeeded);
                public static void RecordSkinnedBoundsRefreshDeferredScheduled()
                    => Stats.RecordSkinnedBoundsRefreshDeferredScheduled();
                public static void RecordSkinnedBoundsRefreshGpuCompleted(long gpuTicks, long applyTicks)
                    => Stats.RecordSkinnedBoundsRefreshGpuCompleted(gpuTicks, applyTicks);
            }

            public static class Vr
            {
                public static void RecordVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
                    => Stats.RecordVrCommandBuildTimes(leftBuildTime, rightBuildTime);
                public static void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
                    => Stats.RecordVrPerViewDrawCounts(leftDraws, rightDraws);
                public static void RecordVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
                    => Stats.RecordVrPerViewVisibleCounts(leftVisible, rightVisible);
                public static void RecordVrRenderSubmitTime(TimeSpan elapsed) => Stats.RecordVrRenderSubmitTime(elapsed);
                public static void RecordVrXrWaitFrameBlockTime(TimeSpan elapsed) => Stats.RecordVrXrWaitFrameBlockTime(elapsed);
                public static void RecordVrXrEndFrameSubmitTime(TimeSpan elapsed) => Stats.RecordVrXrEndFrameSubmitTime(elapsed);
                public static void RecordVrXrPredictedToLatePoseDelta(double millimeters, double degrees)
                    => Stats.RecordVrXrPredictedToLatePoseDelta(millimeters, degrees);
                public static void RecordVrXrPredictedDisplayLeadTime(double leadTimeMs)
                    => Stats.RecordVrXrPredictedDisplayLeadTime(leadTimeMs);
                public static void RecordVrXrMissedDeadlineFrame() => Stats.RecordVrXrMissedDeadlineFrame();
                public static void RecordVrXrTrackingLossFrame() => Stats.RecordVrXrTrackingLossFrame();
                public static void RecordVrXrRelocatePredictedTime(TimeSpan elapsed) => Stats.RecordVrXrRelocatePredictedTime(elapsed);
                public static void RecordVrXrCollectFrustumExpansionDegrees(double degrees)
                    => Stats.RecordVrXrCollectFrustumExpansionDegrees(degrees);
                public static void RecordVrXrPacingThreadIdleTime(TimeSpan elapsed) => Stats.RecordVrXrPacingThreadIdleTime(elapsed);
                public static void RecordVrXrPacingHandoffStall() => Stats.RecordVrXrPacingHandoffStall();
            }

            public static class Vulkan
            {
                public static long VulkanRequestedDraws => Stats.VulkanRequestedDraws;
                public static long VulkanCulledDraws => Stats.VulkanCulledDraws;
                public static long VulkanEmittedIndirectDraws => Stats.VulkanEmittedIndirectDraws;
                public static long VulkanConsumedDraws => Stats.VulkanConsumedDraws;
                public static int VulkanQueueOwnershipTransfers => Stats.VulkanQueueOwnershipTransfers;
                public static int VulkanBarrierStageFlushes => Stats.VulkanBarrierStageFlushes;
                public static int VulkanDescriptorBindSkips => Stats.VulkanDescriptorBindSkips;
                public static int VulkanDescriptorFallbacksCurrentFrame => Stats.VulkanDescriptorFallbacksCurrentFrame;
                public static int VulkanDescriptorBindingFailuresCurrentFrame => Stats.VulkanDescriptorBindingFailuresCurrentFrame;
                public static int VulkanOomFallbackCount => Stats.VulkanOomFallbackCount;
                public static int VulkanValidationMessageCountCurrentFrame => Stats.VulkanValidationMessageCountCurrentFrame;
                public static int VulkanValidationErrorCountCurrentFrame => Stats.VulkanValidationErrorCountCurrentFrame;

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

                public static void RecordVulkanAdhocBarrier(int emittedCount = 0, int redundantCount = 0)
                    => Stats.RecordVulkanAdhocBarrier(emittedCount, redundantCount);
                public static void RecordVulkanAllocation(EVulkanAllocationTelemetryClass allocationClass, long bytes)
                    => Stats.RecordVulkanAllocation((Stats.EVulkanAllocationTelemetryClass)allocationClass, bytes);
                public static void RecordVulkanBarrierPlannerPass(int imageBarrierCount = 0, int bufferBarrierCount = 0, int queueOwnershipTransfers = 0, int stageFlushes = 0)
                    => Stats.RecordVulkanBarrierPlannerPass(imageBarrierCount, bufferBarrierCount, queueOwnershipTransfers, stageFlushes);
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
                    => Stats.RecordVulkanBindChurn(
                        pipelineBinds,
                        descriptorBinds,
                        pushConstantWrites,
                        vertexBufferBinds,
                        indexBufferBinds,
                        pipelineBindSkips,
                        descriptorBindSkips,
                        vertexBufferBindSkips,
                        indexBufferBindSkips);
                public static void RecordVulkanDescriptorBindingFailure(
                    string? programName = null,
                    string? bindingClass = null,
                    string? bindingName = null,
                    uint set = 0,
                    uint binding = 0,
                    bool skippedDraw = false,
                    bool skippedDispatch = false,
                    string? reason = null)
                    => Stats.RecordVulkanDescriptorBindingFailure(programName, bindingClass, bindingName, set, binding, skippedDraw, skippedDispatch, reason);
                public static void RecordVulkanDescriptorFallback(
                    string? programName,
                    string? bindingClass,
                    string? bindingName,
                    uint set,
                    uint binding,
                    int count = 1)
                    => Stats.RecordVulkanDescriptorFallback(programName, bindingClass, bindingName, set, binding, count);
                public static void RecordVulkanDescriptorPoolCreate() => Stats.RecordVulkanDescriptorPoolCreate();
                public static void RecordVulkanDescriptorPoolDestroy() => Stats.RecordVulkanDescriptorPoolDestroy();
                public static void RecordVulkanDescriptorPoolReset() => Stats.RecordVulkanDescriptorPoolReset();
                public static void RecordVulkanDynamicUniformAllocation(long bytes) => Stats.RecordVulkanDynamicUniformAllocation(bytes);
                public static void RecordVulkanDynamicUniformExhaustion() => Stats.RecordVulkanDynamicUniformExhaustion();
                public static void RecordVulkanFrameDiagnostics(
                    int droppedFrameOps,
                    int droppedDrawOps,
                    int droppedComputeOps,
                    int sceneSwapchainWriters,
                    int overlaySwapchainWriters,
                    int forcedDiagnosticSwapchainWriters,
                    int fboOnlyDrawOps,
                    int fboOnlyBlitOps,
                    bool missingSceneSwapchainWriters,
                    string? firstFailedOpType,
                    int firstFailedPassIndex,
                    int firstFailedPipelineIdentity,
                    int firstFailedViewportIdentity,
                    string? firstFailedTargetName,
                    string? firstFailedMaterialName,
                    string? firstFailedShaderName,
                    string? firstFailedMessage,
                    string? diagnosticSummary)
                    => Stats.RecordVulkanFrameDiagnostics(
                        droppedFrameOps,
                        droppedDrawOps,
                        droppedComputeOps,
                        sceneSwapchainWriters,
                        overlaySwapchainWriters,
                        forcedDiagnosticSwapchainWriters,
                        fboOnlyDrawOps,
                        fboOnlyBlitOps,
                        missingSceneSwapchainWriters,
                        firstFailedOpType,
                        firstFailedPassIndex,
                        firstFailedPipelineIdentity,
                        firstFailedViewportIdentity,
                        firstFailedTargetName,
                        firstFailedMaterialName,
                        firstFailedShaderName,
                        firstFailedMessage,
                        diagnosticSummary);
                public static void RecordVulkanFrameGpuCommandBufferTime(TimeSpan elapsed) => Stats.RecordVulkanFrameGpuCommandBufferTime(elapsed);
                public static void RecordVulkanFrameLifecycleTiming(
                    TimeSpan waitFence,
                    TimeSpan acquireImage,
                    TimeSpan recordCommandBuffer,
                    TimeSpan submit,
                    TimeSpan trim,
                    TimeSpan present,
                    TimeSpan total)
                    => Stats.RecordVulkanFrameLifecycleTiming(waitFence, acquireImage, recordCommandBuffer, submit, trim, present, total);
                public static void RecordVulkanGpuDrivenStageTiming(EVulkanGpuDrivenStageTiming stage, TimeSpan elapsed)
                    => Stats.RecordVulkanGpuDrivenStageTiming((Stats.EVulkanGpuDrivenStageTiming)stage, elapsed);
                public static void RecordVulkanIndirectBatchMerge(int requestedBatches, int mergedBatches)
                    => Stats.RecordVulkanIndirectBatchMerge(requestedBatches, mergedBatches);
                public static void RecordVulkanIndirectEffectiveness(uint requestedDraws, uint culledDraws, uint emittedIndirectDraws, uint consumedDraws, uint overflowCount = 0u)
                    => Stats.RecordVulkanIndirectEffectiveness(requestedDraws, culledDraws, emittedIndirectDraws, consumedDraws, overflowCount);
                public static void RecordVulkanIndirectRecordingMode(bool usedSecondary = false, bool usedParallel = false, int opCount = 0)
                    => Stats.RecordVulkanIndirectRecordingMode(usedSecondary, usedParallel, opCount);
                public static void RecordVulkanIndirectSubmission(bool usedCountPath = false, bool usedLoopFallback = false, int apiCalls = 0, uint submittedDraws = 0u)
                    => Stats.RecordVulkanIndirectSubmission(usedCountPath, usedLoopFallback, apiCalls, submittedDraws);
                public static void RecordVulkanOomFallback() => Stats.RecordVulkanOomFallback();
                public static void RecordVulkanPipelineCacheLookup(bool cacheHit) => Stats.RecordVulkanPipelineCacheLookup(cacheHit);
                public static void RecordVulkanPipelineCacheMiss(string? summary) => Stats.RecordVulkanPipelineCacheMiss(summary);
                public static void RecordVulkanQueueOverlapWindow(int overlapCandidatePasses, int transferCost, TimeSpan frameDelta, bool promotedMode, bool demotedMode)
                    => Stats.RecordVulkanQueueOverlapWindow(overlapCandidatePasses, transferCost, frameDelta, promotedMode, demotedMode);
                public static void RecordVulkanQueueSubmit() => Stats.RecordVulkanQueueSubmit();
                public static void RecordVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount)
                    => Stats.RecordVulkanRetiredResourcePlanReplacement(imageCount, bufferCount);
                public static void RecordVulkanValidationMessage(bool isError, string message)
                    => Stats.RecordVulkanValidationMessage(isError, message);
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
            public static bool IsDirectionalCascadeLayeredShadowPass => RenderingPipelineState?.DirectionalCascadeLayeredShadowPass ?? false;
            public static bool IsDirectionalCascadeInstancedLayeredShadowPass => RenderingPipelineState?.DirectionalCascadeInstancedLayeredShadowPass ?? false;
            public static bool IsDirectionalCascadeAtlasGroupedShadowPass => RenderingPipelineState?.DirectionalCascadeAtlasGroupedShadowPass ?? false;
            public static int DirectionalCascadeShadowLayerCount => RenderingPipelineState?.DirectionalCascadeShadowLayerCount ?? 0;
            public static bool IsPointLightLayeredShadowPass => RenderingPipelineState?.PointLightLayeredShadowPass ?? false;
            public static bool IsPointLightInstancedLayeredShadowPass => RenderingPipelineState?.PointLightInstancedLayeredShadowPass ?? false;
            public static bool IsPointLightAtlasGroupedShadowPass => RenderingPipelineState?.PointLightAtlasGroupedShadowPass ?? false;
            public static int PointLightShadowFaceCount => RenderingPipelineState?.PointLightShadowFaceCount ?? 0;
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
            public static bool SupportsOpenGLLayeredFramebuffers { get; internal set; }
            public static bool SupportsOpenGLGeometryShaderLayeredRendering { get; internal set; }
            public static bool SupportsOpenGLVertexShaderLayeredRendering { get; internal set; }
            public static bool SupportsOpenGLViewportArray { get; internal set; }
            public static bool SupportsOpenGLViewportScissorArray { get; internal set; }
            public static bool SupportsOpenGLVertexShaderViewportIndex { get; internal set; }
            public static bool SupportsOpenGLGeometryShaderViewportIndex { get; internal set; }
            public static int MaxOpenGLViewports { get; internal set; } = 1;
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
            public static string OpenGLParallelShaderCompileExtension { get; internal set; } = string.Empty;
            public static bool OpenGLParallelShaderCompileProbePassed { get; internal set; }
            public static string[] OpenGLExtensions { get; internal set; } = [];
            public static string? OpenGLVendor { get; internal set; }
            public static string? OpenGLRendererName { get; internal set; }
            public static string? VulkanDeviceName { get; internal set; }
            public static uint VulkanVendorId { get; internal set; }
            public static uint VulkanDeviceId { get; internal set; }
            public static XRDataBuffer? ForwardPlusLocalLightsBuffer { get; internal set; }
            public static XRDataBuffer? ForwardPlusVisibleIndicesBuffer { get; internal set; }
            public static XRDataBuffer? ForwardPlusTileLightCountsBuffer { get; internal set; }
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

            public ulong RenderFrameId => RuntimeRenderingHostServices.Current.CurrentRenderFrameId;
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
                // RenderFrameId is now sourced from the host bridge; no-op retained for legacy callers.
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
    private bool _allowBlendshapes = RuntimeRenderingHostServiceDefaults.AllowBlendshapes;
    private bool _allowShaderPipelines = RuntimeRenderingHostServiceDefaults.AllowShaderPipelines;
    private bool _allowSkinning = RuntimeRenderingHostServiceDefaults.AllowSkinning;
    private bool _calculateBlendshapesInComputeShader = RuntimeRenderingHostServiceDefaults.CalculateBlendshapesInComputeShader;
    private bool _calculateSkinningInComputeShader = RuntimeRenderingHostServiceDefaults.CalculateSkinningInComputeShader;
    private bool _calculateSkinnedBoundsInComputeShader;
    private bool _skinnedBoundsGpuDirectAabbWrite;
    private bool _enableBlendshapePrecombinePass = RuntimeRenderingHostServiceDefaults.EnableBlendshapePrecombinePass;
    private bool _enableBlendshapePrecombineForDirectVertexPath = RuntimeRenderingHostServiceDefaults.EnableBlendshapePrecombineForDirectVertexPath;
    private bool _enableBlendshapePcaBasisCompression = RuntimeRenderingHostServiceDefaults.EnableBlendshapePcaBasisCompression;
    private int _blendshapePrecombineComputeMinActiveShapes = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineComputeMinActiveShapes;
    private int _blendshapePrecombineDirectMinActiveShapes = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineDirectMinActiveShapes;
    private int _blendshapePrecombineMinAffectedVertices = RuntimeRenderingHostServiceDefaults.BlendshapePrecombineMinAffectedVertices;
    private bool _useIntegerUniformsInShaders = RuntimeRenderingHostServiceDefaults.UseIntegerUniformsInShaders;
    private bool _useSpotShadowAtlas = true;
    private bool _useDirectionalShadowAtlas = true;
    private bool _usePointShadowAtlas = true;
    private uint _shadowAtlasPageSize = 4096u;
    private int _maxShadowAtlasPages = 1;
    private long _maxShadowAtlasMemoryBytes;
    private int _maxShadowTilesRenderedPerFrame = 16;
    private float _maxShadowRenderMilliseconds = 2.0f;
    private uint _minShadowAtlasTileResolution = 128u;
    private uint _maxShadowAtlasTileResolution = 4096u;
    private int _shaderConfigVersion = RuntimeRenderingHostServiceDefaults.ShaderConfigVersion;
    private bool _openXrCullWithFrustum = RuntimeRenderingHostServiceDefaults.OpenXrCullWithFrustum;
    private bool _openXrDebugClearOnly = RuntimeRenderingHostServiceDefaults.OpenXrDebugClearOnly;
    private bool _openXrDebugGl = RuntimeRenderingHostServiceDefaults.OpenXrDebugGl;
    private bool _openXrDebugLifecycle = RuntimeRenderingHostServiceDefaults.OpenXrDebugLifecycle;
    private bool _openXrDebugRenderRightThenLeft = RuntimeRenderingHostServiceDefaults.OpenXrDebugRenderRightThenLeft;
    private bool _openXrPrepareFrameAfterDesktopRender = RuntimeRenderingHostServiceDefaults.OpenXrPrepareFrameAfterDesktopRender;
    private float _openXrDeadlineSafetyMarginMs = RuntimeRenderingHostServiceDefaults.OpenXrDeadlineSafetyMarginMs;
    private OpenXRAPI.OpenXrCollectVisiblePosePolicy _openXrCollectVisiblePosePolicy = OpenXRAPI.OpenXrCollectVisiblePosePolicy.Predicted;
    private float _openXrCollectVisibleFrustumPaddingDegrees = RuntimeRenderingHostServiceDefaults.OpenXrCollectVisibleFrustumPaddingDegrees;
    private OpenXRAPI.OpenXrTrackingLossPolicy _openXrTrackingLossPolicy = OpenXRAPI.OpenXrTrackingLossPolicy.FreezeLastValid;
    private OpenXRAPI.OpenXrActionSyncPolicy _openXrActionSyncPolicy = OpenXRAPI.OpenXrActionSyncPolicy.PredictedOnly;
    private OpenXRAPI.OpenXrRenderPacingMode _openXrRenderPacingMode = OpenXRAPI.OpenXrRenderPacingMode.PostRenderCallback;

    private bool _allowBinaryProgramCaching = RuntimeRenderingHostServiceDefaults.AllowBinaryProgramCaching;
    public bool AllowBinaryProgramCaching
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AllowBinaryProgramCaching
            : _allowBinaryProgramCaching;
        set => _allowBinaryProgramCaching = value;
    }
    public bool AllowBlendshapes
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AllowBlendshapes
            : _allowBlendshapes;
        set => SetShaderSetting(ref _allowBlendshapes, value);
    }
    public bool AllowShaderPipelines
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AllowShaderPipelines
            : _allowShaderPipelines;
        set
        {
            bool previous = AllowShaderPipelines;
            SetShaderSetting(ref _allowShaderPipelines, value);
            if (previous != AllowShaderPipelines)
            {
                global::XREngine.Rendering.OpenGL.OpenGLRenderer.HandleShaderPipelineModeChanged(AllowShaderPipelines);
                XRMaterial.DisposeShaderPipelineProgramsWhenDisabled();
            }
        }
    }
    public bool AllowSkinning
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AllowSkinning
            : _allowSkinning;
        set => SetShaderSetting(ref _allowSkinning, value);
    }
    private bool _asyncProgramBinaryUpload = RuntimeRenderingHostServiceDefaults.AsyncProgramBinaryUpload;
    public bool AsyncProgramBinaryUpload
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AsyncProgramBinaryUpload
            : _asyncProgramBinaryUpload;
        set => _asyncProgramBinaryUpload = value;
    }
    private bool _asyncProgramCompilation = RuntimeRenderingHostServiceDefaults.AsyncProgramCompilation;
    public bool AsyncProgramCompilation
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.AsyncProgramCompilation
            : _asyncProgramCompilation;
        set => _asyncProgramCompilation = value;
    }
    private int _openGLProgramCompileLinkWorkerCount = RuntimeRenderingHostServiceDefaults.OpenGLProgramCompileLinkWorkerCount;
    public int OpenGLProgramCompileLinkWorkerCount
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLProgramCompileLinkWorkerCount
            : _openGLProgramCompileLinkWorkerCount;
        set => _openGLProgramCompileLinkWorkerCount = Math.Clamp(value, 1, 16);
    }
    public bool CacheGpuHiZOcclusionOncePerFrame { get; set; } = true;
    public bool CalculateBlendshapesInComputeShader
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CalculateBlendshapesInComputeShader
            : _calculateBlendshapesInComputeShader;
        set => SetShaderSetting(ref _calculateBlendshapesInComputeShader, value);
    }
    public bool EnableBlendshapePrecombinePass
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.EnableBlendshapePrecombinePass
            : _enableBlendshapePrecombinePass;
        set => SetShaderSetting(ref _enableBlendshapePrecombinePass, value);
    }
    public bool EnableBlendshapePrecombineForDirectVertexPath
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.EnableBlendshapePrecombineForDirectVertexPath
            : _enableBlendshapePrecombineForDirectVertexPath;
        set => SetShaderSetting(ref _enableBlendshapePrecombineForDirectVertexPath, value);
    }
    public bool EnableBlendshapePcaBasisCompression
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.EnableBlendshapePcaBasisCompression
            : _enableBlendshapePcaBasisCompression;
        set => SetShaderSetting(ref _enableBlendshapePcaBasisCompression, value);
    }
    public int BlendshapePrecombineComputeMinActiveShapes
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.BlendshapePrecombineComputeMinActiveShapes
            : _blendshapePrecombineComputeMinActiveShapes;
        set => _blendshapePrecombineComputeMinActiveShapes = Math.Max(1, value);
    }
    public int BlendshapePrecombineDirectMinActiveShapes
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.BlendshapePrecombineDirectMinActiveShapes
            : _blendshapePrecombineDirectMinActiveShapes;
        set => _blendshapePrecombineDirectMinActiveShapes = Math.Max(1, value);
    }
    public int BlendshapePrecombineMinAffectedVertices
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.BlendshapePrecombineMinAffectedVertices
            : _blendshapePrecombineMinAffectedVertices;
        set => _blendshapePrecombineMinAffectedVertices = Math.Max(1, value);
    }
    public bool CalculateSkinnedBoundsInComputeShader
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CalculateSkinnedBoundsInComputeShader
            : _calculateSkinnedBoundsInComputeShader;
        set => _calculateSkinnedBoundsInComputeShader = value;
    }
    public bool CalculateSkinningInComputeShader
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CalculateSkinningInComputeShader
            : _calculateSkinningInComputeShader;
        set => SetShaderSetting(ref _calculateSkinningInComputeShader, value);
    }
    public bool SkinnedBoundsGpuDirectAabbWrite
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.SkinnedBoundsGpuDirectAabbWrite
            : _skinnedBoundsGpuDirectAabbWrite;
        set => _skinnedBoundsGpuDirectAabbWrite = value;
    }
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
    private int _maxAsyncShaderProgramsPerFrame = RuntimeRenderingHostServiceDefaults.MaxAsyncShaderProgramsPerFrame;
    public int MaxAsyncShaderProgramsPerFrame
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxAsyncShaderProgramsPerFrame
            : _maxAsyncShaderProgramsPerFrame;
        set => _maxAsyncShaderProgramsPerFrame = value;
    }
    private EOpenGLShaderLinkStrategy _openGLShaderLinkStrategy = RuntimeRenderingHostServiceDefaults.OpenGLShaderLinkStrategy;
    public EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLShaderLinkStrategy
            : _openGLShaderLinkStrategy;
        set => _openGLShaderLinkStrategy = value;
    }
    private int _openGLShaderCompilerThreadCount = RuntimeRenderingHostServiceDefaults.OpenGLShaderCompilerThreadCount;
    public int OpenGLShaderCompilerThreadCount
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLShaderCompilerThreadCount
            : _openGLShaderCompilerThreadCount;
        set => _openGLShaderCompilerThreadCount = value;
    }
    private bool _openGLParallelShaderCompileProbeEnabled = RuntimeRenderingHostServiceDefaults.OpenGLParallelShaderCompileProbeEnabled;
    public bool OpenGLParallelShaderCompileProbeEnabled
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLParallelShaderCompileProbeEnabled
            : _openGLParallelShaderCompileProbeEnabled;
        set => _openGLParallelShaderCompileProbeEnabled = value;
    }
    private int _openGLParallelShaderCompileProbeTimeoutMs = RuntimeRenderingHostServiceDefaults.OpenGLParallelShaderCompileProbeTimeoutMs;
    public int OpenGLParallelShaderCompileProbeTimeoutMs
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenGLParallelShaderCompileProbeTimeoutMs
            : _openGLParallelShaderCompileProbeTimeoutMs;
        set => _openGLParallelShaderCompileProbeTimeoutMs = value;
    }
    public long MaxShadowAtlasMemoryBytes
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowAtlasMemoryBytes
            : _maxShadowAtlasMemoryBytes;
        set => _maxShadowAtlasMemoryBytes = Math.Max(0L, value);
    }

    public int MaxShadowAtlasPages
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowAtlasPages
            : _maxShadowAtlasPages;
        set => _maxShadowAtlasPages = Math.Clamp(value, 1, 64);
    }

    public uint MaxShadowAtlasTileResolution
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowAtlasTileResolution
            : _maxShadowAtlasTileResolution;
        set => _maxShadowAtlasTileResolution = value;
    }

    public float MaxShadowRenderMilliseconds
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowRenderMilliseconds
            : _maxShadowRenderMilliseconds;
        set => _maxShadowRenderMilliseconds = MathF.Max(0.0f, value);
    }

    public int MaxShadowTilesRenderedPerFrame
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MaxShadowTilesRenderedPerFrame
            : _maxShadowTilesRenderedPerFrame;
        set => _maxShadowTilesRenderedPerFrame = Math.Max(0, value);
    }

    public uint MinShadowAtlasTileResolution
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.MinShadowAtlasTileResolution
            : _minShadowAtlasTileResolution;
        set => _minShadowAtlasTileResolution = value;
    }

    public bool OpenXrCullWithFrustum
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrCullWithFrustum
            : _openXrCullWithFrustum;
        set => _openXrCullWithFrustum = value;
    }
    public bool OpenXrDebugClearOnly
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDebugClearOnly
            : _openXrDebugClearOnly;
        set => _openXrDebugClearOnly = value;
    }
    public bool OpenXrDebugGl
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDebugGl
            : _openXrDebugGl;
        set => _openXrDebugGl = value;
    }
    public bool OpenXrDebugLifecycle
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDebugLifecycle
            : _openXrDebugLifecycle;
        set => _openXrDebugLifecycle = value;
    }
    public bool OpenXrDebugRenderRightThenLeft
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDebugRenderRightThenLeft
            : _openXrDebugRenderRightThenLeft;
        set => _openXrDebugRenderRightThenLeft = value;
    }
    public bool OpenXrPrepareFrameAfterDesktopRender
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrPrepareFrameAfterDesktopRender
            : _openXrPrepareFrameAfterDesktopRender;
        set => _openXrPrepareFrameAfterDesktopRender = value;
    }
    public float OpenXrDeadlineSafetyMarginMs
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrDeadlineSafetyMarginMs
            : _openXrDeadlineSafetyMarginMs;
        set => _openXrDeadlineSafetyMarginMs = MathF.Max(0.0f, value);
    }
    public OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrCollectVisiblePosePolicy
            : _openXrCollectVisiblePosePolicy;
        set => _openXrCollectVisiblePosePolicy = value;
    }
    public float OpenXrCollectVisibleFrustumPaddingDegrees
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrCollectVisibleFrustumPaddingDegrees
            : _openXrCollectVisibleFrustumPaddingDegrees;
        set => _openXrCollectVisibleFrustumPaddingDegrees = Math.Clamp(value, 0.0f, 20.0f);
    }
    public OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrTrackingLossPolicy
            : _openXrTrackingLossPolicy;
        set => _openXrTrackingLossPolicy = value;
    }
    public OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrActionSyncPolicy
            : _openXrActionSyncPolicy;
        set => _openXrActionSyncPolicy = value;
    }
    public OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.OpenXrRenderPacingMode
            : _openXrRenderPacingMode;
        set => _openXrRenderPacingMode = value;
    }
    public bool OutputHDR { get; set; } = true;
    public bool PreferNVStereo { get; set; }
    public bool ProcessMeshImportsAsynchronously { get; set; } = true;
    public bool RenderVRSinglePassStereo { get; set; } = true;
    public int ShaderConfigVersion
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.ShaderConfigVersion
            : _shaderConfigVersion;
        set
        {
            if (_shaderConfigVersion == value)
                return;

            _shaderConfigVersion = value;
            RuntimeEngine.Rendering.RaiseSettingsChanged();
        }
    }
    public uint ShadowAtlasPageSize
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.ShadowAtlasPageSize
            : _shadowAtlasPageSize;
        set => _shadowAtlasPageSize = value;
    }

    public float TsrRenderScale { get; set; } = 1.0f;
    public bool UseAbsoluteBlendshapePositions { get; set; }
    public bool UseDetailPreservingComputeMipmaps { get; set; } = true;
    public bool UseDirectionalShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.UseDirectionalShadowAtlas
            : _useDirectionalShadowAtlas;
        set => _useDirectionalShadowAtlas = value;
    }

    public bool UseGlobalBlendshapeWeightsBufferForComputeSkinning { get; set; } = true;
    public bool UseGlobalSkinPaletteBufferForComputeSkinning { get; set; } = true;
    public bool UseIntegerUniformsInShaders
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.UseIntegerUniformsInShaders
            : _useIntegerUniformsInShaders;
        set => SetShaderSetting(ref _useIntegerUniformsInShaders, value);
    }
    public bool UsePointShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.UsePointShadowAtlas
            : _usePointShadowAtlas;
        set => _usePointShadowAtlas = value;
    }

    public bool UseSkinnedBvhRefitOptimize { get; set; } = true;
    public bool UseSpotShadowAtlas
    {
        get => TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
            ? services.UseSpotShadowAtlas
            : _useSpotShadowAtlas;
        set => _useSpotShadowAtlas = value;
    }

    public RuntimeVulkanRobustnessSettings VulkanRobustnessSettings { get; } = new();
    public float XessCustomScale { get; set; } = 1.0f;
    public float XessSharpness { get; set; }

    private static bool TryGetHostShadowAtlasSettings(out IRuntimeRenderingHostServices services)
    {
        services = RuntimeRenderingHostServices.Current;
        return services.ProvidesShadowAtlasSettings;
    }

    private static bool TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
    {
        services = RuntimeRenderingHostServices.Current;
        return RuntimeRenderingHostServices.HasConcreteHost;
    }

    private void SetShaderSetting(ref bool field, bool value)
    {
        if (field == value)
            return;

        field = value;
        unchecked
        {
            _shaderConfigVersion++;
        }
        RuntimeEngine.Rendering.RaiseSettingsChanged();
    }
}

internal sealed class RuntimeEffectiveSettings
{
    private RuntimeRenderSettings Settings => RuntimeEngine.Rendering.Settings;

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
    public EZeroReadbackMaterialDrawPath ZeroReadbackMaterialDrawPath
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.ZeroReadbackMaterialDrawPath;
            return !string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse(raw, ignoreCase: true, out EZeroReadbackMaterialDrawPath parsed)
                ? parsed
                : _zeroReadbackMaterialDrawPath;
        }
        set => _zeroReadbackMaterialDrawPath = value;
    }
    public EGpuCullingDataLayout GpuCullingDataLayout { get; set; } = EGpuCullingDataLayout.AoSHot;
    private EOcclusionCullingMode _gpuOcclusionCullingMode = EOcclusionCullingMode.GpuHiZ;
    public EOcclusionCullingMode GpuOcclusionCullingMode
    {
        get
        {
            EOcclusionCullingMode resolved = TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
                ? services.GpuOcclusionCullingMode
                : _gpuOcclusionCullingMode;
            string? raw = EffectiveSettingsEnvOverrides.OcclusionCullingMode;
            if (!string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse(raw, ignoreCase: true, out EOcclusionCullingMode parsed))
            {
                resolved = parsed;
            }

            return resolved;
        }
        set => _gpuOcclusionCullingMode = value;
    }
    private int _cpuQueryOcclusionRetestPeriodFrames = 6;
    public int CpuQueryOcclusionRetestPeriodFrames
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.CpuQueryOcclusionRetestPeriodFrames;
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int parsed))
                return Math.Clamp(parsed, 1, 64);

            return TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
                ? Math.Clamp(services.CpuQueryOcclusionRetestPeriodFrames, 1, 64)
                : _cpuQueryOcclusionRetestPeriodFrames;
        }
        set => _cpuQueryOcclusionRetestPeriodFrames = Math.Clamp(value, 1, 64);
    }
    private bool _enableCpuSoftwareOcclusionCulling = false;
    public bool EnableCpuSoftwareOcclusionCulling
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.CpuSocOcclusion;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string trimmed = raw.Trim();
                if (trimmed == "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (trimmed == "0" || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
                ? services.EnableCpuSoftwareOcclusionCulling
                : _enableCpuSoftwareOcclusionCulling;
        }
        set => _enableCpuSoftwareOcclusionCulling = value;
    }
    private int _cpuSocBufferWidth = 256;
    public int CpuSocBufferWidth
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? Math.Clamp(services.CpuSocBufferWidth, 64, 4096)
            : _cpuSocBufferWidth;
        set => _cpuSocBufferWidth = Math.Clamp(value, 64, 4096);
    }
    private int _cpuSocBufferHeight = 128;
    public int CpuSocBufferHeight
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? Math.Clamp(services.CpuSocBufferHeight, 32, 4096)
            : _cpuSocBufferHeight;
        set => _cpuSocBufferHeight = Math.Clamp(value, 32, 4096);
    }
    private int _cpuSocOccluderTriangleBudget = 5000;
    public int CpuSocOccluderTriangleBudget
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? Math.Clamp(services.CpuSocOccluderTriangleBudget, 0, 1_000_000)
            : _cpuSocOccluderTriangleBudget;
        set => _cpuSocOccluderTriangleBudget = Math.Clamp(value, 0, 1_000_000);
    }
    private int _cpuSocMaxOccluders = 64;
    public int CpuSocMaxOccluders
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? Math.Clamp(services.CpuSocMaxOccluders, 0, 4096)
            : _cpuSocMaxOccluders;
        set => _cpuSocMaxOccluders = Math.Clamp(value, 0, 4096);
    }
    private float _cpuSocMinOccluderScreenArea = 0.005f;
    public float CpuSocMinOccluderScreenArea
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? Math.Clamp(services.CpuSocMinOccluderScreenArea, 0.0f, 1.0f)
            : _cpuSocMinOccluderScreenArea;
        set => _cpuSocMinOccluderScreenArea = Math.Clamp(value, 0.0f, 1.0f);
    }
    private bool _cpuSocUseAvx2 = true;
    public bool CpuSocUseAvx2
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CpuSocUseAvx2
            : _cpuSocUseAvx2;
        set => _cpuSocUseAvx2 = value;
    }
    private bool _cpuSocDebugVisualization;
    public bool CpuSocDebugVisualization
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CpuSocDebugVisualization
            : _cpuSocDebugVisualization;
        set => _cpuSocDebugVisualization = value;
    }
    private bool _cpuSocDebugForceVisible;
    public bool CpuSocDebugForceVisible
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.CpuSocDebugForceVisible
            : _cpuSocDebugForceVisible;
        set => _cpuSocDebugForceVisible = value;
    }
    public bool GPURenderDispatch { get; set; }
    public EMeshSubmissionStrategy? ForceMeshSubmissionStrategy
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.ForceMeshSubmissionStrategy;
            if (EMeshSubmissionStrategyExtensions.TryParseMeshSubmissionStrategy(
                    raw,
                    out EMeshSubmissionStrategy parsed,
                    out bool usedLegacyName))
            {
                if (usedLegacyName && !_legacyGpuMeshletForceStrategyWarningLogged)
                {
                    _legacyGpuMeshletForceStrategyWarningLogged = true;
                    RuntimeEngine.LogWarning(
                        "XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuMeshlet is deprecated; use GpuMeshletZeroReadback.");
                }

                return parsed;
            }

            return _forceMeshSubmissionStrategy;
        }
        set => _forceMeshSubmissionStrategy = value;
    }
    public uint MsaaSampleCount { get; set; } = 1u;
    public ESkinnedBoundsRecomputePolicy SkinnedBoundsRecomputePolicy { get; set; } = ESkinnedBoundsRecomputePolicy.Selective;
    public bool UseGpuBvh { get; set; }
    private ECpuSceneCullingStructure _cpuSceneCullingStructure = RuntimeRenderingHostServiceDefaults.CpuSceneCullingStructure;
    public ECpuSceneCullingStructure CpuSceneCullingStructure
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.CpuSceneCullingStructure;
            if (!string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse(raw, ignoreCase: true, out ECpuSceneCullingStructure parsed))
            {
                return parsed;
            }

            return TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
                ? services.CpuSceneCullingStructure
                : _cpuSceneCullingStructure;
        }
        set => _cpuSceneCullingStructure = value;
    }
    public bool ValidateVulkanDescriptorContracts { get; set; }
    public EVulkanGeometryFetchMode VulkanGeometryFetchMode { get; set; } = EVulkanGeometryFetchMode.Atlas;
    public EVulkanGpuDrivenProfile VulkanGpuDrivenProfile { get; set; } = EVulkanGpuDrivenProfile.Auto;
    public EVulkanQueueOverlapMode VulkanQueueOverlapMode { get; set; } = EVulkanQueueOverlapMode.Auto;
    public EXessQualityMode XessQuality { get; set; } = EXessQualityMode.Quality;

    private EMeshSubmissionStrategy? _forceMeshSubmissionStrategy;
    private bool _legacyGpuMeshletForceStrategyWarningLogged;
    private EZeroReadbackMaterialDrawPath _zeroReadbackMaterialDrawPath = EZeroReadbackMaterialDrawPath.FullBucketScan;

    private static bool TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
    {
        services = RuntimeRenderingHostServices.Current;
        return RuntimeRenderingHostServices.HasConcreteHost;
    }
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
    public RuntimeTimerFrame Update { get; } = new(ERuntimeTimerFrameKind.Update);
    public RuntimeTimerFrame Render { get; } = new(ERuntimeTimerFrameKind.Render);
    public event Action? UpdateFrame;
    public event Action? CollectVisible;
    public event Action? SwapBuffers;

    public void RaiseUpdateFrame() => UpdateFrame?.Invoke();
    public void RaiseCollectVisible() => CollectVisible?.Invoke();
    public void RaiseSwapBuffers() => SwapBuffers?.Invoke();
}

internal enum ERuntimeTimerFrameKind
{
    Update,
    Render,
}

internal sealed class RuntimeTimerFrame(ERuntimeTimerFrameKind kind)
{
    public float Delta
        => (float)(kind == ERuntimeTimerFrameKind.Update
            ? RuntimeRenderingHostServices.Current.UpdateDeltaSeconds
            : RuntimeRenderingHostServices.Current.RenderDeltaSeconds);

    public long LastTimestampTicks
        => kind == ERuntimeTimerFrameKind.Update
            ? RuntimeRenderingHostServices.Current.LastUpdateTimestampTicks
            : RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
}

internal sealed class RuntimePlayMode
{
    public event Action? PostEnterPlay;
    public event Action? PreExitPlay;
    public bool IsTransitioning { get; set; }
    public RuntimePlayModeState State { get; set; } = RuntimePlayModeState.Edit;

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
    public bool HoverOutlineEnabled { get; set; } = true;
    public bool SelectionOutlineEnabled { get; set; } = true;
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
    public bool Preview3DWorldOctree { get; set; }
    public bool Preview2DWorldQuadtree { get; set; }
    public bool AllowGpuCpuFallback { get; set; } = true;
    public bool VisualizeTransparencyModeOverlay { get; set; }
    public bool VisualizeTransparencyClassificationOverlay { get; set; }
    public bool EnableZeroReadbackMaterialScatter { get; set; }
    public EZeroReadbackMaterialDrawPath ZeroReadbackMaterialDrawPath { get; set; } = EZeroReadbackMaterialDrawPath.FullBucketScan;
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
    public ColorF4 OctreeIntersectedBoundsColor { get; set; } = ColorF4.LightGray;
    public ColorF4 OctreeContainedBoundsColor { get; set; } = ColorF4.Yellow;
    public ColorF4 QuadtreeIntersectedBoundsColor { get; set; } = ColorF4.LightGray;
    public ColorF4 QuadtreeContainedBoundsColor { get; set; } = ColorF4.Yellow;
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
    public IDisposable Start(string? label = null)
        => RuntimeRenderingHostServices.Current.StartProfileScope(label) ?? DisposableAction.Empty;

    public IDisposable Start(string? label, ProfilerScopeKind scopeKind)
        => RuntimeRenderingHostServices.Current.StartProfileScope(label) ?? DisposableAction.Empty;
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
    public void InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming timing)
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
    private bool _disposed;

    public DisposableAction(Action? dispose)
    {
        _dispose = dispose;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _dispose?.Invoke();
    }
}
