using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

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

    public static void EnqueueMainThreadTask(
        Action action,
        string? name = null,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        => RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
            action,
            name ?? "main-thread facade task",
            renderThreadKind);

    public static void EnqueueMainThreadTask(Action action, RenderThreadJobKind renderThreadKind)
        => EnqueueMainThreadTask(action, name: null, renderThreadKind);

    public static void EnqueueRenderThreadTask(
        Action action,
        string? name = null,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        => RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
            action,
            name ?? "render-thread facade task",
            renderThreadKind);

    public static void EnqueueRenderThreadTask(Action action, RenderThreadJobKind renderThreadKind)
        => EnqueueRenderThreadTask(action, name: null, renderThreadKind);

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

    public static void AddMainThreadCoroutine(
        Func<bool> step,
        string? name = null,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        => RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
            step,
            name ?? "main-thread facade coroutine",
            renderThreadKind);

    public static void AddMainThreadCoroutine(Func<bool> step, RenderThreadJobKind renderThreadKind)
        => AddMainThreadCoroutine(step, name: null, renderThreadKind);

    public static void AddRenderThreadCoroutine(
        Func<bool> step,
        string? name = null,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        => RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
            step,
            name ?? "render-thread facade coroutine",
            renderThreadKind);

    public static void AddRenderThreadCoroutine(Func<bool> step, RenderThreadJobKind renderThreadKind)
        => AddRenderThreadCoroutine(step, name: null, renderThreadKind);

    public static string GetStackTrace() => Environment.StackTrace;
    public static void LogWarning(string message, EOutputVerbosity verbosity = EOutputVerbosity.Normal, ELogCategory category = ELogCategory.General)
        => Debug.Out(message);

    public static RuntimeEngineState State { get; } = new();

    public static partial class Rendering
    {
        [ThreadStatic]
        private static Stack<XRRenderPipelineInstance>? t_pipelineStack;
        [ThreadStatic]
        private static Stack<XRRenderPipelineInstance?>? t_pipelineOverrideStack;

        private static Stack<XRRenderPipelineInstance> PipelineStack => t_pipelineStack ??= new();
        private static Stack<XRRenderPipelineInstance?> PipelineOverrideStack => t_pipelineOverrideStack ??= new();

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

        internal static void RebindSettingsChangedHandlers(
            IRuntimeRenderingHostServices previous,
            IRuntimeRenderingHostServices current)
        {
            RebindHandlers(SettingsChangedHandlers, previous.UnsubscribeRenderingSettingsChanged, current.SubscribeRenderingSettingsChanged);
            RebindHandlers(AntiAliasingSettingsChangedHandlers, previous.UnsubscribeAntiAliasingSettingsChanged, current.SubscribeAntiAliasingSettingsChanged);
        }

        private static void RebindHandlers(Action? handlers, Action<Action> unsubscribe, Action<Action> subscribe)
        {
            if (handlers is null)
                return;

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                if (handler is not Action action)
                    continue;

                unsubscribe(action);
                subscribe(action);
            }
        }

        public static string VulkanUpscaleBridgeEnvVar => XREngineEnvironmentVariables.EnableVulkanUpscaleBridge;
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

        public static ERenderClipDepthRange ResolveEffectiveClipDepthRange(RuntimeGraphicsApiKind backend)
            => Settings.ClipDepthRange;

        public static ERenderClipDepthRange EffectiveClipDepthRange
            => ResolveEffectiveClipDepthRange(RuntimeRenderingHostServices.Current.CurrentRenderBackend);

        public static bool ShouldUseNativeVulkanDepthClipControl
            => Settings.ClipDepthRange == ERenderClipDepthRange.NegativeOneToOne &&
               State.HasVulkanDepthClipControl;

        public static bool ShouldUseVulkanShaderClipDepthRemap
            => Settings.ClipDepthRange == ERenderClipDepthRange.NegativeOneToOne &&
               !State.HasVulkanDepthClipControl;

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

            if (VulkanFeatureProfile.IsActive && !VulkanFeatureProfile.ResolveGpuRenderDispatchPreference(true))
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

            public static void RecordShadowAtlasSolveDiagnostics(ShadowAtlasSolveDiagnostics diagnostics)
            {
                if (EnableTracking && HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderShadowAtlasSolveDiagnostics(diagnostics);
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

            public static void RecordVulkanResourceLifetimeGauges(int liveResourceCount, int trackedDescriptorSetCount)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanResourceLifetimeGauges(liveResourceCount, trackedDescriptorSetCount);
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

            public static void RecordVulkanRecordCommandBufferAllocation(long bytes)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanRecordCommandBufferAllocation(bytes);
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

            public static void RecordVulkanFrameLifecycleDetailTiming(
                TimeSpan sampleTimingQueries,
                TimeSpan drainRetiredResources,
                TimeSpan acquireBridgeSubmit,
                TimeSpan waitSwapchainImage,
                TimeSpan resetDynamicUniformRing)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanFrameLifecycleDetailTiming(
                        sampleTimingQueries,
                        drainRetiredResources,
                        acquireBridgeSubmit,
                        waitSwapchainImage,
                        resetDynamicUniformRing);
                }
            }

            public static void RecordVulkanFrameOpCensus(
                int totalCount,
                int clearCount,
                int meshDrawCount,
                int indirectDrawCount,
                int meshTaskDispatchCount,
                int blitCount,
                int computeCount,
                int swapchainWriteCount,
                int fboWriteCount,
                int uniquePassCount,
                int uniqueContextCount,
                int uniqueTargetCount)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanFrameOpCensus(
                        totalCount,
                        clearCount,
                        meshDrawCount,
                        indirectDrawCount,
                        meshTaskDispatchCount,
                        blitCount,
                        computeCount,
                        swapchainWriteCount,
                        fboWriteCount,
                        uniquePassCount,
                        uniqueContextCount,
                        uniqueTargetCount);
                }
            }

            public static void RecordVulkanCommandBufferCacheOutcome(
                bool reusedClean,
                bool recorded,
                bool forcedDirty,
                bool frameOpSignatureDirty,
                bool plannerDirty,
                bool profilerDirty,
                string? dirtyReason)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanCommandBufferCacheOutcome(
                        reusedClean,
                        recorded,
                        forcedDirty,
                        frameOpSignatureDirty,
                        plannerDirty,
                        profilerDirty,
                        dirtyReason);
                }
            }

            public static void RecordVulkanCommandBuffersDirty(string? reason)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanCommandBuffersDirty(reason);
            }

            public static void RecordVulkanExactResourceInvalidation(
                int exactVariantsDirtied,
                int exactCommandChainsDirtied,
                int unrelatedVariantsPreserved,
                int globalFallbackInvalidations)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanExactResourceInvalidation(
                        exactVariantsDirtied,
                        exactCommandChainsDirtied,
                        unrelatedVariantsPreserved,
                        globalFallbackInvalidations);
                }
            }

            public static void RecordVulkanTrackingBatch(
                int dependencyBinds,
                int uniqueDependencies,
                int imageAccessWrites,
                int compactImageRanges)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanTrackingBatch(
                        dependencyBinds,
                        uniqueDependencies,
                        imageAccessWrites,
                        compactImageRanges);
                }
            }

            public static void RecordVulkanDescriptorExpansion(int cacheHits, int cacheMisses)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanDescriptorExpansion(cacheHits, cacheMisses);
            }

            public static void RecordVulkanTrackingContention(int lifetimeLockContentions, int layoutLockContentions)
            {
                if (HasHostStats)
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanTrackingContention(lifetimeLockContentions, layoutLockContentions);
            }

            public static void RecordVulkanCommandChainMetrics(
                int chainsScheduled = 0,
                int chainsRecorded = 0,
                int chainsReused = 0,
                int chainsFrameDataRefreshed = 0,
                int volatileChainsRecorded = 0,
                int primaryCommandBuffersReused = 0,
                int primaryCommandBuffersRecorded = 0,
                int visibilityPackets = 0,
                int renderPackets = 0,
                int secondaryCommandBuffers = 0,
                TimeSpan chainWorkerRecordTime = default,
                TimeSpan renderThreadWaitForWorkersTime = default,
                string? firstStructuralDirtyReason = null,
                string? firstDescriptorGenerationMismatch = null,
                string? firstResourcePlanRevisionMismatch = null)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanCommandChainMetrics(
                        chainsScheduled,
                        chainsRecorded,
                        chainsReused,
                        chainsFrameDataRefreshed,
                        volatileChainsRecorded,
                        primaryCommandBuffersReused,
                        primaryCommandBuffersRecorded,
                        visibilityPackets,
                        renderPackets,
                        secondaryCommandBuffers,
                        chainWorkerRecordTime,
                        renderThreadWaitForWorkersTime,
                        firstStructuralDirtyReason,
                        firstDescriptorGenerationMismatch,
                        firstResourcePlanRevisionMismatch);
                }
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

            public static void RecordVulkanRetiredResourceDrain(
                int descriptorPools = 0,
                int descriptorSets = 0,
                int commandBuffers = 0,
                int queryPools = 0,
                int bufferViews = 0,
                int pipelines = 0,
                int framebuffers = 0,
                int buffers = 0,
                int bufferMemories = 0,
                int images = 0,
                int imageViews = 0,
                int samplers = 0,
                int imageMemories = 0,
                long imageBytes = 0)
            {
                if (HasHostStats)
                {
                    RuntimeRenderingHostServices.Current.RecordRenderVulkanRetiredResourceDrain(
                        descriptorPools,
                        descriptorSets,
                        commandBuffers,
                        queryPools,
                        bufferViews,
                        pipelines,
                        framebuffers,
                        buffers,
                        bufferMemories,
                        images,
                        imageViews,
                        samplers,
                        imageMemories,
                        imageBytes);
                }
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
                public static void RecordVulkanResourceLifetimeGauges(int liveResourceCount, int trackedDescriptorSetCount)
                    => Stats.RecordVulkanResourceLifetimeGauges(liveResourceCount, trackedDescriptorSetCount);
                public static void RecordVulkanDynamicUniformAllocation(long bytes) => Stats.RecordVulkanDynamicUniformAllocation(bytes);
                public static void RecordVulkanDynamicUniformExhaustion() => Stats.RecordVulkanDynamicUniformExhaustion();
                public static void RecordVulkanRecordCommandBufferAllocation(long bytes) => Stats.RecordVulkanRecordCommandBufferAllocation(bytes);
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
                public static void RecordVulkanFrameLifecycleDetailTiming(
                    TimeSpan sampleTimingQueries,
                    TimeSpan drainRetiredResources,
                    TimeSpan acquireBridgeSubmit,
                    TimeSpan waitSwapchainImage,
                    TimeSpan resetDynamicUniformRing)
                    => Stats.RecordVulkanFrameLifecycleDetailTiming(
                        sampleTimingQueries,
                        drainRetiredResources,
                        acquireBridgeSubmit,
                        waitSwapchainImage,
                        resetDynamicUniformRing);
                public static void RecordVulkanFrameOpCensus(
                    int totalCount,
                    int clearCount,
                    int meshDrawCount,
                    int indirectDrawCount,
                    int meshTaskDispatchCount,
                    int blitCount,
                    int computeCount,
                    int swapchainWriteCount,
                    int fboWriteCount,
                    int uniquePassCount,
                    int uniqueContextCount,
                    int uniqueTargetCount)
                    => Stats.RecordVulkanFrameOpCensus(
                        totalCount,
                        clearCount,
                        meshDrawCount,
                        indirectDrawCount,
                        meshTaskDispatchCount,
                        blitCount,
                        computeCount,
                        swapchainWriteCount,
                        fboWriteCount,
                        uniquePassCount,
                        uniqueContextCount,
                        uniqueTargetCount);
                public static void RecordVulkanCommandBufferCacheOutcome(
                    bool reusedClean,
                    bool recorded,
                    bool forcedDirty,
                    bool frameOpSignatureDirty,
                    bool plannerDirty,
                    bool profilerDirty,
                    string? dirtyReason)
                    => Stats.RecordVulkanCommandBufferCacheOutcome(
                        reusedClean,
                        recorded,
                        forcedDirty,
                        frameOpSignatureDirty,
                        plannerDirty,
                        profilerDirty,
                        dirtyReason);
                public static void RecordVulkanCommandBuffersDirty(string? reason)
                    => Stats.RecordVulkanCommandBuffersDirty(reason);
                public static void RecordVulkanExactResourceInvalidation(
                    int exactVariantsDirtied,
                    int exactCommandChainsDirtied,
                    int unrelatedVariantsPreserved,
                    int globalFallbackInvalidations)
                    => Stats.RecordVulkanExactResourceInvalidation(
                        exactVariantsDirtied,
                        exactCommandChainsDirtied,
                        unrelatedVariantsPreserved,
                        globalFallbackInvalidations);
                public static void RecordVulkanTrackingBatch(
                    int dependencyBinds,
                    int uniqueDependencies,
                    int imageAccessWrites,
                    int compactImageRanges)
                    => Stats.RecordVulkanTrackingBatch(
                        dependencyBinds,
                        uniqueDependencies,
                        imageAccessWrites,
                        compactImageRanges);
                public static void RecordVulkanDescriptorExpansion(int cacheHits, int cacheMisses)
                    => Stats.RecordVulkanDescriptorExpansion(cacheHits, cacheMisses);
                public static void RecordVulkanTrackingContention(int lifetimeLockContentions, int layoutLockContentions)
                    => Stats.RecordVulkanTrackingContention(lifetimeLockContentions, layoutLockContentions);
                public static void RecordVulkanCommandChainMetrics(
                    int chainsScheduled = 0,
                    int chainsRecorded = 0,
                    int chainsReused = 0,
                    int chainsFrameDataRefreshed = 0,
                    int volatileChainsRecorded = 0,
                    int primaryCommandBuffersReused = 0,
                    int primaryCommandBuffersRecorded = 0,
                    int visibilityPackets = 0,
                    int renderPackets = 0,
                    int secondaryCommandBuffers = 0,
                    TimeSpan chainWorkerRecordTime = default,
                    TimeSpan renderThreadWaitForWorkersTime = default,
                    string? firstStructuralDirtyReason = null,
                    string? firstDescriptorGenerationMismatch = null,
                    string? firstResourcePlanRevisionMismatch = null)
                    => Stats.RecordVulkanCommandChainMetrics(
                        chainsScheduled,
                        chainsRecorded,
                        chainsReused,
                        chainsFrameDataRefreshed,
                        volatileChainsRecorded,
                        primaryCommandBuffersReused,
                        primaryCommandBuffersRecorded,
                        visibilityPackets,
                        renderPackets,
                        secondaryCommandBuffers,
                        chainWorkerRecordTime,
                        renderThreadWaitForWorkersTime,
                        firstStructuralDirtyReason,
                        firstDescriptorGenerationMismatch,
                        firstResourcePlanRevisionMismatch);
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
                public static void RecordVulkanRetiredResourceDrain(
                    int descriptorPools = 0,
                    int descriptorSets = 0,
                    int commandBuffers = 0,
                    int queryPools = 0,
                    int bufferViews = 0,
                    int pipelines = 0,
                    int framebuffers = 0,
                    int buffers = 0,
                    int bufferMemories = 0,
                    int images = 0,
                    int imageViews = 0,
                    int samplers = 0,
                    int imageMemories = 0,
                    long imageBytes = 0)
                    => Stats.RecordVulkanRetiredResourceDrain(
                        descriptorPools,
                        descriptorSets,
                        commandBuffers,
                        queryPools,
                        bufferViews,
                        pipelines,
                        framebuffers,
                        buffers,
                        bufferMemories,
                        images,
                        imageViews,
                        samplers,
                        imageMemories,
                        imageBytes);
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
            public static bool ReverseWinding
            {
                get => StateData.ReverseWinding;
                internal set => StateData.ReverseWinding = value;
            }
            public static bool ReverseCulling
            {
                get => StateData.ReverseCulling;
                internal set => StateData.ReverseCulling = value;
            }
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
            public static bool VulkanValidationLayersEnabled { get; internal set; }
            public static bool HasNvRayTracing { get; internal set; }
            public static bool HasVulkanRayTracing { get; internal set; }
            public static bool HasVulkanMemoryDecompression { get; internal set; }
            public static bool HasVulkanCopyMemoryIndirect { get; internal set; }
            public static bool HasVulkanRtxIo { get; internal set; }
            public static bool HasVulkanDepthClipControl { get; internal set; }
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
                => Rendering.PushRenderingPipelineOverride(pipeline);
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
            [ThreadStatic]
            private static Stack<int>? t_renderGraphPasses;
            [ThreadStatic]
            private static Stack<XRCamera?>? t_cameraOverrides;
            [ThreadStatic]
            private static Stack<uint>? t_transformIds;
            [ThreadStatic]
            private static Stack<int>? t_mirrorPasses;
            [ThreadStatic]
            private static Stack<MirrorPassState>? t_mirrorPassStates;
            [ThreadStatic]
            private static bool t_isSceneCapturePass;
            [ThreadStatic]
            private static bool t_isLightProbePass;
            [ThreadStatic]
            private static bool t_reverseWinding;
            [ThreadStatic]
            private static bool t_reverseCulling;

            private static Stack<int> RenderGraphPasses => t_renderGraphPasses ??= new();
            private static Stack<XRCamera?> CameraOverrides => t_cameraOverrides ??= new();
            private static Stack<uint> TransformIds => t_transformIds ??= new();
            private static Stack<int> MirrorPasses => t_mirrorPasses ??= new();
            private static Stack<MirrorPassState> MirrorPassStates => t_mirrorPassStates ??= new();

            private bool _isNvidia;
            private bool _isIntel;
            private bool _isVulkan;
            private readonly record struct MirrorPassState(bool IsSceneCapturePass, bool ReverseCulling);

            public ulong RenderFrameId => RuntimeRenderingHostServices.Current.CurrentRenderFrameId;
            public IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState => RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState;
            public XRRenderPipelineInstance? CurrentRenderingPipeline
            {
                get
                {
                    if (t_pipelineOverrideStack is { Count: > 0 } overrideStack)
                        return overrideStack.Peek();

                    if (t_pipelineStack is { Count: > 0 } pipelineStack)
                        return pipelineStack.Peek();

                    return RuntimeRenderingHostServices.Current.IsRenderThread
                        ? RuntimeRenderingHostServices.Current.CurrentRenderPipelineContext as XRRenderPipelineInstance
                        : null;
                }
            }
            public XRRenderPipelineInstance.RenderingState? RenderingPipelineState => CurrentRenderingPipeline?.RenderState;
            public XRViewport? RenderingViewport => CurrentRenderingPipeline?.RenderState.WindowViewport ?? CurrentRenderingPipeline?.LastWindowViewport;
            public IRuntimeRenderWorld? RenderingWorld => RenderingViewport?.World;
            public XRCamera? RenderingCamera
            {
                get
                {
                    XRRenderPipelineInstance? pipeline = CurrentRenderingPipeline;
                    return RenderingCameraOverride
                        ?? pipeline?.RenderState.RenderingCamera
                        ?? pipeline?.RenderState.SceneCamera
                        ?? pipeline?.LastSceneCamera
                        ?? pipeline?.LastRenderingCamera;
                }
            }
            public XRCamera? RenderingCameraOverride
            {
                get => t_cameraOverrides is { Count: > 0 } stack ? stack.Peek() : null;
                set
                {
                    Stack<XRCamera?> stack = CameraOverrides;
                    if (value is null)
                    {
                        if (stack.Count != 0)
                            stack.Pop();
                    }
                    else
                    {
                        stack.Push(value);
                    }
                }
            }
            public BoundingRectangle RenderArea => RenderingPipelineState?.CurrentRenderRegion ?? BoundingRectangle.Empty;
            public float DefaultDepthClearValue => 1.0f;
            public bool IsShadowPass => CurrentRenderingPipeline?.RenderState.ShadowPass ?? RuntimeRenderingHostServices.Current.IsShadowPass;
            public bool IsSceneCapturePass
            {
                get => t_isSceneCapturePass;
                set => t_isSceneCapturePass = value;
            }
            public bool IsLightProbePass
            {
                get => t_isLightProbePass;
                set => t_isLightProbePass = value;
            }
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
            public bool IsReflectedMirrorPass => (MirrorPassIndex & 1) == 1;
            public int MirrorPassIndex => t_mirrorPasses is { Count: > 0 } stack ? stack.Peek() : 0;
            public int CurrentRenderGraphPassIndex => t_renderGraphPasses is { Count: > 0 } stack ? stack.Peek() : int.MinValue;
            public uint CurrentTransformId => t_transformIds is { Count: > 0 } stack ? stack.Peek() : 0u;
            public bool ReverseWinding
            {
                get => t_reverseWinding;
                internal set => t_reverseWinding = value;
            }
            public bool ReverseCulling
            {
                get => t_reverseCulling;
                internal set => t_reverseCulling = value;
            }

            public void BeginRenderFrame()
            {
                // RenderFrameId is now sourced from the host bridge; no-op retained for legacy callers.
            }

            public IDisposable PushRenderGraphPassIndex(int passIndex)
            {
                Stack<int> stack = RenderGraphPasses;
                stack.Push(passIndex);
                return new DisposableAction(() => stack.Pop());
            }

            public IDisposable PushTransformId(uint transformId)
            {
                Stack<uint> stack = TransformIds;
                stack.Push(transformId);
                return new DisposableAction(() => stack.Pop());
            }

            public IDisposable PushMirrorPass(int mirrorPassIndex)
            {
                Stack<int> stack = MirrorPasses;
                PushMirrorPassState();
                stack.Push(mirrorPassIndex);
                ApplyActiveMirrorPassState();
                return new DisposableAction(() =>
                {
                    if (stack.Count != 0)
                        stack.Pop();
                    RestoreMirrorPassState();
                });
            }

            public void PushMirrorPass()
            {
                PushMirrorPassState();
                MirrorPasses.Push(MirrorPassIndex + 1);
                ApplyActiveMirrorPassState();
            }

            public void PopMirrorPass()
            {
                if (t_mirrorPasses is { Count: > 0 } stack)
                    stack.Pop();
                RestoreMirrorPassState();
            }

            private void PushMirrorPassState()
                => MirrorPassStates.Push(new(IsSceneCapturePass, ReverseCulling));

            private void ApplyActiveMirrorPassState()
            {
                IsSceneCapturePass = true;
                ReverseCulling = IsReflectedMirrorPass;
            }

            private void RestoreMirrorPassState()
            {
                MirrorPassState previous = t_mirrorPassStates is { Count: > 0 } stack
                    ? stack.Pop()
                    : default;

                if (IsMirrorPass)
                {
                    ApplyActiveMirrorPassState();
                    return;
                }

                IsSceneCapturePass = previous.IsSceneCapturePass;
                ReverseCulling = previous.ReverseCulling;
            }
        }
    }
}
