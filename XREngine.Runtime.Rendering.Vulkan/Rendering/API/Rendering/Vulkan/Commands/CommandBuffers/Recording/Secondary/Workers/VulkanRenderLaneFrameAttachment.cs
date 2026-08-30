using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan backend attachment for one stable render lane and scheduler frame
/// slot. It contains one transient/retained arena for every distinct queue
/// family selected by the device.
/// </summary>
internal sealed class VulkanRenderLaneFrameAttachment
{
    private readonly VulkanLaneCommandFamilyArena[] _families;

    internal VulkanRenderLaneFrameAttachment(
        int laneId,
        int frameSlot,
        VulkanLaneCommandFamilyArena[] families,
        uint graphicsQueueFamilyIndex)
    {
        ArgumentNullException.ThrowIfNull(families);
        if (families.Length == 0)
            throw new ArgumentException("At least one Vulkan queue-family arena is required.", nameof(families));

        LaneId = laneId;
        FrameSlot = frameSlot;
        _families = families;
        Graphics = GetFamily(graphicsQueueFamilyIndex);
    }

    internal int LaneId { get; }
    internal int FrameSlot { get; }
    internal int QueueFamilyCount => _families.Length;
    internal VulkanLaneCommandFamilyArena Graphics { get; }

    internal VulkanLaneCommandFamilyArena GetFamilyAt(int index)
        => (uint)index < (uint)_families.Length
            ? _families[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    internal VulkanLaneCommandFamilyArena GetFamily(uint queueFamilyIndex)
    {
        for (int index = 0; index < _families.Length; index++)
            if (_families[index].QueueFamilyIndex == queueFamilyIndex)
                return _families[index];

        throw new InvalidOperationException(
            $"Render lane {LaneId}, frame slot {FrameSlot} has no Vulkan command arena for queue family {queueFamilyIndex}.");
    }
}
