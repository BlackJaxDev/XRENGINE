namespace XREngine.Rendering;

/// <summary>
/// Publishes descriptor resources whose complete identity and layout are owned
/// by an explicit monotonic generation.
/// </summary>
/// <remarks>
/// Implementations must advance <see cref="ResourceGeneration"/> whenever a
/// published texture, view, sampler, image, buffer, array membership, layout,
/// or binding-relevant resource identity changes. Published managed resource
/// wrappers are retained by immutable renderer artifacts until their owning
/// frame work is retired.
/// </remarks>
public interface IRenderResourceBindingPublisher : IRenderBindingPublisher
{
    /// <summary>
    /// Gets a non-zero monotonic generation for all descriptor resources
    /// emitted by <see cref="PublishResources"/>.
    /// </summary>
    ulong ResourceGeneration { get; }

    /// <summary>
    /// Publishes descriptor resources and any tightly coupled numeric metadata.
    /// </summary>
    void PublishResources(
        XRRenderProgram vertexProgram,
        XRRenderProgram materialProgram);
}
