using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Commands;

public sealed partial class RenderCommandCollection
{
    private RenderResourceGeneration? _preparedImportedResourceGeneration;

    private void ClearPreparedImportedResourceGeneration()
        => SetField(ref _preparedImportedResourceGeneration, null, publishNotifications: false);

    private void CapturePreparedImportedResourceGeneration(XRRenderPipelineInstance? owner)
    {
        RenderResourceGeneration? generation = owner?.ActiveGeneration;
        BackendReadyFramePackageIdentity identity = _updatingBackendReadyPackage.Identity;
        bool matches = owner is not null &&
            generation is not null &&
            identity.CollectGeneration != BackendReadyFramePackageIdentity.UnspecifiedCollectGeneration &&
            MatchesPreparedResourceGeneration(owner, generation, in identity);
        SetField(ref _preparedImportedResourceGeneration, matches ? generation : null, publishNotifications: false);
    }

    /// <summary>
    /// Commits imports at the swap handoff after the previous CPU render consumer ends.
    /// Collection cannot mutate the active registry while that consumer uses it.
    /// </summary>
    private void FinalizePreparedImportedResourcesNoLock()
    {
        BackendReadyFramePackage package = _updatingBackendReadyPackage;
        RenderResourceGeneration? generation = _preparedImportedResourceGeneration;
        if (package.State != EBackendReadyFramePackageState.Prepared ||
            generation is null ||
            _ownerPipeline is not XRRenderPipelineInstance owner)
            return;

        BackendReadyFramePackageIdentity expected = package.Identity;
        if (expected.CollectGeneration == BackendReadyFramePackageIdentity.UnspecifiedCollectGeneration ||
            expected != _updatingBackendReadyIdentity ||
            !MatchesPreparedResourceGeneration(owner, generation, in expected))
            return;

        long packageGeneration = package.PackageGeneration;
        long sourceRevision = package.SourceRevision;
        long updatingRevision = _updatingRevision;
        owner.PublishStagedImportedResources(generation, out int descriptorRevision, out int instanceRevision);

        // Notifications can replace a generation. Only this commit may seal the package.
        if (!ReferenceEquals(owner.ActiveGeneration, generation) ||
            generation.Status != RenderResourceGenerationStatus.Active ||
            owner.ResourceGeneration != expected.ResourceGeneration ||
            generation.Registry.DescriptorRevision != descriptorRevision ||
            generation.Registry.InstanceRevision != instanceRevision ||
            package.PackageGeneration != packageGeneration ||
            package.SourceRevision != sourceRevision ||
            _updatingRevision != updatingRevision ||
            !package.TryFinalizeImportedResourceIdentity(in expected, descriptorRevision, instanceRevision))
            return;

        SetField(ref _updatingBackendReadyIdentity, package.Identity, publishNotifications: false);
    }

    private static bool MatchesPreparedResourceGeneration(
        XRRenderPipelineInstance owner,
        RenderResourceGeneration generation,
        in BackendReadyFramePackageIdentity identity)
        => ReferenceEquals(owner.ActiveGeneration, generation) &&
           generation.Status == RenderResourceGenerationStatus.Active &&
           owner.ResourceGeneration == identity.ResourceGeneration &&
           generation.Registry.DescriptorRevision == identity.DescriptorGeneration &&
           generation.Registry.InstanceRevision == identity.ResourceInstanceRevision;
}
