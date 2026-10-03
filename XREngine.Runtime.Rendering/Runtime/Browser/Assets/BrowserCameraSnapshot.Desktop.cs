namespace XREngine.Rendering;

public readonly partial record struct BrowserCameraSnapshot
{
    /// <summary>Copies the render-space view and zero-to-one depth projection of a standard desktop camera.</summary>
    public static BrowserCameraSnapshot FromXRCamera(XRCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        // System.Numerics perspective and orthographic matrices already use zero-to-one NDC depth.
        // Custom, OpenXR and OpenVR projections require an explicit depth contract.
        if (camera.Parameters.GetType() != typeof(XRPerspectiveCameraParameters) &&
            camera.Parameters.GetType() != typeof(XROrthographicCameraParameters))
            throw new NotSupportedException("Browser camera export requires a standard perspective or orthographic projection.");
        if (camera.HasObliqueNearClippingPlane)
            throw new NotSupportedException("Browser camera export does not support an authored oblique near clipping plane.");
        // Camera.ProjectionMatrix includes the active native backend's clip-depth conversion,
        // reversed depth policy and temporal jitter. Export the authored zero-to-one projection.
        return new BrowserCameraSnapshot(camera.Transform.InverseRenderMatrix, camera.Parameters.GetProjectionMatrix());
    }
}
