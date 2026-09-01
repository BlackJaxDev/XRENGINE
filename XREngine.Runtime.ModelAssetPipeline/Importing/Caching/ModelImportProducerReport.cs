namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Complete normalized report from the backend that successfully produced a cold import.
/// </summary>
public sealed class ModelImportProducerReport
{
    public ModelImportProducerReport(
        ModelImportBackendSelection backendSelection,
        ModelImportProducerMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(backendSelection);
        ArgumentNullException.ThrowIfNull(metadata);

        BackendSelection = backendSelection;
        Metadata = metadata;
    }

    public ModelImportBackendSelection BackendSelection { get; }
    public ModelImportProducerMetadata Metadata { get; }
    public IReadOnlyList<ModelImportDependency> Dependencies => Metadata.Dependencies;
    public IReadOnlyList<ModelImportSourceEntity> SourceEntities => Metadata.SourceEntities;
    public IReadOnlyList<ModelImportReferenceKey> ReferenceKeys => Metadata.ReferenceKeys;
    public IReadOnlyList<string> Diagnostics => Metadata.Diagnostics;
    public float? ModelUnitsPerMeter => Metadata.ModelUnitsPerMeter;
}
