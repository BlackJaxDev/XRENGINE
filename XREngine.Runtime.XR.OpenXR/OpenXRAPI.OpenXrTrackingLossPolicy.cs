namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    public enum OpenXrTrackingLossPolicy
    {
        FreezeLastValid = 0,
        Identity = 1,
        SkipFrame = 2
    }
}
