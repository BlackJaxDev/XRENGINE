namespace XREngine.Rendering;

/// <summary>
/// GPU timestamp queries of the current renderer for <see cref="TextureStreamingGpuTimer"/>, through the backend-neutral
/// <see cref="IOcclusionQueryBackendCapability"/> (OpenGL <c>glQueryCounter</c> and a non-blocking result read). The queries
/// are created on first use on the render thread, and again for each new renderer. When the renderer has no timestamp support, nothing is timed.
/// </summary>
internal sealed class RendererTextureStreamingTimestampSource(int queryCount) : ITextureStreamingTimestampSource
{
    private readonly XRRenderQuery?[] _queries = new XRRenderQuery?[queryCount];

    /// <inheritdoc />
    public int QueryCount => _queries.Length;

    /// <inheritdoc />
    public bool TryWriteTimestamp(int query)
    {
        if (!TryGetCapability(out IOcclusionQueryBackendCapability? capability) || capability is null)
            return false;

        XRRenderQuery? renderQuery = _queries[query];
        if (renderQuery is null)
        {
            renderQuery = new XRRenderQuery(RenderQueryDescriptor.Timestamp);
            _queries[query] = renderQuery;
        }

        // The current renderer can be a new one (for example, after a window or context change), so make sure that its
        // query object exists before each write. The call does nothing when the query exists.
        if (!capability.EnsureQueryGenerated(renderQuery))
            return false;

        return capability.WriteTimestamp(renderQuery) == ERenderQueryReadStatus.Ready;
    }

    /// <inheritdoc />
    public ERenderQueryReadStatus TryReadTimestamp(int query, out ulong nanoseconds)
    {
        nanoseconds = 0UL;
        XRRenderQuery? renderQuery = _queries[query];
        if (renderQuery is null || !TryGetCapability(out IOcclusionQueryBackendCapability? capability) || capability is null)
            return ERenderQueryReadStatus.Unsupported;

        ERenderQueryReadStatus status = capability.TryGetTimestamp(renderQuery, out TimestampQueryResult result);
        nanoseconds = result.Nanoseconds;
        return status;
    }

    private static bool TryGetCapability(out IOcclusionQueryBackendCapability? capability)
    {
        capability = null;
        IRuntimeRendererHost? renderer = AbstractRenderer.Current;
        return renderer is not null && renderer.TryGetBackendCapability(out capability);
    }
}
