namespace XREngine.Scene.Prefabs;

/// <summary>
/// Subsystem that produced a Unity prefab import diagnostic.
/// </summary>
public enum SourceImportDiagnosticCategory
{
    ProjectDetection,
    GuidResolution,
    DependencyParsing,
    ModelIdentity,
    PrefabOverride,
    MaterialDowngrade,
    TextureImport,
    AvatarComponent,
    OptionalUnsupported,
    Reimport,
}
