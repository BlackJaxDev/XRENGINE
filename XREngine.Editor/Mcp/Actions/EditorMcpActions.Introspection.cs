using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using XREngine;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.DLSS;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        /// <summary>
        /// Lists all available component types in the current AppDomain.
        /// </summary>
        [XRMcp(Name = "list_component_types")]
        [Description("List available component types and metadata.")]
        public static Task<McpToolResponse> ListComponentTypesAsync(
            McpToolContext context,
            [McpName("include_members"), Description("Include properties and fields in the listing.")]
            bool includeMembers = false)
        {
            var components = GetComponentTypes()
                .OrderBy(type => type.Name)
                .Select(type => BuildComponentInfo(type, includeMembers))
                .ToArray();

            return Task.FromResult(new McpToolResponse("Listed component types.", new { components }));
        }

        /// <summary>
        /// Gets a detailed schema for a specific component type.
        /// </summary>
        [XRMcp(Name = "get_component_schema")]
        [Description("Get detailed component type schema including properties and fields.")]
        public static Task<McpToolResponse> GetComponentSchemaAsync(
            McpToolContext context,
            [McpName("component_type"), Description("Component type name (short or full name).")]
            string componentTypeName)
        {
            if (!McpToolRegistry.TryResolveComponentType(componentTypeName, out var componentType))
                return Task.FromResult(new McpToolResponse($"Component type '{componentTypeName}' not found.", isError: true));

            var schema = BuildComponentInfo(componentType, includeMembers: true);
            return Task.FromResult(new McpToolResponse($"Retrieved schema for '{componentType.Name}'.", schema));
        }

        /// <summary>
        /// Lists immediate child transforms for a scene node.
        /// </summary>
        [XRMcp(Name = "list_transform_children")]
        [Description("List immediate child transforms for a scene node.")]
        public static Task<McpToolResponse> ListTransformChildrenAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to inspect.")]
            string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            var children = node!.Transform.Children
                .Select(child => new
                {
                    transformId = child.ID,
                    nodeId = child.SceneNode?.ID,
                    nodeName = child.SceneNode?.Name,
                    path = child.SceneNode is null ? null : BuildNodePath(child.SceneNode),
                    childCount = child.Children.Count
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse($"Listed {children.Length} child transforms.", new { children }));
        }

        /// <summary>
        /// Gets matrix data for a scene node's transform.
        /// </summary>
        [XRMcp(Name = "get_transform_matrices")]
        [Description("Get local/world/render matrices for a scene node.")]
        public static Task<McpToolResponse> GetTransformMatricesAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to inspect.")]
            string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            var transform = node!.Transform;
            var data = new
            {
                localMatrix = ToMatrixArray(transform.LocalMatrix),
                inverseLocalMatrix = ToMatrixArray(transform.InverseLocalMatrix),
                worldMatrix = ToMatrixArray(transform.WorldMatrix),
                inverseWorldMatrix = ToMatrixArray(transform.InverseWorldMatrix),
                renderMatrix = ToMatrixArray(transform.RenderMatrix),
                inverseRenderMatrix = ToMatrixArray(transform.InverseRenderMatrix)
            };

            return Task.FromResult(new McpToolResponse($"Retrieved transform matrices for '{nodeId}'.", data));
        }

        /// <summary>
        /// Gets decomposed transform state (translation, rotation, scale).
        /// </summary>
        [XRMcp(Name = "get_transform_decomposed")]
        [Description("Get local/world/render translation, rotation, and scale for a scene node.")]
        public static Task<McpToolResponse> GetTransformDecomposedAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID to inspect.")]
            string nodeId)
        {
            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var error))
                return Task.FromResult(new McpToolResponse(error ?? "Scene node not found.", isError: true));

            var transform = node!.Transform;

            var local = DecomposeMatrix(transform.LocalMatrix);
            var world = DecomposeMatrix(transform.WorldMatrix);
            var render = DecomposeMatrix(transform.RenderMatrix);

            var data = new
            {
                local,
                world,
                render
            };

            return Task.FromResult(new McpToolResponse($"Retrieved decomposed transform state for '{nodeId}'.", data));
        }

        /// <summary>
        /// Lists currently loaded assets.
        /// </summary>
        [XRMcp(Name = "list_loaded_assets")]
        [Description("List assets currently loaded by the asset manager.")]
        public static Task<McpToolResponse> ListLoadedAssetsAsync(
            McpToolContext context,
            [McpName("asset_type"), Description("Optional asset type filter (short or full name).")]
            string? assetTypeName = null)
        {
            IEnumerable<XRAsset> assets = Engine.Assets.LoadedAssetsByIDInternal.Values;
            if (!string.IsNullOrWhiteSpace(assetTypeName))
            {
                assets = assets.Where(asset =>
                {
                    var type = asset.GetType();
                    if (string.Equals(type.Name, assetTypeName, StringComparison.OrdinalIgnoreCase))
                        return true;
                    return string.Equals(type.FullName, assetTypeName, StringComparison.OrdinalIgnoreCase);
                });
            }

            var result = assets.Select(asset => new
            {
                id = asset.ID,
                name = asset.Name,
                type = asset.GetType().FullName ?? asset.GetType().Name,
                filePath = asset.FilePath,
                originalPath = asset.OriginalPath,
                isDirty = asset.IsDirty,
                embeddedCount = asset.EmbeddedAssets.Count
            }).ToArray();

            return Task.FromResult(new McpToolResponse($"Listed {result.Length} loaded assets.", new { assets = result }));
        }

        /// <summary>
        /// Gets info for a single asset by ID or path.
        /// </summary>
        [XRMcp(Name = "get_asset_info")]
        [Description("Get detailed info about a loaded asset by ID or path.")]
        public static Task<McpToolResponse> GetAssetInfoAsync(
            McpToolContext context,
            [McpName("asset_id"), Description("Asset GUID to inspect.")]
            string? assetId = null,
            [McpName("asset_path"), Description("Asset path to inspect.")]
            string? assetPath = null)
        {
            if (string.IsNullOrWhiteSpace(assetId) && string.IsNullOrWhiteSpace(assetPath))
                return Task.FromResult(new McpToolResponse("Provide asset_id or asset_path.", isError: true));

            XRAsset? asset = null;
            if (!string.IsNullOrWhiteSpace(assetId))
            {
                if (!Guid.TryParse(assetId, out var guid))
                    return Task.FromResult(new McpToolResponse($"Invalid asset_id '{assetId}'.", isError: true));

                asset = Engine.Assets.GetAssetByID(guid);
            }
            else if (!string.IsNullOrWhiteSpace(assetPath))
            {
                asset = Engine.Assets.GetAssetByPath(assetPath);
            }

            if (asset is null)
                return Task.FromResult(new McpToolResponse("Asset not found.", isError: true));

            var info = new
            {
                id = asset.ID,
                name = asset.Name,
                type = asset.GetType().FullName ?? asset.GetType().Name,
                filePath = asset.FilePath,
                originalPath = asset.OriginalPath,
                isDirty = asset.IsDirty,
                embeddedCount = asset.EmbeddedAssets.Count,
                sourceAssetId = asset.SourceAsset?.ID
            };

            return Task.FromResult(new McpToolResponse($"Retrieved asset info for '{asset.Name ?? asset.ID.ToString()}'.", info));
        }

        /// <summary>
        /// Lists loaded prefab assets (sources and variants).
        /// </summary>
        [XRMcp(Name = "list_prefabs")]
        [Description("List loaded prefab assets.")]
        public static Task<McpToolResponse> ListPrefabsAsync(McpToolContext context)
        {
            var prefabs = Engine.Assets.LoadedAssetsByIDInternal.Values
                .Where(asset => asset is XRPrefabSource || asset is XRPrefabVariant)
                .Select(asset => new
                {
                    id = asset.ID,
                    name = asset.Name,
                    type = asset.GetType().FullName ?? asset.GetType().Name,
                    filePath = asset.FilePath
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse($"Listed {prefabs.Length} prefab assets.", new { prefabs }));
        }

        /// <summary>
        /// Gets the hierarchy structure for a prefab asset.
        /// </summary>
        [XRMcp(Name = "get_prefab_structure")]
        [Description("Get the node hierarchy for a prefab source or variant.")]
        public static Task<McpToolResponse> GetPrefabStructureAsync(
            McpToolContext context,
            [McpName("prefab_id"), Description("Prefab asset GUID to inspect.")]
            string? prefabId = null,
            [McpName("prefab_path"), Description("Prefab asset path to inspect.")]
            string? prefabPath = null)
        {
            if (string.IsNullOrWhiteSpace(prefabId) && string.IsNullOrWhiteSpace(prefabPath))
                return Task.FromResult(new McpToolResponse("Provide prefab_id or prefab_path.", isError: true));

            XRAsset? asset = null;
            if (!string.IsNullOrWhiteSpace(prefabId))
            {
                if (!Guid.TryParse(prefabId, out var guid))
                    return Task.FromResult(new McpToolResponse($"Invalid prefab_id '{prefabId}'.", isError: true));

                asset = Engine.Assets.GetAssetByID(guid);
            }
            else if (!string.IsNullOrWhiteSpace(prefabPath))
            {
                asset = Engine.Assets.GetAssetByPath(prefabPath);
            }

            if (asset is null)
                return Task.FromResult(new McpToolResponse("Prefab asset not found.", isError: true));

            SceneNode? rootNode = asset switch
            {
                XRPrefabSource source => source.RootNode,
                XRPrefabVariant variant => variant.BasePrefab?.RootNode,
                _ => null
            };

            if (rootNode is null)
                return Task.FromResult(new McpToolResponse("Prefab hierarchy is not available.", isError: true));

            var nodes = new List<object>();
            CollectNodes(rootNode, nodes, 0, maxDepth: null);

            var data = new
            {
                prefabId = asset.ID,
                prefabName = asset.Name,
                prefabType = asset.GetType().FullName ?? asset.GetType().Name,
                basePrefabId = (asset as XRPrefabVariant)?.BasePrefabId,
                nodeCount = nodes.Count,
                nodes
            };

            return Task.FromResult(new McpToolResponse("Retrieved prefab hierarchy.", data));
        }

        /// <summary>
        /// Gets the current render state snapshot.
        /// </summary>
        [XRMcp(Name = "get_render_state")]
        [Description("Get current rendering pipeline and camera state.")]
        public static Task<McpToolResponse> GetRenderStateAsync(McpToolContext context)
        {
            var pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
            var renderState = Engine.Rendering.State.RenderingPipelineState;
            var camera = Engine.Rendering.State.RenderingCamera;
            var cameraNode = camera?.Transform?.SceneNode;
            var cameraTransform = camera?.Transform;
            var viewport = Engine.Rendering.State.RenderingViewport;
            var stateViewport = renderState?.WindowViewport;
            var activeViewport = viewport
                ?? stateViewport
                ?? pipeline?.LastWindowViewport
                ?? Engine.EnumerateActiveViewports().FirstOrDefault();
            var activeCamera = camera
                ?? renderState?.SceneCamera
                ?? activeViewport?.ActiveCamera;
            var activeCameraTransform = activeCamera?.Transform ?? activeViewport?.CameraComponent?.Transform;
            var activeCameraNode = activeCameraTransform?.SceneNode;
            var viewportCameraComponent = activeViewport?.CameraComponent;
            var viewportCameraTransform = viewportCameraComponent?.Transform;
            var viewportCameraNode = viewportCameraTransform?.SceneNode;
            var mainPawn = Engine.State.MainPlayer?.ControlledPawnComponent as PawnComponent;
            var pawnCamera = mainPawn?.GetCamera();
            var pawnCameraTransform = pawnCamera?.Transform;
            var pawnNode = mainPawn?.SceneNode;
            var pawnTransform = pawnNode?.Transform;
            var renderArea = Engine.Rendering.State.RenderArea;
            var activeViewportCommands = activeViewport?.RenderPipelineInstance.MeshRenderCommands;
            var renderingViewportCommands = viewport?.RenderPipelineInstance.MeshRenderCommands;
            var activeViewports = Engine
                .EnumerateActiveViewports(Engine.EViewportEnumerationMode.IncludeVrEyeViewports)
                .Select(BuildViewportRenderSummary)
                .ToArray();

            var data = new
            {
                renderFrameId = Engine.Rendering.State.RenderFrameId,
                renderArea = ToMcpRectangle(renderArea),
                isShadowPass = Engine.Rendering.State.IsShadowPass,
                isStereoPass = Engine.Rendering.State.IsStereoPass,
                isMirrorPass = Engine.Rendering.State.IsMirrorPass,
                renderingWorldId = Engine.Rendering.State.RenderingWorld is XRObjectBase renderingWorld ? (Guid?)renderingWorld.ID : null,
                renderingSceneType = Engine.Rendering.State.RenderingScene?.GetType().FullName,
                renderingViewportIndex = viewport?.Index,
                renderingViewportSize = viewport is null ? null : new { width = viewport.Width, height = viewport.Height },
                renderingCameraType = camera?.GetType().FullName,
                renderingCameraNodeId = cameraNode?.ID,
                renderingCameraNodeName = cameraNode?.Name,
                renderingCameraWorldPosition = cameraTransform is null ? null : ToMcpVector3(cameraTransform.WorldTranslation),
                renderingCameraWorldForward = cameraTransform is null ? null : ToMcpVector3(cameraTransform.WorldForward),
                activeViewportIndex = activeViewport?.Index,
                activeViewportSize = activeViewport is null ? null : new { width = activeViewport.Width, height = activeViewport.Height },
                activeViewportInternalSize = activeViewport is null ? null : new { width = activeViewport.InternalWidth, height = activeViewport.InternalHeight },
                activeViewportAutomaticallyCollectVisible = activeViewport?.AutomaticallyCollectVisible,
                activeViewportAutomaticallySwapBuffers = activeViewport?.AutomaticallySwapBuffers,
                activeViewportSuppress3DSceneRendering = activeViewport?.Suppress3DSceneRendering,
                activeViewportCullWithFrustum = activeViewport?.CullWithFrustum,
                activeViewportCameraCullWithFrustum = activeViewport?.CameraComponent?.CullWithFrustum,
                activeViewportRenderingCommandCount = activeViewportCommands?.GetRenderingCommandCount(),
                activeViewportUpdatingCommandCount = activeViewportCommands?.GetUpdatingCommandCount(),
                activeViewportCommandsAddedCount = activeViewportCommands?.GetCommandsAddedCount(),
                activeViewportRenderingCommandPasses = BuildRenderCommandPassSummary(activeViewportCommands),
                activeCameraType = activeCamera?.GetType().FullName,
                activeCameraNodeId = activeCameraNode?.ID,
                activeCameraNodeName = activeCameraNode?.Name,
                activeCameraWorldPosition = activeCameraTransform is null ? null : ToMcpVector3(activeCameraTransform.WorldTranslation),
                activeCameraWorldForward = activeCameraTransform is null ? null : ToMcpVector3(activeCameraTransform.WorldForward),
                viewportCameraComponentId = viewportCameraComponent?.ID,
                viewportCameraComponentName = viewportCameraComponent?.Name,
                viewportCameraNodeId = viewportCameraNode?.ID,
                viewportCameraNodeName = viewportCameraNode?.Name,
                viewportCameraWorldPosition = viewportCameraTransform is null ? null : ToMcpVector3(viewportCameraTransform.WorldTranslation),
                viewportCameraWorldForward = viewportCameraTransform is null ? null : ToMcpVector3(viewportCameraTransform.WorldForward),
                mainPawnComponentId = mainPawn?.ID,
                mainPawnNodeId = pawnNode?.ID,
                mainPawnNodeName = pawnNode?.Name,
                mainPawnWorldPosition = pawnTransform is null ? null : ToMcpVector3(pawnTransform.WorldTranslation),
                mainPawnWorldForward = pawnTransform is null ? null : ToMcpVector3(pawnTransform.WorldForward),
                mainPawnCameraComponentId = pawnCamera?.ID,
                mainPawnCameraComponentName = pawnCamera?.Name,
                mainPawnCameraWorldPosition = pawnCameraTransform is null ? null : ToMcpVector3(pawnCameraTransform.WorldTranslation),
                mainPawnCameraWorldForward = pawnCameraTransform is null ? null : ToMcpVector3(pawnCameraTransform.WorldForward),
                pipelineDebugName = pipeline?.Pipeline?.DebugName,
                pipelineType = pipeline?.Pipeline?.GetType().FullName,
                pipelineDescriptor = pipeline?.DebugDescriptor,
                renderGraphPassIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex,
                renderStateCameraType = renderState?.SceneCamera?.GetType().FullName,
                renderStateViewportIndex = stateViewport?.Index,
                renderStateViewportSize = stateViewport is null ? null : new { width = stateViewport.Width, height = stateViewport.Height },
                renderingViewportAutomaticallyCollectVisible = viewport?.AutomaticallyCollectVisible,
                renderingViewportAutomaticallySwapBuffers = viewport?.AutomaticallySwapBuffers,
                renderingViewportSuppress3DSceneRendering = viewport?.Suppress3DSceneRendering,
                renderingViewportCullWithFrustum = viewport?.CullWithFrustum,
                renderingViewportCameraCullWithFrustum = viewport?.CameraComponent?.CullWithFrustum,
                renderingViewportRenderingCommandCount = renderingViewportCommands?.GetRenderingCommandCount(),
                renderingViewportUpdatingCommandCount = renderingViewportCommands?.GetUpdatingCommandCount(),
                renderingViewportCommandsAddedCount = renderingViewportCommands?.GetCommandsAddedCount(),
                renderingViewportRenderingCommandPasses = BuildRenderCommandPassSummary(renderingViewportCommands),
                activeViewports
            };

            return Task.FromResult(new McpToolResponse("Retrieved render state.", data));
        }

        private static object BuildViewportRenderSummary(XRViewport viewport)
        {
            RenderCommandCollection ownCommands = viewport.RenderPipelineInstance.MeshRenderCommands;
            RenderCommandCollection? overrideCommands = viewport.MeshRenderCommandsOverride;
            RenderCommandCollection activeCommands = overrideCommands ?? ownCommands;
            XRCamera? activeCamera = viewport.ActiveCamera;
            TransformBase? activeCameraTransform = activeCamera?.Transform ?? viewport.CameraComponent?.Transform;
            SceneNode? activeCameraNode = activeCameraTransform?.SceneNode;

            return new
            {
                index = viewport.Index,
                isVrLeftEyeViewport = ReferenceEquals(viewport, Engine.VRState.LeftEyeViewport),
                isVrRightEyeViewport = ReferenceEquals(viewport, Engine.VRState.RightEyeViewport),
                isWindowViewport = Engine.Windows.Any(window => window.Viewports.Contains(viewport)),
                width = viewport.Width,
                height = viewport.Height,
                internalWidth = viewport.InternalWidth,
                internalHeight = viewport.InternalHeight,
                automaticallyCollectVisible = viewport.AutomaticallyCollectVisible,
                automaticallySwapBuffers = viewport.AutomaticallySwapBuffers,
                suppress3DSceneRendering = viewport.Suppress3DSceneRendering,
                cullWithFrustum = viewport.CullWithFrustum,
                cameraCullWithFrustum = viewport.CameraComponent?.CullWithFrustum,
                meshRenderCommandsOverride = overrideCommands is not null,
                ownRenderingCommandCount = ownCommands.GetRenderingCommandCount(),
                ownUpdatingCommandCount = ownCommands.GetUpdatingCommandCount(),
                activeRenderingCommandCount = activeCommands.GetRenderingCommandCount(),
                activeUpdatingCommandCount = activeCommands.GetUpdatingCommandCount(),
                activeCommandPasses = BuildRenderCommandPassSummary(activeCommands),
                cameraType = activeCamera?.GetType().FullName,
                cameraNodeId = activeCameraNode?.ID,
                cameraNodeName = activeCameraNode?.Name,
                cameraWorldPosition = activeCameraTransform is null ? null : ToMcpVector3(activeCameraTransform.WorldTranslation),
                cameraWorldForward = activeCameraTransform is null ? null : ToMcpVector3(activeCameraTransform.WorldForward),
                pipelineInstanceId = viewport.RenderPipelineInstance.InstanceId,
                pipelineDebugName = viewport.RenderPipelineInstance.DebugName,
                pipelineType = viewport.RenderPipeline?.GetType().FullName,
                resourceGeneration = viewport.RenderPipelineInstance.ResourceGeneration
            };
        }

        private static object[]? BuildRenderCommandPassSummary(RenderCommandCollection? commands)
        {
            if (commands is null)
                return null;

            return commands.PassMetadata
                .OrderBy(static pair => pair.Key)
                .Select(pair =>
                {
                    int commandCount = 0;
                    int meshCommandCount = 0;
                    int enabledCommandCount = 0;
                    int enabledMeshCommandCount = 0;
                    string? firstCommandType = null;
                    string? firstMeshName = null;
                    string? firstMaterialName = null;

                    if (commands.TryGetRenderingPassCommands(pair.Key, out IReadOnlyCollection<RenderCommand>? passCommands) &&
                        passCommands is not null)
                    {
                        commandCount = passCommands.Count;
                        foreach (RenderCommand command in passCommands)
                        {
                            if (command is null)
                                continue;

                            firstCommandType ??= command.GetType().Name;

                            if (command.RenderEnabled)
                                enabledCommandCount++;

                            if (command is IRenderCommandMesh meshCommand)
                            {
                                meshCommandCount++;
                                if (command.RenderEnabled)
                                    enabledMeshCommandCount++;

                                firstMeshName ??= meshCommand.Mesh?.Mesh?.Name;
                                firstMaterialName ??= (meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material)?.Name;
                            }
                        }
                    }

                    return new
                    {
                        passIndex = pair.Key,
                        passName = pair.Value.Name,
                        stage = pair.Value.Stage.ToString(),
                        commandCount,
                        meshCommandCount,
                        enabledCommandCount,
                        enabledMeshCommandCount,
                        firstCommandType,
                        firstMeshName,
                        firstMaterialName
                    };
                })
                .Cast<object>()
                .ToArray();
        }

        /// <summary>
        /// Gets the current editor selection.
        /// </summary>
        [XRMcp(Name = "get_selection")]
        [Description("Get the currently selected scene nodes.")]
        public static Task<McpToolResponse> GetSelectionAsync(McpToolContext context)
        {
            var selection = Selection.SceneNodes
                .Where(node => node.World == context.WorldInstance)
                .Select(node => new
                {
                    id = node.ID,
                    name = node.Name,
                    path = BuildNodePath(node)
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse($"Retrieved {selection.Length} selected nodes.", new { selection }));
        }

        /// <summary>
        /// Gets a snapshot of editor/engine play mode state.
        /// </summary>
        [XRMcp(Name = "get_engine_state")]
        [Description("Get engine/editor play mode and high-level state flags.")]
        public static Task<McpToolResponse> GetEngineStateAsync(McpToolContext context)
        {
            var openXrSummary = Engine.VRState.OpenXRApi?.CreateSmokeSummary();
            var data = new
            {
                isEditor = Engine.IsEditor,
                isPlaying = Engine.IsPlaying,
                playModeState = EditorState.CurrentState.ToString(),
                inEditMode = EditorState.InEditMode,
                inPlayMode = EditorState.InPlayMode,
                isPaused = EditorState.IsPaused,
                isTransitioning = EditorState.IsTransitioning,
                timePaused = Engine.Time.Timer.Paused,
                vr = new
                {
                    isInVR = Engine.VRState.IsInVR,
                    activeRuntime = Engine.VRState.ActiveRuntime.ToString(),
                    isOpenVRActive = Engine.VRState.IsOpenVRActive,
                    isOpenXRActive = Engine.VRState.IsOpenXRActive,
                    openXrSessionRunning = Engine.VRState.OpenXRApi?.IsSessionRunning ?? false,
                    openXrRuntimeState = openXrSummary?.RuntimeState,
                    openXrSessionState = openXrSummary?.SessionState,
                    openXr = openXrSummary
                }
            };

            return Task.FromResult(new McpToolResponse("Retrieved engine state.", data));
        }

        /// <summary>
        /// Gets timing information from the engine timer.
        /// </summary>
        [XRMcp(Name = "get_time_state")]
        [Description("Get timing, delta, and target frequency information.")]
        public static Task<McpToolResponse> GetTimeStateAsync(McpToolContext context)
        {
            var timer = Engine.Time.Timer;
            static double TicksToMilliseconds(long ticks)
                => ticks <= 0L ? 0.0 : ticks * 1000.0 / XREngine.Timers.EngineTimer.StopwatchTickFrequency;

            var data = new
            {
                isRunning = timer.IsRunning,
                isPaused = timer.Paused,
                elapsedTime = Engine.ElapsedTime,
                elapsedTicks = Engine.ElapsedTicks,
                delta = Engine.Delta,
                deltaMs = Engine.Delta * 1000.0,
                undilatedDelta = Engine.UndilatedDelta,
                smoothedDelta = Engine.SmoothedDelta,
                smoothedUndilatedDelta = Engine.SmoothedUndilatedDelta,
                fixedDelta = Engine.FixedDelta,
                renderDelta = timer.Render.Delta,
                renderDeltaMs = timer.Render.Delta * 1000.0,
                renderElapsedMs = TicksToMilliseconds(timer.Render.ElapsedTicks),
                collectDelta = timer.Collect.Delta,
                collectDeltaMs = timer.Collect.Delta * 1000.0,
                collectElapsedMs = TicksToMilliseconds(timer.Collect.ElapsedTicks),
                updateDelta = timer.Update.Delta,
                updateDeltaMs = timer.Update.Delta * 1000.0,
                updateElapsedMs = TicksToMilliseconds(timer.Update.ElapsedTicks),
                targetRenderHz = timer.TargetRenderFrequency,
                targetUpdateHz = timer.TargetUpdateFrequency,
                fixedUpdateHz = timer.FixedUpdateFrequency,
                vSync = timer.VSync.ToString()
            };

            return Task.FromResult(new McpToolResponse("Retrieved timing state.", data));
        }

        /// <summary>
        /// Gets current job manager queue/worker information.
        /// </summary>
        [XRMcp(Name = "get_job_manager_state")]
        [Description("Get job manager queues, workers, and queue capacity.")]
        public static Task<McpToolResponse> GetJobManagerStateAsync(McpToolContext context)
        {
            var jobs = Engine.Jobs;
            var queueStats = BuildJobQueueStats(jobs);

            var data = new
            {
                workerCount = jobs.WorkerCount,
                activeCount = jobs.Active.Count,
                isQueueBounded = jobs.IsQueueBounded,
                queueCapacity = jobs.QueueCapacity,
                queueSlotsAvailable = jobs.QueueSlotsAvailable,
                queueSlotsInUse = jobs.QueueSlotsInUse,
                queueStats
            };

            return Task.FromResult(new McpToolResponse("Retrieved job manager state.", data));
        }

        /// <summary>
        /// Lists currently active jobs.
        /// </summary>
        [XRMcp(Name = "list_active_jobs")]
        [Description("List jobs currently executing.")]
        public static Task<McpToolResponse> ListActiveJobsAsync(McpToolContext context)
        {
            var jobs = Engine.Jobs.Active
                .Select(job => new
                {
                    id = job.Id,
                    type = job.GetType().FullName ?? job.GetType().Name,
                    priority = job.Priority.ToString(),
                    affinity = job.Affinity.ToString(),
                    isRunning = job.IsRunning,
                    isCompleted = job.IsCompleted,
                    isFaulted = job.IsFaulted,
                    isCanceled = job.IsCanceled,
                    progress = job.Progress,
                    exception = job.Exception?.Message
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse($"Listed {jobs.Length} active jobs.", new { jobs }));
        }

        /// <summary>
        /// Gets the current undo/redo history.
        /// </summary>
        [XRMcp(Name = "get_undo_history")]
        [Description("Get undo/redo history entries.")]
        public static Task<McpToolResponse> GetUndoHistoryAsync(
            McpToolContext context,
            [McpName("max_entries"), Description("Maximum number of entries per stack to return.")]
            int maxEntries = 50,
            [McpName("include_changes"), Description("Include per-property change details.")]
            bool includeChanges = false)
        {
            maxEntries = Math.Clamp(maxEntries, 1, 500);

            var undo = Undo.PendingUndo
                .Take(maxEntries)
                .Select(entry => MapUndoEntry(entry, includeChanges))
                .ToArray();

            var redo = Undo.PendingRedo
                .Take(maxEntries)
                .Select(entry => MapUndoEntry(entry, includeChanges))
                .ToArray();

            var data = new
            {
                canUndo = Undo.CanUndo,
                canRedo = Undo.CanRedo,
                undo,
                redo
            };

            return Task.FromResult(new McpToolResponse("Retrieved undo history.", data));
        }

        /// <summary>
        /// Lists currently connected input devices.
        /// </summary>
        [XRMcp(Name = "list_input_devices")]
        [Description("List available input devices and connection state.")]
        public static Task<McpToolResponse> ListInputDevicesAsync(McpToolContext context)
        {
            var devices = InputDevice.CurrentDevices
                .Select(kvp => new
                {
                    deviceType = kvp.Key.ToString(),
                    deviceCount = kvp.Value.Count,
                    connectedCount = kvp.Value.Count(dev => dev.IsConnected),
                    devices = kvp.Value.Select(dev => new
                    {
                        index = dev.Index,
                        isConnected = dev.IsConnected,
                        inputInterfaceCount = dev.InputInterfaces.Count,
                        inputInterfaces = dev.InputInterfaces.Select(input => input.GetType().FullName ?? input.GetType().Name).ToArray()
                    }).ToArray()
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse("Listed input devices.", new { devices }));
        }

        /// <summary>
        /// Lists local players and their attached input/viewport state.
        /// </summary>
        [XRMcp(Name = "list_local_players")]
        [Description("List local player controllers, viewports, and input presence.")]
        public static Task<McpToolResponse> ListLocalPlayersAsync(McpToolContext context)
        {
            var players = Engine.State.LocalPlayers
                .Where(player => player is not null)
                .Select(player =>
                {
                    var input = player!.InputDevice as LocalInputInterface;
                    var viewport = player.Viewport as XRViewport;
                    return new
                    {
                        localIndex = player.LocalPlayerIndex.ToString(),
                        controllerType = player.GetType().FullName ?? player.GetType().Name,
                        viewportIndex = viewport?.Index,
                        viewportSize = viewport is null ? null : new { width = viewport.Width, height = viewport.Height },
                        hasKeyboard = input?.Keyboard is not null,
                        hasMouse = input?.Mouse is not null,
                        hasGamepad = input?.Gamepad is not null,
                        hasOpenVRActions = input?.OpenVRActions is not null
                    };
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse($"Listed {players.Length} local players.", new { players }));
        }

        /// <summary>
        /// Gets summary statistics for a scene or all scenes.
        /// </summary>
        [XRMcp(Name = "get_scene_statistics")]
        [Description("Get scene statistics including node and component counts.")]
        public static Task<McpToolResponse> GetSceneStatisticsAsync(
            McpToolContext context,
            [McpName("scene"), Description("Optional scene name or ID. If omitted, returns all scenes.")]
            string? sceneIdOrName = null,
            [McpName("include_component_types"), Description("Include component type counts per scene.")]
            bool includeComponentTypes = false)
        {
            var world = context.WorldInstance.TargetWorld;
            if (world is null)
                return Task.FromResult(new McpToolResponse("No active world found.", isError: true));

            IEnumerable<XRScene> scenes;
            if (!string.IsNullOrWhiteSpace(sceneIdOrName))
            {
                if (!TryResolveScene(context.WorldInstance, sceneIdOrName, out var scene, out var error))
                    return Task.FromResult(new McpToolResponse(error ?? "Scene not found.", isError: true));

                scenes = scene is null ? [] : [scene];
            }
            else
            {
                scenes = world.Scenes;
            }

            var stats = scenes.Select(scene => BuildSceneStats(scene, includeComponentTypes)).ToArray();

            return Task.FromResult(new McpToolResponse("Retrieved scene statistics.", new { scenes = stats }));
        }

        /// <summary>
        /// Lists available transform types.
        /// </summary>
        [XRMcp(Name = "list_transform_types")]
        [Description("List available transform types.")]
        public static Task<McpToolResponse> ListTransformTypesAsync(McpToolContext context)
        {
            var types = TransformBase.TransformTypes
                .Select(type => new
                {
                    name = type.Name,
                    fullName = type.FullName,
                    @namespace = type.Namespace,
                    assembly = type.Assembly.GetName().Name,
                    displayName = type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                })
                .OrderBy(type => type.name)
                .ToArray();

            return Task.FromResult(new McpToolResponse("Listed transform types.", new { types }));
        }

        /// <summary>
        /// Gets rendering capability flags for the current renderer.
        /// </summary>
        [XRMcp(Name = "get_render_capabilities")]
        [Description("Get renderer capability flags (GPU, extensions, ray tracing).")]
        public static Task<McpToolResponse> GetRenderCapabilitiesAsync(McpToolContext context)
        {
            var data = new
            {
                isNvidia = Engine.Rendering.State.IsNVIDIA,
                isIntel = Engine.Rendering.State.IsIntel,
                isVulkan = Engine.Rendering.State.IsVulkan,
                hasNvRayTracing = Engine.Rendering.State.HasNvRayTracing,
                hasVulkanRayTracing = Engine.Rendering.State.HasVulkanRayTracing,
                hasOvrMultiView = Engine.Rendering.State.HasOvrMultiViewExtension,
                nvidiaDlss = new
                {
                    runtimeDllsAvailable = NvidiaDlssManager.RequiredRuntimeDllsAvailable,
                    runtimeDllsUnavailableReason = NvidiaDlssManager.RequiredRuntimeDllsAvailable
                        ? string.Empty
                        : NvidiaDlssManager.RequiredRuntimeDllsUnavailableReason,
                    supported = NvidiaDlssManager.IsSupported,
                    lastError = NvidiaDlssManager.LastError,
                    effectiveEnabled = Engine.EffectiveSettings.EnableNvidiaDlss,
                    effectiveQuality = Engine.EffectiveSettings.DlssQuality.ToString(),
                    frameGenerationRuntimeDllsAvailable = NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable,
                    frameGenerationRuntimeDllsUnavailableReason = NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable
                        ? string.Empty
                        : NvidiaDlssManager.FrameGenerationRuntimeDllsUnavailableReason,
                    frameGenerationAvailable = NvidiaDlssManager.FrameGenerationAvailable,
                    frameGenerationUnavailableReason = NvidiaDlssManager.FrameGenerationUnavailableReason,
                    frameGenerationMaximumFramesToGenerate = NvidiaDlssManager.FrameGenerationMaximumFramesToGenerate,
                    frameGenerationFramesActuallyPresented = NvidiaDlssManager.FrameGenerationFramesActuallyPresented,
                    frameGenerationFramesActuallyPresentedTotal = NvidiaDlssManager.FrameGenerationFramesActuallyPresentedTotal,
                    effectiveFrameGenerationEnabled = Engine.EffectiveSettings.EnableNvidiaDlssFrameGeneration,
                    effectiveFrameGenerationMode = Engine.EffectiveSettings.NvidiaDlssFrameGenerationMode.ToString(),
                    effectiveFrameGenerationRequested = Engine.EffectiveSettings.EnableNvidiaDlssFrameGeneration
                        && Engine.EffectiveSettings.NvidiaDlssFrameGenerationMode != ENvidiaDlssFrameGenerationMode.Off
                },
                openGlExtensions = Engine.Rendering.State.OpenGLExtensions
            };

            return Task.FromResult(new McpToolResponse("Retrieved render capabilities.", data));
        }

        private static IEnumerable<Type> GetComponentTypes()
        {
            var types = new HashSet<Type>();
            var baseType = typeof(XRComponent);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? assemblyTypes = null;
                try
                {
                    assemblyTypes = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    assemblyTypes = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in assemblyTypes)
                {
                    if (!baseType.IsAssignableFrom(type) || type.IsAbstract)
                        continue;

                    types.Add(type);
                }
            }

            return types;
        }

        private static object[] BuildJobQueueStats(JobManager jobs)
            => Enum.GetValues<JobPriority>()
                .Select(priority => new
                {
                    priority = priority.ToString(),
                    queuedAny = jobs.GetQueuedCount(priority, JobAffinity.Any),
                    queuedMainThread = jobs.GetQueuedCount(priority, JobAffinity.MainThread),
                    queuedCollectVisibleSwap = jobs.GetQueuedCount(priority, JobAffinity.CollectVisibleSwap),
                    queuedRemote = jobs.GetQueuedCount(priority, JobAffinity.Remote),
                    averageWaitMs = jobs.GetAverageWait(priority).TotalMilliseconds
                })
                .Cast<object>()
                .ToArray();

        private static object MapUndoEntry(Undo.UndoEntry entry, bool includeChanges)
        {
            object[]? changes = null;
            if (includeChanges)
            {
                changes = entry.Changes
                    .Select(change => new
                    {
                        target = change.TargetDisplayName,
                        targetType = change.TargetType?.FullName ?? change.TargetType?.Name,
                        propertyName = change.PropertyName,
                        previousValue = change.PreviousValue?.ToString(),
                        newValue = change.NewValue?.ToString()
                    })
                    .Cast<object>()
                    .ToArray();
            }

            return new
            {
                description = entry.Description,
                timestampUtc = entry.TimestampUtc,
                changeCount = entry.Changes.Count,
                changes
            };
        }

        private static object BuildSceneStats(XRScene scene, bool includeComponentTypes)
        {
            var rootNodes = scene.RootNodes ?? [];
            var allNodes = rootNodes.SelectMany(EnumerateHierarchy).ToArray();
            int totalComponents = allNodes.Sum(node => node.Components.Count);
            int activeNodes = allNodes.Count(node => node.IsActiveSelf);

            object? componentTypes = null;
            if (includeComponentTypes)
            {
                componentTypes = allNodes
                    .SelectMany(node => node.Components)
                    .GroupBy(comp => comp.GetType())
                    .Select(group => new
                    {
                        type = group.Key.FullName ?? group.Key.Name,
                        count = group.Count()
                    })
                    .OrderByDescending(item => item.count)
                    .ToArray();
            }

            return new
            {
                sceneId = scene.ID,
                sceneName = scene.Name,
                rootNodeCount = rootNodes.Count,
                nodeCount = allNodes.Length,
                activeNodeCount = activeNodes,
                componentCount = totalComponents,
                componentTypes
            };
        }

        private static object BuildComponentInfo(Type type, bool includeMembers)
        {
            var description = type.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var displayName = type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
            var requiresTransform = type.GetCustomAttribute<RequiresTransformAttribute>()?.Type;
            var requiredComponents = type.GetCustomAttributes<RequireComponentsAttribute>()
                .SelectMany(attr => attr.RequiredComponents)
                .Select(required => required.FullName ?? required.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            object[]? properties = null;
            object[]? fields = null;

            if (includeMembers)
            {
                properties = [.. type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(prop => prop.GetIndexParameters().Length == 0)
                    .Select(prop => new
                    {
                        name = prop.Name,
                        type = prop.PropertyType.FullName ?? prop.PropertyType.Name,
                        canRead = prop.CanRead,
                        canWrite = prop.CanWrite,
                        description = prop.GetCustomAttribute<DescriptionAttribute>()?.Description
                    })];

                fields = [.. type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Where(field => !field.IsInitOnly)
                    .Select(field => new
                    {
                        name = field.Name,
                        type = field.FieldType.FullName ?? field.FieldType.Name,
                        isReadOnly = field.IsInitOnly,
                        description = field.GetCustomAttribute<DescriptionAttribute>()?.Description
                    })];
            }

            return new
            {
                name = type.Name,
                fullName = type.FullName,
                @namespace = type.Namespace,
                assembly = type.Assembly.GetName().Name,
                description,
                displayName,
                requiresTransform = requiresTransform?.FullName ?? requiresTransform?.Name,
                requiredComponents,
                properties,
                fields
            };
        }

        private static float[] ToMatrixArray(Matrix4x4 matrix)
            =>
            [
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44
            ];

        private static object DecomposeMatrix(Matrix4x4 matrix)
        {
            Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation);
            return new
            {
                translation = new { x = translation.X, y = translation.Y, z = translation.Z },
                rotation = new { x = rotation.X, y = rotation.Y, z = rotation.Z, w = rotation.W },
                scale = new { x = scale.X, y = scale.Y, z = scale.Z }
            };
        }
    }
}
