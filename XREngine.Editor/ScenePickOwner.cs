using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.Editor;

/// <summary>Canonical pick request owner, including an optional retained viewport hit.</summary>
internal readonly record struct ScenePickOwner(
    XRViewport Viewport,
    XRWindow Window,
    CameraComponent Camera,
    object World,
    NativeScenePointerOwner? Native)
{
    public bool IsCurrent(EditorFlyingCameraPawnComponent pawn, bool requireContact)
        => pawn.IsActiveInHierarchy && Window.IsFocused &&
            ReferenceEquals(pawn.Viewport?.Window, Window) &&
            ReferenceEquals(pawn.Viewport?.World, World) &&
            ReferenceEquals(Viewport.Window, Window) &&
            ReferenceEquals(Viewport.CameraComponent, Camera) &&
            ReferenceEquals(Viewport.World, World) &&
            (Native is { } native
                ? native.IsCurrent(pawn, requireContact)
                : !requireContact || !pawn.IsHoveringUI() && !Engine.Input.IsUIInputCaptured);
}
