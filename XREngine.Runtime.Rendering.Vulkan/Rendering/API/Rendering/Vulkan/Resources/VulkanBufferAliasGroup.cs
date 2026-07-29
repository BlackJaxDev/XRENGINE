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

internal sealed class VulkanBufferAliasGroup
{
    private readonly List<VulkanBufferAllocation> _allocations = new();

    public VulkanBufferAliasGroup(VulkanBufferAliasGroupKey key)
    {
        Key = key;
        AllowsAliasing = true;
        CreateInfoTemplate = VulkanBufferCreateTemplate.FromDescriptor(key.AliasKey.SizeInBytes, key.AliasKey.Target, key.AliasKey.Usage);
    }

    public VulkanBufferAliasGroupKey Key { get; }
    public bool AllowsAliasing { get; private set; }
    public IReadOnlyList<VulkanBufferAllocation> Allocations => _allocations;
    public VulkanBufferCreateTemplate CreateInfoTemplate { get; }

    public VulkanBufferAllocation Add(VulkanBufferAllocationRequest request)
    {
        AllowsAliasing &= request.SupportsAliasing && request.Lifetime == RenderResourceLifetime.Transient;
        VulkanBufferAllocation allocation = new(request, Key, _allocations.Count);
        _allocations.Add(allocation);
        return allocation;
    }
}

