using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DescriptorMappingSourceConstantOffsetEXTNative
{
    public uint HeapOffset;
    public uint HeapArrayStride;
    public SamplerCreateInfo* EmbeddedSampler;
    public uint SamplerHeapOffset;
    public uint SamplerHeapArrayStride;
}
