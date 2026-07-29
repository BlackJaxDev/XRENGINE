namespace XREngine.Scene.Prefabs;

/// <summary>
/// Determines whether a missing Unity dependency blocks visual prefab import.
/// </summary>
public enum UnityImportDependencyKind
{
    RequiredVisual,
    OptionalVisual,
    Animation,
    AvatarBehavior,
    EditorOnly,
    Unsupported,
}
