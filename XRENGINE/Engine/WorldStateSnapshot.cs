using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine
{
    /// <summary>
    /// Captures the state of a world for later restoration when exiting play mode.
    /// Uses the engine's YAML serialization system which handles complex types.
    /// </summary>
    public class WorldStateSnapshot
    {
        /// <summary>
        /// The world this snapshot is for.
        /// </summary>
        public XRWorld SourceWorld { get; }

        /// <summary>
        /// Serialized scene data for each scene in the world.
        /// </summary>
        public Dictionary<string, string> SerializedScenes { get; }

        /// <summary>
        /// World settings at time of snapshot.
        /// </summary>
        public string? SerializedSettings { get; }

        /// <summary>
        /// Serialized GameMode state.
        /// </summary>
        public string? SerializedGameMode { get; }

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
            Dictionary<string, string> serializedScenes,
            string? serializedSettings,
            string? serializedGameMode,
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

            var serializedScenes = new Dictionary<string, string>();
            string? settingsData = null;
            string? gameModeData = null;
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
                            var key = scene.Name ?? scene.GetHashCode().ToString();
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
                if (XRWorldInstance.WorldInstances.TryGetValue(SourceWorld, out var instance))
                {
                    if (instance.IsPlaying)
                    {
                        instance.EndPlay();
                    }
                }

                // Restore settings
                if (!string.IsNullOrEmpty(SerializedSettings))
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
                if (!string.IsNullOrEmpty(SerializedGameMode))
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

                // Restore scenes
                // Note: Full scene restoration is complex - this is a simplified version
                // A complete implementation would need to handle:
                // - Removing nodes added during play
                // - Restoring nodes removed during play
                // - Restoring all property values
                foreach (var kvp in SerializedScenes)
                {
                    var scene = SourceWorld.Scenes.FirstOrDefault(s => 
                        (s.Name ?? s.GetHashCode().ToString()) == kvp.Key);
                    
                    if (scene is not null)
                    {
                        try
                        {
                            RestoreScene(scene, kvp.Value);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"Failed to restore scene '{kvp.Key}': {ex.Message}");
                        }
                    }
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

        private static string? SerializeObject<T>(T obj)
        {
            if (obj is null)
                return null;

            try
            {
                // Use the engine's YAML serializer which handles complex types
                return AssetManager.Serializer.Serialize(obj);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"YAML serialization failed for {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static T? DeserializeObject<T>(string? data) where T : class
        {
            if (string.IsNullOrEmpty(data))
                return null;

            try
            {
                // Use the engine's YAML deserializer
                return AssetManager.Deserializer.Deserialize<T>(data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"YAML deserialization failed for {typeof(T).Name}: {ex.Message}");
                return null;
            }
        }

        private static void RestoreScene(XRScene scene, string data)
        {
            // TODO: Implement proper scene restoration
            // This would involve deserializing the scene data and applying it
            // For now, just log that restoration was attempted
            Debug.Out($"Scene restoration attempted for '{scene.Name}' (full implementation pending)");
        }
    }
}
