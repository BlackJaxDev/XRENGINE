using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Creates and releases the legacy root descriptor-set layout.</summary>
internal static unsafe class VulkanRootDescriptorLayoutService
{
    internal static void Create(VulkanResourceRuntime resources, Vk api, Device device)
    {
        DescriptorSetLayoutBinding binding = new()
        {
            Binding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            StageFlags = ShaderStageFlags.VertexBit,
        };
        DescriptorSetLayoutCreateInfo info = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        if (api.CreateDescriptorSetLayout(device, ref info, null, out DescriptorSetLayout layout) != Result.Success)
            throw new Exception("Failed to create root descriptor set layout.");

        resources.Descriptors.RootSetLayout = layout;
        resources.RegisterDescriptorSetLayout(layout, "Swapchain.DescriptorSetLayout");
    }

    internal static void Destroy(
        VulkanResourceRuntime resources,
        Vk api,
        Device device,
        VulkanCommandRuntime commandRuntime,
        int frameSlot)
    {
        DescriptorSetLayout layout = resources.Descriptors.RootSetLayout;
        resources.Descriptors.RootSetLayout = default;
        resources.DestroyDescriptorSetLayout(
            api, device, frameSlot, layout, "Swapchain.DescriptorSetLayout");
    }
}
