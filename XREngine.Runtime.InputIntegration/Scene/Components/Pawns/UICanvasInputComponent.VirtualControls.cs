using System.Numerics;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{

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
            RefreshPresentedInput() is not { } publication)
            return false;
        if (!publication.TryMapPointer(screenPosition, out canvasPosition, requireInsideViewport))
            return false;
        return float.IsFinite(canvasPosition.X) && float.IsFinite(canvasPosition.Y) &&
            (!requireInsideViewport || publication.CanvasBounds.Contains(canvasPosition));
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

    /// <summary>Resolves button delivery from current device coordinates without waiting for hover publication.</summary>
    private UIInteractableComponent? FindMouseInteractTarget()
    {
        if (TryGetTouchMouseTarget(out UIInteractableComponent? target))
            return target;
        if (GetOwningInput() is not LocalInputInterface { Mouse: { } mouse })
            return null;

        Vector2 position = mouse.CursorPosition;
        if (TryGetContactCoordinate(position, true, out Vector2 canvasPosition))
            CursorPositionWorld2D = canvasPosition;
        return FindContactInteractable(position, out _, out _, out _);
    }

    private UIInteractableComponent? FindContactInteractable(Vector2 screenPosition, out Vector2 localPosition, out bool overflow, out bool hasUiHit)
    {
        localPosition = default;
        overflow = false;
        hasUiHit = false;
        if (OwningPawn?.Viewport is not XRViewport viewport || GetCameraCanvas() is not { } canvas ||
            RefreshPresentedInput() is not { } publication)
            return null;
        if (!publication.TryMapPointer(screenPosition, out Vector2 position) ||
            !publication.CanvasBounds.Contains(position))
            return null;

        UIHitRecord? blocker = null;
        foreach (ref readonly UIHitRecord record in publication.Hits)
        {
            if (!record.BlocksInputBehind || !IsCurrentHitRecord(record) || !record.Contains(position))
                continue;
            if (blocker is not { } previous || IsBetterInputCandidate(record.Depth,
                record.LayerIndex, record.IndexWithinLayer, previous.Depth, previous.LayerIndex, previous.IndexWithinLayer))
                blocker = record;
        }
        UIHitRecord? best = null;
        foreach (ref readonly UIHitRecord record in publication.Hits)
        {
            if (!record.IsInteractable || !IsCurrentHitRecord(record) || !record.Contains(position) ||
                blocker is { } front && !record.IsInSubtreeOf(front))
                continue;
            if (best is not { } previous || IsBetterInputCandidate(record.Depth,
                record.LayerIndex, record.IndexWithinLayer, previous.Depth, previous.LayerIndex, previous.IndexWithinLayer))
                best = record;
        }
        hasUiHit = best is not null || blocker is not null;
        if (best is not { } selected)
            return null;
        // Contact events can precede the hover tick. Retain the exact accepted record for dispatch.
        _inputRecords[selected.MembershipId] = selected;
        localPosition = Vector2.Transform(position, selected.CanvasToLocal);
        return selected.RenderInfo.Owner as UIInteractableComponent;
    }
}
