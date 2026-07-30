using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct DescriptorMappingSourceDataEXTNative
{
    [FieldOffset(0)] public DescriptorMappingSourceConstantOffsetEXTNative ConstantOffset;
    [FieldOffset(0)] public DescriptorMappingSourcePushIndexEXTNative PushIndex;
    [FieldOffset(0)] public DescriptorMappingSourceHeapDataEXTNative HeapData;
    [FieldOffset(0)] public uint PushDataOffset;
    [FieldOffset(0)] public uint PushAddressOffset;
}
