using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DescriptorSetAndBindingMappingEXTNative
{
    public StructureType SType;
    public void* PNext;
    public uint DescriptorSet;
    public uint FirstBinding;
    public uint BindingCount;
    public VulkanSpirvResourceTypeFlagsEXT ResourceMask;
    public VulkanDescriptorMappingSourceEXT Source;
    public DescriptorMappingSourceDataEXTNative SourceData;
}
