using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures the exact collect-domain package that authored one advanced
/// visibility family. Render-frame identity is intentionally absent: package
/// publication and frame-plan sealing use different timing domains.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityBackendPackageSnapshot(
    BackendReadyFramePackage Package,
    long PackageGeneration,
    BackendReadyFramePackageIdentity Identity,
    BackendReadyCanonicalFrameRecord CanonicalFrame,
    BackendReadyCanonicalScenePublication CanonicalScenePublication)
{
    internal bool IsValid
        => Package is not null &&
           PackageGeneration != 0L &&
           CanonicalScenePublication.Sequence != 0u;

    internal bool TryGetCurrent(out BackendReadyFramePackage package)
    {
        package = Package;
        return IsValid &&
               package.State == EBackendReadyFramePackageState.Published &&
               package.PackageGeneration == PackageGeneration &&
               package.Identity == Identity &&
               package.CanonicalFrame == CanonicalFrame &&
               package.CanonicalScenePublication == CanonicalScenePublication;
    }

    internal bool MatchesScenePublication(
        in AdvancedGpuScenePublication publication)
        => IsValid && publication.IsValid &&
           CanonicalScenePublication.DatabaseEpoch == publication.DatabaseEpoch &&
           CanonicalScenePublication.Sequence == publication.Sequence &&
           CanonicalScenePublication.FrameGeneration == publication.FrameGeneration &&
           CanonicalScenePublication.TopologyGeneration == publication.TopologyGeneration &&
           CanonicalScenePublication.ContentGeneration == publication.ContentGeneration &&
           CanonicalScenePublication.LookupGeneration == publication.LookupGeneration;

    internal static bool TryCapture(
        BackendReadyFramePackage? package,
        out VulkanAdvancedVisibilityBackendPackageSnapshot snapshot)
    {
        if (package is null ||
            package.State != EBackendReadyFramePackageState.Published ||
            package.PackageGeneration == 0L ||
            package.CanonicalScenePublication.Sequence == 0u)
        {
            snapshot = default;
            return false;
        }

        snapshot = new(
            package,
            package.PackageGeneration,
            package.Identity,
            package.CanonicalFrame,
            package.CanonicalScenePublication);
        return true;
    }
}
