using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.UI.State;

namespace XREngine.Editor;

/// <summary>Identity of one accepted native scene hit in its owning input canvas.</summary>
internal readonly record struct NativeScenePointerOwner(
    NativeSceneViewportInput Pane,
    UICanvasInputComponent Input,
    XRViewport Viewport,
    XRWindow Window,
    CameraComponent Camera,
    object World,
    UIElementIdentity ElementIdentity,
    long InteractionRevision,
    ulong CanvasLifetime)
{
    public bool IsCurrent(EditorFlyingCameraPawnComponent pawn, bool requireContact)
    {
        if (!pawn.IsActiveInHierarchy || !Pane.CanInteract ||
            !ReferenceEquals(Pane.Panel?.Window, Window) || !Window.IsFocused ||
            !ReferenceEquals(Pane.Panel?.SceneViewport, Viewport) ||
            !ReferenceEquals(pawn.Viewport?.Window, Window) ||
            !ReferenceEquals(pawn.Viewport?.World, World) ||
            !ReferenceEquals(Viewport.Window, Window) ||
            !ReferenceEquals(Viewport.CameraComponent, Camera) ||
            !ReferenceEquals(Viewport.World, World) ||
            !ReferenceEquals(Input.OwningPawn, pawn) ||
            !ReferenceEquals(Input.GetCameraCanvas(), Pane.UserInterfaceCanvas) ||
            !Input.IsPresentedPointerOwnerCurrent(Pane, ElementIdentity,
                InteractionRevision, CanvasLifetime))
            return false;
        if (!requireContact)
            return true;
        return Input.TryGetPresentedPointerUv(Pane, out _, out UIElementIdentity identity,
            out long revision, out ulong lifetime) &&
            identity == ElementIdentity && revision == InteractionRevision && lifetime == CanvasLifetime;
    }
}
