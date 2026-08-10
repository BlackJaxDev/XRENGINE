using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned creation of frozen query frame operations.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal bool EnsureQueryGenerated(VkRenderQuery? query)
    {
        if (query is null)
            return false;
        if (!query.IsGenerated)
            query.Generate();
        return true;
    }

    internal bool TryEnqueueQueryOperation(
        VulkanFrameOperationQueue queue,
        bool hasActivePipeline,
        VkRenderQuery? query,
        RenderQueryDescriptor descriptor,
        ERenderQueryOperation operation,
        int currentPassIndex,
        in FrameOpContext context)
    {
        if (!hasActivePipeline || query is null)
            return false;

        int passIndex = EnsureValidPassIndex(currentPassIndex, "Query", context.PassMetadata);
        return EnqueueQueryOperation(
            queue,
            query,
            descriptor,
            operation,
            passIndex,
            ResolveCurrentFrameOpDrawTarget(),
            context);
    }

    internal bool EnqueueQueryOperation(
        VulkanFrameOperationQueue queue,
        VkRenderQuery query,
        RenderQueryDescriptor descriptor,
        ERenderQueryOperation operation,
        int passIndex,
        XRFrameBuffer? target,
        in FrameOpContext context)
        => queue.EnqueuePreparedQuery(query, descriptor, operation, passIndex, target, context);

    internal ERenderQueryReadStatus TryGetTimestamp(
        VkRenderQuery? query,
        out TimestampQueryResult result)
    {
        if (query is not null)
            return query.TryGetTimestamp(out result);

        result = default;
        return ERenderQueryReadStatus.InvalidState;
    }

    internal ERenderQueryReadStatus TryGetAnySamplesPassed(
        VkRenderQuery? query,
        out OcclusionQueryResult result,
        in RenderQueryTicket expectedTicket)
    {
        if (query is not null)
            return query.TryGetAnySamplesPassed(out result, expectedTicket);

        result = default;
        return ERenderQueryReadStatus.InvalidState;
    }

    internal RenderQueryTicket GetQueryTicket(VkRenderQuery? query)
        => query?.Ticket ?? default;
}
