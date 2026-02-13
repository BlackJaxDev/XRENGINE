using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using static XREngine.Rendering.XRWorldInstance;

namespace XREngine
{
    /// <summary>
    /// Play mode diagnostics and viewport rebinding for the engine.
    /// </summary>
    public static partial class Engine
    {
        #region Play Mode Diagnostics and Viewport Rebinding

        /// <summary>
        /// Handler for post-snapshot restore to rebind runtime rendering state.
        /// </summary>
        private static void OnPostSnapshotRestore_RebindRuntimeRendering(XRWorld? restoredWorld)
            => RebindRuntimeRendering(restoredWorld, "PostSnapshotRestore");

        /// <summary>
        /// Handler for post-enter-play to rebind runtime rendering state.
        /// </summary>
        private static void OnPostEnterPlay_RebindRuntimeRendering()
            => RebindRuntimeRendering(ResolveStartupWorldForRebind(), "PostEnterPlay");

        /// <summary>
        /// Logs diagnostic information about viewport rebinding during play mode transitions.
        /// </summary>
        private static void LogViewportRebindSummary(string phase, XRWorldInstance worldInstance)
        {
            string worldName = worldInstance.TargetWorld?.Name ?? "<unknown>";
            Debug.RenderingEvery(
                $"ViewportRebind.{phase}.Summary.{worldInstance.GetHashCode()}",
                TimeSpan.FromSeconds(0.5),
                "[ViewportDiag] {0}: PlayMode={1} World={2} Windows={3}",
                phase,
                PlayMode.State,
                worldName,
                _windows.Count);

            for (int i = 0; i < State.LocalPlayers.Length; i++)
            {
                var player = State.LocalPlayers[i];
                string playerType = player?.GetType().Name ?? "<null>";
                string pawnName = player?.ControlledPawn?.Name ?? "<null>";
                int playerHash = player?.GetHashCode() ?? 0;
                int viewportHash = player?.Viewport?.GetHashCode() ?? 0;
                bool hasCamera = player?.ControlledPawn?.GetCamera() is not null;

                Debug.RenderingEvery(
                    $"ViewportRebind.{phase}.Player.{worldInstance.GetHashCode()}.{i}",
                    TimeSpan.FromSeconds(0.5),
                    "[ViewportDiag] {0}: P{1} CtrlType={2} CtrlHash={3} ViewportHash={4} Pawn={5} PawnHasCamera={6}",
                    phase,
                    i + 1,
                    playerType,
                    playerHash,
                    viewportHash,
                    pawnName,
                    hasCamera);
            }
        }

        /// <summary>
        /// Resolves the world to use for viewport rebinding during play mode transitions.
        /// </summary>
        private static XRWorld? ResolveStartupWorldForRebind()
        {
            if (PlayMode.Configuration.StartupWorld is not null)
                return PlayMode.Configuration.StartupWorld;

            var firstWindowWorld = Windows.FirstOrDefault()?.TargetWorldInstance?.TargetWorld;
            if (firstWindowWorld is not null)
                return firstWindowWorld;

            var firstInstanceWorld = XRWorldInstance.WorldInstances.Values.FirstOrDefault()?.TargetWorld;
            return firstInstanceWorld;
        }

        /// <summary>
        /// Rebinds runtime rendering state for viewports and windows after play mode transitions.
        /// </summary>
        /// <remarks>
        /// This handles:
        /// <list type="bullet">
        ///   <item><description>Re-associating viewports with world instances</description></item>
        ///   <item><description>Repairing stale player-controller references</description></item>
        ///   <item><description>Rebinding camera components</description></item>
        ///   <item><description>Invalidating GPU resource caches that may have become stale</description></item>
        /// </list>
        /// </remarks>
        private static void RebindRuntimeRendering(XRWorld? world, string phase)
        {
            if (world is null)
                return;

            try
            {
                XRWorldInstance? worldInstance = XRWorldInstance.GetOrInitWorld(world);

                LogViewportRebindSummary(phase, worldInstance);

                DumpWorldRenderablesOncePerPhase(worldInstance, phase);
                DumpWorldHierarchyRootsOncePerPhase(worldInstance, phase);

                foreach (var window in _windows)
                {
                    Debug.RenderingEvery(
                        $"ViewportRebind.{phase}.Window.{window.GetHashCode()}",
                        TimeSpan.FromSeconds(0.5),
                        "[ViewportDiag] {0}: WindowHash={1} TargetWorldMatch={2} Viewports={3} PresentationMode={4}",
                        phase,
                        window.GetHashCode(),
                        ReferenceEquals(window.TargetWorldInstance, worldInstance),
                        window.Viewports.Count,
                        Engine.EditorPreferences.ViewportPresentationMode);

                    // Ensure the window is targeting the restored world instance.
                    if (!ReferenceEquals(window.TargetWorldInstance, worldInstance))
                        window.TargetWorldInstance = worldInstance;

                    // If a window ended up with zero viewports (runtime-only), log it loudly.
                    if (window.Viewports.Count == 0)
                    {
                        Debug.RenderingWarningEvery(
                            $"ViewportRebind.{phase}.WindowNoViewports.{window.GetHashCode()}",
                            TimeSpan.FromSeconds(0.5),
                            "[ViewportDiag] {0}: WindowHash={1} has 0 viewports. LocalPlayers={2}",
                            phase,
                            window.GetHashCode(),
                            State.LocalPlayers.Count(p => p is not null));
                    }

                    // Ensure viewports are linked to this window and have a world override.
                    foreach (var viewport in EnumerateActiveViewports(window))
                    {
                        viewport.Window = window;

                        // Repair stale player-controller references.
                        var associated = viewport.AssociatedPlayer;
                        if (associated is not null)
                        {
                            var current = State.GetLocalPlayer(associated.LocalPlayerIndex) ?? State.GetOrCreateLocalPlayer(associated.LocalPlayerIndex);
                            if (!ReferenceEquals(current, associated))
                            {
                                Debug.Out(
                                    "[{0}] Rebind: viewport {1} had stale AssociatedPlayer. OldHash={2} NewHash={3} Index={4}",
                                    phase,
                                    viewport.Index,
                                    associated.GetHashCode(),
                                    current.GetHashCode(),
                                    associated.LocalPlayerIndex);
                                viewport.AssociatedPlayer = current;
                            }
                        }
                        else
                        {
                            // Try to infer association from the player's Viewport pointer.
                            var inferred = State.LocalPlayers.FirstOrDefault(p => p is not null && ReferenceEquals(p.Viewport, viewport));
                            if (inferred is not null)
                            {
                                Debug.Out("[{0}] Rebind: inferred AssociatedPlayer for viewport {1} -> P{2}", phase, viewport.Index, (int)inferred.LocalPlayerIndex + 1);
                                viewport.AssociatedPlayer = inferred;
                            }
                        }

                        // LocalPlayerController.Viewport is runtime-only and can be lost across snapshot restore.
                        if (viewport.AssociatedPlayer is not null && !ReferenceEquals(viewport.AssociatedPlayer.Viewport, viewport))
                            viewport.AssociatedPlayer.Viewport = viewport;

                        // Player viewports should render the active/restored world instance.
                        if (viewport.AssociatedPlayer is not null && !ReferenceEquals(viewport.WorldInstanceOverride, worldInstance))
                            viewport.WorldInstanceOverride = worldInstance;

                        // Rebind camera from controlled pawn (may have changed across restore / BeginPlay).
                        var playerForRebind = viewport.AssociatedPlayer;
                        var playerPawnCamera = playerForRebind?.ControlledPawn?.GetCamera();
                        if (playerForRebind is not null && playerPawnCamera is not null)
                        {
                            playerForRebind.RefreshViewportCamera();
                            viewport.EnsureViewportBoundToCamera();
                        }
                        else
                        {
                            Debug.Out(
                                "[{0}] Rebind: skipping RefreshViewportCamera for VP[{1}] (player={2}) because ControlledPawn camera is null.",
                                phase,
                                viewport.Index,
                                playerForRebind?.LocalPlayerIndex.ToString() ?? "<null>");
                        }

                        var p = viewport.AssociatedPlayer;
                        var pawn = p?.ControlledPawn;
                        var pawnCam = pawn?.GetCamera();
                        var cam = viewport.ActiveCamera;
                        int camViewportCount = cam?.Viewports.Count ?? 0;
                        Debug.RenderingEvery(
                            $"ViewportRebind.{phase}.VP.{window.GetHashCode()}.{viewport.Index}",
                            TimeSpan.FromSeconds(0.5),
                            "[ViewportDiag] {0}: Win={1} VP[{2}] AssocP={3} PHash={4} P.ViewportMatch={5} Pawn={6} PawnCamNull={7} VP.CameraComponentNull={8} ActiveCameraNull={9} Camera.Viewports={10} WorldNull={11}",
                            phase,
                            window.GetHashCode(),
                            viewport.Index,
                            p is null ? "<null>" : $"P{(int)p.LocalPlayerIndex + 1}",
                            p?.GetHashCode() ?? 0,
                            p is not null && ReferenceEquals(p.Viewport, viewport),
                            pawn?.Name ?? "<null>",
                            pawnCam is null,
                            viewport.CameraComponent is null,
                            cam is null,
                            camViewportCount,
                            viewport.World is null);

                        // Snapshot restore can invalidate cached GPU resources.
                        if (phase is "PostSnapshotRestore" or "PostEnterPlay")
                        {
                            var capturedViewport = viewport;
                            EnqueueSwapTask(() =>
                            {
                                try
                                {
                                    capturedViewport.RenderPipelineInstance.DestroyCache();
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogException(ex, $"[{phase}] Failed to destroy render cache for viewport {capturedViewport.Index}.");
                                }
                            });
                        }

                        // Only warn when we *expected* a camera/world to exist.
                        if (viewport.ActiveCamera is null && viewport.AssociatedPlayer?.ControlledPawn?.GetCamera() is not null)
                            Debug.LogWarning($"[{phase}] Viewport {viewport.Index} has no ActiveCamera (player={viewport.AssociatedPlayer?.LocalPlayerIndex}).");
                        if (viewport.World is null && viewport.AssociatedPlayer?.ControlledPawn?.GetCamera() is not null)
                            Debug.LogWarning($"[{phase}] Viewport {viewport.Index} has no World (player={viewport.AssociatedPlayer?.LocalPlayerIndex}).");
                    }

                    // If viewports exist but no players are associated, keep at least player one wired.
                    if (window.Viewports.Count > 0 && window.Viewports.All(vp => vp.AssociatedPlayer is null))
                    {
                        var mainPlayer = State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One);
                        window.RegisterController(mainPlayer, autoSizeAllViewports: false);
                        mainPlayer.RefreshViewportCamera();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed runtime rendering rebind during {phase}");
            }
        }

        /// <summary>
        /// Diagnostic method to dump renderable information for a world instance.
        /// Used for debugging rendering issues during play mode transitions.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only reflection for diagnostics; safe in editor builds.")]
        private static void DumpWorldRenderablesOncePerPhase(XRWorldInstance worldInstance, string phase)
        {
            Debug.RenderingEvery(
                $"RenderDump.World.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[RenderDiag] World dump ({0}). World={1} Roots={2} VisualScene={3} TrackedRenderables={4}",
                phase,
                worldInstance.TargetWorld?.Name ?? "<unknown>",
                worldInstance.RootNodes.Count,
                worldInstance.VisualScene?.GetType().Name ?? "<null>",
                worldInstance.VisualScene?.Renderables?.Count ?? -1);

            int nodeCount = 0;
            int renderableComponentCount = 0;
            int meshCount = 0;
            int meshRenderCommandCount = 0;
            int iRenderableCount = 0;
            int iRenderableWithWorldCount = 0;
            int totalRenderInfoCount = 0;
            var componentTypes = new List<string>();

            static bool TryGetRenderedObjects(object? component, out System.Collections.IEnumerable? renderedObjects)
            {
                renderedObjects = null;
                if (component is null)
                    return false;

                var renderedObjectsProperty = component.GetType().GetProperty(
                    "RenderedObjects",
                    BindingFlags.Public | BindingFlags.Instance);

                if (renderedObjectsProperty is null)
                    return false;

                renderedObjects = renderedObjectsProperty.GetValue(component) as System.Collections.IEnumerable;
                return true;
            }

            foreach (var root in worldInstance.RootNodes)
            {
                foreach (var node in SceneNodePrefabUtility.EnumerateHierarchy(root))
                {
                    nodeCount++;

                    foreach (var component in node.Components)
                    {
                        componentTypes.Add(component.GetType().Name);

                        if (component is Components.Scene.Mesh.RenderableComponent rc)
                        {
                            renderableComponentCount++;
                            meshCount += rc.Meshes.Count;
                            foreach (var mesh in rc.Meshes)
                                meshRenderCommandCount += mesh.RenderInfo.RenderCommands.Count;
                        }

                        if (TryGetRenderedObjects(component, out var renderedObjects))
                        {
                            iRenderableCount++;
                            if (renderedObjects is not null)
                            {
                                foreach (var ri in renderedObjects)
                                {
                                    totalRenderInfoCount++;

                                    if (ri is null)
                                        continue;

                                    var worldInstanceProperty = ri.GetType().GetProperty(
                                        "WorldInstance",
                                        BindingFlags.Public | BindingFlags.Instance);

                                    if (worldInstanceProperty?.GetValue(ri) is not null)
                                        iRenderableWithWorldCount++;
                                }
                            }
                        }
                    }
                }
            }

            Debug.RenderingEvery(
                $"RenderDump.WorldCounts.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[RenderDiag] World dump counts ({0}). Nodes={1} RenderableComponents={2} Meshes={3} MeshRenderCommands={4} IRenderables={5} WithWorld={6}/{7}",
                phase,
                nodeCount,
                renderableComponentCount,
                meshCount,
                meshRenderCommandCount,
                iRenderableCount,
                iRenderableWithWorldCount,
                totalRenderInfoCount);

            Debug.RenderingEvery(
                $"RenderDump.ComponentTypes.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[RenderDiag] Component types ({0}): {1}",
                phase,
                string.Join(", ", componentTypes));
        }

        /// <summary>
        /// Diagnostic method to dump hierarchy roots for a world instance.
        /// Used for debugging scene graph issues during play mode transitions.
        /// </summary>
        private static void DumpWorldHierarchyRootsOncePerPhase(XRWorldInstance worldInstance, string phase)
        {
            Debug.RenderingEvery(
                $"SnapshotHierarchy.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[SnapshotDiag] Hierarchy roots ({0}). World={1} RootNodes={2}",
                phase,
                worldInstance.TargetWorld?.Name ?? "<unknown>",
                worldInstance.RootNodes.Count);

            int totalReachableNodes = 0;
            var visited = new HashSet<SceneNode>();

            foreach (var root in worldInstance.RootNodes)
            {
                if (root is null)
                    continue;

                int childCount = 0;
                foreach (var childTfm in root.Transform.Children)
                    if (childTfm?.SceneNode is not null)
                        childCount++;

                Debug.RenderingEvery(
                    $"SnapshotHierarchy.Root.{phase}.{worldInstance.GetHashCode()}.{root.GetHashCode()}",
                    TimeSpan.FromDays(1),
                    "[SnapshotDiag] Root '{0}' children={1} world={2}",
                    root.Name ?? "<unnamed>",
                    childCount,
                    root.World is null ? "<null>" : "set");

                totalReachableNodes += CountReachableNodes(root, visited);
            }

            Debug.RenderingEvery(
                $"SnapshotHierarchy.Totals.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[SnapshotDiag] Reachable nodes via Transform.Children ({0}) = {1}",
                phase,
                totalReachableNodes);

            static int CountReachableNodes(SceneNode node, HashSet<SceneNode> visited)
            {
                if (node is null)
                    return 0;
                if (!visited.Add(node))
                    return 0;

                int count = 1;
                foreach (var childTfm in node.Transform.Children)
                {
                    if (childTfm?.SceneNode is SceneNode childNode)
                        count += CountReachableNodes(childNode, visited);
                }
                return count;
            }
        }

        #endregion
    }
}
