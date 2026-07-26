using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.XeSS;

namespace XREngine
{
    public static partial class EngineRenderingSettingsApplication
    {
            private static string? _lastVulkanFeatureFingerprint;

            public static bool UsePipelineV2
                => Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UsePipelineV2) == "1";

            public static RenderPipeline NewRenderPipeline()
                => NewRenderPipeline(stereo: false);

            public static RenderPipeline NewRenderPipeline(bool stereo)
                => RuntimeEngine.Rendering.Settings.RvcPipelineMode != ERvcPipelineMode.Off
                    ? NewRvcRenderPipeline(stereo)
                    : (Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false) && !stereo
                    ? new DebugOpaqueRenderPipeline()
                    : UsePipelineV2
                        ? new DefaultRenderPipeline2(stereo)
                        : new DefaultRenderPipeline(stereo);

            public static void ApplyRenderPipelinePreference()
            {
                bool preferDebug = Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false;
                bool preferRvc = RuntimeEngine.Rendering.Settings.RvcPipelineMode != ERvcPipelineMode.Off;
                foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
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
                RvcRenderPipeline pipeline = new(stereo, RuntimeEngine.Rendering.Settings.RvcPipelineMode);
                ApplyRvcSettings(pipeline);
                return pipeline;
            }

            private static void ApplyRvcSettings(RvcRenderPipeline pipeline)
            {
                pipeline.ApplyRvcSettings(new RvcRenderingSettings(
                    RuntimeEngine.Rendering.Settings.RvcPipelineMode,
                    RuntimeEngine.Rendering.Settings.RvcQuadViewEnabled,
                    RuntimeEngine.Rendering.Settings.RvcStereoReuseEnabled,
                    RuntimeEngine.Rendering.Settings.RvcInsetWideReuseEnabled,
                    RuntimeEngine.Rendering.Settings.RvcTemporalReuseEnabled,
                    RuntimeEngine.Rendering.Settings.RvcPeripheralLightAggregationEnabled,
                    RuntimeEngine.Rendering.Settings.RvcDiagnosticOverlayEnabled,
                    RuntimeEngine.Rendering.Settings.RvcDebugViewMode,
                    RuntimeEngine.Rendering.Settings.RvcLightGridSpace));

                pipeline.RvcQualitySettings = new RvcQualitySettings(
                    RuntimeEngine.Rendering.Settings.RvcFovealRadiusDegrees,
                    RuntimeEngine.Rendering.Settings.RvcGuardBandDegrees,
                    RuntimeEngine.Rendering.Settings.RvcMidFieldRadiusDegrees,
                    RuntimeEngine.Rendering.Settings.RvcPeripheralMaxRate,
                    RuntimeEngine.Rendering.Settings.RvcForceFullResNearDistanceMeters,
                    RuntimeEngine.Rendering.Settings.RvcDerivativeStrategy,
                    RuntimeEngine.Rendering.Settings.RvcFovealAntiAliasingPath,
                    RuntimeEngine.Rendering.Settings.RvcReuseMaxNormalAngleDegrees,
                    RuntimeEngine.Rendering.Settings.RvcReuseMaxDepthDeltaMeters,
                    RuntimeEngine.Rendering.Settings.RvcReuseMaxRoughnessBucketDelta);
            }

            private static bool IsStereoPipeline(RenderPipeline pipeline)
                => pipeline is DefaultRenderPipeline { Stereo: true }
                    or DefaultRenderPipeline2 { Stereo: true };

            public static void ApplyGlobalIlluminationModePreference()
            {
                var mode = Engine.EffectiveSettings.GlobalIlluminationMode;
                foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
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
                RuntimeEngine.Rendering.NotifyAntiAliasingSettingChanged();
                RuntimeEngine.Rendering.InvalidateAllVulkanUpscaleBridges("anti-aliasing settings changed");
            }

            public static void ApplyGpuRenderDispatchPreference()
            {
                static void Apply()
                {
                    EMeshSubmissionStrategy strategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
                    bool useGpu = strategy != EMeshSubmissionStrategy.CpuDirect;
                    
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyRenderDispatchPreference(useGpu);

                    foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
                    {
                        RenderPipeline? pipeline = viewport.RenderPipeline;
                        if (pipeline is null)
                            continue;

                        if (pipeline is DebugOpaqueRenderPipeline debugPipeline)
                            debugPipeline.MeshSubmissionStrategy = strategy;
                        else
                            RuntimeEngine.Rendering.ApplyMeshSubmissionStrategyToPipeline(pipeline, strategy);
                    }
                }

                Engine.InvokeOnMainThread(Apply, "RuntimeEngine.Rendering.ApplyGpuRenderDispatchPreference", true);
                LogVulkanFeatureProfileFingerprint();
            }

            public static void ApplyCpuSceneCullingStructurePreference()
            {
                static void Apply()
                {
                    ECpuSceneCullingStructure structure = Engine.EffectiveSettings.CpuSceneCullingStructure;
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyCpuSceneCullingStructurePreference(structure);
                }

                Engine.InvokeOnMainThread(Apply, "RuntimeEngine.Rendering.ApplyCpuSceneCullingStructurePreference", true);
            }

            public static void LogVulkanFeatureProfileFingerprint(bool force = false)
            {
                if (!RuntimeEngine.Rendering.IsVulkanRendererActive())
                    return;

                var configuredProfile = Engine.EffectiveSettings.VulkanGpuDrivenProfile;
                var activeProfile = VulkanFeatureProfile.ActiveProfile;

                bool requestedGpuDispatch = Engine.EffectiveSettings.GPURenderDispatch;
                EMeshSubmissionStrategy meshSubmissionStrategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(requestedGpuDispatch);
                bool effectiveGpuDispatch = meshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect;

                bool effectiveGpuBvh = VulkanFeatureProfile.ResolveGpuBvhUsage(meshSubmissionStrategy);

                EOcclusionCullingMode requestedOcclusion = Engine.EffectiveSettings.GpuOcclusionCullingMode;
                EOcclusionCullingMode effectiveOcclusion = VulkanFeatureProfile.ResolveOcclusionCullingMode(requestedOcclusion);
                EGpuSortDomainPolicy sortPolicy = RuntimeEngine.Rendering.Settings.GpuSortDomainPolicy;
                EZeroReadbackMaterialDrawPath zeroReadbackDrawPath = Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath;
                EVulkanQueueOverlapMode requestedQueueOverlap = Engine.EffectiveSettings.VulkanQueueOverlapMode;
                EVulkanQueueOverlapMode effectiveQueueOverlap = VulkanFeatureProfile.ResolveQueueOverlapMode(requestedQueueOverlap);

                bool effectiveComputePasses = VulkanFeatureProfile.ResolveComputeDependentPassesPreference(true);
                bool effectiveImGui = VulkanFeatureProfile.ResolveImGuiPreference(true);
                AbstractRenderer? renderer = AbstractRenderer.Current
                    ?? RuntimeEngine.Windows.Select(static window => window.Renderer).FirstOrDefault(static renderer => renderer is not null);
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
                    bool enableDlss = Engine.EffectiveSettings.EnableNvidiaDlss;
                    bool enableFrameGeneration = Engine.EffectiveSettings.EnableNvidiaDlssFrameGeneration;
                    ENvidiaDlssFrameGenerationMode frameGenerationMode = Engine.EffectiveSettings.NvidiaDlssFrameGenerationMode;
                    bool frameGenerationRequested = enableFrameGeneration && frameGenerationMode != ENvidiaDlssFrameGenerationMode.Off;
                    bool frameGenerationAvailable = false;
                    string? frameGenerationUnavailableReason = null;
                    if (frameGenerationRequested)
                    {
                        frameGenerationAvailable = NvidiaDlssManager.FrameGenerationAvailable;
                        if (!frameGenerationAvailable)
                            frameGenerationUnavailableReason = NvidiaDlssManager.FrameGenerationUnavailableReason;
                    }

                    foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
                    {
                        if (!supported || !enableDlss)
                            NvidiaDlssManager.ResetViewport(viewport);
                        else
                            NvidiaDlssManager.ApplyToViewport(viewport, RuntimeEngine.Rendering.Settings);
                    }

                    XREngine.Debug.Rendering(
                        "[NvidiaDLSS] Preference changed. RuntimeDlls={0} Supported={1} EnableDLSS={2} Quality={3} CustomScale={4:F2} Sharpness={5:F2} FrameGenerationEnabled={6} FrameGenerationMode={7} FrameGenerationRequested={8} FrameGenerationAvailable={9} FrameGenerationUnavailableReason={10} LastError={11}",
                        NvidiaDlssManager.RequiredRuntimeDllsAvailable,
                        supported,
                        enableDlss,
                        Engine.EffectiveSettings.DlssQuality,
                        RuntimeEngine.Rendering.Settings.DlssCustomScale,
                        RuntimeEngine.Rendering.Settings.DlssSharpness,
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
                            frameGenerationUnavailableReason ?? NvidiaDlssManager.LastError ?? "unknown reason");
                    }

                    RuntimeEngine.Rendering.NotifyVulkanUpscaleBridgeVendorSelectionChanged("NVIDIA DLSS preference changed");
                    RefreshWindowsAfterVendorUpscalePreferenceChanged();
                }
                Engine.InvokeOnMainThread(Apply, "RuntimeEngine.Rendering.ApplyNvidiaDlssPreference", true);
            }

            public static void ApplyIntelXessPreference()
            {
                static void Apply()
                {
                    foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
                    {
                        if (!IntelXessManager.IsSupported || !Engine.EffectiveSettings.EnableIntelXess)
                            IntelXessManager.ResetViewport(viewport);
                        else
                            IntelXessManager.ApplyToViewport(viewport, RuntimeEngine.Rendering.Settings);
                    }

                    RuntimeEngine.Rendering.NotifyVulkanUpscaleBridgeVendorSelectionChanged("Intel XeSS preference changed");
                    RefreshWindowsAfterVendorUpscalePreferenceChanged();
                }
                Engine.InvokeOnMainThread(Apply, "RuntimeEngine.Rendering.ApplyIntelXessPreference", true);
            }

            private static void RefreshWindowsAfterVendorUpscalePreferenceChanged()
            {
                foreach (var window in RuntimeEngine.Windows)
                {
                    window.InvalidateScenePanelResources();
                    window.RequestRenderStateRecheck(resetCircuitBreaker: true);
                }
            }

            /// <summary>
            /// Pushes the effective parallel tick setting into the engine rendering settings.
            /// </summary>
            public static void ApplyTickGroupedItemsInParallelPreference()
                => RuntimeEngine.Rendering.Settings.TickGroupedItemsInParallel = Engine.EffectiveSettings.TickGroupedItemsInParallel;

            /// <summary>
            /// Pushes the effective shader pipeline setting into the engine rendering settings.
            /// </summary>
            public static void ApplyAllowShaderPipelinesPreference()
                => RuntimeEngine.Rendering.Settings.AllowShaderPipelines = Engine.EffectiveSettings.AllowShaderPipelines;

            /// <summary>
            /// Pushes the effective skeletal skinning setting into the engine rendering settings.
            /// </summary>
            public static void ApplyAllowSkinningPreference()
                => RuntimeEngine.Rendering.Settings.AllowSkinning = Engine.EffectiveSettings.AllowSkinning;

            /// <summary>
            /// Pushes the effective child matrix recalc loop type into the engine rendering settings.
            /// </summary>
            public static void ApplyRecalcChildMatricesLoopTypePreference()
                => RuntimeEngine.Rendering.Settings.RecalcChildMatricesLoopType = Engine.EffectiveSettings.RecalcChildMatricesLoopType;

            /// <summary>
            /// Pushes the effective compute-rendering settings into the engine rendering settings.
            /// </summary>
            public static void ApplyComputeRenderingPreference()
            {
                RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader = Engine.EffectiveSettings.CalculateSkinningInComputeShader;
                RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader = Engine.EffectiveSettings.CalculateBlendshapesInComputeShader;
                RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader = Engine.EffectiveSettings.CalculateSkinnedBoundsInComputeShader;
                RuntimeEngine.Rendering.Settings.SkinnedBoundsGpuDirectAabbWrite = Engine.EffectiveSettings.SkinnedBoundsGpuDirectAabbWrite;
                RuntimeEngine.Rendering.Settings.UseDetailPreservingComputeMipmaps = Engine.EffectiveSettings.UseDetailPreservingComputeMipmaps;
            }

    }
}
