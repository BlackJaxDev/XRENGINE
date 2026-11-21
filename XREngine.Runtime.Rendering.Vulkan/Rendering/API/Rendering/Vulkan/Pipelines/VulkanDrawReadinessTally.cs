namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Counts the pipeline results of one draw readiness request over the required pass contexts of one frame, and gives the
/// combined state. A struct with no allocation; the frame-loop readiness prewarm makes one for each request in each frame.
/// </summary>
internal struct VulkanDrawReadinessTally
{
    /// <summary>The number of required pass contexts that were checked.</summary>
    public int Required { get; private set; }

    /// <summary>The number of required pass contexts whose pipelines are ready.</summary>
    public int Ready { get; private set; }

    /// <summary>The number of required pass contexts whose pipelines failed with a result that a retry cannot clear.</summary>
    public int Failed { get; private set; }

    private string? _firstFailureReason;
    private string? _firstPendingReason;

    /// <summary>Adds the result of one required pass context.</summary>
    /// <param name="ready">True when the pipelines of the context are ready.</param>
    /// <param name="retryable">True when a not-ready result can clear in a later frame (a compile runs).</param>
    /// <param name="reason">The reason of a not-ready result.</param>
    public void Add(bool ready, bool retryable, string reason)
    {
        Required++;
        if (ready)
        {
            Ready++;
            return;
        }

        if (retryable)
        {
            _firstPendingReason ??= reason;
            return;
        }

        Failed++;
        _firstFailureReason ??= reason;
    }

    /// <summary>
    /// Gives the combined state: failed when any context failed, not started when no context was required, compiling when any
    /// context is not ready, and ready only when every required context is ready.
    /// </summary>
    /// <param name="noContextReason">The reason to give when no context was required.</param>
    /// <param name="reason">The reason of the state.</param>
    public readonly ERenderDrawReadiness ToState(string noContextReason, out string reason)
    {
        if (Failed > 0)
        {
            reason = _firstFailureReason ?? "a graphics pipeline of the view failed";
            return ERenderDrawReadiness.Failed;
        }

        if (Required == 0)
        {
            reason = noContextReason;
            return ERenderDrawReadiness.NotStarted;
        }

        if (Ready < Required)
        {
            reason = _firstPendingReason ?? "a graphics pipeline of the view compiles";
            return ERenderDrawReadiness.Compiling;
        }

        reason = "Ready";
        return ERenderDrawReadiness.Ready;
    }
}
