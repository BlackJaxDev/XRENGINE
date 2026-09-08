namespace XREngine.Rendering;

/// <summary>
/// Reports whether a framebuffer blit was accepted by the active renderer.
/// Acceptance only means that an immediate backend issued the operation or a
/// deferred backend retained it in its frame plan; it never certifies GPU completion.
/// </summary>
public readonly record struct FrameBufferBlitSubmission(bool Accepted, bool Completed, string? Reason = null)
{
    public static FrameBufferBlitSubmission CompletedImmediately()
        => new(true, true);

    public static FrameBufferBlitSubmission Enqueued()
        => new(true, false);

    public static FrameBufferBlitSubmission Rejected(string reason)
        => new(false, false, reason);
}
