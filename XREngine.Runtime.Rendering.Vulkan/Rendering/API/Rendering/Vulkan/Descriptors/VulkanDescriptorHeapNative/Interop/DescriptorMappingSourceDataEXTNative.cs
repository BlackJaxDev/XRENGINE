using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

// The largest native union member is VkDescriptorMappingSourceIndirectIndexEXT
// (56 bytes on the supported 64-bit ABI), even though this bridge uses PushIndex.
[StructLayout(LayoutKind.Explicit, Size = 56)]
internal unsafe struct DescriptorMappingSourceDataEXTNative
{
    [FieldOffset(0)] public DescriptorMappingSourceConstantOffsetEXTNative ConstantOffset;
    [FieldOffset(0)] public DescriptorMappingSourcePushIndexEXTNative PushIndex;
    [FieldOffset(0)] public DescriptorMappingSourceHeapDataEXTNative HeapData;
    [FieldOffset(0)] public uint PushDataOffset;
    [FieldOffset(0)] public uint PushAddressOffset;
}
