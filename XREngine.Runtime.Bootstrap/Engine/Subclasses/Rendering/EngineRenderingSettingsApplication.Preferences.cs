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
                            request,
                            advancedMode,
                            capabilities),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(request),
                        request,
                        "Unknown render pipeline purpose."),
                };
            }

            internal static RenderPipeline NewStandardRenderPipeline(
                RenderPipelineRequest request,
                EAdvancedRenderPipelineMode mode,
                in AdvancedRenderPipelineCapabilities capabilities)
            {
                // Desktop cameras are configured before a physical output exists.
                // Make Advanced the source asset for the normal Available/Required
                // policy and bind its output-local capability reservation later.
                // Disabled/Diagnostic and independently owned offscreen outputs
                // retain their explicit selection semantics.
                if (request.Purpose == ERenderPipelinePurpose.DesktopScene &&
                    request.OutputId == 0 &&
                    (mode is EAdvancedRenderPipelineMode.Available or
                        EAdvancedRenderPipelineMode.Required))
                {
                    return new AdvancedRenderPipeline(request.Stereo);
                }

                AdvancedRenderPipelineSelectionResult selection =
                    ResolveAdvancedRenderPipelineSelection(
                        request,
                        mode,
                        capabilities,
                        reservationRenderer: null,
                        retainConfiguredSource: false,
                        out _,
                        out _);

                return selection.EffectiveKind switch
                {
                    ERenderPipelineKind.Advanced =>
                        new AdvancedRenderPipeline(request.Stereo, selection.CapabilityResult),
                    ERenderPipelineKind.LegacyDefault =>
                        new DefaultRenderPipeline(request.Stereo),
                    _ => throw new AdvancedRenderPipelineNotSupportedException(selection),
                };
            }

            public static void ApplyRenderPipelinePreference()
            {
                foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports(
                             RuntimeEngine.EViewportEnumerationMode.IncludeVrEyeViewports))
                    ApplyRenderPipelineOutputBinding(viewport);
            }

            private static void ApplyRenderPipelineOutputBinding(XRViewport viewport)
            {
                if (viewport.IsDestroyed)
                {
                    viewport.RenderPipelineInstance.ClearAdvancedOutputBinding();
                    return;
                }

                RenderPipelineRequest request = viewport.PipelineRequest;
                RenderPipeline? pipeline = viewport.RenderPipeline;
                if (pipeline is null)
                {
                    viewport.RenderPipelineInstance.ClearAdvancedOutputBinding();
                    return;
                }

                // OpenXR eye pipelines are explicit output-owned exceptions to
                // camera source authority. Their lifecycle owns distinct RVC
                // command chains and only copies compatible visual features.
                if (request.Purpose == ERenderPipelinePurpose.OpenXrEye)
                {
                    viewport.RenderPipelineInstance.ClearAdvancedOutputBinding();
                    if (pipeline is RvcRenderPipeline rvcPipeline &&
                        rvcPipeline.Stereo == request.Stereo)
                    {
                        ApplyRvcSettings(rvcPipeline);
                    }
                    else if (!pipeline.OverrideProtected &&
                             !viewport.SetRenderPipelineFromCamera)
                    {
                        viewport.RenderPipeline = NewRenderPipeline(request);
                    }
                    return;
                }

                // A configured source is authoritative. Default, debug, capture,
                // and custom pipelines are never promoted or replaced here.
                if (pipeline is not AdvancedRenderPipeline)
                {
                    viewport.RenderPipelineInstance.ClearAdvancedOutputBinding();
                    return;
                }

                IRuntimeRendererHost? renderer = viewport.Window?.Renderer
                    ?? RuntimeRenderingHostServices.FrameTiming.CurrentRenderer;
                AdvancedRenderPipelineCapabilities capabilities =
                    renderer?.GetAdvancedRenderPipelineCapabilities()
                    ?? AdvancedRenderPipelineCapabilities.NoRenderer;
                EAdvancedRenderPipelineMode mode = AdvancedRenderPipelineMode;
                AdvancedRenderPipelineSelectionResult selection =
                    ResolveAdvancedRenderPipelineSelection(
                        request,
                        mode,
                        capabilities,
                        renderer,
                        retainConfiguredSource: true,
                        out AdvancedVisibilityFamilyReservation reservation,
                        out string reservationFailureReason);

                EAdvancedRenderPipelineOutputBindingState state = mode switch
                {
                    EAdvancedRenderPipelineMode.Disabled =>
                        EAdvancedRenderPipelineOutputBindingState.Disabled,
                    EAdvancedRenderPipelineMode.Diagnostic =>
                        EAdvancedRenderPipelineOutputBindingState.DiagnosticOnly,
                    _ when selection.SelectsAdvanced =>
                        EAdvancedRenderPipelineOutputBindingState.Bound,
                    _ => EAdvancedRenderPipelineOutputBindingState.Rejected,
                };
                string? failureReason = state switch
                {
                    EAdvancedRenderPipelineOutputBindingState.Bound => null,
                    EAdvancedRenderPipelineOutputBindingState.Disabled =>
                        "Advanced output binding is disabled by policy.",
                    EAdvancedRenderPipelineOutputBindingState.DiagnosticOnly =>
                        "Advanced output binding is diagnostic-only by policy.",
                    _ => reservationFailureReason,
                };
                AdvancedRenderPipelineOutputBinding binding = new(
                    request,
                    selection.CapabilityResult,
                    reservation,
                    state,
                    failureReason);
                viewport.RenderPipelineInstance.ApplyAdvancedOutputBinding(in binding);
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
                RenderPipelineRequest request,
                EAdvancedRenderPipelineMode mode,
                in AdvancedRenderPipelineCapabilities capabilities,
                IRuntimeRendererHost? reservationRenderer,
                bool retainConfiguredSource,
                out AdvancedVisibilityFamilyReservation reservation,
                out string reservationFailureReason)
            {
                reservation = default;
                // The public snapshot deliberately stays fail-closed: it has no
                // output identity. Only this selection boundary may turn a live,
                // sticky reservation into the promoted shader-family capability.
                AdvancedRenderPipelineCapabilities effectiveCapabilities = capabilities;
                reservationFailureReason = "Reservation was not requested for this output.";
                if ((mode == EAdvancedRenderPipelineMode.Available ||
                     mode == EAdvancedRenderPipelineMode.Required) &&
                    request.Purpose == ERenderPipelinePurpose.DesktopScene &&
                    !request.Stereo && request.OutputId != 0 &&
                    reservationRenderer is not null &&
                    reservationRenderer.TryReserveAdvancedVisibilityFamily(
                        request.OutputId,
                        out reservation,
                        out reservationFailureReason))
                {
                    effectiveCapabilities = capabilities with
                    {
                        ShaderFamily = EAdvancedShaderFamily.VisibilityBuffer,
                    };
                }
                AdvancedRenderPipelineSelectionResult selection =
                    AdvancedRenderPipelineSelectionResolver.Resolve(mode, effectiveCapabilities, request.Stereo);

                lock (AdvancedPipelineSelectionLock)
                    _lastAdvancedPipelineSelection = selection;

                RuntimeEngine.Rendering.Stats.RendererState.UpdateAdvancedPipelineContext(selection);

                if (selection.RequiresFailure)
                {
                    Debug.RenderingError(
                        "[AdvancedPipeline] Required output reservation failed. Output={0} Reason={1}",
                        request.OutputId,
                        reservationFailureReason);
                    throw new AdvancedRenderPipelineNotSupportedException(
                        selection,
                        reservationFailureReason);
                }

                if (mode == EAdvancedRenderPipelineMode.Diagnostic)
                {
                    Debug.Rendering("[AdvancedPipeline] {0}", selection.Diagnostic);
                }
                else if (mode == EAdvancedRenderPipelineMode.Available &&
                         !selection.SelectsAdvanced)
                {
                    if (retainConfiguredSource)
                    {
                        Debug.RenderingWarningEvery(
                            $"AdvancedPipeline.OutputUnbound.{request.OutputId}.{selection.CapabilityResult.RejectionReason}",
                            TimeSpan.FromSeconds(10),
                            "[AdvancedPipeline] Configured AdvancedRenderPipeline retained, but output {0} is unbound. Reason={1} Capability={2}",
                            request.OutputId,
                            reservationFailureReason,
                            selection.CapabilityResult.Diagnostic);
                    }
                    else
                    {
                        Debug.RenderingWarningEvery(
                            $"AdvancedPipeline.AvailableFallback.{selection.CapabilityResult.RejectionReason}",
                            TimeSpan.FromSeconds(10),
                            "[AdvancedPipeline] {0}",
                            selection.Diagnostic);
                    }
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
                    EMeshSubmissionStrategy requestedStrategy =
                        RuntimeEngine.Rendering.ResolveRequestedMeshSubmissionStrategy();
                    EMeshSubmissionStrategy effectiveStrategy =
                        RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
                    bool useGpu = effectiveStrategy != EMeshSubmissionStrategy.CpuDirect;
                    
                    foreach (RuntimeWorld world in Engine.WorldInstances)
                        world.GetRenderWorld()?.ApplyRenderDispatchPreference(useGpu);

                    foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports())
                    {
                        RenderPipeline? pipeline = viewport.RenderPipeline;
                        if (pipeline is null)
                            continue;

                        if (pipeline is DebugOpaqueRenderPipeline debugPipeline)
                            debugPipeline.MeshSubmissionStrategy = requestedStrategy;
                        else
                            RuntimeEngine.Rendering.ApplyMeshSubmissionStrategyToPipeline(
                                pipeline,
                                requestedStrategy);
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
                    foreach (RuntimeWorld world in Engine.WorldInstances)
                        world.GetRenderWorld()?.ApplyCpuSceneCullingStructurePreference(structure);
                }

                Engine.InvokeOnMainThread(Apply, "RuntimeEngine.Rendering.ApplyCpuSceneCullingStructurePreference", true);
            }

            public static void ApplyGpuMeshBvhPickingPreference()
            {
                static void Apply()
                {
                    bool enabled = Engine.EditorPreferences.GpuMeshBvhClickPickEnabled;
                    foreach (RuntimeWorld world in Engine.WorldInstances)
                        if (world.GetRenderWorld() is { } renderWorld)
                            renderWorld.GpuMeshBvhPickingEnabled = enabled;
                }

                Engine.InvokeOnMainThread(Apply, "RuntimeEngine.Rendering.ApplyGpuMeshBvhPickingPreference", true);
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
