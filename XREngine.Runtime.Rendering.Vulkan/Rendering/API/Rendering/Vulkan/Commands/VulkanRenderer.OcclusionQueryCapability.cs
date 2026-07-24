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
        => EnqueueOcclusionQueryBegin(query);

    /// <inheritdoc />
    public bool EndOcclusionQuery(XRRenderQuery query)
        => EnqueueOcclusionQueryEnd(query);

    /// <inheritdoc />
    public ERenderQueryReadStatus WriteTimestamp(XRRenderQuery query)
        => EnqueueTimestampQuery(query)
            ? ERenderQueryReadStatus.Ready
            : ERenderQueryReadStatus.InvalidState;

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
