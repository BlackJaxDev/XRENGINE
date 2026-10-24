using System.Numerics;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    /// <summary>
    /// Samples the current device pointer through one accepted presentation's hit test,
    /// local transform and size. The live layout is never used for the returned UV.
    /// </summary>
    public bool TryGetPresentedPointerUv(UIInteractableComponent target, out Vector2 normalizedPoint)
        => TryGetPresentedPointerUv(target, out normalizedPoint, out _, out _, out _);

    public bool TryGetPresentedPointerUv(UIInteractableComponent target, out Vector2 normalizedPoint,
        out UIElementIdentity identity, out long interactionRevision, out ulong canvasLifetime)
    {
        if (TryGetPresentedPointerTarget(out UIInteractableComponent? current, out normalizedPoint,
            out identity, out interactionRevision, out canvasLifetime) &&
            ReferenceEquals(current, target))
            return true;
        normalizedPoint = default;
        identity = default;
        interactionRevision = 0;
        canvasLifetime = 0;
        return false;
    }

    public bool TryGetPresentedPointerTarget(out UIInteractableComponent? target,
        out Vector2 normalizedPoint, out UIElementIdentity identity,
        out long interactionRevision, out ulong canvasLifetime)
    {
        target = null;
        normalizedPoint = default;
        identity = default;
        interactionRevision = 0;
        canvasLifetime = 0;
        if (!IsActiveInHierarchy || GetOwningInput() is not LocalInputInterface { Mouse: { } mouse })
            return false;
        target = FindContactInteractable(mouse.CursorPosition, out Vector2 localPoint, out _, out _);
        if (target is null ||
            !_inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord record) ||
            !IsCurrentHitRecord(record) ||
            !record.TryNormalizeLocal(localPoint, out normalizedPoint))
            return false;
        identity = record.Identity;
        interactionRevision = record.InteractionRevision;
        canvasLifetime = _inputPublication?.CanvasLifetime ?? 0;
        return canvasLifetime != 0;
    }

    /// <summary>Checks a captured hit owner without requiring the cursor to stay inside it.</summary>
    public bool IsPresentedPointerOwnerCurrent(UIInteractableComponent target,
        UIElementIdentity identity, long interactionRevision, ulong canvasLifetime)
    {
        if (!IsActiveInHierarchy)
            return false;
        UIPublication? publication = RefreshPresentedInput();
        if (publication is null)
            return false;
        try
        {
            return publication.CanvasLifetime == canvasLifetime &&
                _inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord record) &&
                record.Identity == identity && record.InteractionRevision == interactionRevision &&
                IsCurrentHitRecord(record) && target.CanInteract;
        }
        finally { publication.Release(); }
    }
    /// <summary>Checks camera blocking against displayed geometry, including an owned pointer drag.</summary>
    public bool IsPointerBlockingCamera()
    {
        if (!IsActiveInHierarchy || _inputDeactivating || _inputTerminal)
            return false;
        if (_pressedTarget is not null && HasCurrentPointerPressOwner())
            return true;
        if (RefreshPresentedInput() is not { } publication)
            return false;
        try
        {
            TopMostInteractable = null;
            if (!IsActiveInHierarchy || _inputDeactivating || _inputTerminal ||
                !ReferenceEquals(publication, _inputPublication))
                return false;
            Vector2 outputPosition = GetOwningInput() is LocalInputInterface { Mouse: { } mouse }
                ? mouse.CursorPosition : _cursorOutputPosition;
            if (!publication.TryMapPointer(outputPosition, out Vector2 position))
                return false;
            UIHitRecord? best = FindPresentedPointerHit(publication, position, out bool blocked);
            TopMostInteractable = best?.RenderInfo.Owner as UIInteractableComponent;
            return TopMostInteractable is not null || blocked;
        }
        finally { publication.Release(); }
    }

    /// <summary>Samples one displayed hit and mapping for a queued menu or editor action.</summary>
    public bool TryGetPresentedPointerPosition(UIInteractableComponent target, out Vector2 canvasPosition)
    {
        canvasPosition = default;
        if (!IsActiveInHierarchy || _inputDeactivating || _inputTerminal ||
            RefreshPresentedInput() is not { } publication)
            return false;
        try
        {
            if (!IsActiveInHierarchy || _inputDeactivating || _inputTerminal ||
                !ReferenceEquals(publication, _inputPublication))
                return false;
            Vector2 outputPosition = GetOwningInput() is LocalInputInterface { Mouse: { } mouse }
                ? mouse.CursorPosition : _cursorOutputPosition;
            if (!publication.TryMapPointer(outputPosition, out Vector2 position) ||
                !publication.CanvasBounds.Contains(position))
                return false;
            UIHitRecord? selected = FindPresentedPointerHit(publication, position, out _);
            if (selected is not { } hit || !ReferenceEquals(hit.RenderInfo.Owner, target))
                return false;
            canvasPosition = position;
            return true;
        }
        finally { publication.Release(); }
    }

    private UIHitRecord? FindPresentedPointerHit(UIPublication publication, Vector2 position, out bool blocked)
    {
        UIHitRecord? blocker = null;
        for (int page = 0; page < publication.HitRecordPageCount; page++)
            foreach (ref readonly UIHitRecord record in publication.GetHitRecordPage(page))
                if (record.BlocksInputBehind && IsCurrentHitRecord(record) && record.Contains(position) &&
                    (blocker is not { } prior || IsBetterInputCandidate(record.Depth,
                        record.LayerIndex, record.IndexWithinLayer, prior.Depth, prior.LayerIndex, prior.IndexWithinLayer)))
                    blocker = record;
        UIHitRecord? best = null;
        for (int page = 0; page < publication.HitRecordPageCount; page++)
            foreach (ref readonly UIHitRecord record in publication.GetHitRecordPage(page))
            {
                if (!record.IsInteractable || !IsCurrentHitRecord(record) || !record.Contains(position) ||
                    blocker is { } front && !record.IsInSubtreeOf(front))
                    continue;
                if (best is not { } prior || IsBetterInputCandidate(record.Depth,
                    record.LayerIndex, record.IndexWithinLayer, prior.Depth, prior.LayerIndex, prior.IndexWithinLayer))
                    best = record;
            }
        blocked = blocker is not null;
        return best;
    }

}
