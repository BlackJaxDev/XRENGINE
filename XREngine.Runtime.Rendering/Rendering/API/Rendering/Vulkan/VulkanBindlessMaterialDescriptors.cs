using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanBindlessMaterialDescriptors
{
    public const string TextureArrayBindingName = "XR_BindlessMaterialTextures";
    public const uint TextureArraySet = VulkanRenderer.DescriptorSetMaterial;
    public const uint TextureArrayBinding = 31u;
    public const uint MaxTextureDescriptorCount = 4096u;

    public static bool IsBindlessTextureArrayBinding(DescriptorBindingInfo binding)
        => binding.Set == TextureArraySet &&
           binding.Binding == TextureArrayBinding &&
           binding.Name == TextureArrayBindingName &&
           binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage;

    public static bool IsBindlessTextureArrayBinding(
        uint set,
        DescriptorSetLayoutBinding binding)
        => set == TextureArraySet &&
           binding.Binding == TextureArrayBinding &&
           binding.DescriptorCount == MaxTextureDescriptorCount &&
           binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage;

    public static uint ResolveDescriptorCount(DescriptorBindingInfo binding)
        => IsBindlessTextureArrayBinding(binding)
            ? MaxTextureDescriptorCount
            : Math.Max(binding.Count, 1u);

    public static uint[] BuildVariableDescriptorCounts(IReadOnlyList<DescriptorBindingInfo> bindings, int setCount)
    {
        uint[] counts = new uint[setCount];
        for (int i = 0; i < bindings.Count; i++)
        {
            DescriptorBindingInfo binding = bindings[i];
            if (!IsBindlessTextureArrayBinding(binding) || binding.Set >= counts.Length)
                continue;

            counts[binding.Set] = MaxTextureDescriptorCount;
        }

        return counts;
    }
}
