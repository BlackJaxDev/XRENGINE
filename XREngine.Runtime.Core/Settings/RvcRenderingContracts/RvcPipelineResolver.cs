namespace XREngine;

/// <summary>
/// Resolves the appropriate RVC pipeline mode based on the provided rendering settings and capability matrix.
/// </summary>
public static class RvcPipelineResolver
{
    /// <summary>
    /// Resolves the RVC pipeline mode based on the given rendering settings and capability matrix.
    /// </summary>
    /// <param name="settings">The rendering settings to consider for pipeline resolution.</param>
    /// <param name="capabilities">The capability matrix indicating available features and backends.</param>
    /// <returns>A <see cref="RvcPipelineResolution"/> representing the resolved pipeline mode and associated details.</returns>
    public static RvcPipelineResolution Resolve(
        in RvcRenderingSettings settings,
        in RvcCapabilityMatrix capabilities)
    {
        // Early out if the pipeline mode is explicitly turned off.
        if (settings.PipelineMode == ERvcPipelineMode.Off)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.DisabledBySettings, "RVC is disabled by settings.");

        // Early out if the forward+ oracle path is unavailable.
        if (!capabilities.ForwardPlusOracleAvailable)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingForwardPlusOracle, "Forward+ oracle path is unavailable.");

        // Resolve the descriptor and foveation rate backends from the capability matrix.
        ERvcDescriptorBackend descriptorBackend = capabilities.ResolveDescriptorBackend();
        ERvcFoveationRateBackend foveationBackend = capabilities.ResolveFoveationRateBackend();

        // Early out if the pipeline mode is ForwardPlusOracle, in which case RVC cache passes are bypassed.
        if (settings.PipelineMode == ERvcPipelineMode.ForwardPlusOracle)
        {
            return new(
                settings.PipelineMode,
                ERvcPipelineMode.ForwardPlusOracle,
                IsRvcActive: false,
                descriptorBackend,
                foveationBackend,
                ERvcFallbackReason.None,
                "Forward+ oracle mode is active; RVC cache passes are intentionally bypassed.");
        }

        // Early out if the frame-graph resources are unavailable.
        if (!capabilities.FrameGraphAvailable)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingFrameGraph, "RVC requires explicit frame-graph resources.");
        
        // Early out if the visibility targets are unavailable.
        if (!capabilities.VisibilityTargetsAvailable)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingVisibilityTargets, "RVC visibility targets are unavailable.");
        
        // Early out if the full RVC mode is requested but only the OpenGL backend is available.
        if (settings.PipelineMode == ERvcPipelineMode.Full && capabilities.OpenGlBackend)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.UnsupportedOpenGlProductionPath, "Full RVC is Vulkan-only; OpenGL may host correctness slices only.");
        
        // Early out if the material-cache mode is requested but no descriptor backend is available.
        if (settings.PipelineMode >= ERvcPipelineMode.MaterialCache && descriptorBackend == ERvcDescriptorBackend.None)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingDescriptorBackend, "RVC material-cache modes require descriptor heap or descriptor indexing material/resource rows.");

        // Early out if quad-view is enabled but the OpenXR quad-view runtime is unavailable.
        if (settings.QuadViewEnabled && !capabilities.OpenXrQuadViewsSupported)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingQuadViewRuntime, "Quad-view RVC was requested but the OpenXR quad-view runtime path is unavailable.");
        
        // Early out if the material-cache mode is requested but no foveation rate backend is available.
        if (settings.PipelineMode >= ERvcPipelineMode.MaterialCache && foveationBackend == ERvcFoveationRateBackend.None)
            return Fallback(settings.PipelineMode, ERvcFallbackReason.MissingFoveationRateBackend, "RVC material-cache modes require a fragment-shading-rate, fragment-density, or OpenXR foveation backend.");

        // If none of the early out conditions were met, the RVC mode is supported.
        return new(
            settings.PipelineMode,
            settings.PipelineMode,
            IsRvcActive: true,
            descriptorBackend,
            foveationBackend,
            ERvcFallbackReason.None,
            "RVC mode is supported by the declared capability matrix.");
    }

    private static RvcPipelineResolution Fallback(
        ERvcPipelineMode requestedMode,
        ERvcFallbackReason reason,
        string diagnostic)
        => new(
            requestedMode,
            ERvcPipelineMode.ForwardPlusOracle,
            IsRvcActive: false,
            ERvcDescriptorBackend.None,
            ERvcFoveationRateBackend.None,
            reason,
            diagnostic);
}
