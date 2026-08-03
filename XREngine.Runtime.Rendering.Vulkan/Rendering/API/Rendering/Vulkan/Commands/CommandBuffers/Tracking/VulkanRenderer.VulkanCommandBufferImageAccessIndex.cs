using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed class VulkanCommandBufferImageAccessIndex(int initialCapacity = 32)
    {
        private readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> _states = new Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState>(initialCapacity);

        public int Count => _states.Count;

        public void Clear()
            => _states.Clear();

        public void Record(
            ulong imageHandle,
            in ImageSubresourceRange range,
            in VulkanImageAccessState state)
        {
            uint levelCount = Math.Max(range.LevelCount, 1u);
            uint layerCount = Math.Max(range.LayerCount, 1u);
            for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
            {
                uint mip = range.BaseMipLevel + mipOffset;
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint layer = range.BaseArrayLayer + layerOffset;
                    RecordAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, state);
                    RecordAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, state);
                    RecordAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, state);
                }
            }
        }

        public bool TryGet(
            ulong imageHandle,
            in ImageSubresourceRange range,
            out VulkanImageAccessState state)
        {
            VulkanImageAccessState? combined = null;
            uint levelCount = Math.Max(range.LevelCount, 1u);
            uint layerCount = Math.Max(range.LayerCount, 1u);
            for (uint mipOffset = 0; mipOffset < levelCount; mipOffset++)
            {
                uint mip = range.BaseMipLevel + mipOffset;
                for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
                {
                    uint layer = range.BaseArrayLayer + layerOffset;
                    if (!TryMergeAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.ColorBit, ref combined) ||
                        !TryMergeAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.DepthBit, ref combined) ||
                        !TryMergeAspect(imageHandle, mip, layer, range.AspectMask, ImageAspectFlags.StencilBit, ref combined))
                    {
                        state = VulkanImageAccessState.Undefined;
                        return false;
                    }
                }
            }

            state = combined ?? VulkanImageAccessState.Undefined;
            return combined.HasValue;
        }

        private void RecordAspect(
            ulong imageHandle,
            uint mip,
            uint layer,
            ImageAspectFlags rangeAspect,
            ImageAspectFlags trackedAspect,
            in VulkanImageAccessState state)
        {
            if ((rangeAspect & trackedAspect) == 0)
                return;

            _states[new VulkanTrackedImageSubresource(imageHandle, mip, layer, trackedAspect)] = state;
        }

        private bool TryMergeAspect(
            ulong imageHandle,
            uint mip,
            uint layer,
            ImageAspectFlags rangeAspect,
            ImageAspectFlags trackedAspect,
            ref VulkanImageAccessState? combined)
        {
            if ((rangeAspect & trackedAspect) == 0)
                return true;

            VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, trackedAspect);
            if (!_states.TryGetValue(key, out VulkanImageAccessState current) ||
                current.Layout == ImageLayout.Undefined)
                return false;

            if (!combined.HasValue)
            {
                combined = current;
                return true;
            }

            VulkanImageAccessState prior = combined.Value;
            if (prior.Layout != current.Layout ||
                (prior.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                 current.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                 prior.QueueFamilyIndex != current.QueueFamilyIndex))
                return false;

            combined = prior with
            {
                StageMask = prior.StageMask | current.StageMask,
                AccessMask = prior.AccessMask | current.AccessMask,
                QueueFamilyIndex = prior.QueueFamilyIndex != Vk.QueueFamilyIgnored
                    ? prior.QueueFamilyIndex
                    : current.QueueFamilyIndex,
                ExpectedDescriptorLayout = prior.ExpectedDescriptorLayout == current.ExpectedDescriptorLayout
                    ? prior.ExpectedDescriptorLayout
                    : ImageLayout.Undefined,
                Serial = Math.Max(prior.Serial, current.Serial),
            };
            return true;
        }
    }
}

