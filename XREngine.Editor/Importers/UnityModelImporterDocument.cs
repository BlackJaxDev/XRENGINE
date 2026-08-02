namespace XREngine.Scene.Importers;

/// <summary>
/// Relevant, non-executable settings from a Unity ModelImporter .meta document.
/// </summary>
public sealed class UnityModelImporterDocument
{
    public string SourceMetaPath { get; init; } = string.Empty;
    public int FileIdsGeneration { get; init; } = 2;
    public bool ImportBlendShapes { get; init; } = true;
    public bool ImportAnimation { get; init; } = true;
    public int AnimationType { get; init; }
    public float GlobalScale { get; init; } = 1.0f;
    public bool UseFileScale { get; init; } = true;
    public bool UseFileUnits { get; init; } = true;
    public bool BakeAxisConversion { get; init; }
    public bool PreserveHierarchy { get; init; }
    public bool SortHierarchyByName { get; init; }
    public int MaterialImportMode { get; init; }
    public int MaterialName { get; init; }
    public int MaterialSearch { get; init; }
    public int MaterialLocation { get; init; }
    public IReadOnlyList<UnityModelSkeletonTransform> SkeletonTransforms { get; init; } = [];
    public IReadOnlyList<UnityExternalMaterialRemap> ExternalMaterialRemaps { get; init; } = [];
}
