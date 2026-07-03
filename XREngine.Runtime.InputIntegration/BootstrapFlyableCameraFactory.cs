using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Runtime.InputIntegration;

public interface IBootstrapInputBridge
{
    XRComponent? CreateFlyableCameraPawn(SceneNode cameraNode);
    void SetFlyableCameraRenderOnDemand(XRComponent pawn, bool enabled);
}

public static class BootstrapInputBridge
{
    public static IBootstrapInputBridge? Current { get; set; }
}
