using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frame-package-owned global tables copied into prepared-frame storage. This
/// captures numeric records only; it never retains the mutable package.
/// </summary>
internal readonly record struct VulkanPreparedFrameGlobalResourceSnapshot(
    BackendReadyCanonicalFrameRecord Frame,
    BackendReadyCanonicalScenePublication Scene,
    AdvancedSharedGpuSceneDatabase? Database,
    AdvancedGpuScenePublicationReference Publication,
    ulong PackageGeneration,
    int ViewCount,
    int PassCount,
    int DiagnosticCount)
{
    internal bool Matches(in AdvancedGpuScenePublicationReference publication)
        => PackageGeneration != 0u &&
           Database is not null &&
           Publication == publication &&
           Scene.DatabaseEpoch == publication.DatabaseEpoch &&
           Scene.Sequence == publication.Sequence;
}
