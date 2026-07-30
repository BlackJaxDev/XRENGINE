using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ImageDescriptorInfoEXTNative
{
    public StructureType SType;
    public void* PNext;
    public ImageViewCreateInfo* View;
    public ImageLayout Layout;
}
