using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Physics.Physx;
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
                => Environment.GetEnvironmentVariable("XRE_USE_PIPELINE_V2") == "1";

            public static RenderPipeline NewRenderPipeline()
                => NewRenderPipeline(stereo: false);

            public static RenderPipeline NewRenderPipeline(bool stereo)
                => (Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false) && !stereo
                    ? new DebugOpaqueRenderPipeline()
                    : UsePipelineV2
                        ? new DefaultRenderPipeline2(stereo)
                        : new DefaultRenderPipeline(stereo);

            public static void ApplyRenderPipelinePreference()
            {
                bool preferDebug = Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false;
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
                    bool useGpu = ResolveGpuRenderDispatchPreference(Engine.EffectiveSettings.GPURenderDispatch);
                    
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyRenderDispatchPreference(useGpu);

                    foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                    {
                        RenderPipeline? pipeline = viewport.RenderPipeline;
                        if (pipeline is null)
                            continue;

                        if (pipeline is DebugOpaqueRenderPipeline debugPipeline)
                            debugPipeline.GpuRenderDispatch = useGpu;
                        else
                            ApplyGpuRenderDispatchToPipeline(pipeline, useGpu);
                    }
                }

                Engine.InvokeOnMainThread(Apply, "Engine.Rendering.ApplyGpuRenderDispatchPreference", true);
                LogVulkanFeatureProfileFingerprint();
            }

            public static bool IsVulkanRendererActive()
            {
                if (State.RenderingViewport?.Window?.Renderer is VulkanRenderer)
                    return true;

                if (AbstractRenderer.Current is VulkanRenderer)
                    return true;

                foreach (XRWindow window in Engine.Windows)
                    if (window.Renderer is VulkanRenderer)
                        return true;

                return false;
            }

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

            public static void ApplyGpuBvhPreference()
            {
                static void Apply()
                {
                    bool useGpuBvh = VulkanFeatureProfile.ResolveGpuBvhPreference(Engine.EffectiveSettings.UseGpuBvh);
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyGpuBvhPreference(useGpuBvh);
                }

                Engine.InvokeOnMainThread(Apply, "Engine.Rendering.ApplyGpuBvhPreference", true);
                LogVulkanFeatureProfileFingerprint();
            }

            public static void LogVulkanFeatureProfileFingerprint(bool force = false)
            {
                if (!IsVulkanRendererActive())
                    return;

                var configuredProfile = Engine.EffectiveSettings.VulkanGpuDrivenProfile;
                var activeProfile = VulkanFeatureProfile.ActiveProfile;

                bool requestedGpuDispatch = Engine.EffectiveSettings.GPURenderDispatch;
                bool effectiveGpuDispatch = VulkanFeatureProfile.ResolveGpuRenderDispatchPreference(requestedGpuDispatch);

                bool requestedGpuBvh = Engine.EffectiveSettings.UseGpuBvh;
                bool effectiveGpuBvh = VulkanFeatureProfile.ResolveGpuBvhPreference(requestedGpuBvh);

                EOcclusionCullingMode requestedOcclusion = Engine.EffectiveSettings.GpuOcclusionCullingMode;
                EOcclusionCullingMode effectiveOcclusion = VulkanFeatureProfile.ResolveOcclusionCullingMode(requestedOcclusion);
                EGpuSortDomainPolicy sortPolicy = Engine.Rendering.Settings.GpuSortDomainPolicy;
                EVulkanQueueOverlapMode requestedQueueOverlap = Engine.EffectiveSettings.VulkanQueueOverlapMode;
                EVulkanQueueOverlapMode effectiveQueueOverlap = VulkanFeatureProfile.ResolveQueueOverlapMode(requestedQueueOverlap);

                bool effectiveComputePasses = VulkanFeatureProfile.ResolveComputeDependentPassesPreference(true);
                bool effectiveImGui = VulkanFeatureProfile.ResolveImGuiPreference(true);
                bool supportsIndirectCount = AbstractRenderer.Current?.SupportsIndirectCountDraw() == true;
                string dispatchPath = effectiveGpuDispatch ? "GPUDriven" : "CPUFallback";

                string fingerprint = string.Format(
                    "[VulkanProfile] Configured={0} Active={1} ComputePasses={2} GpuDispatch={3}(requested={4}) GpuBvh={5}(requested={6}) Occlusion={7}->{8} SortPolicy={9} QueueOverlap={10}(requested={11}) ImGui={12} DrawIndirectCountExt={13} DispatchPath={14}",
                    configuredProfile,
                    activeProfile,
                    effectiveComputePasses,
                    effectiveGpuDispatch,
                    requestedGpuDispatch,
                    effectiveGpuBvh,
                    requestedGpuBvh,
                    requestedOcclusion,
                    effectiveOcclusion,
                    sortPolicy,
                    effectiveQueueOverlap,
                    requestedQueueOverlap,
                    effectiveImGui,
                    supportsIndirectCount,
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
                    foreach (XRViewport viewport in Engine.EnumerateActiveViewports())
                    {
                        if (!NvidiaDlssManager.IsSupported || !Settings.EnableNvidiaDlss)
                            NvidiaDlssManager.ResetViewport(viewport);
                        else
                            NvidiaDlssManager.ApplyToViewport(viewport, Settings);
                    }

                    NotifyVulkanUpscaleBridgeVendorSelectionChanged("NVIDIA DLSS preference changed");
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
                }
                Engine.InvokeOnMainThread(Apply, "Engine.Rendering.ApplyIntelXessPreference", true);
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
            /// Pushes the effective child matrix recalc loop type into the engine rendering settings.
            /// </summary>
            public static void ApplyRecalcChildMatricesLoopTypePreference()
                => Settings.RecalcChildMatricesLoopType = Engine.EffectiveSettings.RecalcChildMatricesLoopType;

            /// <summary>
            /// Pushes the effective compute skinning setting into the engine rendering settings.
            /// </summary>
            public static void ApplyComputeSkinningPreference()
            {
                Settings.CalculateSkinningInComputeShader = Engine.EffectiveSettings.CalculateSkinningInComputeShader;
                Settings.CalculateBlendshapesInComputeShader = Engine.EffectiveSettings.CalculateBlendshapesInComputeShader;
            }

            internal static void ApplyGpuRenderDispatchToPipeline(RenderPipeline pipeline, bool useGpu)
            {
                foreach (ViewportRenderCommand command in EnumerateCommands(pipeline.CommandChain))
                {
                    switch (command)
                    {
                        case VPRC_RenderMeshesPass renderPass:
                            renderPass.GPUDispatch = useGpu;
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
