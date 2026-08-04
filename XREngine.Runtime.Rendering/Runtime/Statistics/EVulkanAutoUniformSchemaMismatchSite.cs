namespace XREngine;

/// <summary>
/// Exact validation site that rejected an auto-uniform fast-path write plan.
/// </summary>
public enum EVulkanAutoUniformSchemaMismatchSite
{
    None,
    BlockIdentityOrSize,
    Frequency,
    Parity,
    Count,
}
