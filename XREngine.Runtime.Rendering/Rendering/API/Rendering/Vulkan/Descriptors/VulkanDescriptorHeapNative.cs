using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanDescriptorHeapExt
{
    public const string ExtensionName = "VK_EXT_descriptor_heap";
    public const string ShaderUntypedPointersExtensionName = "VK_KHR_shader_untyped_pointers";

    private const int ExtensionStructureTypeBase = 1000135000;

    public static readonly StructureType TexelBufferDescriptorInfoSType = (StructureType)(ExtensionStructureTypeBase + 0);
    public static readonly StructureType ImageDescriptorInfoSType = (StructureType)(ExtensionStructureTypeBase + 1);
    public static readonly StructureType ResourceDescriptorInfoSType = (StructureType)(ExtensionStructureTypeBase + 2);
    public static readonly StructureType BindHeapInfoSType = (StructureType)(ExtensionStructureTypeBase + 3);
    public static readonly StructureType PushDataInfoSType = (StructureType)(ExtensionStructureTypeBase + 4);
    public static readonly StructureType DescriptorSetAndBindingMappingSType = (StructureType)(ExtensionStructureTypeBase + 5);
    public static readonly StructureType ShaderDescriptorSetAndBindingMappingInfoSType = (StructureType)(ExtensionStructureTypeBase + 6);
    public static readonly StructureType PhysicalDeviceDescriptorHeapPropertiesSType = (StructureType)(ExtensionStructureTypeBase + 8);
    public static readonly StructureType PhysicalDeviceDescriptorHeapFeaturesSType = (StructureType)(ExtensionStructureTypeBase + 9);
    public static readonly StructureType CommandBufferInheritanceDescriptorHeapInfoSType = (StructureType)(ExtensionStructureTypeBase + 10);
    public static readonly StructureType PipelineCreateFlags2CreateInfoSType = (StructureType)1000470005;

    public const BufferUsageFlags DescriptorHeapBufferUsage = (BufferUsageFlags)(1u << 28);
    public const ulong PipelineCreate2DescriptorHeapBit = 1ul << 36;
    public const ulong SamplerHeapReadAccess2 = 1ul << 57;
    public const ulong ResourceHeapReadAccess2 = 1ul << 58;
}

internal enum VulkanDescriptorMappingSourceEXT : uint
{
    HeapWithConstantOffset = 0,
    HeapWithPushIndex = 1,
    HeapWithIndirectIndex = 2,
    HeapWithIndirectIndexArray = 3,
    ResourceHeapData = 4,
    PushData = 5,
    PushAddress = 6,
    IndirectAddress = 7,
    HeapWithShaderRecordIndex = 8,
    ShaderRecordData = 9,
    ShaderRecordAddress = 10,
}

[Flags]
internal enum VulkanSpirvResourceTypeFlagsEXT : uint
{
    Sampler = 1u << 0,
    SampledImage = 1u << 1,
    ReadOnlyImage = 1u << 2,
    ReadWriteImage = 1u << 3,
    CombinedSampledImage = 1u << 4,
    UniformBuffer = 1u << 5,
    ReadOnlyStorageBuffer = 1u << 6,
    ReadWriteStorageBuffer = 1u << 7,
    AccelerationStructure = 1u << 8,
    All = 0x7FFFFFFF,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PhysicalDeviceDescriptorHeapFeaturesEXTNative
{
    public StructureType SType;
    public void* PNext;
    public Bool32 DescriptorHeap;
    public Bool32 DescriptorHeapCaptureReplay;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PhysicalDeviceDescriptorHeapPropertiesEXTNative
{
    public StructureType SType;
    public void* PNext;
    public ulong SamplerHeapAlignment;
    public ulong ResourceHeapAlignment;
    public ulong MaxSamplerHeapSize;
    public ulong MaxResourceHeapSize;
    public ulong MinSamplerHeapReservedRange;
    public ulong MinSamplerHeapReservedRangeWithEmbedded;
    public ulong MinResourceHeapReservedRange;
    public ulong SamplerDescriptorSize;
    public ulong ImageDescriptorSize;
    public ulong BufferDescriptorSize;
    public ulong SamplerDescriptorAlignment;
    public ulong ImageDescriptorAlignment;
    public ulong BufferDescriptorAlignment;
    public ulong MaxPushDataSize;
    public nuint ImageCaptureReplayOpaqueDataSize;
    public uint MaxDescriptorHeapEmbeddedSamplers;
    public uint SamplerYcbcrConversionCount;
    public Bool32 SparseDescriptorHeaps;
    public Bool32 ProtectedDescriptorHeaps;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HostAddressRangeEXTNative
{
    public void* Address;
    public nuint Size;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HostAddressRangeConstEXTNative
{
    public void* Address;
    public nuint Size;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceAddressRangeEXTNative
{
    public ulong Address;
    public ulong Size;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TexelBufferDescriptorInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public Format Format;
    public DeviceAddressRangeEXTNative AddressRange;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ImageDescriptorInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public ImageViewCreateInfo* View;
    public ImageLayout Layout;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct ResourceDescriptorDataEXTNative
{
    [FieldOffset(0)] public ImageDescriptorInfoEXTNative* Image;
    [FieldOffset(0)] public TexelBufferDescriptorInfoEXTNative* TexelBuffer;
    [FieldOffset(0)] public DeviceAddressRangeEXTNative* AddressRange;
    [FieldOffset(0)] public void* TensorARM;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ResourceDescriptorInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public DescriptorType Type;
    public ResourceDescriptorDataEXTNative Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BindHeapInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public DeviceAddressRangeEXTNative HeapRange;
    public ulong ReservedRangeOffset;
    public ulong ReservedRangeSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PushDataInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public uint Offset;
    public HostAddressRangeConstEXTNative Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DescriptorMappingSourceConstantOffsetEXTNative
{
    public uint HeapOffset;
    public uint HeapArrayStride;
    public SamplerCreateInfo* EmbeddedSampler;
    public uint SamplerHeapOffset;
    public uint SamplerHeapArrayStride;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DescriptorMappingSourcePushIndexEXTNative
{
    public uint HeapOffset;
    public uint PushOffset;
    public uint HeapIndexStride;
    public uint HeapArrayStride;
    public SamplerCreateInfo* EmbeddedSampler;
    public Bool32 UseCombinedImageSamplerIndex;
    public uint SamplerHeapOffset;
    public uint SamplerPushOffset;
    public uint SamplerHeapIndexStride;
    public uint SamplerHeapArrayStride;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DescriptorMappingSourceHeapDataEXTNative
{
    public uint HeapOffset;
    public uint PushOffset;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct DescriptorMappingSourceDataEXTNative
{
    [FieldOffset(0)] public DescriptorMappingSourceConstantOffsetEXTNative ConstantOffset;
    [FieldOffset(0)] public DescriptorMappingSourcePushIndexEXTNative PushIndex;
    [FieldOffset(0)] public DescriptorMappingSourceHeapDataEXTNative HeapData;
    [FieldOffset(0)] public uint PushDataOffset;
    [FieldOffset(0)] public uint PushAddressOffset;
}

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

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ShaderDescriptorSetAndBindingMappingInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public uint MappingCount;
    public DescriptorSetAndBindingMappingEXTNative* Mappings;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CommandBufferInheritanceDescriptorHeapInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public BindHeapInfoEXTNative* SamplerHeapBindInfo;
    public BindHeapInfoEXTNative* ResourceHeapBindInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipelineCreateFlags2CreateInfoNative
{
    public StructureType SType;
    public void* PNext;
    public ulong Flags;
}
