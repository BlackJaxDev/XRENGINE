using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns the optional EXT descriptor-heap native function table.</summary>
internal unsafe sealed class VulkanDescriptorHeapNativeFunctions
{
    private delegate* unmanaged[Stdcall]<CommandBuffer, BindHeapInfoEXTNative*, void> _cmdBindSamplerHeap;
    private delegate* unmanaged[Stdcall]<CommandBuffer, BindHeapInfoEXTNative*, void> _cmdBindResourceHeap;
    private delegate* unmanaged[Stdcall]<CommandBuffer, PushDataInfoEXTNative*, void> _cmdPushData;
    private delegate* unmanaged[Stdcall]<PhysicalDevice, DescriptorType, ulong> _getPhysicalDeviceDescriptorSize;
    private delegate* unmanaged[Stdcall]<Device, uint, SamplerCreateInfo*, HostAddressRangeEXTNative*, Result> _writeSamplerDescriptors;
    private delegate* unmanaged[Stdcall]<Device, uint, ResourceDescriptorInfoEXTNative*, HostAddressRangeEXTNative*, Result> _writeResourceDescriptors;

    public bool TryLoad(Vk api, Instance instance, Device device, out string reason)
    {
        reason = string.Empty;
        _cmdBindSamplerHeap = (delegate* unmanaged[Stdcall]<CommandBuffer, BindHeapInfoEXTNative*, void>)(nint)api.GetDeviceProcAddr(device, "vkCmdBindSamplerHeapEXT");
        _cmdBindResourceHeap = (delegate* unmanaged[Stdcall]<CommandBuffer, BindHeapInfoEXTNative*, void>)(nint)api.GetDeviceProcAddr(device, "vkCmdBindResourceHeapEXT");
        _cmdPushData = (delegate* unmanaged[Stdcall]<CommandBuffer, PushDataInfoEXTNative*, void>)(nint)api.GetDeviceProcAddr(device, "vkCmdPushDataEXT");
        _writeSamplerDescriptors = (delegate* unmanaged[Stdcall]<Device, uint, SamplerCreateInfo*, HostAddressRangeEXTNative*, Result>)(nint)api.GetDeviceProcAddr(device, "vkWriteSamplerDescriptorsEXT");
        _writeResourceDescriptors = (delegate* unmanaged[Stdcall]<Device, uint, ResourceDescriptorInfoEXTNative*, HostAddressRangeEXTNative*, Result>)(nint)api.GetDeviceProcAddr(device, "vkWriteResourceDescriptorsEXT");
        _getPhysicalDeviceDescriptorSize = (delegate* unmanaged[Stdcall]<PhysicalDevice, DescriptorType, ulong>)(nint)api.GetInstanceProcAddr(instance, "vkGetPhysicalDeviceDescriptorSizeEXT");

        if (_cmdBindSamplerHeap != null && _cmdBindResourceHeap != null && _cmdPushData != null &&
            _writeSamplerDescriptors != null && _writeResourceDescriptors != null)
            return true;

        reason = $"missing entry points: bindSampler={_cmdBindSamplerHeap != null}, bindResource={_cmdBindResourceHeap != null}, pushData={_cmdPushData != null}, writeSampler={_writeSamplerDescriptors != null}, writeResource={_writeResourceDescriptors != null}.";
        return false;
    }

    public void CmdBindSamplerHeap(CommandBuffer commandBuffer, BindHeapInfoEXTNative* bindInfo)
        => _cmdBindSamplerHeap(commandBuffer, bindInfo);
    public void CmdBindResourceHeap(CommandBuffer commandBuffer, BindHeapInfoEXTNative* bindInfo)
        => _cmdBindResourceHeap(commandBuffer, bindInfo);
    public void CmdPushData(CommandBuffer commandBuffer, PushDataInfoEXTNative* pushDataInfo)
        => _cmdPushData(commandBuffer, pushDataInfo);
    public bool TryGetDescriptorSize(PhysicalDevice physicalDevice, DescriptorType descriptorType, out ulong size)
    {
        size = 0;
        if (_getPhysicalDeviceDescriptorSize == null)
            return false;
        size = _getPhysicalDeviceDescriptorSize(physicalDevice, descriptorType);
        return size > 0;
    }
    public Result WriteSamplerDescriptors(Device device, uint samplerCount, SamplerCreateInfo* samplers, HostAddressRangeEXTNative* descriptors)
        => _writeSamplerDescriptors(device, samplerCount, samplers, descriptors);
    public Result WriteResourceDescriptors(Device device, uint resourceCount, ResourceDescriptorInfoEXTNative* resources, HostAddressRangeEXTNative* descriptors)
        => _writeResourceDescriptors(device, resourceCount, resources, descriptors);
    public void ReleaseDelegates()
    {
        _cmdBindSamplerHeap = null;
        _cmdBindResourceHeap = null;
        _cmdPushData = null;
        _getPhysicalDeviceDescriptorSize = null;
        _writeSamplerDescriptors = null;
        _writeResourceDescriptors = null;
    }
}
