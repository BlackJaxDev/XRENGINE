using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceAddressRangeEXTNative
{
    public ulong Address;
    public ulong Size;
}
