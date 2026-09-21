namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Describes whether a selected GI provider can currently be admitted by a host.
/// Static capability support is intentionally separate from per-generation resource readiness.
/// </summary>
public enum EGlobalIlluminationSupportState
{
    Supported,
    Unsupported,
    Initializing,
    Failed,
}
