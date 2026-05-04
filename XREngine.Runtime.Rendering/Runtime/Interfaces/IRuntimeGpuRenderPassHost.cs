using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Backend-owned GPU pass object consumed by runtime command collection and visibility recording.
/// </summary>
public interface IRuntimeGpuRenderPassHost
{
    /// <summary>
    /// Gets the logical render-pass index this GPU pass contributes to.
    /// </summary>
    int RenderPass { get; }

    /// <summary>
    /// Gets the maximum number of render commands the backend pass can consume.
    /// </summary>
    uint CommandCapacity { get; }

    /// <summary>
    /// Gets the number of active views configured for the current pass, normally one for mono or two for stereo.
    /// </summary>
    uint ActiveViewCount { get; }

    /// <summary>
    /// Gets the view whose indirect command stream should be consumed by APIs that require a single source view.
    /// </summary>
    uint IndirectSourceViewId { get; }

    /// <summary>
    /// Configures the per-view descriptors and constants used by the backend GPU pass.
    /// </summary>
    void ConfigureViewSet(ReadOnlySpan<GPUViewDescriptor> descriptors, ReadOnlySpan<GPUViewConstants> constants);

    /// <summary>
    /// Selects the view whose indirect command data should be used for single-source indirect draws.
    /// </summary>
    void SetIndirectSourceViewId(uint viewId);

    /// <summary>
    /// Reads aggregate visible draw, instance, and overflow counts produced by the backend pass.
    /// </summary>
    void GetVisibleCounts(out uint drawCount, out uint instanceCount, out uint overflowMarker);

    /// <summary>
    /// Reads the visible draw count produced for one configured view.
    /// </summary>
    uint ReadPerViewDrawCount(uint viewId);
}
