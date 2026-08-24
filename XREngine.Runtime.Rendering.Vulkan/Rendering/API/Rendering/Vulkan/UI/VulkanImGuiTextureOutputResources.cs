using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns native descriptor-set allocation, publication, updates, and retirement
/// for textures registered with the ImGui output backend.
/// </summary>
internal sealed unsafe class VulkanImGuiTextureOutputResources(
    VulkanDeviceContext device,
    VulkanResourceRuntime resources)
{
    internal DescriptorSet AllocateAndWrite(
        VulkanImGuiResources imgui,
        ImageView imageView,
        Sampler sampler,
        ImageLayout imageLayout)
    {
        if (imgui.DescriptorPool.Handle == 0 || imgui.DescriptorSetLayout.Handle == 0)
            return default;

        DescriptorSetLayout layout = imgui.DescriptorSetLayout;
        DescriptorSetAllocateInfo allocateInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = imgui.DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        if (device.Api.AllocateDescriptorSets(
                device.Device,
                ref allocateInfo,
                out DescriptorSet descriptorSet) != Result.Success)
        {
            return default;
        }

        resources.DescriptorLifetime.RegisterDescriptorSet(
            imgui.DescriptorPool,
            descriptorSet,
            usesUpdateAfterBind: false,
            owner: "ImGui.Texture.DescriptorSet");
        Write(descriptorSet, imageView, sampler, imageLayout);
        return descriptorSet;
    }

    internal void Retire(VulkanImGuiResources imgui, DescriptorSet descriptorSet)
        => resources.DescriptorLifetime.RetireDescriptorSet(
            imgui.DescriptorPool,
            descriptorSet);

    private void Write(
        DescriptorSet descriptorSet,
        ImageView imageView,
        Sampler sampler,
        ImageLayout imageLayout)
    {
        DescriptorImageInfo imageInfo = new()
        {
            Sampler = sampler,
            ImageView = imageView,
            ImageLayout = imageLayout,
        };
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo,
        };
        resources.DescriptorLifetime.UpdateDescriptorSets(1, &write);
    }
}
