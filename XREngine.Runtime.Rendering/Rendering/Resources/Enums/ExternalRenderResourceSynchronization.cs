namespace XREngine.Rendering.Resources;

/// <summary>
/// Defines the synchronization boundary that makes an imported resource safe
/// for pipeline use.
/// </summary>
public enum ExternalRenderResourceSynchronization
{
    /// <summary>
    /// The resource is synchronized at the frame boundary, meaning it is safe to use
    /// within the scope of a single frame without additional synchronization.
    /// </summary>
    FrameBoundary,
    /// <summary>
    /// The resource's synchronization is managed by the caller, meaning the caller is responsible
    /// for ensuring the resource is safe to use within the pipeline.
    /// </summary>
    CallerProvided,
    /// <summary>
    /// The resource uses an acquire-release synchronization model, where the pipeline acquires
    /// the resource before use and releases it afterward.
    /// </summary>
    AcquireRelease,
    /// <summary>
    /// The resource's synchronization is managed by the backend, meaning the backend ensures
    /// the resource is safe to use within the pipeline.
    /// </summary>
    BackendManaged,
}
