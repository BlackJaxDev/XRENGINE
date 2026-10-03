using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private readonly RenderInfo2D?[] _contactHits = new RenderInfo2D?[256];

    private LocalPlayerController? OwningLocalPlayer => OwningPawn?.Controller as LocalPlayerController;
    private bool IsVirtualGamepadDispatch => OwningLocalPlayer?.IsVirtualGamepadDispatch ?? false;
    private bool IsVirtualMouseConsumed => OwningLocalPlayer?.VirtualMouseConsumedThisFrame ?? false;
    private bool IsTouchMouseCancelled => OwningLocalPlayer?.TouchMouseCancellationPending ?? false;

    internal bool CanRouteVirtualInput(LocalPlayerController owner)
        => IsActiveInHierarchy && ReferenceEquals(OwningPawn?.Controller, owner) &&
            ReferenceEquals(owner.ControlledPawn, OwningPawn) &&
            GetCameraCanvas() is { IsActiveInHierarchy: true } canvas && canvas.CanvasTransform.IsVisibleInHierarchy;

    internal bool TryGetContactCoordinate(Vector2 screenPosition, bool requireInsideViewport, out Vector2 canvasPosition)
    {
        canvasPosition = default;
        if (OwningPawn?.Viewport is not XRViewport viewport || GetCameraCanvas() is not { } canvas ||
            viewport.Width <= 0 || viewport.Height <= 0)
            return false;

        Vector2 viewportPosition = viewport.ScreenToViewportCoordinate(screenPosition);
        if (requireInsideViewport && (viewportPosition.X < 0 || viewportPosition.Y < 0 ||
            viewportPosition.X > viewport.Width || viewportPosition.Y > viewport.Height))
            return false;
        Vector2 normalized = viewport.NormalizeViewportCoordinate(viewportPosition);
        normalized.Y = 1 - normalized.Y;
        var transform = canvas.CanvasTransform;
        XRCamera? camera = transform.DrawSpace switch
        {
            ECanvasDrawSpace.Screen => canvas.Camera2D,
            ECanvasDrawSpace.Camera => transform.CameraSpaceCamera ?? viewport.ActiveCamera,
            _ => null,
        };
        Vector2? result = GetUICoordinate(viewport, camera, normalized, transform, transform.DrawSpace);
        if (result is not { } coordinate || !float.IsFinite(coordinate.X) || !float.IsFinite(coordinate.Y))
            return false;
        canvasPosition = coordinate;
        return true;
    }

    /// <summary>Uses the same canvas coordinates, input blockers and ordering as ordinary UI hit testing.</summary>
    internal UIVirtualInputComponent? FindVirtualControl(Vector2 screenPosition, out Vector2 localPosition, out bool overflow, out bool hasUiHit)
        => FindContactInteractable(screenPosition, out localPosition, out overflow, out hasUiHit) as UIVirtualInputComponent;

    private bool TryGetTouchMouseTarget(out UIInteractableComponent? target)
    {
        target = null;
        if (OwningLocalPlayer is not { IsTouchMouseDispatch: true } player)
            return false;
        Vector2 position = player.TouchMousePosition;
        if (TryGetContactCoordinate(position, true, out Vector2 canvasPosition))
            CursorPositionWorld2D = canvasPosition;
        target = FindContactInteractable(position, out _, out _, out _);
        if (target is UIVirtualInputComponent)
            target = null;
        return true;
    }

    private UIInteractableComponent? FindContactInteractable(Vector2 screenPosition, out Vector2 localPosition, out bool overflow, out bool hasUiHit)
    {
        localPosition = default;
        overflow = false;
        hasUiHit = false;
        if (!TryGetContactCoordinate(screenPosition, true, out Vector2 position) ||
            GetCameraCanvas()?.VisualScene2D is not { } scene)
            return null;

        bool complete = scene.TryFindInputIntersections(position, _contactHits.AsSpan(), out int count);
        try
        {
            if (!complete)
            {
                overflow = true;
                return null;
            }
            TransformBase? blocker = null;
            int blockerDepth = int.MinValue;
            for (int index = 0; index < count; index++)
            {
                if (_contactHits[index]?.Owner is not UIComponent ui || !IsContactHit(ui, position) ||
                    ui.Transform is not UIBoundableTransform { BlocksInputBehind: true } bounds)
                    continue;
                if (bounds.Depth > blockerDepth)
                {
                    blocker = bounds;
                    blockerDepth = bounds.Depth;
                }
            }

            UIInteractableComponent? best = null;
            int depth = int.MinValue, layer = int.MinValue, order = int.MinValue;
            for (int index = 0; index < count; index++)
            {
                if (_contactHits[index] is not { } hit || hit.Owner is not UIInteractableComponent ui ||
                    !IsContactHit(ui, position) || (blocker is not null && !IsDescendantOfOrSelf(ui.Transform, blocker)))
                    continue;
                if (!IsBetterInputCandidate(ui.Transform.Depth, hit.LayerIndex, hit.IndexWithinLayer, depth, layer, order))
                    continue;
                best = ui;
                depth = ui.Transform.Depth;
                layer = hit.LayerIndex;
                order = hit.IndexWithinLayer;
            }
            hasUiHit = best is not null || blocker is not null;
            if (best is not null)
                localPosition = best.UITransform.CanvasToLocal(position);
            return best;
        }
        finally
        {
            Array.Clear(_contactHits, 0, count);
        }
    }

    private static bool IsContactHit(UIComponent component, Vector2 canvasPosition)
        => UIClipRegion.ContainsHit(component, canvasPosition);
}
