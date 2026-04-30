using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine;

public class VulkanRobustnessSettings : XRBase
{
    private EVulkanAllocatorBackend _allocatorBackend = EVulkanAllocatorBackend.Legacy;
    private EVulkanSynchronizationBackend _syncBackend = EVulkanSynchronizationBackend.Legacy;
    private EVulkanDescriptorUpdateBackend _descriptorUpdateBackend = EVulkanDescriptorUpdateBackend.Legacy;
    private bool _dynamicUniformBufferEnabled;

    [Category("Vulkan")]
    [DisplayName("Allocator Backend")]
    [Description("Selects the Vulkan allocation backend. Legacy uses per-resource allocations; Suballocator enables pooled block allocation.")]
    public EVulkanAllocatorBackend AllocatorBackend
    {
        get => _allocatorBackend;
        set => SetField(ref _allocatorBackend, value);
    }

    [Category("Vulkan")]
    [DisplayName("Synchronization Backend")]
    [Description("Selects the Vulkan synchronization backend. Legacy uses vkCmdPipelineBarrier/vkQueueSubmit; Sync2 uses synchronization2 when supported.")]
    public EVulkanSynchronizationBackend SyncBackend
    {
        get => _syncBackend;
        set => SetField(ref _syncBackend, value);
    }

    [Category("Vulkan")]
    [DisplayName("Descriptor Update Backend")]
    [Description("Selects the Vulkan descriptor update backend. Legacy uses direct descriptor writes; Template enables descriptor update templates when supported.")]
    public EVulkanDescriptorUpdateBackend DescriptorUpdateBackend
    {
        get => _descriptorUpdateBackend;
        set => SetField(ref _descriptorUpdateBackend, value);
    }

    [Category("Vulkan")]
    [DisplayName("Dynamic Uniform Buffer")]
    [Description("When enabled, per-draw engine uniforms use a shared ring buffer with dynamic offsets instead of individual per-mesh UBOs. Reduces descriptor pool pressure at the cost of ring buffer memory.")]
    public bool DynamicUniformBufferEnabled
    {
        get => _dynamicUniformBufferEnabled;
        set => SetField(ref _dynamicUniformBufferEnabled, value);
    }
}