using System.Collections.Concurrent;
using System.Collections.Generic;
using XREngine.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.XeSS;
using XREngine.Scene;
using XREngine.Scene.Physics.Jolt;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
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
                => Rendering.Settings.UseDebugOpaquePipeline
                    ? new DebugOpaqueRenderPipeline()
                    : new DefaultRenderPipeline();

            public static void ApplyRenderPipelinePreference()
            {
                bool preferDebug = Rendering.Settings.UseDebugOpaquePipeline;

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
                bool useGpu = Engine.EffectiveSettings.GPURenderDispatch;

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

                Engine.InvokeOnMainThread(() => Apply(), true);
            }

            public static void ApplyGpuBvhPreference()
            {
                bool useGpuBvh = Engine.EffectiveSettings.UseGpuBvh;

                void Apply()
                {
                    foreach (var worldInstance in Engine.WorldInstances)
                        worldInstance?.ApplyGpuBvhPreference(useGpuBvh);
                }

                Engine.InvokeOnMainThread(() => Apply(), true);
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

                Engine.InvokeOnMainThread(() => Apply(), true);
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

                Engine.InvokeOnMainThread(() => Apply(), true);
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
