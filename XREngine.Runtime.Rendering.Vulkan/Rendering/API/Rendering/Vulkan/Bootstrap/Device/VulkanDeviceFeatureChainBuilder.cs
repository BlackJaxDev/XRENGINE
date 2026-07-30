using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Stack-only builder for a Vulkan feature <c>pNext</c> chain. Feature nodes
/// remain in the caller's stack frame and are never captured or allocated.
/// </summary>
internal unsafe ref struct VulkanDeviceFeatureChainBuilder
{
    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanStructureHeader
    {
        public StructureType SType;
        public void* PNext;
    }

    public void* Head { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prepend<T>(ref T feature, bool enabled = true)
        where T : unmanaged
    {
        if (!enabled)
            return;

        VulkanStructureHeader* header = (VulkanStructureHeader*)Unsafe.AsPointer(ref feature);
        header->PNext = Head;
        Head = header;
    }
}
