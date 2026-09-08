namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Identifies the runtime-specific reason an eye-resolution change requires a
/// full OpenXR instance and service refresh instead of an in-session replacement.
/// </summary>
internal enum OpenXrRuntimeDimensionRefreshRequirement
{
    /// <summary>The runtime supports replacing the active session swapchains in place.</summary>
    None,

    /// <summary>Monado must restart its simulated display profile before reporting new recommended dimensions.</summary>
    MonadoSimulatedDisplayProfile,
}
