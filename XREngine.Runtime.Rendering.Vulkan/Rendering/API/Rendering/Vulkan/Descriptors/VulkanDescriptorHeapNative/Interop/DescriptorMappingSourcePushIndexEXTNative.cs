using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

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
