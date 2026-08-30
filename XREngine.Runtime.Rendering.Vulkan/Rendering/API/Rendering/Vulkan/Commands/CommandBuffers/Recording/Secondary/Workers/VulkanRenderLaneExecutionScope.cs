namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Publishes the exact lane attachment while a render-domain item encodes
/// native commands. Nested command-chain recording remains inline on this lane
/// instead of recursively dispatching another render-domain batch.
/// </summary>
internal readonly ref struct VulkanRenderLaneExecutionScope
{
    [ThreadStatic]
    private static VulkanRenderLaneFrameAttachment? s_current;

    private readonly VulkanRenderLaneFrameAttachment? _previous;

    internal VulkanRenderLaneExecutionScope(VulkanRenderLaneFrameAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _previous = s_current;
        if (_previous is not null &&
            (_previous.LaneId != attachment.LaneId ||
             _previous.FrameSlot != attachment.FrameSlot))
        {
            throw new InvalidOperationException(
                $"Nested Vulkan recording attempted to migrate from lane {_previous.LaneId}, slot {_previous.FrameSlot} " +
                $"to lane {attachment.LaneId}, slot {attachment.FrameSlot}.");
        }

        s_current = attachment;
    }

    internal static bool TryGetCurrent(out VulkanRenderLaneFrameAttachment? attachment)
    {
        attachment = s_current;
        return attachment is not null;
    }

    public void Dispose()
        => s_current = _previous;
}
