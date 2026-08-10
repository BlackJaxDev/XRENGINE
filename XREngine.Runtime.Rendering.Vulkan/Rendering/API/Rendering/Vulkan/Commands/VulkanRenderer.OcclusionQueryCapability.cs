namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IOcclusionQueryBackendCapability
{
    /// <inheritdoc />
    public bool EnsureQueryGenerated(XRRenderQuery query)
    {
        VkRenderQuery? apiQuery = GenericToAPI<VkRenderQuery>(query);
        if (apiQuery is null)
            return false;
        if (!apiQuery.IsGenerated)
            apiQuery.Generate();
        return true;
    }

    /// <inheritdoc />
    public bool BeginOcclusionQuery(XRRenderQuery query)
        => query.Descriptor.Kind == ERenderQueryKind.Occlusion &&
           EnqueuePreparedQuery(query, ERenderQueryOperation.Begin);

    /// <inheritdoc />
    public bool EndOcclusionQuery(XRRenderQuery query)
        => query.Descriptor.Kind == ERenderQueryKind.Occlusion &&
           EnqueuePreparedQuery(query, ERenderQueryOperation.End);

    /// <inheritdoc />
    public ERenderQueryReadStatus WriteTimestamp(XRRenderQuery query)
        => query.Descriptor.Kind is ERenderQueryKind.Timestamp or ERenderQueryKind.ElapsedTime &&
           EnqueuePreparedQuery(query, ERenderQueryOperation.WriteTimestamp)
            ? ERenderQueryReadStatus.Ready
            : ERenderQueryReadStatus.InvalidState;

    private bool EnqueuePreparedQuery(XRRenderQuery query, ERenderQueryOperation operation)
    {
        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null ||
            GenericToAPI<VkRenderQuery>(query) is not { } apiQuery)
        {
            return false;
        }

        FrameOpContext context = CaptureFrameOpContext();
        int passIndex = EnsureValidPassIndex(
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            "Query",
            context.PassMetadata);
        return _frameOperationQueue.EnqueuePreparedQuery(
            apiQuery,
            query.Descriptor,
            operation,
            passIndex,
            ResolveCurrentFrameOpDrawTarget(),
            context);
    }

    /// <inheritdoc />
    public ERenderQueryReadStatus TryGetTimestamp(XRRenderQuery query, out TimestampQueryResult result)
    {
        VkRenderQuery? apiQuery = GenericToAPI<VkRenderQuery>(query);
        if (apiQuery is not null)
            return apiQuery.TryGetTimestamp(out result);

        result = default;
        return ERenderQueryReadStatus.InvalidState;
    }

    /// <inheritdoc />
    public ERenderQueryReadStatus TryGetAnySamplesPassed(
        XRRenderQuery query,
        out OcclusionQueryResult result,
        in RenderQueryTicket expectedTicket = default)
    {
        VkRenderQuery? apiQuery = GenericToAPI<VkRenderQuery>(query);
        if (apiQuery is not null)
            return apiQuery.TryGetAnySamplesPassed(out result, expectedTicket);

        result = default;
        return ERenderQueryReadStatus.InvalidState;
    }

    /// <inheritdoc />
    public RenderQueryTicket GetTicket(XRRenderQuery query)
        => GenericToAPI<VkRenderQuery>(query)?.Ticket ?? default;
}
