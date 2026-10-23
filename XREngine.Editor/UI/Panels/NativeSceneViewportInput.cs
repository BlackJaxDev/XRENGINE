using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>The presented hit surface for a native scene viewport in its owning canvas.</summary>
internal sealed class NativeSceneViewportInput : UIInteractableComponent
{
    public SceneViewPanel? Panel { get; set; }

    public NativeSceneViewportInput()
    {
        RegisterInputsOnFocus = false;
        IsTabStop = false;
        AccessibilityLabel = "Scene viewport";
    }

    public bool TrySample(EditorFlyingCameraPawnComponent pawn, UICanvasInputComponent input,
        Vector2 normalizedPoint, XREngine.Rendering.UI.State.UIElementIdentity identity,
        long revision, ulong lifetime, out NativeScenePointerOwner owner)
    {
        owner = default;
        SceneViewPanel? panel = Panel;
        XRViewport? ownerViewport = pawn.Viewport;
        XRViewport? sceneViewport = panel?.SceneViewport;
        if (!CanInteract || panel?.Window is not { IsFocused: true } window ||
            ownerViewport is null || !ReferenceEquals(ownerViewport.Window, window) ||
            sceneViewport is null || !ReferenceEquals(sceneViewport.Window, window) ||
            sceneViewport.CameraComponent is null || sceneViewport.World is null ||
            !ReferenceEquals(sceneViewport.World, ownerViewport.World) ||
            !ReferenceEquals(input.OwningPawn, pawn) ||
            !ReferenceEquals(input.GetCameraCanvas(), UserInterfaceCanvas))
            return false;
        owner = new(this, input, sceneViewport, window, sceneViewport.CameraComponent,
            sceneViewport.World, identity, revision, lifetime);
        return true;
    }
}
