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
}
