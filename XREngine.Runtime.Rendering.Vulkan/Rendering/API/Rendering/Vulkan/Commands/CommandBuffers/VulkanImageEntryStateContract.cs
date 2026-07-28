using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Defines compatibility between the image state present before a cached
/// primary executes and the source state encoded when that primary was
/// recorded.
/// </summary>
internal static class VulkanImageEntryStateContract
{
    /// <summary>
    /// Returns the first correctness-relevant incompatibility. Serial values
    /// are intentionally excluded because they are telemetry, not Vulkan
    /// command identity.
    /// </summary>
    public static EVulkanPrimaryEntryStateMismatch Compare(
        in VulkanRenderer.VulkanImageAccessState actual,
        in VulkanRenderer.VulkanImageAccessState expected)
    {
        if (expected.Layout == ImageLayout.Undefined)
            return EVulkanPrimaryEntryStateMismatch.UnknownExpectedLayout;
        if (actual.Layout == ImageLayout.Undefined)
            return EVulkanPrimaryEntryStateMismatch.UnknownActualLayout;
        if (actual.Layout != expected.Layout)
            return EVulkanPrimaryEntryStateMismatch.Layout;

        if (actual.ResourceGeneration != expected.ResourceGeneration &&
            (actual.ResourceGeneration != 0 || expected.ResourceGeneration != 0))
        {
            return EVulkanPrimaryEntryStateMismatch.ResourceGeneration;
        }

        if (actual.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
            expected.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
            actual.QueueFamilyIndex != expected.QueueFamilyIndex)
        {
            return EVulkanPrimaryEntryStateMismatch.QueueFamily;
        }

        // A recorded source dependency may be broader than the state that is
        // actually present. It must never be narrower, or a producer stage or
        // access can escape the encoded barrier.
        if ((actual.StageMask & ~expected.StageMask) != 0)
            return EVulkanPrimaryEntryStateMismatch.StageMask;
        if ((actual.AccessMask & ~expected.AccessMask) != 0)
            return EVulkanPrimaryEntryStateMismatch.AccessMask;

        if (actual.ExpectedDescriptorLayout != expected.ExpectedDescriptorLayout)
            return EVulkanPrimaryEntryStateMismatch.DescriptorLayout;

        return EVulkanPrimaryEntryStateMismatch.None;
    }
}
