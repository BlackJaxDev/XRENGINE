using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct ResourceDescriptorDataEXTNative
{
    [FieldOffset(0)] public ImageDescriptorInfoEXTNative* Image;
    [FieldOffset(0)] public TexelBufferDescriptorInfoEXTNative* TexelBuffer;
    [FieldOffset(0)] public DeviceAddressRangeEXTNative* AddressRange;
    [FieldOffset(0)] public void* TensorARM;
}
