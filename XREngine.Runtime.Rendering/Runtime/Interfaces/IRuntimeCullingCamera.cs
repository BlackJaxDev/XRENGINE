using XREngine.Data.Geometry;

namespace XREngine.Rendering;

/// <summary>
/// Camera contract for systems that need culling volumes in addition to render matrices.
/// </summary>
public interface IRuntimeCullingCamera : IRuntimeRenderCamera
{
    /// <summary>
    /// Builds the world-space frustum used for culling this camera.
    /// </summary>
    Frustum WorldFrustum();

    /// <summary>
    /// Gets the world-space orthographic camera bounds when the camera is orthographic.
    /// </summary>
    BoundingRectangleF? GetOrthoCameraBounds();
}
