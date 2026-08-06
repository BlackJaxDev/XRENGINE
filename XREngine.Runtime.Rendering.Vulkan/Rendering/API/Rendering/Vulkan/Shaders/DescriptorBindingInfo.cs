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
    internal static EVulkanDescriptorBindingRequirement ClassifyRequirement(
        DescriptorType descriptorType,
        string? name)
        => descriptorType is DescriptorType.StorageBuffer or
                DescriptorType.StorageBufferDynamic &&
            name is ("LightProbePositions" or
                "LightProbeTetrahedra" or
                "LightProbeParameters" or
                "LightProbeGridCells" or
                "LightProbeGridIndices")
                ? EVulkanDescriptorBindingRequirement.Optional
                : EVulkanDescriptorBindingRequirement.Required;
}

