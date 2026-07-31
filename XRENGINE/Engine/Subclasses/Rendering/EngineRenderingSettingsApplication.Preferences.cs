using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine
{
    public static partial class EngineRenderingSettingsApplication
    {
            private static string? _lastVulkanFeatureFingerprint;
            private static readonly object AdvancedPipelineSelectionLock = new();
            private static AdvancedRenderPipelineSelectionResult _lastAdvancedPipelineSelection =
                AdvancedRenderPipelineSelectionResolver.Resolve(
                    EAdvancedRenderPipelineMode.Available,
                    AdvancedRenderPipelineCapabilities.NoRenderer,
                    stereo: false);

            public static EAdvancedRenderPipelineMode AdvancedRenderPipelineMode
                => ResolveAdvancedRenderPipelineMode();

            public static AdvancedRenderPipelineSelectionResult LastAdvancedRenderPipelineSelection
            {
                get
                {
                    lock (AdvancedPipelineSelectionLock)
                        return _lastAdvancedPipelineSelection;
                }
            }

            public static RenderPipeline NewRenderPipeline()
                => NewRenderPipeline(RenderPipelineRequest.DesktopScene());

            public static RenderPipeline NewRenderPipeline(bool stereo)
                => NewRenderPipeline(RenderPipelineRequest.DesktopScene(stereo));

            public static RenderPipeline NewRenderPipeline(RenderPipelineRequest request)
            {
                AdvancedRenderPipelineCapabilities capabilities =
                    RuntimeRenderingHostServices.FrameTiming.CurrentRenderer?
                        .GetAdvancedRenderPipelineCapabilities()
                    ?? AdvancedRenderPipelineCapabilities.NoRenderer;

                return NewRenderPipeline(
                    request,
                    AdvancedRenderPipelineMode,
                    capabilities,
                    RuntimeEngine.Rendering.Settings.RvcPipelineMode,
                    Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false);
            }

            internal static RenderPipeline NewRenderPipeline(
                RenderPipelineRequest request,
                EAdvancedRenderPipelineMode advancedMode,
                in AdvancedRenderPipelineCapabilities capabilities,
                ERvcPipelineMode rvcMode,
                bool useDebugOpaquePipeline)
            {
                return request.Purpose switch
                {
                    ERenderPipelinePurpose.OpenXrEye =>
                        NewRvcRenderPipeline(request.Stereo, rvcMode),
                    ERenderPipelinePurpose.DesktopScene
                        when useDebugOpaquePipeline && !request.Stereo =>
                        new DebugOpaqueRenderPipeline(),
                    ERenderPipelinePurpose.DesktopScene or
                    ERenderPipelinePurpose.OffscreenCapture =>
                        NewStandardRenderPipeline(
                            request.Stereo,
                            advancedMode,
                            capabilities),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(request),
                        request,
                        "Unknown render pipeline purpose."),
                };
            }

            internal static RenderPipeline NewStandardRenderPipeline(
                bool stereo,
                EAdvancedRenderPipelineMode mode,
                in AdvancedRenderPipelineCapabilities capabilities)
            {
                AdvancedRenderPipelineSelectionResult selection =
                    ResolveAdvancedRenderPipelineSelection(stereo, mode, capabilities);

                return selection.EffectiveKind switch
                {
                    ERenderPipelineKind.Advanced =>
                        new AdvancedRenderPipeline(stereo, selection.CapabilityResult),
                    ERenderPipelineKind.LegacyDefault =>
                        new DefaultRenderPipeline(stereo),
                    _ => throw new AdvancedRenderPipelineNotSupportedException(selection),
                };
            }

            public static void ApplyRenderPipelinePreference()
            {
                bool preferDebug = Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false;
                foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports(
                             RuntimeEngine.EViewportEnumerationMode.IncludeVrEyeViewports))
                {
                    RenderPipelineRequest request = viewport.PipelineRequest;
                    RenderPipeline? pipeline = viewport.RenderPipeline;

                    if (pipeline is null)
                    {
                        viewport.RenderPipeline = NewRenderPipeline(request);
                        continue;
                    }

                    if (pipeline.OverrideProtected)
                        continue;

                    if (request.Purpose == ERenderPipelinePurpose.OpenXrEye)
                    {
                        if (pipeline is RvcRenderPipeline rvcPipeline &&
                            rvcPipeline.Stereo == request.Stereo)
                        {
                            ApplyRvcSettings(rvcPipeline);
                            continue;
                        }

                        viewport.RenderPipeline = NewRenderPipeline(request);
                        continue;
                    }

                    if (pipeline is RvcRenderPipeline)
                    {
                        viewport.RenderPipeline = NewRenderPipeline(request);
                        continue;
                    }

                    bool debugAllowed =
                        request.Purpose == ERenderPipelinePurpose.DesktopScene &&
                        !request.Stereo;
                    if (preferDebug && debugAllowed)
                    {
                        if (pipeline is DefaultRenderPipeline or AdvancedRenderPipeline)
                        {
                            viewport.RenderPipeline = new DebugOpaqueRenderPipeline();
                            continue;
                        }
                    }
                    else if (pipeline is DebugOpaqueRenderPipeline)
                    {
                        viewport.RenderPipeline = NewRenderPipeline(request);
                        continue;
                    }

                    if (pipeline is not DefaultRenderPipeline and not AdvancedRenderPipeline)
                        continue;

                    AdvancedRenderPipelineCapabilities capabilities =
                        RuntimeRenderingHostServices.FrameTiming.CurrentRenderer?
                            .GetAdvancedRenderPipelineCapabilities()
                        ?? AdvancedRenderPipelineCapabilities.NoRenderer;
                    AdvancedRenderPipelineSelectionResult selection =
                        ResolveAdvancedRenderPipelineSelection(
                            request.Stereo,
                            AdvancedRenderPipelineMode,
                            capabilities);

                    if (selection.SelectsAdvanced)
                    {
                        if (pipeline is AdvancedRenderPipeline advancedPipeline)
                            advancedPipeline.ApplyCapabilityResult(selection.CapabilityResult);
                        else
                            viewport.RenderPipeline =
                                new AdvancedRenderPipeline(
                                    request.Stereo,
                                    selection.CapabilityResult);
                    }
                    else if (pipeline is AdvancedRenderPipeline)
                    {
                        viewport.RenderPipeline =
                            new DefaultRenderPipeline(request.Stereo);
                    }
                }
            }

            private static EAdvancedRenderPipelineMode ResolveAdvancedRenderPipelineMode()
            {
                string? raw = EffectiveSettingsEnvOverrides.AdvancedRenderPipelineMode;
                if (string.IsNullOrWhiteSpace(raw))
                    return RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode;

                if (Enum.TryParse(
                        raw,
                        ignoreCase: true,
                        out EAdvancedRenderPipelineMode parsed) &&
                    Enum.IsDefined(typeof(EAdvancedRenderPipelineMode), parsed))
                {
                    return parsed;
                }

                Debug.RenderingWarningEvery(
                    "AdvancedPipeline.InvalidSelectionMode",
                    TimeSpan.FromSeconds(30),
                    "[AdvancedPipeline] Ignoring invalid {0} value '{1}'. Expected Disabled, Available, Required, or Diagnostic.",
                    XREngineEnvironmentVariables.AdvancedRenderPipelineMode,
                    raw);
                return RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode;
            }

            private static AdvancedRenderPipelineSelectionResult ResolveAdvancedRenderPipelineSelection(
                bool stereo,
                EAdvancedRenderPipelineMode mode,
                in AdvancedRenderPipelineCapabilities capabilities)
            {
                AdvancedRenderPipelineSelectionResult selection =
                    AdvancedRenderPipelineSelectionResolver.Resolve(mode, capabilities, stereo);

                lock (AdvancedPipelineSelectionLock)
                    _lastAdvancedPipelineSelection = selection;

                RuntimeEngine.Rendering.Stats.RendererState.UpdateAdvancedPipelineContext(selection);

                if (selection.RequiresFailure)
                    throw new AdvancedRenderPipelineNotSupportedException(selection);

                if (mode == EAdvancedRenderPipelineMode.Diagnostic)
                {
                    Debug.Rendering("[AdvancedPipeline] {0}", selection.Diagnostic);
                }
                else if (mode == EAdvancedRenderPipelineMode.Available &&
                         !selection.SelectsAdvanced)
                {
                    Debug.RenderingWarningEvery(
                        $"AdvancedPipeline.AvailableFallback.{selection.CapabilityResult.RejectionReason}",
                        TimeSpan.FromSeconds(10),
                        "[AdvancedPipeline] {0}",
                        selection.Diagnostic);
                }

                return selection;
            }

            private static RvcRenderPipeline NewRvcRenderPipeline(
                bool stereo,
                ERvcPipelineMode mode)
            {
                RvcRenderPipeline pipeline = new(stereo, mode);
                ApplyRvcSettings(pipeline, mode);
                return pipeline;
            }

            private static void ApplyRvcSettings(RvcRenderPipeline pipeline)
                => ApplyRvcSettings(
                    pipeline,
                    RuntimeEngine.Rendering.Settings.RvcPipelineMode);

            private static void ApplyRvcSettings(
                RvcRenderPipeline pipeline,
                ERvcPipelineMode mode)
                => pipeline.ApplyRuntimeSettings(
                    RuntimeEngine.Rendering.Settings,
                    mode);

            public static void ApplyGlobalIlluminationModePreference()
            {
                var mode = Engine.EffectiveSettings.GlobalIlluminationMode;
                foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports(
                             RuntimeEngine.EViewportEnumerationMode.IncludeVrEyeViewports))
                {
                    if (viewport.RenderPipeline is IGlobalIlluminationPipelineProvider pipeline)
                        pipeline.GlobalIlluminationMode = mode;
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
                    bool supported = VendorUpscaleRuntime.IsDlssSupported;
                    bool enableDlss = Engine.EffectiveSettings.EnableNvidiaDlss;
                    bool enableFrameGeneration = Engine.EffectiveSettings.EnableNvidiaDlssFrameGeneration;
                    ENvidiaDlssFrameGenerationMode frameGenerationMode = Engine.EffectiveSettings.NvidiaDlssFrameGenerationMode;
                    bool frameGenerationRequested = enableFrameGeneration && frameGenerationMode != ENvidiaDlssFrameGenerationMode.Off;
                    bool frameGenerationAvailable = false;
                    string? frameGenerationUnavailableReason = null;
                    if (frameGenerationRequested)
                    {
                        frameGenerationAvailable = VendorUpscaleRuntime.IsDlssFrameGenerationSupported;
                        if (!frameGenerationAvailable)
                            frameGenerationUnavailableReason = VendorUpscaleRuntime.DlssFrameGenerationUnavailableReason;
                    }

                    foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
                    {
                        if (!supported || !enableDlss)
                            VendorUpscaleRuntime.ResetDlssViewport(viewport);
                        else
                            VendorUpscaleRuntime.ApplyDlssToViewport(viewport, RuntimeEngine.Rendering.Settings);
                    }

                    XREngine.Debug.Rendering(
                        "[NvidiaDLSS] Preference changed. RuntimeDlls={0} Supported={1} EnableDLSS={2} Quality={3} CustomScale={4:F2} Sharpness={5:F2} FrameGenerationEnabled={6} FrameGenerationMode={7} FrameGenerationRequested={8} FrameGenerationAvailable={9} FrameGenerationUnavailableReason={10} LastError={11}",
                        VendorUpscaleRuntime.AreDlssRuntimeLibrariesAvailable,
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
                        VendorUpscaleRuntime.DlssLastError ?? "<none>");

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
                            frameGenerationUnavailableReason ?? VendorUpscaleRuntime.DlssLastError ?? "unknown reason");
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
                        if (!VendorUpscaleRuntime.IsXessSupported || !Engine.EffectiveSettings.EnableIntelXess)
                            VendorUpscaleRuntime.ResetXessViewport(viewport);
                        else
                            VendorUpscaleRuntime.ApplyXessToViewport(viewport, RuntimeEngine.Rendering.Settings);
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
