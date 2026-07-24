using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IOcclusionQueryBackendCapability
{
    /// <inheritdoc />
    public bool EnsureQueryGenerated(XRRenderQuery query)
    {
        GLRenderQuery? apiQuery = GenericToAPI<GLRenderQuery>(query);
        if (apiQuery is null)
            return false;
        if (!apiQuery.IsGenerated)
            apiQuery.Generate();
        return true;
    }

    /// <inheritdoc />
    public bool BeginOcclusionQuery(XRRenderQuery query)
        => GenericToAPI<GLRenderQuery>(query)?.BeginQuery() == ERenderQueryReadStatus.Ready;

    /// <inheritdoc />
    public bool EndOcclusionQuery(XRRenderQuery query)
        => GenericToAPI<GLRenderQuery>(query)?.EndQuery() == ERenderQueryReadStatus.Ready;

    /// <inheritdoc />
    public ERenderQueryReadStatus WriteTimestamp(XRRenderQuery query)
        => GenericToAPI<GLRenderQuery>(query)?.WriteTimestamp() ?? ERenderQueryReadStatus.InvalidState;

    /// <inheritdoc />
    public ERenderQueryReadStatus TryGetTimestamp(XRRenderQuery query, out TimestampQueryResult result)
    {
        GLRenderQuery? apiQuery = GenericToAPI<GLRenderQuery>(query);
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
        GLRenderQuery? apiQuery = GenericToAPI<GLRenderQuery>(query);
        if (apiQuery is not null)
            return apiQuery.TryGetAnySamplesPassed(out result, expectedTicket);

        result = default;
        return ERenderQueryReadStatus.InvalidState;
    }

    /// <inheritdoc />
    public RenderQueryTicket GetTicket(XRRenderQuery query)
        => GenericToAPI<GLRenderQuery>(query)?.Ticket ?? default;
}
