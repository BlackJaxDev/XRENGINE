using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Generation-owned, serial-checked reservation made before image acquisition.</summary>
internal readonly record struct VulkanWsiPresentReservation(
    VulkanWsiPresentCompletion? Owner, Fence Fence, int Slot, ulong Serial);
