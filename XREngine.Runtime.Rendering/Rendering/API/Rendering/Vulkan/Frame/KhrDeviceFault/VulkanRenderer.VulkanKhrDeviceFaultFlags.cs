using System;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Provides constants, structures, and enumerations for handling KHR device fault reporting in Vulkan.
    /// </summary>
    [Flags]
    private enum VulkanKhrDeviceFaultFlags : uint
    {
        DeviceLost = 0x00000001,
        MemoryAddress = 0x00000002,
        InstructionAddress = 0x00000004,
        Vendor = 0x00000008,
        WatchdogTimeout = 0x00000010,
        Overflow = 0x00000020,
    }
}
