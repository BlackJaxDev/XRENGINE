using System.Collections.Generic;
using System.Linq;
using XREngine.Rendering;
using XREngine.Scene;

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
            bool isValid)
        {
            SourceWorld = sourceWorld;
            SerializedScenes = serializedScenes;
            SerializedSettings = serializedSettings;
            SerializedGameMode = serializedGameMode;
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

            var serializedScenes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            byte[]? settingsData = null;
            byte[]? gameModeData = null;
            bool isValid = true;

            try
            {
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
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to serialize scene '{scene.Name}': {ex.Message}");
                        isValid = false;
                    }
                }

                // Serialize world settings
                try
                {
                    settingsData = SerializeObject(world.Settings);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to serialize world settings: {ex.Message}");
                    isValid = false;
                }

                // Serialize game mode if present
                if (world.DefaultGameMode is not null)
                {
                    try
                    {
                        gameModeData = SerializeObject(world.DefaultGameMode);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to serialize game mode: {ex.Message}");
                        // Game mode serialization failure is not critical
                    }
                }

                Debug.Out($"World state snapshot captured ({serializedScenes.Count} scenes, valid: {isValid})");

                return new WorldStateSnapshot(
                    world,
                    serializedScenes,
                    settingsData,
                    gameModeData,
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

            if (!IsValid)
            {
                Debug.LogWarning("Attempting to restore from invalid snapshot - some state may not be restored");
            }

            try
            {
                // End play on any active world instances first
                XRWorldInstance? runtimeInstance = null;
                if (XRWorldInstance.WorldInstances.TryGetValue(SourceWorld, out var instance))
                {
                    runtimeInstance = instance;
                    if (instance.IsPlaying)
                    {
                        instance.EndPlay();
                    }
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
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to restore world settings: {ex.Message}");
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
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to restore game mode: {ex.Message}");
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
                            RestoreScene(scene, kvp.Value, runtimeInstance);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"Failed to restore scene '{kvp.Key}': {ex.Message}");
                        }
                        continue;
                    }

                    // Scene existed during capture but no longer present; recreate it
                    var recreatedScene = DeserializeObject<XRScene>(kvp.Value);
                    if (recreatedScene is null)
                    {
                        Debug.LogWarning($"Failed to deserialize scene '{kvp.Key}' while recreating snapshot state");
                        continue;
                    }

                    SourceWorld.Scenes.Add(recreatedScene);
                    runtimeInstance?.LoadScene(recreatedScene);
                }

                // Remove any scenes that were introduced after the snapshot
                var scenesToRemove = SourceWorld.Scenes
                    .Where(scene => !processedSceneKeys.Contains(GetSceneKey(scene)))
                    .ToList();

                foreach (var removedScene in scenesToRemove)
                {
                    runtimeInstance?.UnloadScene(removedScene);
                    SourceWorld.Scenes.Remove(removedScene);
                }

                Debug.Out($"World state restored from snapshot taken at {CaptureTime}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "Failed to restore world state from snapshot");
                return false;
            }
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
            var restoredScene = DeserializeObject<XRScene>(data);
            if (restoredScene is null)
            {
                Debug.LogWarning($"Scene restoration skipped because scene data could not be deserialized.");
                return;
            }

            runtimeInstance?.UnloadScene(scene);

            scene.Name = restoredScene.Name;
            scene.IsVisible = restoredScene.IsVisible;
            scene.RootNodes = restoredScene.RootNodes ?? new List<SceneNode>();

            runtimeInstance?.LoadScene(scene);
        }

        private static string GetSceneKey(XRScene scene)
            => scene.Name ?? scene.GetHashCode().ToString();
    }
}
