using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable identity and publication metadata for one frame-data range
/// referenced by a prepared mesh draw.
/// </summary>
internal readonly record struct VulkanPreparedFrameDataPayloadHandle(
    VkBufferHandle Storage,
    ulong Offset,
    uint Range,
    uint DescriptorSet,
    uint DescriptorBinding,
    int FrameIndex,
    int DrawUniformSlot,
    ulong ArenaGeneration,
    EVulkanBindingFrequencyMask FrequencyMask,
    VulkanAutoUniformPublicationSnapshot Publication,
    ulong MaterialGeneration)
{
    internal bool IsValidFor(
        int frameIndex,
        int drawUniformSlot,
        ulong arenaGeneration)
        => Storage.Handle != 0 &&
            Range != 0 &&
            FrameIndex == frameIndex &&
            DrawUniformSlot == drawUniformSlot &&
            ArenaGeneration == arenaGeneration &&
            FrequencyMask != EVulkanBindingFrequencyMask.None;

    internal bool ReferencesFrequency(EVulkanBindingFrequency frequency)
    {
        int bitIndex = (int)frequency - 1;
        if ((uint)bitIndex >= 7u)
            return false;

        return (FrequencyMask & (EVulkanBindingFrequencyMask)(1 << bitIndex)) !=
            EVulkanBindingFrequencyMask.None;
    }

    internal ulong GetContentGeneration(EVulkanBindingFrequency frequency)
        => Publication.GetGeneration(frequency, MaterialGeneration);
}
