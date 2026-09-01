using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native generation required by a resident command-template artifact.
/// </summary>
internal readonly record struct VulkanResidentTemplateDependencyRequest(
    EVulkanResidentTemplateDependencyKind Kind,
    ulong Handle,
    ulong Generation)
{
    internal bool TryGetKey(out VulkanResourceLifetimeKey key)
    {
        key = new VulkanResourceLifetimeKey(GetObjectType(Kind), Handle);
        return Handle != 0u && Generation != 0u;
    }

    private static ObjectType GetObjectType(EVulkanResidentTemplateDependencyKind kind)
        => kind switch
        {
            EVulkanResidentTemplateDependencyKind.Pipeline => ObjectType.Pipeline,
            EVulkanResidentTemplateDependencyKind.PipelineLayout => ObjectType.PipelineLayout,
            EVulkanResidentTemplateDependencyKind.DescriptorSetLayout => ObjectType.DescriptorSetLayout,
            EVulkanResidentTemplateDependencyKind.Buffer => ObjectType.Buffer,
            EVulkanResidentTemplateDependencyKind.BufferView => ObjectType.BufferView,
            EVulkanResidentTemplateDependencyKind.RenderPass => ObjectType.RenderPass,
            EVulkanResidentTemplateDependencyKind.Image => ObjectType.Image,
            EVulkanResidentTemplateDependencyKind.ImageView => ObjectType.ImageView,
            EVulkanResidentTemplateDependencyKind.Sampler => ObjectType.Sampler,
            _ => ObjectType.Unknown,
        };
}
