using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    public bool TryFocusPresented(UIInteractableComponent target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsActiveInHierarchy || RefreshPresentedInput() is not { } publication)
            return false;
        try
        {
            if (!_inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord record) ||
                !record.IsInteractable || !record.IsTabStop ||
                record.FocusScopeIdentity != publication.ActiveFocusScopeIdentity ||
                !IsCurrentHitRecord(record) || !target.CanInteract ||
                GetCameraCanvas() is not { IsActiveInHierarchy: true } canvas ||
                !ReferenceEquals(target.UserInterfaceCanvas, canvas))
                return false;
            FocusMouseTarget(target);
            return ReferenceEquals(FocusedComponent, target);
        }
        finally { publication.Release(); }
    }
}
