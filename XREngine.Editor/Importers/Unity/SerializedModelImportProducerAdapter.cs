using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor.Importers.SerializedAssets;

/// <summary>
/// Normalizes Editor-owned Unity prefab conversion evidence into the shared model-import contract.
/// </summary>
internal static class SerializedModelImportProducerAdapter
{
    public const string StableBackendId = "xrengine.unity-prefab";
    public const uint ImplementationVersion = ModelImportBackendVersions.SerializedPrefab;

    public static ModelImportBackendDescriptor Descriptor { get; } = new(
        StableBackendId,
        ImplementationVersion,
        supportedExtensions: [".prefab"],
        priority: 300,
        ModelImportBackendCapabilities.StableSourceEntityIds
            | ModelImportBackendCapabilities.StructuralDependencyDiscovery);

    public static ModelImportProducerReport CreateReport(
        string sourceFilePath,
        ModelImportOptions importOptions,
        SerializedPrefabImportManifest? manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentNullException.ThrowIfNull(importOptions);

        ModelImportBackendRegistry registry = new();
        using IDisposable registration = registry.Install(Descriptor);
        ModelImportBackendResolution resolution = ModelImportBackendResolver.Resolve(
            sourceFilePath,
            importOptions,
            registry: registry);
        ModelImportBackendDescriptor producer = resolution.Candidates.Single(candidate =>
            candidate.StableId.Equals(StableBackendId, StringComparison.Ordinal));
        ModelImportBackendSelection selection = new(resolution, producer);

        List<ModelImportDependency> dependencies =
        [
            ModelImportDependency.FromFile(
                sourceFilePath,
                ModelImportDependencyKind.EntrySource,
                isRequired: true,
                producerKey: "unity:entry"),
        ];
        List<ModelImportSourceEntity> entities = [];
        List<ModelImportReferenceKey> references = [];
        List<string> diagnostics = [];

        if (manifest is not null)
        {
            foreach (SourceImportDependencyManifestEntry dependency in manifest.Dependencies)
            {
                ModelImportDependencyKind kind = MapDependencyKind(dependency);
                bool isRequired = dependency.Kind == SourceImportDependencyKind.RequiredVisual;
                string normalizedPath = ResolveManifestDependencyPath(manifest, dependency);
                string? stableKey = CreateStableKey(dependency);

                dependencies.Add(new ModelImportDependency(
                    normalizedPath,
                    kind,
                    isRequired,
                    Math.Max(0L, dependency.Length),
                    Math.Max(0L, dependency.LastWriteTimeUtcTicks),
                    dependency.Sha256,
                    stableKey));

                if (stableKey is not null)
                {
                    entities.Add(new ModelImportSourceEntity(
                        stableKey,
                        MapEntityKind(dependency),
                        dependency.NormalizedPath,
                        isStable: true));
                }

                ModelImportReferenceKind? referenceKind = MapReferenceKind(dependency);
                if (referenceKind is ModelImportReferenceKind mappedKind)
                {
                    references.Add(new ModelImportReferenceKey(
                        mappedKind,
                        stableKey ?? dependency.NormalizedPath));
                }
            }

            foreach (SourceImportDiagnostic diagnostic in manifest.Diagnostics)
                diagnostics.Add(diagnostic.ToString());
        }

        ModelImportProducerMetadata metadata = new(
            dependencies,
            entities,
            references,
            diagnostics);
        return new ModelImportProducerReport(selection, metadata);
    }

    private static string ResolveManifestDependencyPath(
        SerializedPrefabImportManifest manifest,
        SourceImportDependencyManifestEntry dependency)
    {
        if (dependency.NormalizedPath.StartsWith("missing://", StringComparison.OrdinalIgnoreCase))
            return dependency.NormalizedPath;

        string? resolvedPath = manifest.ResolveDependencySourcePath(dependency.NormalizedPath);
        return string.IsNullOrWhiteSpace(resolvedPath)
            ? dependency.NormalizedPath.Replace('\\', '/')
            : ModelImportPathNormalizer.NormalizeAbsolutePath(resolvedPath);
    }

    private static string? CreateStableKey(SourceImportDependencyManifestEntry dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency.SourceGuid)
            || dependency.LocalFileId is not long localFileId)
            return null;

        return $"unity:{dependency.SourceGuid.ToLowerInvariant()}:{localFileId}";
    }

    private static ModelImportDependencyKind MapDependencyKind(
        SourceImportDependencyManifestEntry dependency)
    {
        if (dependency.Kind == SourceImportDependencyKind.Animation)
            return ModelImportDependencyKind.ReferencedAnimation;
        if (IsTexturePath(dependency.NormalizedPath))
            return ModelImportDependencyKind.ReferencedTexture;

        return dependency.Kind == SourceImportDependencyKind.RequiredVisual
            ? ModelImportDependencyKind.Structural
            : ModelImportDependencyKind.ReferencedAsset;
    }

    private static ModelImportEntityKind MapEntityKind(
        SourceImportDependencyManifestEntry dependency)
    {
        if (dependency.Kind == SourceImportDependencyKind.Animation)
            return ModelImportEntityKind.Animation;
        if (IsTexturePath(dependency.NormalizedPath))
            return ModelImportEntityKind.Texture;
        if (string.Equals(Path.GetExtension(dependency.NormalizedPath), ".mat", StringComparison.OrdinalIgnoreCase))
            return ModelImportEntityKind.Material;

        return ModelImportEntityKind.Other;
    }

    private static ModelImportReferenceKind? MapReferenceKind(
        SourceImportDependencyManifestEntry dependency)
    {
        if (dependency.Kind == SourceImportDependencyKind.Animation)
            return ModelImportReferenceKind.Animation;
        if (IsTexturePath(dependency.NormalizedPath))
            return ModelImportReferenceKind.Texture;
        if (string.Equals(Path.GetExtension(dependency.NormalizedPath), ".mat", StringComparison.OrdinalIgnoreCase))
            return ModelImportReferenceKind.Material;

        return null;
    }

    private static bool IsTexturePath(string path)
        => Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".gif"
                or ".tif" or ".tiff" or ".psd" or ".exr" or ".hdr" or ".dds";
}
