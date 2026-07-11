using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal struct DescriptorMappingSourceHeapDataEXTNative
{
    public uint HeapOffset;
    public uint PushOffset;
}
