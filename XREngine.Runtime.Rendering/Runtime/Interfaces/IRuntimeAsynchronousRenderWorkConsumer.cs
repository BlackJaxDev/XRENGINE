namespace XREngine.Rendering;

/// <summary>
/// A backend owner of asynchronous render work, such as one desktop recorder.
/// Every member runs on the render owner thread. Members must not wait for worker
/// or GPU completion.
/// </summary>
public interface IRuntimeAsynchronousRenderWorkConsumer
{
    /// <summary>
    /// True while the consumer owns a building or submitted CPU batch, joined output
    /// that is not yet submitted or discarded, or an unfinished drain.
    /// </summary>
    bool HasUnresolvedWork { get; }

    /// <summary>
    /// The renderer that owns this consumer's resources. A renderer-scoped drain
    /// drains only the consumers of that renderer.
    /// </summary>
    AbstractRenderer Renderer { get; }

    /// <summary>
    /// Earliest absolute <see cref="System.Diagnostics.Stopwatch"/> deadline of the
    /// consumer's outstanding CPU work, or <see cref="long.MaxValue"/> when none.
    /// </summary>
    long NextDeadlineTicks { get; }

    /// <summary>
    /// Checks completion and deadlines. It can join a completed batch or discard
    /// stale output, but it never submits or presents output.
    /// </summary>
    void ServiceAsynchronousWork();

    /// <summary>
    /// Cancels and joins CPU work, discards unsubmitted output, and transfers
    /// accepted work to backend retirement. Returns true when the consumer holds no
    /// CPU ownership and no pending output.
    /// </summary>
    bool DrainAsynchronousWork();
}
