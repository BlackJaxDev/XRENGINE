namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies whether an image subresource is engine-owned or is currently in
/// an externally managed OpenXR acquire/release epoch.
/// </summary>
internal enum EVulkanExternalImageOwnership : byte
{
    EngineOwned = 0,
    OpenXrRuntimeAcquired,
    OpenXrRuntimeReleasePending,
}
