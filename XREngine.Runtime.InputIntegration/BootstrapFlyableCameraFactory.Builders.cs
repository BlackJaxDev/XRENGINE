using XREngine.Components;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap.Builders;

public static class BootstrapFlyableCameraFactory
{
    private const string DefaultFlyableCameraPawnTypeName = "XREngine.Components.FlyingCameraPawnComponent, XREngine";

    public static XRComponent CreateFlyableCameraPawn(SceneNode cameraNode, bool allowEditorPawn)
    {
        ArgumentNullException.ThrowIfNull(cameraNode);

        XRComponent? pawn = allowEditorPawn
            ? BootstrapInputBridge.Current?.CreateFlyableCameraPawn(cameraNode)
            : null;

        return pawn ?? CreateDefaultFlyableCameraPawn(cameraNode);
    }

    private static XRComponent CreateDefaultFlyableCameraPawn(SceneNode cameraNode)
    {
        Type? pawnType = Type.GetType(DefaultFlyableCameraPawnTypeName, throwOnError: false);
        if (pawnType is null || !typeof(XRComponent).IsAssignableFrom(pawnType))
        {
            throw new InvalidOperationException(
                $"Default flyable camera pawn type '{DefaultFlyableCameraPawnTypeName}' could not be resolved. " +
                "Ensure the XRENGINE runtime assembly is referenced by the host application.");
        }

        return cameraNode.AddComponent(pawnType)
            ?? throw new InvalidOperationException($"Failed to create flyable camera pawn of type '{pawnType.FullName}'.");
    }
}