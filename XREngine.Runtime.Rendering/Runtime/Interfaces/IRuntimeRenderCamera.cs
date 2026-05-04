using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

/// <summary>
/// Camera data required by runtime rendering code without exposing the concrete camera component.
/// </summary>
public interface IRuntimeRenderCamera
{
    /// <summary>
    /// Gets the world transform used to position the camera.
    /// </summary>
    TransformBase Transform { get; }

    /// <summary>
    /// Gets the projection matrix used for the current camera pass.
    /// </summary>
    Matrix4x4 ProjectionMatrix { get; }

    /// <summary>
    /// Gets the near clipping plane distance.
    /// </summary>
    float NearZ { get; }

    /// <summary>
    /// Gets the far clipping plane distance.
    /// </summary>
    float FarZ { get; }

    /// <summary>
    /// Gets whether this camera represents the left stereo eye, right stereo eye, or a non-stereo camera.
    /// </summary>
    bool? StereoEyeLeft { get; }

    /// <summary>
    /// Returns whether this camera renders objects assigned to the supplied scene layer.
    /// </summary>
    bool RendersLayer(int layer);

    /// <summary>
    /// Computes the signed distance from the render near plane to a world-space point.
    /// </summary>
    float DistanceFromRenderNearPlane(Vector3 point);
}
