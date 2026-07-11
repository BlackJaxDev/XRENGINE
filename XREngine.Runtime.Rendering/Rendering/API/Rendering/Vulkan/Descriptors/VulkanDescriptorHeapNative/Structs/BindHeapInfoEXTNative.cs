using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BindHeapInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public DeviceAddressRangeEXTNative HeapRange;
    public ulong ReservedRangeOffset;
    public ulong ReservedRangeSize;
}
