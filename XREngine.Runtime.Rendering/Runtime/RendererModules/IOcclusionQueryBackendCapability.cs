namespace XREngine.Rendering;

/// <summary>
/// Begins and ends backend hardware occlusion queries without exposing API wrappers.
/// </summary>
public interface IOcclusionQueryBackendCapability
{
    bool EnsureQueryGenerated(XRRenderQuery query);
    bool BeginOcclusionQuery(XRRenderQuery query);
    bool EndOcclusionQuery(XRRenderQuery query);
    ERenderQueryReadStatus WriteTimestamp(XRRenderQuery query);
    ERenderQueryReadStatus TryGetTimestamp(XRRenderQuery query, out TimestampQueryResult result);
    ERenderQueryReadStatus TryGetAnySamplesPassed(
        XRRenderQuery query,
        out OcclusionQueryResult result,
        in RenderQueryTicket expectedTicket = default);
    RenderQueryTicket GetTicket(XRRenderQuery query);
}
