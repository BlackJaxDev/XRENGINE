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

internal readonly record struct VulkanImageAllocation(
    VulkanAllocationRequest Request,
    VulkanAliasGroupKey AliasGroup,
    int GroupIndex)
{
    public string Name => Request.Name;
    public TextureResourceDescriptor Descriptor => Request.Descriptor;
    public RenderResourceLifetime Lifetime => Request.Lifetime;
    public RenderResourceSizePolicy SizePolicy => Request.SizePolicy;
    public bool SupportsAliasing => Request.SupportsAliasing;
}

