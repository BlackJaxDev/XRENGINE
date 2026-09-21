namespace XREngine.Rendering;

/// <summary>
/// Marks a deferred publication that copies one source into the desktop window.
/// The marker carries engine references only; the Vulkan backend resolves native
/// descriptor identity after selecting the accepted draw from the sealed plan.
/// </summary>
public interface IWindowPresentationBindingPublisher : IDeferredRenderBindingPublisher
{
    /// <summary>Gets the source captured for one desktop presentation token.</summary>
    bool TryGetWindowPresentationSource(
        ulong token,
        out XRTexture? sourceTexture,
        out XRFrameBuffer? sourceFrameBuffer);
}
