using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

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
