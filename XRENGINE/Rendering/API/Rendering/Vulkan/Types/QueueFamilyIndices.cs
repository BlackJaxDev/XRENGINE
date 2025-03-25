using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public struct QueueFamilyIndices
    {
        public uint? GraphicsFamilyIndex { get; set; }
        public uint? PresentFamilyIndex { get; set; }

        public readonly bool IsComplete()
            => GraphicsFamilyIndex.HasValue;
    }

    private QueueFamilyIndices? _familyQueueIndicesCache = null;
    public QueueFamilyIndices FamilyQueueIndices => _familyQueueIndicesCache ??= FindQueueFamilies(_physicalDevice);

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
                indices.GraphicsFamilyIndex = i;

            khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, surface, out var presentSupport);

            if (presentSupport)
                indices.PresentFamilyIndex = i;

            if (indices.IsComplete())
                break;
        }

        return indices;
    }
}