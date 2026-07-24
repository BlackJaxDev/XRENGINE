using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides methods for validating Vulkan descriptor binding contracts against expected descriptor sets and types.
/// </summary>
internal static class VulkanDescriptorContracts
{
    /// <summary>
    /// The set of allowed descriptor types for the global descriptor set.
    /// </summary>
    private static readonly HashSet<DescriptorType> GlobalsAllowedTypes =
    [
        DescriptorType.UniformBuffer,
        DescriptorType.UniformBufferDynamic,
        DescriptorType.StorageBuffer,
        DescriptorType.CombinedImageSampler,
        DescriptorType.SampledImage,
        DescriptorType.StorageImage
    ];

    /// <summary>
    /// The set of allowed descriptor types for the compute descriptor set.
    /// </summary>
    private static readonly HashSet<DescriptorType> ComputeAllowedTypes =
    [
        DescriptorType.StorageBuffer,
        DescriptorType.UniformBuffer,
        DescriptorType.StorageImage,
        DescriptorType.CombinedImageSampler,
        DescriptorType.SampledImage
    ];

    /// <summary>
    /// The set of allowed descriptor types for the material descriptor set.
    /// </summary>
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

    /// <summary>
    /// The set of allowed descriptor types for the per-pass descriptor set.
    /// </summary>
    private static readonly HashSet<DescriptorType> PerPassAllowedTypes =
    [
        DescriptorType.StorageBuffer,
        DescriptorType.UniformBuffer,
        DescriptorType.StorageImage,
        DescriptorType.CombinedImageSampler,
        DescriptorType.SampledImage
    ];

    /// <summary>
    /// Validates the descriptor binding contract against the expected descriptor sets and types.
    /// </summary>
    /// <param name="reflectedBindings">The list of descriptor binding information to validate.</param>
    /// <param name="error">The error message if the validation fails.</param>
    /// <returns>True if the descriptor binding contract is valid; otherwise, false.</returns>
    public static bool TryValidateContract(
        IReadOnlyList<DescriptorBindingInfo> reflectedBindings,
        out string error)
    {
        error = string.Empty;

        // If there are no reflected bindings, the contract is trivially valid.
        if (reflectedBindings.Count == 0)
            return true;

        // Validate each reflected descriptor binding against the allowed types and counts.
        foreach (DescriptorBindingInfo binding in reflectedBindings)
        {
            // Check if the descriptor set index is within the supported range.
            if (binding.Set >= VulkanRenderer.DescriptorSetTierCount)
            {
                error = $"Descriptor binding '{binding.Name}' uses unsupported set={binding.Set}. Expected 0..{VulkanRenderer.DescriptorSetTierCount - 1}.";
                return false;
            }

            // Determine the set of allowed descriptor types for the current descriptor set.
            HashSet<DescriptorType> allowed = binding.Set switch
            {
                VulkanRenderer.DescriptorSetGlobals => GlobalsAllowedTypes,
                VulkanRenderer.DescriptorSetCompute => ComputeAllowedTypes,
                VulkanRenderer.DescriptorSetMaterial => MaterialAllowedTypes,
                VulkanRenderer.DescriptorSetPerPass => PerPassAllowedTypes,
                _ => GlobalsAllowedTypes,
            };

            // Check if the descriptor type of the current binding is allowed for its descriptor set.
            if (!allowed.Contains(binding.DescriptorType))
            {
                error = $"Descriptor binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}) has disallowed descriptor type {binding.DescriptorType}.";
                return false;
            }

            // Check if the descriptor count for the current binding is valid (non-zero).
            if (binding.Count == 0)
            {
                error = $"Descriptor binding '{binding.Name}' (set={binding.Set}, binding={binding.Binding}) has invalid descriptor count 0.";
                return false;
            }
        }

        // Ensure that if the material descriptor set is present, it contains at least one image resource.
        bool hasMaterialTier = reflectedBindings.Any(x => x.Set == VulkanRenderer.DescriptorSetMaterial);
        if (!hasMaterialTier)
            return true;

        // Check if the material descriptor set contains at least one image resource.
        bool hasMaterialResource = reflectedBindings.Any(x =>
            x.Set == VulkanRenderer.DescriptorSetMaterial &&
            (x.DescriptorType == DescriptorType.CombinedImageSampler ||
             x.DescriptorType == DescriptorType.SampledImage ||
             x.DescriptorType == DescriptorType.StorageImage));

        // If no image resources are found in the material descriptor set, report an error.
        if (!hasMaterialResource)
        {
            error = "Material descriptor tier (set=2) is present but has no image resources; this likely indicates shader-layout drift.";
            return false;
        }

        // All descriptor contract checks passed; the reflected bindings are valid.
        return true;
    }
}
