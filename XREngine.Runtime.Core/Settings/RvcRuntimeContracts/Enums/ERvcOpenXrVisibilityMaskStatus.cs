namespace XREngine;

/// <summary>
/// OpenXR visibility mask status used by the engine.
/// </summary>
public enum ERvcOpenXrVisibilityMaskStatus
{
    /// <summary>
    /// Visibility mask not requested.
    /// </summary>
    NotRequested,
    /// <summary>
    /// OpenXR extension missing.
    /// </summary>
    ExtensionMissing,
    /// <summary>
    /// Native function missing.
    /// </summary>
    NativeFunctionMissing,
    /// <summary>
    /// Awaiting runtime mesh.
    /// </summary>
    AwaitingRuntimeMesh,
    /// <summary>
    /// Runtime mesh unavailable.
    /// </summary>
    RuntimeMeshUnavailable,
    /// <summary>
    /// Ready for stencil prepass.
    /// </summary>
    ReadyForStencilPrepass,
    /// <summary>
    /// Invalidated by runtime.
    /// </summary>
    InvalidatedByRuntime,
}
