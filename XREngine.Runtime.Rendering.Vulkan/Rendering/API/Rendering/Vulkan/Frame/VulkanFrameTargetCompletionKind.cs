namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies who completes ownership of a rendered final image.</summary>
internal enum VulkanFrameTargetCompletionKind
{
    None = 0,
    RendererOwned = 1,
    WsiPresent = 2,
    OpenXrRuntimeRelease = 3,
}
