using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Version 1: a 16-byte root referencing one immutable, 16-byte-aligned 64-byte parameter block.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal readonly struct VulkanAdvancedNativeShadingAddressRoot(ulong parameterAddress, uint kernelIndex, uint flags)
{
    [FieldOffset(0)] internal readonly ulong ParameterAddress = parameterAddress;
    [FieldOffset(8)] internal readonly uint KernelIndex = kernelIndex;
    [FieldOffset(12)] internal readonly uint Flags = flags;
}
