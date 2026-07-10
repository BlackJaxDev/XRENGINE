namespace XREngine;

/// <summary>
/// Visibility payload formats used by the engine.
/// </summary>
public enum ERvcVisibilityPayloadFormat
{
    /// <summary>
    /// RG32 unsigned integer format with 64-bit identity.
    /// </summary>
    Rg32UintIdentity64,
    /// <summary>
    /// R32 unsigned integer packed format.
    /// </summary>
    R32UintPacked,
    /// <summary>
    /// Backend native 64-bit format.
    /// </summary>
    BackendNative64,
}
