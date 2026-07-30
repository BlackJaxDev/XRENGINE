namespace XREngine.Rendering.Commands;

/// <summary>
/// Performs bounded validation without traversing scene or material state.
/// </summary>
public static class BackendReadyFramePackageValidator
{
    public static BackendReadyFramePackageValidationResult Validate(
        BackendReadyFramePackage package,
        in BackendReadyFramePackageValidationContext context)
    {
        if (package.State != EBackendReadyFramePackageState.Published)
        {
            return BackendReadyFramePackageValidationResult.Reject(
                EBackendReadyFramePackageValidationFailure.NotPublished);
        }

        BackendReadyFramePackageIdentity identity = package.Identity;
        if (identity.CollectGeneration < 0L)
            return BackendReadyFramePackageValidationResult.Success;

        if (identity.CollectGeneration != context.ConsumedCollectGeneration)
        {
            return BackendReadyFramePackageValidationResult.Reject(
                EBackendReadyFramePackageValidationFailure.CollectGenerationMismatch);
        }

        if (identity.CommandGeneration != context.CommandGeneration)
        {
            return BackendReadyFramePackageValidationResult.Reject(
                EBackendReadyFramePackageValidationFailure.CommandGenerationMismatch);
        }

        if (identity.ResourceGeneration != context.ResourceGeneration)
        {
            return BackendReadyFramePackageValidationResult.Reject(
                EBackendReadyFramePackageValidationFailure.ResourceGenerationMismatch);
        }

        if (identity.DescriptorGeneration != context.DescriptorGeneration)
        {
            return BackendReadyFramePackageValidationResult.Reject(
                EBackendReadyFramePackageValidationFailure.DescriptorGenerationMismatch);
        }

        if (identity.RenderGraphGeneration != context.RenderGraphGeneration)
        {
            return BackendReadyFramePackageValidationResult.Reject(
                EBackendReadyFramePackageValidationFailure.RenderGraphGenerationMismatch);
        }

        if (identity.ViewportWidth != context.ViewportWidth ||
            identity.ViewportHeight != context.ViewportHeight ||
            identity.InternalWidth != context.InternalWidth ||
            identity.InternalHeight != context.InternalHeight)
        {
            return BackendReadyFramePackageValidationResult.Reject(
                EBackendReadyFramePackageValidationFailure.ViewportMismatch);
        }

        return BackendReadyFramePackageValidationResult.Success;
    }
}
