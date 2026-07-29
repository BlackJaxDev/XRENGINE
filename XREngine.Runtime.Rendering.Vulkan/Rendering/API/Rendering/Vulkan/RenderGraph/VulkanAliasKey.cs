using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanAliasKey(
    RenderResourceSizePolicy SizePolicy,
    string? FormatLabel,
    ESizedInternalFormat? SizedInternalFormat,
    EPixelInternalFormat? InternalFormat,
    RenderPipelineResourceUsage Usage,
    uint Samples,
    uint MipLevelCount,
    uint ArrayLayers,
    bool StereoCompatible,
    bool RequiresStorageUsage);
