using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retrieves vendor binary debug information for a KHR device fault.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal unsafe delegate Result VkGetDeviceFaultDebugInfoKhrDelegate(
    Device device,
    VulkanKhrDeviceFaultDebugInfo* pDebugInfo);
