namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable descriptors for the built-in ModelingBridge import backends.
/// </summary>
public static class ModelImportBackendDescriptors
{
    public static ModelImportBackendDescriptor NativeGltf { get; } = new(
        ModelImportBackendIds.NativeGltf,
        implementationVersion: ModelImportBackendVersions.NativeGltf,
        supportedExtensions: [".gltf", ".glb"],
        priority: 200,
        ModelImportBackendCapabilities.NativeParser
            | ModelImportBackendCapabilities.StructuralDependencyDiscovery);

    public static ModelImportBackendDescriptor NativeFbx { get; } = new(
        ModelImportBackendIds.NativeFbx,
        implementationVersion: ModelImportBackendVersions.NativeFbx,
        supportedExtensions: [".fbx"],
        priority: 200,
        ModelImportBackendCapabilities.NativeParser
            | ModelImportBackendCapabilities.StableSourceEntityIds
            | ModelImportBackendCapabilities.StructuralDependencyDiscovery);

    public static ModelImportBackendDescriptor Assimp { get; } = new(
        ModelImportBackendIds.Assimp,
        implementationVersion: ModelImportBackendVersions.Assimp,
        supportedExtensions: [".fbx", ".glb", ".gltf", ".obj"],
        priority: 100,
        ModelImportBackendCapabilities.GeneralPurposeFallback
            | ModelImportBackendCapabilities.StructuralDependencyDiscovery);

    public static IReadOnlyList<ModelImportBackendDescriptor> BuiltIns { get; }
        = Array.AsReadOnly([NativeGltf, NativeFbx, Assimp]);
}
