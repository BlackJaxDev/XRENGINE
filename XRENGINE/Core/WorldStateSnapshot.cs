using System.Collections.Generic;
using System.Linq;
using System.Text;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XRWorld = XREngine.Scene.XRWorld;

namespace XREngine
{
    /// <summary>
    /// Captures the state of a world for later restoration when exiting play mode.
    /// Uses the cooked binary serializer, augmented with snapshot-specific filtering to keep payloads minimal.
    /// </summary>
    public class WorldStateSnapshot
    {
        /// <summary>
        /// The world this snapshot is for.
        /// </summary>
        public XRWorld SourceWorld { get; }

        /// <summary>
        /// Serialized binary scene data keyed by a stable scene identifier.
        /// </summary>
        public Dictionary<string, byte[]> SerializedScenes { get; }

        /// <summary>
        /// World settings at time of snapshot.
        /// </summary>
        public byte[]? SerializedSettings { get; }

        /// <summary>
        /// Serialized GameMode state.
        /// </summary>
        public byte[]? SerializedGameMode { get; }

        /// <summary>
        /// Runtime-only roots that already existed when the snapshot was captured.
        /// These roots are not owned by an <see cref="XRScene"/> and therefore are not
        /// present in <see cref="SerializedScenes"/>, but they are still part of the
        /// editor state that must survive a play-mode round trip.
        /// </summary>
        public IReadOnlySet<Guid> CapturedRuntimeOnlyRootIds { get; }

        /// <summary>
        /// Timestamp when the snapshot was created.
        /// </summary>
        public DateTime CaptureTime { get; }

        /// <summary>
        /// Whether the snapshot was captured successfully.
        /// </summary>
        public bool IsValid { get; }

        private WorldStateSnapshot(
            XRWorld sourceWorld,
            Dictionary<string, byte[]> serializedScenes,
            byte[]? serializedSettings,
            byte[]? serializedGameMode,
            HashSet<Guid> capturedRuntimeOnlyRootIds,
            bool isValid)
        {
            SourceWorld = sourceWorld;
            SerializedScenes = serializedScenes;
            SerializedSettings = serializedSettings;
            SerializedGameMode = serializedGameMode;
            CapturedRuntimeOnlyRootIds = capturedRuntimeOnlyRootIds;
            CaptureTime = DateTime.UtcNow;
            IsValid = isValid;
        }

        /// <summary>
        /// Creates a snapshot of the given world.
        /// </summary>
        public static WorldStateSnapshot? Capture(XRWorld? world)
        {
            if (world is null)
                return null;

            using var diagnosticScope = SnapshotDiagnostics.BeginScope("Capture", world);
            var serializedScenes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            byte[]? settingsData = null;
            byte[]? gameModeData = null;
            HashSet<Guid> capturedRuntimeOnlyRootIds = CaptureRuntimeOnlyRootIds(world);
            bool isValid = true;

            try
            {
                LogWorldSceneTree(world, "BeforeSerialize");
                SnapshotDiagnostics.LogWorldAssetSummary(world, "BeforeSerialize");

                // Serialize each scene
                foreach (var scene in world.Scenes)
                {
                    try
                    {
                        var sceneData = SerializeObject(scene);
                        if (sceneData is not null)
                        {
                            var key = GetSceneKey(scene);
                            serializedScenes[key] = sceneData;
                            SnapshotDiagnostics.LogScenePayload("Serialized", scene, sceneData.Length);
                        }
                        else
                        {
                            SnapshotDiagnostics.Warning($"Scene '{scene.Name ?? "<unnamed>"}' serialized to a null payload.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to serialize scene '{scene.Name}': {ex.Message}");
                        SnapshotDiagnostics.Warning($"Failed to serialize scene '{scene.Name ?? "<unnamed>"}': {ex}");
                        isValid = false;
                    }
                }

                // Serialize world settings
                try
                {
                    settingsData = SerializeObject(world.Settings);
                    SnapshotDiagnostics.Log($"Serialized world settings payloadBytes={settingsData?.Length ?? 0}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to serialize world settings: {ex.Message}");
                    SnapshotDiagnostics.Warning($"Failed to serialize world settings: {ex}");
                    isValid = false;
                }

                // Serialize game mode if present
                if (world.DefaultGameMode is not null)
                {
                    try
                    {
                        gameModeData = SerializeObject(world.DefaultGameMode);
                        SnapshotDiagnostics.Log($"Serialized default game mode type={world.DefaultGameMode.GetType().FullName} payloadBytes={gameModeData?.Length ?? 0}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to serialize game mode: {ex.Message}");
                        SnapshotDiagnostics.Warning($"Failed to serialize game mode '{world.DefaultGameMode.GetType().FullName}': {ex}");
                        // Game mode serialization failure is not critical
                    }
                }

                Debug.Out($"World state snapshot captured ({serializedScenes.Count} scenes, valid: {isValid})");

                return new WorldStateSnapshot(
                    world,
                    serializedScenes,
                    settingsData,
                    gameModeData,
                    capturedRuntimeOnlyRootIds,
                    isValid);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "Failed to capture world state snapshot");
                // Return a minimal snapshot that can still be used for reference
                return new WorldStateSnapshot(
                    world,
                    serializedScenes,
                    null,
                    null,
                    capturedRuntimeOnlyRootIds,
                    false);
            }
        }

        /// <summary>
        /// Creates a snapshot of a world instance.
        /// </summary>
        public static WorldStateSnapshot? Capture(XRWorldInstance? worldInstance)
        {
            return worldInstance?.TargetWorld is not null 
                ? Capture(worldInstance.TargetWorld) 
                : null;
        }

        /// <summary>
        /// Restores the world to the captured state.
        /// </summary>
        public bool Restore()
        {
            if (SourceWorld is null)
                return false;

            using var diagnosticScope = SnapshotDiagnostics.BeginScope("Restore", SourceWorld);
            if (!IsValid)
            {
                Debug.LogWarning("Attempting to restore from invalid snapshot - some state may not be restored");
                SnapshotDiagnostics.Warning("Attempting to restore from invalid snapshot - some state may not be restored.");
            }

            try
            {
                SnapshotDiagnostics.LogWorldAssetSummary(SourceWorld, "BeforeRestore");

                // End play on any active world instances first
                XRWorldInstance? runtimeInstance = null;
                if (XRWorldInstance.WorldInstances.TryGetValue(SourceWorld, out var instance))
                {
                    runtimeInstance = instance;
                    SnapshotDiagnostics.Log(
                        $"Runtime instance before restore: hash={instance.GetHashCode()} playState={instance.PlayState} roots={instance.RootNodes.Count} physics={instance.PhysicsEnabled}");
                    if (instance.PlayState == XRWorldInstance.EPlayState.Playing)
                        instance.EndPlay();
                }

                // Restore settings
                if (SerializedSettings is { Length: > 0 })
                {
                    try
                    {
                        var settings = DeserializeObject<WorldSettings>(SerializedSettings);
                        if (settings is not null)
                        {
                            SourceWorld.Settings = settings;
                            SnapshotDiagnostics.Log($"Restored world settings payloadBytes={SerializedSettings.Length}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to restore world settings: {ex.Message}");
                        SnapshotDiagnostics.Warning($"Failed to restore world settings: {ex}");
                    }
                }

                // Restore game mode
                if (SerializedGameMode is { Length: > 0 })
                {
                    try
                    {
                        var gameMode = DeserializeObject<GameMode>(SerializedGameMode);
                        if (gameMode is not null)
                        {
                            SourceWorld.DefaultGameMode = gameMode;
                            SnapshotDiagnostics.Log($"Restored default game mode type={gameMode.GetType().FullName} payloadBytes={SerializedGameMode.Length}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to restore game mode: {ex.Message}");
                        SnapshotDiagnostics.Warning($"Failed to restore game mode: {ex}");
                    }
                }

                // Restore scenes by rehydrating serialized data, recreating missing scenes,
                // and removing anything that was spawned while the snapshot was active.
                var processedSceneKeys = new HashSet<string>(StringComparer.Ordinal);

                foreach (var kvp in SerializedScenes)
                {
                    processedSceneKeys.Add(kvp.Key);
                    var scene = SourceWorld.Scenes.FirstOrDefault(s => GetSceneKey(s) == kvp.Key);

                    if (scene is not null)
                    {
                        try
                        {
                            SnapshotDiagnostics.Log($"Restoring existing scene key='{kvp.Key}' payloadBytes={kvp.Value.Length} currentRoots={scene.RootNodes?.Count ?? 0}");
                            RestoreScene(scene, kvp.Value, runtimeInstance);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"Failed to restore scene '{kvp.Key}': {ex.Message}");
                            SnapshotDiagnostics.Warning($"Failed to restore existing scene '{kvp.Key}': {ex}");
                        }
                        continue;
                    }

                    // Scene existed during capture but no longer present; recreate it
                    SnapshotDiagnostics.Log($"Recreating missing scene key='{kvp.Key}' payloadBytes={kvp.Value.Length}");
                    var recreatedScene = DeserializeObject<XRScene>(kvp.Value);
                    if (recreatedScene is null)
                    {
                        Debug.LogWarning($"Failed to deserialize scene '{kvp.Key}' while recreating snapshot state");
                        SnapshotDiagnostics.Warning($"Failed to deserialize scene '{kvp.Key}' while recreating snapshot state.");
                        continue;
                    }

                    SourceWorld.Scenes.Add(recreatedScene);
                    runtimeInstance?.LoadScene(recreatedScene);
                    SnapshotDiagnostics.Log($"Recreated scene '{recreatedScene.Name ?? "<unnamed>"}' roots={recreatedScene.RootNodes?.Count ?? 0}");
                }

                // Remove any scenes that were introduced after the snapshot
                var scenesToRemove = SourceWorld.Scenes
                    .Where(scene => !processedSceneKeys.Contains(GetSceneKey(scene)))
                    .ToList();

                foreach (var removedScene in scenesToRemove)
                {
                    SnapshotDiagnostics.Log($"Removing scene introduced after snapshot: '{removedScene.Name ?? "<unnamed>"}' roots={removedScene.RootNodes?.Count ?? 0}");
                    runtimeInstance?.UnloadScene(removedScene);
                    SourceWorld.Scenes.Remove(removedScene);
                }

                // IMPORTANT:
                // Gameplay code can spawn SceneNodes directly into XRWorldInstance.RootNodes without
                // attaching them to any XRScene (e.g. player pawns). Those roots are not tracked by
                // scene serialization and will survive snapshot restore unless explicitly removed.
                //
                // After restoring all scenes, destroy roots introduced after capture. Runtime-only
                // editor roots that existed at capture (for example the editor camera pawn) are
                // deliberately retained even though they are not owned by a serialized XRScene.
                if (runtimeInstance is not null)
                {
                    var expectedRoots = new HashSet<SceneNode>(System.Collections.Generic.ReferenceEqualityComparer.Instance);

                    foreach (var scene in SourceWorld.Scenes)
                    {
                        if (scene.RootNodes is null)
                            continue;

                        foreach (var root in scene.RootNodes)
                        {
                            if (root is null)
                                continue;
                            expectedRoots.Add(root);
                        }
                    }

                    int removedCount = 0;
                    foreach (var root in runtimeInstance.RootNodes.ToArray())
                    {
                        if (root is null)
                            continue;

                        if (expectedRoots.Contains(root))
                            continue;

                        if (CapturedRuntimeOnlyRootIds.Contains(root.ID))
                            continue;

                        removedCount++;
                        Debug.Out($"[SnapshotRestore] Destroying orphan root node '{root.Name ?? SceneNode.DefaultName}' (Hash={root.GetHashCode()})");
                        runtimeInstance.RootNodes.Remove(root);
                        root.Destroy(now: true);
                    }

                    if (removedCount > 0)
                        Debug.Out($"[SnapshotRestore] Removed {removedCount} orphan root node(s) not present in restored scenes.");
                }

                LogWorldSceneTree(SourceWorld, "AfterDeserialize");
                SnapshotDiagnostics.LogWorldAssetSummary(SourceWorld, "AfterDeserialize");
                if (runtimeInstance is not null)
                    SnapshotDiagnostics.LogWorldInstanceAssetSummary(runtimeInstance, "AfterDeserialize");

                Debug.Out($"World state restored from snapshot taken at {CaptureTime}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "Failed to restore world state from snapshot");
                SnapshotDiagnostics.Warning($"Failed to restore world state from snapshot: {ex}");
                return false;
            }
        }

        private static HashSet<Guid> CaptureRuntimeOnlyRootIds(XRWorld world)
        {
            var result = new HashSet<Guid>();
            if (!XRWorldInstance.WorldInstances.TryGetValue(world, out XRWorldInstance? runtimeInstance))
                return result;

            var sceneRoots = new HashSet<SceneNode>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (XRScene scene in world.Scenes)
            {
                if (scene.RootNodes is null)
                    continue;

                foreach (SceneNode? root in scene.RootNodes)
                    if (root is not null)
                        sceneRoots.Add(root);
            }

            foreach (SceneNode? root in runtimeInstance.RootNodes)
                if (root is not null && !sceneRoots.Contains(root))
                    result.Add(root.ID);

            SnapshotDiagnostics.Log($"Captured {result.Count} runtime-only root identity/identities.");
            return result;
        }

        private static byte[]? SerializeObject<T>(T obj)
        {
            if (obj is null)
                return null;

            try
            {
                return SnapshotBinarySerializer.Serialize(obj);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Snapshot serialization failed for {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static T? DeserializeObject<T>(byte[]? data) where T : class
        {
            if (data is null || data.Length == 0)
                return null;

            try
            {
                return SnapshotBinarySerializer.Deserialize<T>(data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Snapshot deserialization failed for {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static void RestoreScene(
            XRScene scene,
            byte[] data,
            XRWorldInstance? runtimeInstance)
        {
            SnapshotDiagnostics.Log($"RestoreScene begin: target='{scene.Name ?? "<unnamed>"}' payloadBytes={data.Length} runtimeInstance={(runtimeInstance is null ? "<null>" : runtimeInstance.GetHashCode().ToString())}");
            var restoredScene = DeserializeObject<XRScene>(data);
            if (restoredScene is null)
            {
                Debug.LogWarning($"Scene restoration skipped because scene data could not be deserialized.");
                SnapshotDiagnostics.Warning($"Scene restoration skipped for '{scene.Name ?? "<unnamed>"}' because scene data could not be deserialized.");
                return;
            }

            runtimeInstance?.UnloadScene(scene);

            scene.Name = restoredScene.Name;
            scene.IsVisible = restoredScene.IsVisible;
            scene.RootNodes = restoredScene.RootNodes ?? new List<SceneNode>();

            runtimeInstance?.LoadScene(scene);
            SnapshotDiagnostics.Log($"RestoreScene end: target='{scene.Name ?? "<unnamed>"}' visible={scene.IsVisible} roots={scene.RootNodes.Count}");
        }

        private static string GetSceneKey(XRScene scene)
            => scene.Name ?? scene.GetHashCode().ToString();

        private static void LogWorldSceneTree(XRWorld world, string phase)
        {
            Debug.Out($"[SnapshotDiag] Scene tree ({phase}). World={world.Name ?? "<unnamed>"} Scenes={world.Scenes.Count}");

            if (world.Scenes.Count == 0)
            {
                Debug.Out($"[SnapshotDiag] Scene tree ({phase}) has no scenes.");
                return;
            }

            for (int sceneIndex = 0; sceneIndex < world.Scenes.Count; sceneIndex++)
            {
                XRScene scene = world.Scenes[sceneIndex];
                Debug.Out(
                    $"[SnapshotDiag] Scene[{sceneIndex}] '{scene.Name ?? "<unnamed>"}' Visible={scene.IsVisible} Roots={scene.RootNodes?.Count ?? 0}");

                if (scene.RootNodes is null || scene.RootNodes.Count == 0)
                    continue;

                for (int rootIndex = 0; rootIndex < scene.RootNodes.Count; rootIndex++)
                {
                    SceneNode? root = scene.RootNodes[rootIndex];
                    if (root is null)
                    {
                        Debug.Out($"[SnapshotDiag]   Root[{rootIndex}] <null>");
                        continue;
                    }

                    LogSceneNodeRecursive(root, depth: 1);
                }
            }
        }

        private static void LogSceneNodeRecursive(SceneNode node, int depth)
        {
            string indent = new(' ', depth * 2);
            string transformType = node.Transform?.GetType().FullName ?? "<null>";
            string componentTypes = node.Components.Count == 0
                ? "<none>"
                : string.Join(", ", node.Components.Select(component => component?.GetType().FullName ?? "<null>"));
            int childCount = node.Transform?.Children.Count(child => child?.SceneNode is not null) ?? 0;

            var line = new StringBuilder();
            line.Append("[SnapshotDiag] ");
            line.Append(indent);
            line.Append("- Node='");
            line.Append(node.Name ?? SceneNode.DefaultName);
            line.Append("' Transform=");
            line.Append(transformType);
            line.Append(" Components=[");
            line.Append(componentTypes);
            line.Append("] Children=");
            line.Append(childCount);

            if (node.Transform is { } tfm)
            {
                var wt = tfm.WorldTranslation;
                var wr = tfm.WorldRotation;
                var rt = tfm.RenderTranslation;
                var rr = tfm.RenderRotation;
                line.Append($" WorldPos=<{wt.X:G5},{wt.Y:G5},{wt.Z:G5}>");
                line.Append($" WorldRot=<{wr.X:G4},{wr.Y:G4},{wr.Z:G4},{wr.W:G4}>");
                line.Append($" RenderPos=<{rt.X:G5},{rt.Y:G5},{rt.Z:G5}>");
                line.Append($" RenderRot=<{rr.X:G4},{rr.Y:G4},{rr.Z:G4},{rr.W:G4}>");
            }

            Debug.Out(line.ToString());

            foreach (SceneNode childNode in SceneNodePrefabUtility.EnumerateHierarchy(node).Skip(1).Where(child => ReferenceEquals(child.Parent, node)))
                LogSceneNodeRecursive(childNode, depth + 1);
        }
    }
}
