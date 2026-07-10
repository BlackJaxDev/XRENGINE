namespace XREngine;

/// <summary>
/// Resource alias rules used by the engine.
/// </summary>
[Flags]
public enum ERvcResourceAliasRule
{
    /// <summary>
    /// No resource alias rule.
    /// </summary>
    None = 0,
    /// <summary>
    /// Resource can only be aliased within the same view.
    /// </summary>
    SameViewOnly = 1 << 0,
    /// <summary>
    /// No read-after-write overlap allowed.
    /// </summary>
    NoReadAfterWriteOverlap = 1 << 1,
    /// <summary>
    /// No history aliasing allowed.
    /// </summary>
    NoHistoryAlias = 1 << 2,
    /// <summary>
    /// Debug resources should never alias.
    /// </summary>
    DebugResourcesNeverAlias = 1 << 3,
    /// <summary>
    /// External swapchain resources should never alias.
    /// </summary>
    ExternalSwapchainNeverAlias = 1 << 4,
}
