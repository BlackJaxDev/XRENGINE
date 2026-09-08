namespace XREngine.Rendering;

/// <summary>
/// Transfers a resolved history candidate and its immutable output contract to
/// one backend-owned submission path.
/// </summary>
internal readonly struct RenderFrameViewHistoryBackendReservation(
    RenderFrameViewHistoryCandidateToken candidate,
    RenderOutputRequest output,
    XRFrameBuffer? targetFrameBuffer,
    int backendSlot,
    uint backendGeneration)
{
    internal RenderFrameViewHistoryCandidateToken Candidate { get; } = candidate;
    internal RenderOutputRequest Output { get; } = output;
    /// <summary>
    /// Frozen physical output selected by the pipeline invocation. A null
    /// framebuffer denotes the backend's default presentation target.
    /// </summary>
    internal XRFrameBuffer? TargetFrameBuffer { get; } = targetFrameBuffer;
    internal int BackendSlot { get; } = backendSlot;
    internal uint BackendGeneration { get; } = backendGeneration;
    internal bool IsValid => Candidate.IsValid && BackendSlot >= 0 && BackendGeneration != 0U;
}
