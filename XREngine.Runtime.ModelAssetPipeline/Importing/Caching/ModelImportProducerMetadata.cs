using System.Collections.ObjectModel;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Producer-owned normalized metadata captured while a parsed source document is available.
/// </summary>
public sealed class ModelImportProducerMetadata
{
    private readonly ReadOnlyCollection<ModelImportDependency> _dependencies;
    private readonly ReadOnlyCollection<ModelImportSourceEntity> _sourceEntities;
    private readonly ReadOnlyCollection<ModelImportReferenceKey> _referenceKeys;
    private readonly ReadOnlyCollection<string> _diagnostics;

    public ModelImportProducerMetadata(
        IEnumerable<ModelImportDependency>? dependencies,
        IEnumerable<ModelImportSourceEntity>? sourceEntities,
        IEnumerable<ModelImportReferenceKey>? referenceKeys,
        IEnumerable<string>? diagnostics = null,
        float? modelUnitsPerMeter = null)
    {
        ModelImportDependency[] normalizedDependencies = (dependencies ?? [])
            .OrderBy(static dependency => dependency.NormalizedPath, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.Kind)
            .ThenBy(static dependency => dependency.ProducerKey, StringComparer.Ordinal)
            .DistinctBy(static dependency => (
                dependency.NormalizedPath,
                dependency.Kind,
                dependency.ProducerKey),
                DependencyIdentityComparer.Instance)
            .ToArray();

        ModelImportSourceEntity[] normalizedEntities = (sourceEntities ?? [])
            .OrderBy(static entity => entity.Kind)
            .ThenBy(static entity => entity.Key, StringComparer.Ordinal)
            .DistinctBy(static entity => (entity.Kind, entity.Key))
            .ToArray();

        ModelImportReferenceKey[] normalizedReferences = (referenceKeys ?? [])
            .OrderBy(static reference => reference.Kind)
            .ThenBy(static reference => reference.Key, StringComparer.Ordinal)
            .DistinctBy(static reference => (reference.Kind, reference.Key))
            .ToArray();

        string[] normalizedDiagnostics = (diagnostics ?? [])
            .Where(static diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static diagnostic => diagnostic, StringComparer.Ordinal)
            .ToArray();

        _dependencies = Array.AsReadOnly(normalizedDependencies);
        _sourceEntities = Array.AsReadOnly(normalizedEntities);
        _referenceKeys = Array.AsReadOnly(normalizedReferences);
        _diagnostics = Array.AsReadOnly(normalizedDiagnostics);
        ModelUnitsPerMeter = modelUnitsPerMeter is float value
            && float.IsFinite(value)
            && value > 0.0f
                ? value
                : null;
    }

    public IReadOnlyList<ModelImportDependency> Dependencies => _dependencies;
    public IReadOnlyList<ModelImportSourceEntity> SourceEntities => _sourceEntities;
    public IReadOnlyList<ModelImportReferenceKey> ReferenceKeys => _referenceKeys;
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>
    /// Imported model-space units per meter when the producing format declares a
    /// reliable source-unit convention; otherwise <see langword="null"/>.
    /// </summary>
    public float? ModelUnitsPerMeter { get; }

    private sealed class DependencyIdentityComparer
        : IEqualityComparer<(string NormalizedPath, ModelImportDependencyKind Kind, string? ProducerKey)>
    {
        public static DependencyIdentityComparer Instance { get; } = new();

        public bool Equals(
            (string NormalizedPath, ModelImportDependencyKind Kind, string? ProducerKey) x,
            (string NormalizedPath, ModelImportDependencyKind Kind, string? ProducerKey) y)
            => x.Kind == y.Kind
                && string.Equals(x.NormalizedPath, y.NormalizedPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ProducerKey, y.ProducerKey, StringComparison.Ordinal);

        public int GetHashCode(
            (string NormalizedPath, ModelImportDependencyKind Kind, string? ProducerKey) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.NormalizedPath),
                value.Kind,
                value.ProducerKey is null ? 0 : StringComparer.Ordinal.GetHashCode(value.ProducerKey));
    }
}
