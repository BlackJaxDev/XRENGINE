using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct DescriptorBindingInfo(
    uint Set,
    uint Binding,
    DescriptorType DescriptorType,
    ShaderStageFlags StageFlags,
    uint Count,
    string Name,
    ImageViewType? ExpectedImageViewType = null,
    EVulkanDescriptorBindingRequirement Requirement =
        EVulkanDescriptorBindingRequirement.Required)
{
    internal static DescriptorBindingInfo NormalizeKnownMetadata(
        DescriptorBindingInfo binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.Name))
        {
            EVulkanDescriptorBindingRequirement requirement =
                ClassifyRequirement(binding.DescriptorType, binding.Name);
            return requirement == binding.Requirement
                ? binding
                : binding with { Requirement = requirement };
        }

        if (binding.Set != VulkanDescriptorManager.PerPassSetIndex)
        {
            return binding;
        }

        string name = binding.DescriptorType switch
        {
            DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic =>
                ResolveForwardBufferName(binding.Binding),
            DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.Sampler =>
                ResolveForwardImageName(binding.Binding),
            _ => string.Empty,
        };
        if (name.Length == 0)
            return binding;

        return binding with
        {
            Name = name,
            Requirement = ClassifyRequirement(binding.DescriptorType, name),
        };
    }

    private static string ResolveForwardBufferName(uint binding)
        => binding switch
        {
            0u => "LightProbePositions",
            1u => "LightProbeTetrahedra",
            2u => "LightProbeParameters",
            3u => "LightProbeGridCells",
            4u => "LightProbeGridIndices",
            20u => "ForwardPlusLocalLightsBuffer",
            21u => "ForwardPlusVisibleIndicesBuffer",
            22u => "ForwardDirectionalLightsBuffer",
            35u => "ForwardPointLightsBuffer",
            36u => "ForwardSpotLightsBuffer",
            37u => "ForwardPointShadowMetadataBuffer",
            38u => "ForwardSpotShadowMetadataBuffer",
            _ => string.Empty,
        };

    private static string ResolveForwardImageName(uint binding)
        => binding switch
        {
            6u => "BRDF",
            7u => "IrradianceArray",
            8u => "PrefilterArray",
            9u => "DirectionalShadowAtlas",
            15u => "DirectionalShadowMaps",
            17u => "DirectionalShadowMapArrays",
            19u => "PointLightShadowMaps",
            23u => "SpotLightShadowMaps",
            28u => "ForwardContactDepthView",
            29u => "ForwardContactNormalView",
            30u => "ForwardContactDepthViewArray",
            31u => "ForwardContactNormalViewArray",
            32u => "SpotLightShadowAtlas",
            34u => "PointLightShadowAtlas",
            _ => string.Empty,
        };

    internal static EVulkanDescriptorBindingRequirement ClassifyRequirement(
        DescriptorType descriptorType,
        string? name)
        => IsOptionalProbeBinding(descriptorType, name)
                ? EVulkanDescriptorBindingRequirement.Optional
                : EVulkanDescriptorBindingRequirement.Required;

    private static bool IsOptionalProbeBinding(
        DescriptorType descriptorType,
        string? name)
        => descriptorType switch
        {
            DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic =>
                name is ("LightProbePositions" or
                    "LightProbeTetrahedra" or
                    "LightProbeParameters" or
                    "LightProbeGridCells" or
                    "LightProbeGridIndices"),
            DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.Sampler =>
                name is ("IrradianceArray" or "PrefilterArray"),
            _ => false,
        };
}

