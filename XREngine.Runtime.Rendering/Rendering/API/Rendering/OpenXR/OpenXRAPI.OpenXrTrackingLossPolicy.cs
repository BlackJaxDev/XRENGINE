namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    public enum OpenXrTrackingLossPolicy
    {
        FreezeLastValid,
        Identity,
        SkipFrame
    }
}
