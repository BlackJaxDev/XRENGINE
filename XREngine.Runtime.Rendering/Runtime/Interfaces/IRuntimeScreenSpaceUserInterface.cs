using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Screen-space UI hooks used by viewports and render pipelines to size, collect, and draw overlay UI.
/// </summary>
public interface IRuntimeScreenSpaceUserInterface
{
    /// <summary>
    /// Gets whether the UI root should participate in viewport collection and rendering.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Gets whether the UI root is drawn directly in screen space instead of camera space.
    /// </summary>
    bool IsScreenSpace { get; }

    /// <summary>
    /// Resizes the screen-space UI root to match a viewport display size.
    /// </summary>
    void ResizeScreenSpace(Vector2 size);

    /// <summary>
    /// Resizes the camera-space UI root for the supplied camera and camera parameters.
    /// </summary>
    void ResizeCameraSpace(XRCamera camera, XRCameraParameters parameters);

    /// <summary>
    /// Clears camera-space bindings associated with the supplied camera.
    /// </summary>
    void ClearCameraSpaceCamera(XRCamera camera);

    /// <summary>
    /// Attempts to resolve ImGui display metrics for the active viewport or camera-space UI target.
    /// </summary>
    bool TryGetImGuiDisplayMetrics(
        IRuntimeViewportHost? viewport,
        XRCamera? camera,
        out Vector2 displaySize,
        out Vector2 displayPosition,
        out Vector2 framebufferScale);

    /// <summary>
    /// Collects visible screen-space UI items for the supplied viewport.
    /// </summary>
    void CollectVisibleItemsScreenSpace(IRuntimeViewportHost? viewport);

    /// <summary>
    /// Swaps the UI command buffers used by screen-space rendering.
    /// </summary>
    void SwapBuffersScreenSpace();

    /// <summary>
    /// Renders screen-space UI into the current viewport or supplied output framebuffer.
    /// </summary>
    void RenderScreenSpace(IRuntimeViewportHost? viewport, XRFrameBuffer? outputFBO);
}
