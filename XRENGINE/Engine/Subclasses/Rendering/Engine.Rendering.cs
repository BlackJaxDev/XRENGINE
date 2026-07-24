using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Scene.Physics.Physx;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.XeSS;
using XREngine.Scene;
using XREngine.Scene.Physics.Jolt;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            private static string? _lastVulkanFeatureFingerprint;

            //TODO: create objects for only relevant windows that house the viewports that this object is visible in

            /// <summary>
            /// Called when a new render object is created.
            /// Tells all current windows to create an API-specific object for this object.
            /// </summary>
            /// <param name="obj"></param>
            /// <returns></returns>
            public static AbstractRenderAPIObject?[] CreateObjectsForAllWindows(GenericRenderObject obj)
            {
                lock (Windows)
                    return [.. Windows.Select(window => window.Renderer.GetOrCreateAPIRenderObject(obj))];
            }

            /// <summary>
            /// Called when a new window is created.
            /// Tells all current render objects to create an API-specific object for this window.
            /// </summary>
            /// <param name="renderer"></param>
            /// <returns></returns>
            public static ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> CreateObjectsForNewRenderer(AbstractRenderer renderer)
            {
                ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> roDic = [];
                lock (GenericRenderObject.RenderObjectCache)
                {
                    foreach (var pair in GenericRenderObject.RenderObjectCache)
                        foreach (var obj in pair.Value)
                        {
                            AbstractRenderAPIObject? apiRO = renderer.GetOrCreateAPIRenderObject(obj);
                            if (apiRO is null)
                                continue;

                            roDic.TryAdd(obj, apiRO);
                            obj.AddWrapper(apiRO);
                        }
                }
                
                return roDic;
            }

            public static void DestroyObjectsForRenderer(AbstractRenderer renderer)
            {
                lock (GenericRenderObject.RenderObjectCache)
                {
                    foreach (var pair in GenericRenderObject.RenderObjectCache)
                    {
                        foreach (var obj in pair.Value)
                        {
                            List<AbstractRenderAPIObject> wrappers =
                            [
                                .. obj.APIWrappers.Where(wrapper => ReferenceEquals(wrapper.Owner, renderer))
                            ];

                            foreach (AbstractRenderAPIObject apiRO in wrappers)
                            {
                                try
                                {
                                    apiRO.Destroy();
                                }
                                catch
                                {
                                }

                                try
                                {
                                    obj.RemoveWrapper(apiRO);
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                }
            }

            public static AbstractPhysicsScene NewPhysicsScene()
                => UserSettings.PhysicsLibrary switch
                {
                    EPhysicsLibrary.Jitter => new JitterScene(),
                    EPhysicsLibrary.Jolt => new JoltScene(),
                    _ => new PhysxScene(),
                };

            public static VisualScene3D NewVisualScene()
                => new();

            public static bool UsePipelineV2
                => Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UsePipelineV2) == "1";

            public static RenderPipeline NewRenderPipeline()
                => NewRenderPipeline(stereo: false);

            public static RenderPipeline NewRenderPipeline(bool stereo)
                => Settings.RvcPipelineMode != ERvcPipelineMode.Off
                    ? NewRvcRenderPipeline(stereo)
                    : (Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false) && !stereo
                    ? new DebugOpaqueRenderPipeline()
                    : UsePipelineV2
                        ? new DefaultRenderPipeline2(stereo)
                        : new DefaultRenderPipeline(stereo);

            public static void ApplyRenderPipelinePreference()
            {
                bool preferDebug = Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false;
                bool preferRvc = Settings.RvcPipelineMode != ERvcPipelineMode.Off;
                foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                {
                    RenderPipeline? pipeline = viewport.RenderPipeline;

                    if (pipeline is null)
                    {
                        viewport.RenderPipeline = NewRenderPipeline();
                        continue;
                    }

                    if (pipeline.OverrideProtected)
                        continue;

                    if (preferRvc)
                    {
                        if (pipeline is RvcRenderPipeline rvcPipeline)
                        {
                            ApplyRvcSettings(rvcPipeline);
                            continue;
                        }

                        viewport.RenderPipeline = NewRvcRenderPipeline(IsStereoPipeline(pipeline));
                        continue;
                    }

                    if (pipeline is RvcRenderPipeline previousRvcPipeline)
                    {
                        viewport.RenderPipeline = NewRenderPipeline(previousRvcPipeline.Stereo);
                        continue;
                    }

                    if (preferDebug)
                    {
                        if (pipeline is DefaultRenderPipeline { Stereo: false })
                            viewport.RenderPipeline = new DebugOpaqueRenderPipeline();
                        else if (pipeline is DefaultRenderPipeline2 { Stereo: false })
                            viewport.RenderPipeline = new DebugOpaqueRenderPipeline();
                    }
                    else if (pipeline is DebugOpaqueRenderPipeline)
                    {
                        viewport.RenderPipeline = NewRenderPipeline(stereo: false);
                    }
                }
            }

            private static RvcRenderPipeline NewRvcRenderPipeline(bool stereo)
            {
                RvcRenderPipeline pipeline = new(stereo, Settings.RvcPipelineMode);
                ApplyRvcSettings(pipeline);
                return pipeline;
            }

            private static void ApplyRvcSettings(RvcRenderPipeline pipeline)
            {
                pipeline.ApplyRvcSettings(new RvcRenderingSettings(
                    Settings.RvcPipelineMode,
                    Settings.RvcQuadViewEnabled,
                    Settings.RvcStereoReuseEnabled,
                    Settings.RvcInsetWideReuseEnabled,
                    Settings.RvcTemporalReuseEnabled,
                    Settings.RvcPeripheralLightAggregationEnabled,
                    Settings.RvcDiagnosticOverlayEnabled,
                    Settings.RvcDebugViewMode,
                    Settings.RvcLightGridSpace));

                pipeline.RvcQualitySettings = new RvcQualitySettings(
                    Settings.RvcFovealRadiusDegrees,
                    Settings.RvcGuardBandDegrees,
                    Settings.RvcMidFieldRadiusDegrees,
                    Settings.RvcPeripheralMaxRate,
                    Settings.RvcForceFullResNearDistanceMeters,
                    Settings.RvcDerivativeStrategy,
                    Settings.RvcFovealAntiAliasingPath,
                    Settings.RvcReuseMaxNormalAngleDegrees,
                    Settings.RvcReuseMaxDepthDeltaMeters,
                    Settings.RvcReuseMaxRoughnessBucketDelta);
            }

            public static void ApplyGlobalIlluminationModePreference()
            {
                var mode = Engine.EffectiveSettings.GlobalIlluminationMode;
                foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                {
                    if (viewport.RenderPipeline is DefaultRenderPipeline defaultPipeline)
                        defaultPipeline.GlobalIlluminationMode = mode;
                    else if (viewport.RenderPipeline is DefaultRenderPipeline2 v2Pipeline)
                        v2Pipeline.GlobalIlluminationMode = mode;
                }
            }

            /// <summary>
            /// Applies anti-aliasing overrides resolved through the settings cascade.
            /// Engine defaults remain untouched; consumers must read from
            /// <see cref="Engine.EffectiveSettings"/> when they need the resolved value.
            /// </summary>
            public static void ApplyAntiAliasingPreference()
            {
                AntiAliasingSettingsChanged?.Invoke();
                InvalidateAllVulkanUpscaleBridges("anti-aliasing settings changed");
            }

            public static void ApplyGpuRenderDispatchPreference()
            {
                static void Apply()
                {
                    EMeshSubmissionStrategy strategy = ResolveMeshSubmissionStrategy();
                    bool useGpu = strategy != EMeshSubmissionStrategy.CpuDirect;
                    
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyRenderDispatchPreference(useGpu);

                    foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                    {
                        RenderPipeline? pipeline = viewport.RenderPipeline;
                        if (pipeline is null)
                            continue;

                        if (pipeline is DebugOpaqueRenderPipeline debugPipeline)
                            debugPipeline.MeshSubmissionStrategy = strategy;
                        else
                            ApplyMeshSubmissionStrategyToPipeline(pipeline, strategy);
                    }
                }

                Engine.InvokeOnMainThread(Apply, "Engine.Rendering.ApplyGpuRenderDispatchPreference", true);
                LogVulkanFeatureProfileFingerprint();
            }

            public static bool IsVulkanRendererActive()
            {
                if (State.RenderingViewport?.Window?.Renderer?.BackendId == RendererBackendId.Vulkan)
                    return true;

                if (AbstractRenderer.Current?.BackendId == RendererBackendId.Vulkan)
                    return true;

                foreach (XRWindow window in Engine.Windows)
                    if (window.Renderer?.BackendId == RendererBackendId.Vulkan)
                        return true;

                return false;
            }

            public readonly record struct MeshSubmissionStrategyResolverInputs(
                bool RequestedGpuDispatch,
                EMeshSubmissionStrategy? ForcedStrategy,
                bool EnableGpuIndirectDebugLogging,
                bool EnableGpuIndirectValidationLogging,
                bool EnableGpuIndirectCpuFallback,
                bool EnableZeroReadbackMaterialScatter,
                bool EnableEditorZeroReadbackMaterialScatter,
                bool VulkanFeatureProfileActive,
                EVulkanGpuDrivenProfile ActiveVulkanProfile,
                bool EnforceStrictNoFallbacks,
                bool GpuRenderDispatchAllowed,
                bool SupportsIndirectCountDraw,
                EMeshShaderDialect MeshShaderDialect,
                bool SupportsDirectMeshTaskDispatch,
                bool SupportsIndirectCountMeshTaskDispatch,
                bool SupportsMeshletDispatch);

            /// <summary>
            /// Strategy the user requested via <c>ForceMeshSubmissionStrategy</c> when the
            /// resolver had to downgrade it (typically a meshlet strategy on a backend that
            /// can't dispatch mesh tasks). Null when no downgrade is active.
            /// </summary>
            public static EMeshSubmissionStrategy? LastMeshletDowngradeRequested { get; private set; }
            /// <summary>Strategy the resolver substituted for the requested meshlet strategy.</summary>
            public static EMeshSubmissionStrategy? LastMeshletDowngradeResolved { get; private set; }
            /// <summary>Human-readable reason for the last meshlet downgrade.</summary>
            public static string? LastMeshletDowngradeReason { get; private set; }
            /// <summary>Active render backend snapshotted by the last mesh-submission resolve.</summary>
            public static RuntimeGraphicsApiKind LastResolvedRendererBackend { get; private set; }
            /// <summary>Mesh-shader dialect (None / OpenGLKHR / OpenGLNV / Vulkan) the active renderer reported.</summary>
            public static EMeshShaderDialect LastResolvedMeshShaderDialect { get; private set; }
            /// <summary>True when the active renderer reported a production meshlet dispatch path.</summary>
            public static bool LastResolvedSupportsMeshletDispatch { get; private set; }

            public static EMeshSubmissionStrategy ResolveMeshSubmissionStrategy(bool? requestedGpuDispatch = null)
            {
                MeshSubmissionStrategyResolverInputs inputs = CreateMeshSubmissionStrategyResolverInputs(requestedGpuDispatch);
                EMeshSubmissionStrategy strategy = ResolveMeshSubmissionStrategy(inputs);
                SnapshotMeshSubmissionResolverState(inputs, strategy);
                WarnIfMeshSubmissionStrategyDowngraded(inputs, strategy);
                return strategy;
            }

            private static void SnapshotMeshSubmissionResolverState(
                MeshSubmissionStrategyResolverInputs inputs,
                EMeshSubmissionStrategy strategy)
            {
                LastResolvedRendererBackend = GetActiveRenderer() switch
                {
                    null => RuntimeGraphicsApiKind.Unknown,
                    var r when IsVulkanRendererActive() => RuntimeGraphicsApiKind.Vulkan,
                    _ => RuntimeGraphicsApiKind.OpenGL,
                };
                LastResolvedMeshShaderDialect = inputs.MeshShaderDialect;
                LastResolvedSupportsMeshletDispatch = inputs.SupportsMeshletDispatch;

                if (inputs.ForcedStrategy is { } forced &&
                    forced.IsAnyMeshletStrategy() &&
                    strategy != forced)
                {
                    LastMeshletDowngradeRequested = forced;
                    LastMeshletDowngradeResolved = strategy;
                    LastMeshletDowngradeReason = GetMeshletFallbackReason(inputs);
                }
                else
                {
                    LastMeshletDowngradeRequested = null;
                    LastMeshletDowngradeResolved = null;
                    LastMeshletDowngradeReason = null;
                }
            }

            public static EMeshSubmissionStrategy ResolveMeshSubmissionStrategy(MeshSubmissionStrategyResolverInputs inputs)
            {
                if (inputs.ForcedStrategy.HasValue)
                {
                    if (inputs.ForcedStrategy.Value.IsAnyMeshletStrategy())
                        return ResolveForcedMeshletSubmissionStrategy(inputs);

                    return inputs.ForcedStrategy.Value;
                }

                if (!inputs.RequestedGpuDispatch)
                    return EMeshSubmissionStrategy.CpuDirect;

                if (inputs.VulkanFeatureProfileActive && !inputs.GpuRenderDispatchAllowed)
                    return EMeshSubmissionStrategy.CpuDirect;

                bool diagnosticsProfile = inputs.VulkanFeatureProfileActive &&
                    inputs.ActiveVulkanProfile == EVulkanGpuDrivenProfile.Diagnostics;
                bool shippingFastProfile = inputs.VulkanFeatureProfileActive &&
                    inputs.ActiveVulkanProfile == EVulkanGpuDrivenProfile.ShippingFast;
                bool instrumentationRequested = diagnosticsProfile
                    || inputs.EnableGpuIndirectDebugLogging
                    || inputs.EnableGpuIndirectValidationLogging
                    || inputs.EnableGpuIndirectCpuFallback;
                bool zeroReadbackRequested = shippingFastProfile
                    || inputs.EnableZeroReadbackMaterialScatter
                    || inputs.EnableEditorZeroReadbackMaterialScatter;

                if (zeroReadbackRequested)
                {
                    if (inputs.SupportsIndirectCountDraw)
                        return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

                    return inputs.EnforceStrictNoFallbacks
                        ? EMeshSubmissionStrategy.CpuDirect
                        : EMeshSubmissionStrategy.GpuIndirectInstrumented;
                }

                if (instrumentationRequested)
                    return EMeshSubmissionStrategy.GpuIndirectInstrumented;

                return inputs.SupportsIndirectCountDraw
                    ? EMeshSubmissionStrategy.GpuIndirectInstrumented
                    : EMeshSubmissionStrategy.CpuDirect;
            }

            private static EMeshSubmissionStrategy ResolveForcedMeshletSubmissionStrategy(MeshSubmissionStrategyResolverInputs inputs)
            {
                if (inputs.SupportsMeshletDispatch)
                {
                    if (inputs.ForcedStrategy == EMeshSubmissionStrategy.GpuMeshletInstrumented &&
                        IsMeshletInstrumentationAllowed(inputs))
                    {
                        return EMeshSubmissionStrategy.GpuMeshletInstrumented;
                    }

                    return EMeshSubmissionStrategy.GpuMeshletZeroReadback;
                }

                if (inputs.SupportsIndirectCountDraw)
                    return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

                return inputs.EnforceStrictNoFallbacks
                    ? EMeshSubmissionStrategy.CpuDirect
                    : EMeshSubmissionStrategy.GpuIndirectInstrumented;
            }

            private static bool IsMeshletInstrumentationAllowed(MeshSubmissionStrategyResolverInputs inputs)
                => inputs.SupportsMeshletDispatch &&
                   ((inputs.VulkanFeatureProfileActive &&
                     inputs.ActiveVulkanProfile == EVulkanGpuDrivenProfile.Diagnostics) ||
                    inputs.EnableGpuIndirectDebugLogging);

            public static bool ResolveGpuRenderDispatchPreference(bool requestedGpuDispatch)
            {
                bool resolved = VulkanFeatureProfile.ResolveGpuRenderDispatchPreference(requestedGpuDispatch);
                if (!resolved && requestedGpuDispatch && IsVulkanRendererActive())
                {
                    XREngine.Debug.RenderingWarningEvery(
                        "RenderDispatch.VulkanProfileDisabled",
                        TimeSpan.FromSeconds(2),
                        "[RenderDispatch] GPU render dispatch disabled by Vulkan feature profile {0}.",
                        VulkanFeatureProfile.ActiveProfile);
                }

                return resolved;
            }

            private static MeshSubmissionStrategyResolverInputs CreateMeshSubmissionStrategyResolverInputs(bool? requestedGpuDispatch)
            {
                AbstractRenderer? renderer = GetActiveRenderer();
                bool rendererKnown = renderer is not null;
                bool supportsIndirectCount = renderer?.SupportsIndirectCountDraw() ?? true;
                EMeshShaderDialect meshShaderDialect = renderer?.MeshShaderDialect ?? EMeshShaderDialect.None;
                bool supportsDirectMeshTaskDispatch = renderer?.SupportsDirectMeshTaskDispatch() ?? false;
                bool supportsIndirectCountMeshTaskDispatch = renderer?.SupportsIndirectCountMeshTaskDispatch() ?? false;
                bool supportsMeshletDispatch = renderer?.SupportsMeshletDispatch() ?? false;

                return new MeshSubmissionStrategyResolverInputs(
                    RequestedGpuDispatch: requestedGpuDispatch ?? Engine.EffectiveSettings.GPURenderDispatch,
                    ForcedStrategy: Engine.EffectiveSettings.ForceMeshSubmissionStrategy,
                    EnableGpuIndirectDebugLogging: Engine.EffectiveSettings.EnableGpuIndirectDebugLogging,
                    EnableGpuIndirectValidationLogging: Engine.EffectiveSettings.EnableGpuIndirectValidationLogging,
                    EnableGpuIndirectCpuFallback: Engine.EffectiveSettings.EnableGpuIndirectCpuFallback,
                    EnableZeroReadbackMaterialScatter: Engine.EffectiveSettings.EnableZeroReadbackMaterialScatter,
                    EnableEditorZeroReadbackMaterialScatter: Engine.EditorPreferences?.Debug?.EnableZeroReadbackMaterialScatter == true,
                    VulkanFeatureProfileActive: VulkanFeatureProfile.IsActive,
                    ActiveVulkanProfile: VulkanFeatureProfile.ActiveProfile,
                    EnforceStrictNoFallbacks: VulkanFeatureProfile.EnforceStrictNoFallbacks,
                    GpuRenderDispatchAllowed: VulkanFeatureProfile.ResolveGpuRenderDispatchPreference(true),
                    SupportsIndirectCountDraw: rendererKnown ? supportsIndirectCount : true,
                    MeshShaderDialect: meshShaderDialect,
                    SupportsDirectMeshTaskDispatch: supportsDirectMeshTaskDispatch,
                    SupportsIndirectCountMeshTaskDispatch: supportsIndirectCountMeshTaskDispatch,
                    SupportsMeshletDispatch: supportsMeshletDispatch);
            }

            private static AbstractRenderer? GetActiveRenderer()
            {
                if (State.RenderingViewport?.Window?.Renderer is { } viewportRenderer)
                    return viewportRenderer;

                if (AbstractRenderer.Current is { } currentRenderer)
                    return currentRenderer;

                foreach (XRWindow window in Engine.Windows)
                    if (window.Renderer is { } renderer)
                        return renderer;

                return null;
            }

            private static void WarnIfMeshSubmissionStrategyDowngraded(
                MeshSubmissionStrategyResolverInputs inputs,
                EMeshSubmissionStrategy strategy)
            {
                if (!inputs.RequestedGpuDispatch)
                    return;

                if (inputs.ForcedStrategy is { } forcedStrategy &&
                    forcedStrategy.IsAnyMeshletStrategy() &&
                    strategy != forcedStrategy)
                {
                    string reason = GetMeshletFallbackReason(inputs);
                    XREngine.Debug.RenderingWarningEvery(
                        "RenderDispatch.MeshSubmissionStrategy.UnsupportedGpuMeshlet",
                        TimeSpan.FromSeconds(2),
                        "[RenderDispatch] Mesh submission strategy downgraded from {0} to {1}. Dialect={2}; DirectTaskDispatch={3}; IndirectCountTaskDispatch={4}; FallbackReason={5}.",
                        forcedStrategy,
                        strategy,
                        inputs.MeshShaderDialect,
                        inputs.SupportsDirectMeshTaskDispatch,
                        inputs.SupportsIndirectCountMeshTaskDispatch,
                        reason);
                    return;
                }

                if (inputs.ForcedStrategy.HasValue)
                    return;

                if (inputs.VulkanFeatureProfileActive && !inputs.GpuRenderDispatchAllowed)
                {
                    XREngine.Debug.RenderingWarningEvery(
                        "RenderDispatch.MeshSubmissionStrategy.VulkanGpuDispatchDisabled",
                        TimeSpan.FromSeconds(2),
                        "[RenderDispatch] GPU mesh submission requested but Vulkan feature profile {0} has GPU render dispatch disabled. Effective={1}.",
                        inputs.ActiveVulkanProfile,
                        strategy);
                    return;
                }

                bool wantedZeroReadback = (inputs.VulkanFeatureProfileActive &&
                        inputs.ActiveVulkanProfile == EVulkanGpuDrivenProfile.ShippingFast)
                    || inputs.EnableZeroReadbackMaterialScatter
                    || inputs.EnableEditorZeroReadbackMaterialScatter;

                if (wantedZeroReadback &&
                    !inputs.SupportsIndirectCountDraw &&
                    strategy != EMeshSubmissionStrategy.GpuIndirectZeroReadback)
                {
                    XREngine.Debug.RenderingWarningEvery(
                        "RenderDispatch.MeshSubmissionStrategy.UnsupportedZeroReadback",
                        TimeSpan.FromSeconds(2),
                        "[RenderDispatch] Mesh submission strategy downgraded from {0} because the active renderer does not support indirect-count draws. Effective={1}.",
                        EMeshSubmissionStrategy.GpuIndirectZeroReadback,
                        strategy);
                }

                if (strategy == EMeshSubmissionStrategy.CpuDirect && inputs.SupportsIndirectCountDraw)
                    return;

                if (strategy == EMeshSubmissionStrategy.CpuDirect && !inputs.SupportsIndirectCountDraw)
                {
                    XREngine.Debug.RenderingWarningEvery(
                        "RenderDispatch.MeshSubmissionStrategy.UnsupportedGpuDispatch",
                        TimeSpan.FromSeconds(2),
                        "[RenderDispatch] GPU mesh submission requested but the active renderer cannot satisfy the selected profile without CPU fallback. Effective={0}.",
                        strategy);
                }
            }

            private static string GetMeshletFallbackReason(MeshSubmissionStrategyResolverInputs inputs)
            {
                if (inputs.SupportsMeshletDispatch)
                {
                    if (inputs.ForcedStrategy == EMeshSubmissionStrategy.GpuMeshletInstrumented &&
                        !IsMeshletInstrumentationAllowed(inputs))
                    {
                        return "meshlet instrumentation requires the Diagnostics Vulkan profile or EnableGpuIndirectDebugLogging";
                    }

                    return "production meshlet dispatch is available";
                }

                if (inputs.MeshShaderDialect == EMeshShaderDialect.None)
                    return "no mesh shader dialect is available";

                if (inputs.SupportsDirectMeshTaskDispatch && !inputs.SupportsIndirectCountMeshTaskDispatch)
                    return "only diagnostic CPU-count mesh task dispatch is available";

                if (!inputs.SupportsIndirectCountMeshTaskDispatch)
                    return "production indirect-count mesh task dispatch is unavailable";

                return "production meshlet dispatch is unavailable";
            }

            public static void ApplyCpuSceneCullingStructurePreference()
            {
                static void Apply()
                {
                    ECpuSceneCullingStructure structure = Engine.EffectiveSettings.CpuSceneCullingStructure;
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyCpuSceneCullingStructurePreference(structure);
                }

                Engine.InvokeOnMainThread(Apply, "Engine.Rendering.ApplyCpuSceneCullingStructurePreference", true);
            }

            public static void LogVulkanFeatureProfileFingerprint(bool force = false)
            {
                if (!IsVulkanRendererActive())
                    return;

                var configuredProfile = Engine.EffectiveSettings.VulkanGpuDrivenProfile;
                var activeProfile = VulkanFeatureProfile.ActiveProfile;

                bool requestedGpuDispatch = Engine.EffectiveSettings.GPURenderDispatch;
                EMeshSubmissionStrategy meshSubmissionStrategy = ResolveMeshSubmissionStrategy(requestedGpuDispatch);
                bool effectiveGpuDispatch = meshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect;

                bool effectiveGpuBvh = VulkanFeatureProfile.ResolveGpuBvhUsage(meshSubmissionStrategy);

                EOcclusionCullingMode requestedOcclusion = Engine.EffectiveSettings.GpuOcclusionCullingMode;
                EOcclusionCullingMode effectiveOcclusion = VulkanFeatureProfile.ResolveOcclusionCullingMode(requestedOcclusion);
                EGpuSortDomainPolicy sortPolicy = Engine.Rendering.Settings.GpuSortDomainPolicy;
                EZeroReadbackMaterialDrawPath zeroReadbackDrawPath = Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath;
                EVulkanQueueOverlapMode requestedQueueOverlap = Engine.EffectiveSettings.VulkanQueueOverlapMode;
                EVulkanQueueOverlapMode effectiveQueueOverlap = VulkanFeatureProfile.ResolveQueueOverlapMode(requestedQueueOverlap);

                bool effectiveComputePasses = VulkanFeatureProfile.ResolveComputeDependentPassesPreference(true);
                bool effectiveImGui = VulkanFeatureProfile.ResolveImGuiPreference(true);
                AbstractRenderer? renderer = GetActiveRenderer();
                bool supportsIndirectCount = renderer?.SupportsIndirectCountDraw() == true;
                EMeshShaderDialect meshShaderDialect = renderer?.MeshShaderDialect ?? EMeshShaderDialect.None;
                bool supportsDirectMeshTaskDispatch = renderer?.SupportsDirectMeshTaskDispatch() == true;
                bool supportsIndirectCountMeshTaskDispatch = renderer?.SupportsIndirectCountMeshTaskDispatch() == true;
                bool supportsMeshletDispatch = renderer?.SupportsMeshletDispatch() == true;
                string meshletFallbackReason = supportsMeshletDispatch
                    ? "Ready"
                    : (renderer?.MeshletDispatchUnsupportedReason ?? "No active renderer");
                string dispatchPath = meshSubmissionStrategy.ToString();

                string fingerprint = string.Format(
                    "[VulkanProfile] Configured={0} Active={1} ComputePasses={2} GpuDispatch={3}(requested={4}) MeshStrategy={5} ForceMeshStrategy={6} ZeroReadbackDrawPath={7} GpuBvh={8}(strategy-driven) Occlusion={9}->{10} SortPolicy={11} QueueOverlap={12}(requested={13}) ImGui={14} DrawIndirectCountExt={15} MeshletDialect={16} MeshletDirectTaskDispatch={17} MeshletIndirectCountDispatch={18} MeshletDispatch={19} MeshletFallbackReason={20} DispatchPath={21}",
                    configuredProfile,
                    activeProfile,
                    effectiveComputePasses,
                    effectiveGpuDispatch,
                    requestedGpuDispatch,
                    meshSubmissionStrategy,
                    Engine.EffectiveSettings.ForceMeshSubmissionStrategy?.ToString() ?? "<auto>",
                    zeroReadbackDrawPath,
                    effectiveGpuBvh,
                    requestedOcclusion,
                    effectiveOcclusion,
                    sortPolicy,
                    effectiveQueueOverlap,
                    requestedQueueOverlap,
                    effectiveImGui,
                    supportsIndirectCount,
                    meshShaderDialect,
                    supportsDirectMeshTaskDispatch,
                    supportsIndirectCountMeshTaskDispatch,
                    supportsMeshletDispatch,
                    meshletFallbackReason,
                    dispatchPath);

                if (!force && string.Equals(_lastVulkanFeatureFingerprint, fingerprint, StringComparison.Ordinal))
                    return;

                _lastVulkanFeatureFingerprint = fingerprint;
                XREngine.Debug.Rendering(fingerprint);
            }

            public static void ApplyNvidiaDlssPreference()
            {
                static void Apply()
                {
                    bool supported = NvidiaDlssManager.IsSupported;
                    bool enableDlss = EffectiveSettings.EnableNvidiaDlss;
                    bool enableFrameGeneration = EffectiveSettings.EnableNvidiaDlssFrameGeneration;
                    ENvidiaDlssFrameGenerationMode frameGenerationMode = EffectiveSettings.NvidiaDlssFrameGenerationMode;
                    bool frameGenerationRequested = enableFrameGeneration && frameGenerationMode != ENvidiaDlssFrameGenerationMode.Off;
                    bool frameGenerationAvailable = false;
                    string? frameGenerationUnavailableReason = null;
                    if (frameGenerationRequested)
                        frameGenerationAvailable = NvidiaDlssManager.Native.IsFrameGenerationAvailable(out frameGenerationUnavailableReason);

                    foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                    {
                        if (!supported || !enableDlss)
                            NvidiaDlssManager.ResetViewport(viewport);
                        else
                            NvidiaDlssManager.ApplyToViewport(viewport, Settings);
                    }

                    XREngine.Debug.Rendering(
                        "[NvidiaDLSS] Preference changed. RuntimeDlls={0} Supported={1} EnableDLSS={2} Quality={3} CustomScale={4:F2} Sharpness={5:F2} FrameGenerationEnabled={6} FrameGenerationMode={7} FrameGenerationRequested={8} FrameGenerationAvailable={9} FrameGenerationUnavailableReason={10} LastError={11}",
                        NvidiaDlssManager.RequiredRuntimeDllsAvailable,
                        supported,
                        enableDlss,
                        EffectiveSettings.DlssQuality,
                        Settings.DlssCustomScale,
                        Settings.DlssSharpness,
                        enableFrameGeneration,
                        frameGenerationMode,
                        frameGenerationRequested,
                        frameGenerationAvailable,
                        frameGenerationUnavailableReason ?? "<none>",
                        NvidiaDlssManager.LastError ?? "<none>");

                    if (enableFrameGeneration && frameGenerationMode == ENvidiaDlssFrameGenerationMode.Off)
                    {
                        XREngine.Debug.RenderingWarningEvery(
                            "NvidiaDLSS.FrameGenerationModeOff",
                            TimeSpan.FromSeconds(5),
                            "[NvidiaDLSS] Frame generation is enabled, but NvidiaDlssFrameGenerationMode is Off. Select OneX, TwoX, or ThreeX to request DLSS-G.");
                    }
                    else if (frameGenerationRequested && !frameGenerationAvailable)
                    {
                        XREngine.Debug.RenderingWarningEvery(
                            "NvidiaDLSS.FrameGenerationUnavailable",
                            TimeSpan.FromSeconds(5),
                            "[NvidiaDLSS] Frame generation is requested, but unavailable: {0}",
                            frameGenerationUnavailableReason ?? NvidiaDlssManager.Native.LastError ?? "unknown reason");
                    }

                    NotifyVulkanUpscaleBridgeVendorSelectionChanged("NVIDIA DLSS preference changed");
                    RefreshWindowsAfterVendorUpscalePreferenceChanged();
                }
                Engine.InvokeOnMainThread(Apply, "Engine.Rendering.ApplyNvidiaDlssPreference", true);
            }

            public static void ApplyIntelXessPreference()
            {
                static void Apply()
                {
                    foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                    {
                        if (!IntelXessManager.IsSupported || !EffectiveSettings.EnableIntelXess)
                            IntelXessManager.ResetViewport(viewport);
                        else
                            IntelXessManager.ApplyToViewport(viewport, Settings);
                    }

                    NotifyVulkanUpscaleBridgeVendorSelectionChanged("Intel XeSS preference changed");
                    RefreshWindowsAfterVendorUpscalePreferenceChanged();
                }
                Engine.InvokeOnMainThread(Apply, "Engine.Rendering.ApplyIntelXessPreference", true);
            }

            private static void RefreshWindowsAfterVendorUpscalePreferenceChanged()
            {
                foreach (var window in Engine.Windows)
                {
                    window.InvalidateScenePanelResources();
                    window.RequestRenderStateRecheck(resetCircuitBreaker: true);
                }
            }

            /// <summary>
            /// Pushes the effective parallel tick setting into the engine rendering settings.
            /// </summary>
            public static void ApplyTickGroupedItemsInParallelPreference()
                => Settings.TickGroupedItemsInParallel = Engine.EffectiveSettings.TickGroupedItemsInParallel;

            /// <summary>
            /// Pushes the effective shader pipeline setting into the engine rendering settings.
            /// </summary>
            public static void ApplyAllowShaderPipelinesPreference()
                => Settings.AllowShaderPipelines = Engine.EffectiveSettings.AllowShaderPipelines;

            /// <summary>
            /// Pushes the effective skeletal skinning setting into the engine rendering settings.
            /// </summary>
            public static void ApplyAllowSkinningPreference()
                => Settings.AllowSkinning = Engine.EffectiveSettings.AllowSkinning;

            /// <summary>
            /// Pushes the effective child matrix recalc loop type into the engine rendering settings.
            /// </summary>
            public static void ApplyRecalcChildMatricesLoopTypePreference()
                => Settings.RecalcChildMatricesLoopType = Engine.EffectiveSettings.RecalcChildMatricesLoopType;

            /// <summary>
            /// Pushes the effective compute-rendering settings into the engine rendering settings.
            /// </summary>
            public static void ApplyComputeRenderingPreference()
            {
                Settings.CalculateSkinningInComputeShader = Engine.EffectiveSettings.CalculateSkinningInComputeShader;
                Settings.CalculateBlendshapesInComputeShader = Engine.EffectiveSettings.CalculateBlendshapesInComputeShader;
                Settings.CalculateSkinnedBoundsInComputeShader = Engine.EffectiveSettings.CalculateSkinnedBoundsInComputeShader;
                Settings.SkinnedBoundsGpuDirectAabbWrite = Engine.EffectiveSettings.SkinnedBoundsGpuDirectAabbWrite;
                Settings.UseDetailPreservingComputeMipmaps = Engine.EffectiveSettings.UseDetailPreservingComputeMipmaps;
            }

            internal static void ApplyGpuRenderDispatchToPipeline(RenderPipeline pipeline, bool useGpu)
                => ApplyMeshSubmissionStrategyToPipeline(
                    pipeline,
                    useGpu ? ResolveMeshSubmissionStrategy(true) : EMeshSubmissionStrategy.CpuDirect);

            internal static void ApplyMeshSubmissionStrategyToPipeline(RenderPipeline pipeline, EMeshSubmissionStrategy strategy)
            {
                bool useGpu = strategy != EMeshSubmissionStrategy.CpuDirect;
                foreach (ViewportRenderCommand command in EnumerateCommands(pipeline.CommandChain))
                {
                    switch (command)
                    {
                        case VPRC_RenderMeshesPass renderPass:
                            renderPass.MeshSubmissionStrategy = strategy;
                            break;
                        case VPRC_RenderCubemap cubemapPass:
                            cubemapPass.MeshSubmissionStrategy = strategy;
                            break;
                        case VPRC_RenderToCubemapFace cubemapFacePass:
                            cubemapFacePass.MeshSubmissionStrategy = strategy;
                            break;
                        case VPRC_RenderToTextureArray textureArrayPass:
                            textureArrayPass.MeshSubmissionStrategy = strategy;
                            break;
                        case VPRC_VoxelConeTracingPass voxelPass:
                            voxelPass.GpuDispatch = useGpu;
                            break;
                    }
                }
            }

            private static IEnumerable<ViewportRenderCommand> EnumerateCommands(ViewportRenderCommandContainer container)
            {
                foreach (ViewportRenderCommand command in container.Commands)
                {
                    yield return command;

                    switch (command)
                    {
                        case VPRC_IfElse ifElse:
                            if (ifElse.TrueCommands is not null)
                            {
                                foreach (var nested in EnumerateCommands(ifElse.TrueCommands))
                                    yield return nested;
                            }
                            if (ifElse.FalseCommands is not null)
                            {
                                foreach (var nested in EnumerateCommands(ifElse.FalseCommands))
                                    yield return nested;
                            }
                            break;
                        case VPRC_Switch switchCommand:
                            if (switchCommand.Cases is not null)
                            {
                                foreach (var caseContainer in switchCommand.Cases.Values)
                                {
                                    foreach (var nested in EnumerateCommands(caseContainer))
                                        yield return nested;
                                }
                            }
                            if (switchCommand.DefaultCase is not null)
                            {
                                foreach (var nested in EnumerateCommands(switchCommand.DefaultCase))
                                    yield return nested;
                            }
                            break;
                    }
                }
            }
        }
    }
}
