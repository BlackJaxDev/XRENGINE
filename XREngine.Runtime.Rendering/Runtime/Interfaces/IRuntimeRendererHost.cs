namespace XREngine.Rendering;

/// <summary>
/// Marker interface for host renderer implementations such as OpenGL or Vulkan renderers.
/// </summary>
public partial interface IRuntimeRendererHost
{
    /// <summary>
    /// Stable backend identity that does not expose a concrete renderer type.
    /// </summary>
    RendererBackendId BackendId => default;

    /// <summary>
    /// Backend module generation that created this renderer.
    /// </summary>
    long BackendGeneration => 0;

    /// <summary>
    /// Gets whether this backend has presented a complete frame that is suitable for
    /// accepting a renderer replacement. Recovery-only frames must return false.
    /// </summary>
    bool IsBackendReplacementFrameReady => true;

    /// <summary>
    /// Resolves an optional backend capability without a concrete renderer cast in stable layers.
    /// </summary>
    bool TryGetBackendCapability<TCapability>(out TCapability? capability)
        where TCapability : class
    {
        capability = this as TCapability;
        return capability is not null;
    }

    /// <summary>
    /// True after the backend detected a terminal graphics-device loss.
    /// </summary>
    bool IsDeviceLost { get; }
}
