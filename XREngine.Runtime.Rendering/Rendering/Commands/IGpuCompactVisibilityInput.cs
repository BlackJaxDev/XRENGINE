namespace XREngine.Rendering.Commands;

/// <summary>
/// Optional GPU-owned visibility stream consumed by compact indirect
/// submission. Producers may be frustum culling, a future Hi-Z pass, or an
/// explicit conservative bypass; consumers never inspect the count on the CPU.
/// </summary>
public interface IGpuCompactVisibilityInput
{
    /// <summary>
    /// Gets the GPU buffer containing compact command or draw identifiers.
    /// </summary>
    XRDataBuffer CommandIds { get; }

    /// <summary>
    /// Gets the GPU-owned count consumed by indirect dispatch.
    /// </summary>
    XRDataBuffer CommandCount { get; }

    /// <summary>
    /// Gets the maximum number of command identifiers the producer can write.
    /// </summary>
    uint Capacity { get; }

    /// <summary>
    /// Gets the physical-resource generation used for stable binding identity.
    /// </summary>
    ulong ResourceGeneration { get; }

    /// <summary>
    /// Gets whether this input conservatively bypasses an optional visibility
    /// test while preserving the same GPU buffer/count contract.
    /// </summary>
    bool IsConservativeBypass { get; }
}
