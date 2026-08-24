using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retrieves KHR device fault reports from a logical device.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal unsafe delegate Result VkGetDeviceFaultReportsKhrDelegate(
    Device device,
    ulong timeout,
    uint* pFaultCounts,
    VulkanKhrDeviceFaultInfo* pFaultInfo);
