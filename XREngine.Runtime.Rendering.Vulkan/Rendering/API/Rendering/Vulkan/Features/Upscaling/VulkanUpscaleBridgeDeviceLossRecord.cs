using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures the first device-loss result reported by the isolated upscale bridge
/// device before later Vulkan calls can obscure its origin.
/// </summary>
internal sealed record VulkanUpscaleBridgeDeviceLossRecord(
    DateTimeOffset ObservedUtc,
    string Operation,
    Result Result,
    string DeviceName,
    uint VendorId,
    uint DeviceId,
    int SlotIndex,
    EVulkanUpscaleBridgeVendor? DispatchVendor,
    ulong AllocationGeneration,
    ulong PublicationGeneration,
    string? ResourceName);
