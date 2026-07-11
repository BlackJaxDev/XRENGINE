using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static unsafe partial class VulkanUpscaleBridgeProbe
{
    private sealed class Candidate
    {
        public PhysicalDevice Device { get; init; }
        public PhysicalDeviceProperties Properties { get; init; }
        public required HashSet<string> ExtensionNames { get; init; }
        public string DeviceName { get; init; } = string.Empty;
        public uint VendorId { get; init; }
        public uint DeviceId { get; init; }
        public uint? GraphicsQueueFamilyIndex { get; init; }
        public bool HasExternalMemory { get; init; }
        public bool HasExternalSemaphore { get; init; }
        public bool HasExternalMemoryWin32 { get; init; }
        public bool HasExternalSemaphoreWin32 { get; init; }
        public bool RendererMatches { get; init; }
        public bool VendorMatches { get; init; }

        public bool SupportsBridgeImport
            => HasExternalMemory && HasExternalSemaphore && HasExternalMemoryWin32 && HasExternalSemaphoreWin32;
    }
}
