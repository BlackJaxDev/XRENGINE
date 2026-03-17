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

public static class BootstrapFlyableCameraFactory
{
    public static PawnComponent CreateFlyableCameraPawn(SceneNode cameraNode, bool allowEditorPawn)
    {
        ArgumentNullException.ThrowIfNull(cameraNode);

        PawnComponent? pawn = allowEditorPawn
            ? BootstrapInputBridge.Current?.CreateFlyableCameraPawn(cameraNode)
            : null;

        return pawn ?? cameraNode.AddComponent<FlyingCameraPawnComponent>()!;
    }
}