using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanDescriptorContracts
{
    private static readonly HashSet<DescriptorType> GlobalsAllowedTypes =
    [
        DescriptorType.UniformBuffer,
        DescriptorType.StorageBuffer,
        DescriptorType.CombinedImageSampler,
        DescriptorType.SampledImage,
        DescriptorType.StorageImage
    ];

    private static readonly HashSet<DescriptorType> ComputeAllowedTypes =
    [
        DescriptorType.StorageBuffer,
        DescriptorType.UniformBuffer,
        DescriptorType.StorageImage,
        DescriptorType.CombinedImageSampler,
        DescriptorType.SampledImage
    ];

    private static readonly HashSet<DescriptorType> MaterialAllowedTypes =
    [
        DescriptorType.CombinedImageSampler,
        DescriptorType.SampledImage,
        DescriptorType.StorageImage,
        DescriptorType.UniformBuffer,
        DescriptorType.StorageBuffer,
        DescriptorType.UniformTexelBuffer,
        DescriptorType.StorageTexelBuffer
    ];

    private static readonly HashSet<DescriptorType> PerPassAllowedTypes =
    [
        DescriptorType.StorageBuffer,
        DescriptorType.UniformBuffer,
        DescriptorType.StorageImage,
        DescriptorType.CombinedImageSampler,
        DescriptorType.SampledImage
    ];

    public static bool TryValidateContract(
        IReadOnlyList<DescriptorBindingInfo> reflectedBindings,
        out string error)
    {
        error = string.Empty;
        if (reflectedBindings.Count == 0)
            return true;

        foreach (DescriptorBindingInfo binding in reflectedBindings)
        {
            if (binding.Set >= VulkanRenderer.DescriptorSetTierCount)
            {
                error = $"Descriptor binding '{binding.Name}' uses unsupported set={binding.Set}. Expected 0..{VulkanRenderer.DescriptorSetTierCount - 1}.";
                return false;
            }

            HashSet<DescriptorType> allowed = binding.Set switch
            {
                VulkanRenderer.DescriptorSetGlobals => GlobalsAllowedTypes,
                VulkanRenderer.DescriptorSetCompute => ComputeAllowedTypes,
                VulkanRenderer.DescriptorSetMaterial => MaterialAllowedTypes,
                VulkanRenderer.DescriptorSetPerPass => PerPassAllowedTypes,
                _ => GlobalsAllowedTypes,
            };

            if (!allowed.Contains(binding.DescriptorType))
            {
                error = $"Descriptor binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}) has disallowed descriptor type {binding.DescriptorType}.";
                return false;
            }

            if (binding.Count == 0)
            {
                error = $"Descriptor binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}) has invalid descriptor count 0.";
                return false;
            }
        }

        bool hasMaterialTier = reflectedBindings.Any(x => x.Set == VulkanRenderer.DescriptorSetMaterial);
        if (!hasMaterialTier)
            return true;

        bool hasMaterialResource = reflectedBindings.Any(x =>
            x.Set == VulkanRenderer.DescriptorSetMaterial &&
            (x.DescriptorType == DescriptorType.CombinedImageSampler ||
             x.DescriptorType == DescriptorType.SampledImage ||
             x.DescriptorType == DescriptorType.StorageImage));

        if (!hasMaterialResource)
        {
            error = "Material descriptor tier (set=2) is present but has no image resources; this likely indicates shader-layout drift.";
            return false;
        }

        return true;
    }
}
