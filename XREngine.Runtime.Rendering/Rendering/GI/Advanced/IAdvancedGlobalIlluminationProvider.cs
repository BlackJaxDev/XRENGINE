namespace XREngine.Rendering;

/// <summary>
/// Narrow provider contract for global illumination in the Advanced Render Pipeline.
/// Exactly one mode contributes to native shading to prevent double-counting indirect radiance.
/// </summary>
public interface IAdvancedGlobalIlluminationProvider
{
    /// <summary>
    /// The global illumination mode supplied by this provider.
    /// </summary>
    EGlobalIlluminationMode ActiveMode { get; }

    /// <summary>
    /// Human-readable provider name (e.g. "SurfelGI", "RadianceCascades", "VoxelConeTracing").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Whether this provider is supported on the current hardware and graphics API.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Whether this provider requires historical frame feedback buffers.
    /// </summary>
    bool RequiresTemporalHistory { get; }

    /// <summary>
    /// Optional output texture name (e.g. "SurfelGITexture" or "RadianceCascadeGI") if screen-space radiance is produced.
    /// </summary>
    string? OutputResourceName { get; }
}
