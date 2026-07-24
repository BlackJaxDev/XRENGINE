using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PushDataInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public uint Offset;
    public HostAddressRangeConstEXTNative Data;
}
