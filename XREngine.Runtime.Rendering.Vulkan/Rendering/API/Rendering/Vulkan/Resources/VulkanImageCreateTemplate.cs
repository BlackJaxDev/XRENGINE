using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanImageCreateTemplate(
    RenderResourceSizePolicy SizePolicy,
    uint Layers,
    string? FormatLabel,
    ESizedInternalFormat? SizedInternalFormat,
    EPixelInternalFormat? InternalFormat,
    RenderPipelineResourceUsage Usage,
    uint Samples,
    RenderResourceMipPolicy MipPolicy)
{
    public static VulkanImageCreateTemplate FromDescriptor(VulkanAliasKey aliasKey)
        => new(
            aliasKey.SizePolicy,
            Math.Max(aliasKey.ArrayLayers, 1u),
            aliasKey.FormatLabel,
            aliasKey.SizedInternalFormat,
            aliasKey.InternalFormat,
            aliasKey.Usage,
            Math.Max(1u, aliasKey.Samples),
            new RenderResourceMipPolicy(0u, Math.Max(1u, aliasKey.MipLevelCount)));
}

