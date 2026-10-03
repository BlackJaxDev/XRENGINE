namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    public enum OpenXrCollectVisiblePosePolicy
    {
        Predicted = 0,
        RelocatePredicted = 1,
        PaddedFrustum = 2
    }
}
