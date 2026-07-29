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

internal sealed class VulkanPhysicalBufferGroup
{
    private readonly List<VulkanBufferAllocation> _logicalResources = new();
    private Buffer _buffer;
    private DeviceMemory _memory;
    private bool _allocated;

    internal VulkanPhysicalBufferGroup(
        VulkanBufferAliasGroup logicalGroup,
        BufferUsageFlags usage)
    {
        Key = logicalGroup.Key;
        AllowsAliasing = logicalGroup.AllowsAliasing;
        Template = logicalGroup.CreateInfoTemplate;
        Usage = usage;
    }

    public VulkanBufferAliasGroupKey Key { get; }
    public bool AllowsAliasing { get; }
    public VulkanBufferCreateTemplate Template { get; }
    public BufferUsageFlags Usage { get; }
    public ulong SizeInBytes => Template.SizeInBytes;
    public IReadOnlyList<VulkanBufferAllocation> LogicalResources => _logicalResources;
    public bool IsAllocated => _allocated;
    public Buffer Buffer => _buffer;
    public DeviceMemory Memory => _memory;

    internal void AddLogical(VulkanBufferAllocation allocation)
        => _logicalResources.Add(allocation);

    public void EnsureAllocated(VulkanRenderer renderer)
    {
        if (_allocated)
            return;

        renderer.AllocatePhysicalBuffer(this, ref _buffer, ref _memory);
        _allocated = true;
    }

    public void Destroy(VulkanRenderer renderer)
    {
        if (!_allocated)
            return;

        renderer.DestroyPhysicalBuffer(ref _buffer, ref _memory);
        _allocated = false;
    }

    public void DestroyImmediate(VulkanRenderer renderer)
    {
        if (!_allocated)
            return;

        renderer.DestroyPhysicalBufferImmediate(ref _buffer, ref _memory);
        _allocated = false;
    }
}
