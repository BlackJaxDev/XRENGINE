using XREngine.Data.Colors;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen diagnostic clear state consumed by one primary recording.</summary>
internal readonly record struct VulkanCommandClearStateSnapshot(
    ColorF4 ClearColor,
    float ClearDepth,
    uint ClearStencil,
    bool ForceMagentaSwapchain);
