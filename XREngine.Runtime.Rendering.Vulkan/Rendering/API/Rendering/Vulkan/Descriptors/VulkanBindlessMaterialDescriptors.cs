using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides constants and utility methods for managing bindless material texture descriptors in Vulkan.
/// This class defines the binding name, set, and binding index for the bindless texture array, 
/// as well as the maximum number of texture descriptors allowed. 
/// It also provides utility methods to check if a given binding corresponds 
/// to the bindless texture array and to resolve descriptor counts accordingly.
/// </summary>
internal static class VulkanBindlessMaterialDescriptors
{
    /// <summary>
    /// The name of the bindless texture array binding.
    /// </summary>
    public const string TextureArrayBindingName = "XR_BindlessMaterialTextures";
    /// <summary>
    /// The descriptor set index for the bindless texture array binding.
    /// </summary>
    public const uint TextureArraySet = VulkanRenderer.DescriptorSetMaterial;
    /// <summary>
    /// The binding index for the bindless texture array binding.
    /// </summary>
    public const uint TextureArrayBinding = 31u;
    /// <summary>
    /// The maximum number of texture descriptors allowed in the bindless texture array.
    /// </summary>
    public const uint MaxTextureDescriptorCount = 4096u;

    /// <summary>
    /// Determines whether the specified binding corresponds to the bindless texture array binding.
    /// </summary>
    /// <param name="binding">The descriptor binding information to check.</param>
    /// <returns>True if the binding corresponds to the bindless texture array binding; otherwise, false.</returns>
    public static bool IsBindlessTextureArrayBinding(DescriptorBindingInfo binding)
        => binding.Set == TextureArraySet &&
           binding.Binding == TextureArrayBinding &&
           binding.Name == TextureArrayBindingName &&
           binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage;

    /// <summary>
    /// Determines whether the specified descriptor set and binding correspond to the bindless texture array binding.
    /// </summary>
    /// <param name="set">The descriptor set index to check.</param>
    /// <param name="binding">The descriptor set layout binding to check.</param>
    /// <returns>True if the set and binding correspond to the bindless texture array binding; otherwise, false.</returns>
    public static bool IsBindlessTextureArrayBinding(
        uint set,
        DescriptorSetLayoutBinding binding)
        => set == TextureArraySet &&
           binding.Binding == TextureArrayBinding &&
           binding.DescriptorCount == MaxTextureDescriptorCount &&
           binding.DescriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage;

    /// <summary>
    /// Resolves the descriptor count for the specified binding, taking into account the bindless texture array binding.
    /// </summary>
    /// <param name="binding">The descriptor binding information to resolve the count for.</param>
    /// <returns>The resolved descriptor count for the binding.</returns>
    public static uint ResolveDescriptorCount(DescriptorBindingInfo binding)
        => IsBindlessTextureArrayBinding(binding)
            ? MaxTextureDescriptorCount
            : Math.Max(binding.Count, 1u);

    /// <summary>
    /// Builds an array of variable descriptor counts for the specified descriptor bindings and set count, taking into account the bindless texture array binding.
    /// </summary>
    /// <param name="bindings">The list of descriptor binding information to process.</param>
    /// <param name="setCount">The number of descriptor sets.</param>
    /// <returns>An array of variable descriptor counts for each descriptor set.</returns>
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
