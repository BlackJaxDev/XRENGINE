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

        public readonly bool IsComplete()
            => GraphicsFamilyIndex.HasValue && PresentFamilyIndex.HasValue;
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
        var indices = new QueueFamilyIndices();

        uint queueFamilityCount = 0;
        Api!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilityCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilityCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
        {
            Api!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilityCount, queueFamiliesPtr);
        }


        for (uint i = 0; i < queueFamilies.Length; i++)
        {
            QueueFamilyProperties queueFamily = queueFamilies[i];
            if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                indices.GraphicsFamilyIndex = i;
                indices.GraphicsFamilySupportsCompute = queueFamily.QueueFlags.HasFlag(QueueFlags.ComputeBit);
            }

            if (queueFamily.QueueFlags.HasFlag(QueueFlags.ComputeBit) &&
                (!indices.ComputeFamilyIndex.HasValue || !queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit)))
            {
                indices.ComputeFamilyIndex = i;
            }

            if (queueFamily.QueueFlags.HasFlag(QueueFlags.TransferBit) &&
                (!indices.TransferFamilyIndex.HasValue ||
                 (!queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit) && !queueFamily.QueueFlags.HasFlag(QueueFlags.ComputeBit))))
            {
                indices.TransferFamilyIndex = i;
            }

            khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, surface, out var presentSupport);

            if (presentSupport)
                indices.PresentFamilyIndex = i;

            if (indices.IsComplete())
                break;
        }

        indices.ComputeFamilyIndex ??= indices.GraphicsFamilyIndex;
        indices.TransferFamilyIndex ??= indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex;

        return indices;
    }
}
