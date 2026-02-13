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
                foreach (var pair in GenericRenderObject.RenderObjectCache)
                    foreach (var obj in pair.Value)
                        if (renderer.TryGetAPIRenderObject(obj, out var apiRO) && apiRO is not null)
                            obj.RemoveWrapper(apiRO);
            }

            public static AbstractPhysicsScene NewPhysicsScene()
                => UserSettings.PhysicsLibrary switch
                {
                    EPhysicsLibrary.Jolt => new JoltScene(),
                    _ => new PhysxScene(),
                };

            public static VisualScene3D NewVisualScene()
                => new();

            public static RenderPipeline NewRenderPipeline()
                => (Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false)
                    ? new DebugOpaqueRenderPipeline()
                    : new DefaultRenderPipeline();

            public static void ApplyRenderPipelinePreference()
            {
                bool preferDebug = Engine.EditorPreferences?.Debug?.UseDebugOpaquePipeline ?? false;

                foreach (XRWindow window in Engine.Windows)
                {
                    foreach (XRViewport viewport in window.Viewports)
                    {
                        RenderPipeline? pipeline = viewport.RenderPipeline;

                        if (pipeline is null)
                        {
                            viewport.RenderPipeline = NewRenderPipeline();
                            continue;
                        }

                        if (preferDebug)
                        {
                            if (pipeline is DefaultRenderPipeline defaultPipeline && !defaultPipeline.Stereo)
                                viewport.RenderPipeline = new DebugOpaqueRenderPipeline();
                        }
                        else if (pipeline is DebugOpaqueRenderPipeline)
                        {
                            viewport.RenderPipeline = new DefaultRenderPipeline();
                        }
                    }
                }
            }

            public static void ApplyGlobalIlluminationModePreference()
            {
                var mode = Engine.UserSettings.GlobalIlluminationMode;

                foreach (XRWindow window in Engine.Windows)
                {
                    foreach (XRViewport viewport in window.Viewports)
                    {
                        if (viewport.RenderPipeline is DefaultRenderPipeline defaultPipeline)
                            defaultPipeline.GlobalIlluminationMode = mode;
                    }
                }
            }

            public static void ApplyGpuRenderDispatchPreference()
            {
                bool useGpu = ResolveGpuRenderDispatchPreference(Engine.EffectiveSettings.GPURenderDispatch);

                void Apply()
                {
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyRenderDispatchPreference(useGpu);

                    foreach (XRWindow window in Engine.Windows)
                    {
                        foreach (XRViewport viewport in window.Viewports)
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
                }

                Engine.InvokeOnMainThread(() => Apply(), "Engine.Rendering.ApplyGpuRenderDispatchPreference", true);
                LogVulkanFeatureProfileFingerprint();
            }

            public static bool IsVulkanRendererActive()
            {
                if (State.RenderingViewport?.Window?.Renderer is VulkanRenderer)
                    return true;

                if (AbstractRenderer.Current is VulkanRenderer)
                    return true;

                foreach (XRWindow window in Engine.Windows)
                {
                    if (window.Renderer is VulkanRenderer)
                        return true;
                }

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
                bool useGpuBvh = VulkanFeatureProfile.ResolveGpuBvhPreference(Engine.EffectiveSettings.UseGpuBvh);

                void Apply()
                {
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyGpuBvhPreference(useGpuBvh);
                }

                Engine.InvokeOnMainThread(() => Apply(), "Engine.Rendering.ApplyGpuBvhPreference", true);
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

                bool effectiveComputePasses = VulkanFeatureProfile.ResolveComputeDependentPassesPreference(true);
                bool effectiveImGui = VulkanFeatureProfile.ResolveImGuiPreference(true);
                bool supportsIndirectCount = AbstractRenderer.Current?.SupportsIndirectCountDraw() == true;
                string dispatchPath = effectiveGpuDispatch ? "GPUDriven" : "CPUFallback";

                string fingerprint = string.Format(
                    "[VulkanProfile] Configured={0} Active={1} ComputePasses={2} GpuDispatch={3}(requested={4}) GpuBvh={5}(requested={6}) Occlusion={7}->{8} SortPolicy={9} ImGui={10} DrawIndirectCountExt={11} DispatchPath={12}",
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
                void Apply()
                {
                    foreach (XRWindow window in Engine.Windows)
                    {
                        foreach (XRViewport viewport in window.Viewports)
                        {
                            if (!NvidiaDlssManager.IsSupported || !Settings.EnableNvidiaDlss)
                                NvidiaDlssManager.ResetViewport(viewport);
                            else
                                NvidiaDlssManager.ApplyToViewport(viewport, Settings);
                        }
                    }
                }

                Engine.InvokeOnMainThread(() => Apply(), "Engine.Rendering.ApplyNvidiaDlssPreference", true);
            }

            public static void ApplyIntelXessPreference()
            {
                void Apply()
                {
                    foreach (XRWindow window in Engine.Windows)
                    {
                        foreach (XRViewport viewport in window.Viewports)
                        {
                            if (!IntelXessManager.IsSupported || !EffectiveSettings.EnableIntelXess)
                                IntelXessManager.ResetViewport(viewport);
                            else
                                IntelXessManager.ApplyToViewport(viewport, Settings);
                        }
                    }
                }

                Engine.InvokeOnMainThread(() => Apply(), "Engine.Rendering.ApplyIntelXessPreference", true);
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
