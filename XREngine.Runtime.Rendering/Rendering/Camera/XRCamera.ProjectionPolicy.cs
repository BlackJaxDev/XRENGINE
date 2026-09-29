namespace XREngine.Rendering;

public partial class XRCamera
{
    /// <summary>Whether this camera has an authored world-space oblique near clipping plane.</summary>
    public bool HasObliqueNearClippingPlane => _obliqueNearClippingPlane.HasValue;
}
