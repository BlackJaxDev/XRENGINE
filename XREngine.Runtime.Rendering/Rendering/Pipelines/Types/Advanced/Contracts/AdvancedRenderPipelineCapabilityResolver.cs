namespace XREngine.Rendering;

/// <summary>
/// Validates backend capabilities in a deterministic order so each rejected request
/// exposes one stable machine-readable reason.
/// </summary>
public static class AdvancedRenderPipelineCapabilityResolver
{
    public static AdvancedRenderPipelineCapabilityResult Resolve(
        in AdvancedRenderPipelineCapabilities capabilities,
        bool stereo)
    {
        EAdvancedRenderPipelineRejectionReason reason = ResolveRejectionReason(capabilities, stereo);
        return new(capabilities, reason == EAdvancedRenderPipelineRejectionReason.None, reason);
    }

    public static AdvancedRenderPipelineCapabilityResult ResolveCurrent(bool stereo)
    {
        IRuntimeRendererHost? renderer = RuntimeRenderingHostServices.FrameTiming.CurrentRenderer;
        AdvancedRenderPipelineCapabilities capabilities =
            renderer?.GetAdvancedRenderPipelineCapabilities() ??
            AdvancedRenderPipelineCapabilities.NoRenderer;
        return Resolve(capabilities, stereo);
    }

    private static EAdvancedRenderPipelineRejectionReason ResolveRejectionReason(
        in AdvancedRenderPipelineCapabilities capabilities,
        bool stereo)
    {
        if (!capabilities.RendererAvailable)
            return EAdvancedRenderPipelineRejectionReason.RendererUnavailable;
        if (capabilities.Backend == RuntimeGraphicsApiKind.Unknown)
            return EAdvancedRenderPipelineRejectionReason.UnsupportedBackend;
        if (!capabilities.SupportsIntegerRenderTargets ||
            capabilities.VisibilityTargetEncoding == EAdvancedVisibilityTargetEncoding.None)
        {
            return EAdvancedRenderPipelineRejectionReason.MissingIntegerRenderTargets;
        }
        if (!capabilities.SupportsComputeShaders)
            return EAdvancedRenderPipelineRejectionReason.MissingComputeShaders;
        if (!capabilities.SupportsStorageBuffers)
            return EAdvancedRenderPipelineRejectionReason.MissingStorageBuffers;
        if (capabilities.IndirectSubmission == EAdvancedIndirectSubmissionMode.None)
            return EAdvancedRenderPipelineRejectionReason.MissingIndirectSubmission;
        if (capabilities.TextureIndirection == EAdvancedTextureIndirectionMode.None)
            return EAdvancedRenderPipelineRejectionReason.MissingTextureIndirection;
        if (capabilities.Synchronization == EAdvancedSynchronizationMode.None)
            return EAdvancedRenderPipelineRejectionReason.MissingSynchronization;
        if (!capabilities.SupportsFrameSlotStorage)
            return EAdvancedRenderPipelineRejectionReason.MissingFrameSlotStorage;
        if (stereo && !capabilities.SupportsStereoArrayResources)
            return EAdvancedRenderPipelineRejectionReason.MissingStereoArrayResources;
        if (capabilities.ShaderFamily != EAdvancedShaderFamily.VisibilityBuffer)
            return EAdvancedRenderPipelineRejectionReason.MissingShaderFamily;

        return EAdvancedRenderPipelineRejectionReason.None;
    }
}
