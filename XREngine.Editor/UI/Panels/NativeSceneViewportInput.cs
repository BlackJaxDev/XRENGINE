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
        ref Vector2 normalizedPoint, XREngine.Rendering.UI.State.UIElementIdentity identity,
        long revision, ulong lifetime, out NativeScenePointerOwner owner)
    {
        owner = default;
        SceneViewPanel? panel = Panel;
        XRViewport? ownerViewport = pawn.Viewport;
        XRViewport? sceneViewport = panel?.SceneViewport;
        if (!CanInteract || panel?.Window is not { IsFocused: true } window ||
            ownerViewport is null || !ReferenceEquals(ownerViewport.Window, window) ||
            sceneViewport is null || !ReferenceEquals(sceneViewport.Window, window) ||
            sceneViewport.CameraComponent is not { } camera ||
            sceneViewport.World is not { } world ||
            !SceneViewPanel.CameraBelongsToWorld(camera, world) ||
            !ReferenceEquals(world, ownerViewport.World) ||
            !ReferenceEquals(input.OwningPawn, pawn) ||
            !ReferenceEquals(input.GetCameraCanvas(), UserInterfaceCanvas))
            return false;
        if (!panel.IsDockedSceneViewport(sceneViewport) &&
            !TryMapToWindowViewport(sceneViewport, ref normalizedPoint))
            return false;
        owner = new(this, input, sceneViewport, window, camera,
            world, identity, revision, lifetime);
        return true;
    }

    /// <summary>
    /// Converts a point from this panel's normalized space to the normalized space of the window viewport
    /// that renders the scene behind the editor UI. The canvas uses window pixels with the origin at the
    /// bottom left, the same space as the viewport region.
    /// </summary>
    private bool TryMapToWindowViewport(XRViewport viewport, ref Vector2 normalizedPoint)
    {
        var region = BoundableTransform.AxisAlignedRegion;
        if (viewport.Width <= 0 || viewport.Height <= 0 || region.Width <= 0 || region.Height <= 0)
            return false;
        float x = region.X + normalizedPoint.X * region.Width;
        float y = region.Y + normalizedPoint.Y * region.Height;
        normalizedPoint = new Vector2((x - viewport.X) / viewport.Width, (y - viewport.Y) / viewport.Height);
        return normalizedPoint.X is >= 0.0f and <= 1.0f && normalizedPoint.Y is >= 0.0f and <= 1.0f;
    }
}
