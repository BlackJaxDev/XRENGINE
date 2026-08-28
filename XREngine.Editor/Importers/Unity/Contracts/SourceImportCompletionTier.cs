namespace XREngine.Scene.Prefabs;

/// <summary>
/// Completeness reached by a Unity prefab conversion.
/// </summary>
public enum SourceImportCompletionTier
{
    None,
    VisualPrefab,
    /// <summary>
    /// The import produced native avatar behavior in addition to the visual prefab.
    /// This does not imply source-platform runtime parity.
    /// </summary>
    VisualAndAvatarBehavior,
}
