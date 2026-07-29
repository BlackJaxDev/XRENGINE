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

internal sealed class VulkanImageAliasGroup
{
    private readonly List<VulkanImageAllocation> _allocations = new();

    public VulkanImageAliasGroup(VulkanAliasGroupKey key)
    {
        Key = key;
        AllowsAliasing = true;
        CreateInfoTemplate = VulkanImageCreateTemplate.FromDescriptor(key.AliasKey);
    }

    public VulkanAliasGroupKey Key { get; }
    public bool AllowsAliasing { get; private set; }
    public IReadOnlyList<VulkanImageAllocation> Allocations => _allocations;
    public VulkanImageCreateTemplate CreateInfoTemplate { get; }

    public VulkanImageAllocation Add(VulkanAllocationRequest request)
    {
        AllowsAliasing &= request.SupportsAliasing && request.Lifetime == RenderResourceLifetime.Transient;
        VulkanImageAllocation allocation = new(request, Key, _allocations.Count);
        _allocations.Add(allocation);
        return allocation;
    }
}

