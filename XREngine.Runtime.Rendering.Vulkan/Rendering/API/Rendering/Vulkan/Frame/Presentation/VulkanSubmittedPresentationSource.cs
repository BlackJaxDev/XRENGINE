using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retains the exact native source of the last accepted authored desktop frame.
/// Descriptor preparation is speculative and must never advance this receipt.
/// Access is serialized by the desktop frame loop.
/// </summary>
internal sealed class VulkanSubmittedPresentationSource : IDisposable
{
    private VulkanPresentationSourceTuple _source;
    private VulkanResidentTemplateDependencyLease? _lease;

    /// <summary>
    /// Publishes only after graphics submission accepts the source's final draw.
    /// Same-queue ordering makes the source available to subsequent replay without
    /// waiting on the CPU for GPU completion.
    /// </summary>
    internal bool TryCommit(
        in VulkanPresentationSourceTuple source,
        VulkanResourceRuntime resources,
        VulkanCommandSynchronizationState synchronization,
        out string failureReason)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = source.Aspect,
            LevelCount = 1,
            LayerCount = 1,
        };
        if (!source.HasLogicalSource || !source.IsComplete)
        {
            failureReason = "The accepted presentation source is incomplete.";
            return false;
        }
        if (!synchronization.TryGetSubmittedImageLayout(source.Image, in range, out ImageLayout layout) ||
            layout == ImageLayout.Undefined)
        {
            failureReason = $"The accepted presentation source image 0x{source.Image.Handle:X} " +
                $"generation {source.ImageAllocationGeneration} has no submitted layout (layout={layout}, aspect={source.Aspect}).";
            return false;
        }

        // Keep steady-state publication allocation-free. Only a new native
        // generation needs a new structural lease, not every descriptor slot.
        if (!resources.TryValidateRetainedPresentationSourceForReplay(source, _lease, out _))
        {
            if (!resources.TryValidatePresentationSourceForReplay(source, out failureReason))
                return false;

            Span<VulkanResidentTemplateDependencyRequest> dependencies =
                stackalloc VulkanResidentTemplateDependencyRequest[3];
            dependencies[0] = new(EVulkanResidentTemplateDependencyKind.Image,
                source.Image.Handle, source.ImageAllocationGeneration);
            dependencies[1] = new(EVulkanResidentTemplateDependencyKind.ImageView,
                source.ImageView.Handle, source.ImageViewGeneration);
            dependencies[2] = new(EVulkanResidentTemplateDependencyKind.Sampler,
                source.Sampler.Handle, source.SamplerGeneration);
            if (!resources.TryAcquireResidentTemplateDependencies(dependencies, out var lease, out var reason))
            {
                failureReason = reason ?? "The submitted presentation source could not be retained.";
                return false;
            }

            VulkanResidentTemplateDependencyLease? previous = _lease;
            _lease = lease;
            // Recorded replay commands independently pin their native uses.
            previous?.Dispose();
        }

        _source = source with { ExpectedLayout = layout };
        failureReason = string.Empty;
        return true;
    }

    /// <summary>Returns the retained submission receipt, never a pending descriptor publication.</summary>
    internal void Capture(out VulkanPresentationSourceTuple source, out VulkanResidentTemplateDependencyLease? lease)
    {
        source = _source;
        lease = _lease;
    }

    public void Dispose()
    {
        _source = default;
        _lease?.Dispose();
        _lease = null;
    }
}
