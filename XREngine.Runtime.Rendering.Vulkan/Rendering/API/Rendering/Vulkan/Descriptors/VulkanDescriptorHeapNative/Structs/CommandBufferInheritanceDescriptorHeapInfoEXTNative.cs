using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CommandBufferInheritanceDescriptorHeapInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public BindHeapInfoEXTNative* SamplerHeapBindInfo;
    public BindHeapInfoEXTNative* ResourceHeapBindInfo;
}
