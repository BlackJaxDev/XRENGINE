namespace XREngine.Rendering.Residency;

/// <summary>
/// Gives access to the shared <see cref="ITextureResidencyRequests"/> seam of imported texture streaming.
/// </summary>
public static class TextureResidencyRequests
{
    /// <summary>
    /// The residency request seam of the shared imported texture streaming manager. The value is the same object for
    /// the life of the process.
    /// </summary>
    /// <remarks>
    /// The first read can create the shared manager on the calling thread. The creation only sets fields: it starts no
    /// thread, allocates no GPU resource, and does not need a render host. The runtime makes the creation thread-safe.
    /// Any thread can read it. It does not throw.
    /// </remarks>
    public static ITextureResidencyRequests Shared
        => ImportedTextureStreamingManager.Instance;
}
