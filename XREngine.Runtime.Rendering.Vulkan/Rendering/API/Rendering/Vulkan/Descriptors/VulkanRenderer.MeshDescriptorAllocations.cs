using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private bool TryAcquireSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        XRMaterial material,
        out VkMeshRenderer.DescriptorAllocation allocation)
        => _descriptorManager.TryAcquireSharedMeshDescriptorAllocation(
            key,
            material,
            out allocation);

    private VkMeshRenderer.DescriptorAllocation PublishSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        VkMeshRenderer.DescriptorAllocation allocation,
        out bool published)
    {
        using VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.DescriptorPublication);
        return _descriptorManager.PublishSharedMeshDescriptorAllocation(
            key,
            allocation,
            out published);
    }

    private bool ReleaseSharedMeshDescriptorAllocation(
        in VkMeshRenderer.DescriptorAllocationKey key,
        VkMeshRenderer.DescriptorAllocation allocation)
        => _descriptorManager.ReleaseSharedMeshDescriptorAllocation(key, allocation);
}
