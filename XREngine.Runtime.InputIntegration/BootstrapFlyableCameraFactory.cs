using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Runtime.InputIntegration;

public interface IBootstrapInputBridge
{
    PawnComponent? CreateFlyableCameraPawn(SceneNode cameraNode);
}

public static class BootstrapInputBridge
{
    public static IBootstrapInputBridge? Current { get; set; }
}