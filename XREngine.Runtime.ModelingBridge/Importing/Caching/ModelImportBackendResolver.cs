namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Resolves model import options and host preferences into an ordered backend snapshot.
/// </summary>
public static class ModelImportBackendResolver
{
    public const uint PolicyVersion = 1;

    public static ModelImportBackendResolution Resolve(
        string sourcePath,
        ModelImportOptions importOptions,
        FbxImportBackend preferredFbxBackend = FbxImportBackend.Auto,
        GltfImportBackend preferredGltfBackend = GltfImportBackend.Auto,
        ModelImportBackendRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(importOptions);

        string sourceExtension = NormalizeSourceExtension(Path.GetExtension(sourcePath));
        ModelImportBackendPolicy requestedPolicy;
        ModelImportBackendPolicy hostPreference;

        if (sourceExtension.Equals(".fbx", StringComparison.Ordinal))
        {
            requestedPolicy = Normalize(importOptions.FbxBackend);
            hostPreference = Normalize(preferredFbxBackend);
        }
        else if (sourceExtension.Equals(".gltf", StringComparison.Ordinal)
            || sourceExtension.Equals(".glb", StringComparison.Ordinal))
        {
            requestedPolicy = Normalize(importOptions.GltfBackend);
            hostPreference = Normalize(preferredGltfBackend);
        }
        else
        {
            requestedPolicy = ModelImportBackendPolicy.Auto;
            hostPreference = ModelImportBackendPolicy.Auto;
        }

        IReadOnlyList<ModelImportBackendDescriptor> descriptorSnapshot
            = (registry ?? ModelImportBackendRegistry.Default).GetSnapshot();
        IEnumerable<ModelImportBackendDescriptor> eligible = descriptorSnapshot
            .Where(descriptor => descriptor.SupportsExtension(sourceExtension));

        IEnumerable<ModelImportBackendDescriptor> candidates = requestedPolicy switch
        {
            ModelImportBackendPolicy.Native => eligible.Where(static descriptor =>
                (descriptor.Capabilities & ModelImportBackendCapabilities.NativeParser) != 0),
            ModelImportBackendPolicy.Assimp => eligible.Where(static descriptor =>
                descriptor.StableId.Equals(ModelImportBackendIds.Assimp, StringComparison.Ordinal)),
            ModelImportBackendPolicy.Auto when hostPreference == ModelImportBackendPolicy.Assimp => eligible.Where(static descriptor =>
                descriptor.StableId.Equals(ModelImportBackendIds.Assimp, StringComparison.Ordinal)),
            ModelImportBackendPolicy.Auto => eligible,
            _ => throw new ArgumentOutOfRangeException(nameof(importOptions), requestedPolicy, "Unknown model import backend policy."),
        };

        return new ModelImportBackendResolution(
            PolicyVersion,
            sourceExtension,
            requestedPolicy,
            hostPreference,
            candidates);
    }

    private static string NormalizeSourceExtension(string extension)
        => string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : ModelImportBackendDescriptor.NormalizeExtension(extension);

    private static ModelImportBackendPolicy Normalize(FbxImportBackend backend)
        => backend switch
        {
            FbxImportBackend.Auto => ModelImportBackendPolicy.Auto,
            FbxImportBackend.Native => ModelImportBackendPolicy.Native,
            FbxImportBackend.Assimp => ModelImportBackendPolicy.Assimp,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown FBX import backend policy."),
        };

    private static ModelImportBackendPolicy Normalize(GltfImportBackend backend)
        => backend switch
        {
            GltfImportBackend.Auto => ModelImportBackendPolicy.Auto,
            GltfImportBackend.Native => ModelImportBackendPolicy.Native,
            GltfImportBackend.Assimp => ModelImportBackendPolicy.Assimp,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown glTF import backend policy."),
        };
}
