namespace XREngine.Scene.Prefabs;

/// <summary>
/// Native hierarchy and retained import manifest produced from a Unity prefab.
/// </summary>
public sealed class UnityPrefabConversionResult
{
    public SceneNode? RootNode { get; set; }
    public UnityPrefabImportManifest? Manifest { get; set; }
}
