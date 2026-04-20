using XREngine.Components;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap.Builders;

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