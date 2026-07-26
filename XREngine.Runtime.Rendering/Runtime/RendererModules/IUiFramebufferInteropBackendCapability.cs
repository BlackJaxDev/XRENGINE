namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral information needed by UI integrations that render into an engine framebuffer.
/// </summary>
/// <param name="BindingId">The backend-native framebuffer binding identity.</param>
/// <param name="StencilBits">The stencil precision exposed by the primary color attachment.</param>
public readonly record struct UiFramebufferInteropInfo(uint BindingId, int StencilBits);

/// <summary>
/// Exposes the narrow framebuffer information required by native UI renderers without leaking
/// concrete graphics wrapper types into the stable rendering kernel.
/// </summary>
public interface IUiFramebufferInteropBackendCapability
{
    bool TryGetUiFramebufferInterop(
        XRFrameBuffer frameBuffer,
        out UiFramebufferInteropInfo interopInfo);
}
