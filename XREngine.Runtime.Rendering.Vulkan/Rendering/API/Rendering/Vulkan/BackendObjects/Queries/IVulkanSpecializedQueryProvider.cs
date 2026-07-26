using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Records property, performance, or video queries that cannot use the generic
/// begin/end command family. Providers are registered only by a real subsystem owner.
/// </summary>
public interface IVulkanSpecializedQueryProvider
{
    ERenderQueryKind Kind { get; }

    bool HasRequiredExternalOwnership { get; }

    bool TryRecord(
        VulkanRenderer renderer,
        CommandBuffer commandBuffer,
        QueryPool queryPool,
        uint firstQuery,
        in RenderQueryDescriptor descriptor,
        ReadOnlySpan<ulong> sourceHandles,
        out string? reason);
}
