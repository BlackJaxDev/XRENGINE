namespace XREngine;

/// <summary>
/// Represents the different pipeline modes available for the RVC rendering system.
/// </summary>
public enum ERvcPipelineMode
{
    /// <summary>
    /// The RVC pipeline is turned off. No RVC passes will be executed.
    /// </summary>
    Off,
    /// <summary>
    /// The Forward+ oracle pipeline mode. RVC cache passes are bypassed.
    /// </summary>
    ForwardPlusOracle,
    /// <summary>
    /// The visibility-only debug pipeline mode. Only visibility passes are executed for debugging purposes.
    /// </summary>
    VisibilityOnlyDebug,
    /// <summary>
    /// The material-cache pipeline mode. Executes material caching passes.
    /// </summary>
    MaterialCache,
    /// <summary>
    /// The shared-lighting pipeline mode. Executes shared lighting passes.
    /// </summary>
    SharedLighting,
    /// <summary>
    /// The full RVC pipeline mode. Executes all RVC passes.
    /// </summary>
    Full,
}
