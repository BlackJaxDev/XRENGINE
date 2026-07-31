using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public struct QueueFamilyIndices
    {
        public uint? GraphicsFamilyIndex { get; set; }
        public uint? PresentFamilyIndex { get; set; }
        public uint? ComputeFamilyIndex { get; set; }
        public uint? TransferFamilyIndex { get; set; }
        /// <summary>
        /// Whether the selected graphics family can execute compute commands recorded
        /// into the primary graphics command stream.
        /// </summary>
        public bool GraphicsFamilySupportsCompute { get; set; }

        public readonly bool IsComplete(bool requirePresentQueue = true)
            => GraphicsFamilyIndex.HasValue &&
                (!requirePresentQueue || PresentFamilyIndex.HasValue);
    }

    private QueueFamilyIndices? _familyQueueIndicesCache = null;
    public QueueFamilyIndices FamilyQueueIndices
    {
        get
        {
            if (_familyQueueIndicesCache.HasValue)
                return _familyQueueIndicesCache.Value;

            // Capability surfaces can be inspected while the renderer is being constructed.
            // Do not cache the empty result; physical-device selection will populate the real value.
            if (_physicalDevice.Handle == 0)
                return default;

            return (_familyQueueIndicesCache = FindQueueFamilies(_physicalDevice)).Value;
        }
    }

    private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
    {
        VulkanPhysicalDeviceCapabilitySnapshot snapshot =
            VulkanDeviceCapabilityQuery.Query(Api!, device);
        return VulkanQueueFamilySelector.Select(
            snapshot.QueueFamilyArray,
            _targetDriver.RequiresPresentQueue ? khrSurface : null,
            device,
            surface);
    }
}
