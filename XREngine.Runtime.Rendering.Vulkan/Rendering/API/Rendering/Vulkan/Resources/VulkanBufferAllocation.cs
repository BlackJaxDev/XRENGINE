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

internal readonly record struct VulkanBufferAllocation(
    VulkanBufferAllocationRequest Request,
    VulkanBufferAliasGroupKey AliasGroup,
    int GroupIndex)
{
    public string Name => Request.Name;
    public BufferResourceDescriptor Descriptor => Request.Descriptor;
    public RenderResourceLifetime Lifetime => Request.Lifetime;
    public ulong SizeInBytes => Request.SizeInBytes;
    public EBufferTarget Target => Request.Target;
    public EBufferUsage Usage => Request.Usage;
    public bool SupportsAliasing => Request.SupportsAliasing;
}

