namespace XREngine.Scene.Prefabs;

/// <summary>
/// Native hierarchy and retained import manifest produced from a Unity prefab.
/// </summary>
public sealed class SerializedPrefabConversionResult
{
    public SceneNode? RootNode { get; set; }
    public SerializedPrefabImportManifest? Manifest { get; set; }

    /// <summary>
    /// True when the Unity importer cooked the finalized prefab hierarchy before
    /// returning it. Consumers use this explicit handoff rather than attempting
    /// to infer freshness from payload values or hashes.
    /// </summary>
    public bool MeshletCookingCompleted { get; set; }
}
