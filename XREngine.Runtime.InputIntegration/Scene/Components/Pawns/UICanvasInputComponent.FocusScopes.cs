using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private readonly List<(UIElementIdentity Scope, UIInteractableComponent? Target,
        UIElementIdentity TargetIdentity)> _focusReturns = [];
    private UIElementIdentity _activeFocusScope;
    private bool _hasFocusScope;
    private bool _changingFocusScope;

    private void SynchronizeFocusScope(UIPublication? publication)
    {
        if (_changingFocusScope)
            return;
        if (publication is null)
        {
            _focusReturns.Clear();
            _hasFocusScope = false;
            return;
        }
        UIElementIdentity next = publication.ActiveFocusScopeIdentity;
        if (_hasFocusScope && next == _activeFocusScope)
            return;
        bool hadScope = _hasFocusScope;
        UIElementIdentity previous = _activeFocusScope;
        _activeFocusScope = next;
        _hasFocusScope = true;
        if (!hadScope && publication.FocusScopes.Length == 0)
            return;

        _changingFocusScope = true;
        try
        {
            UIInteractableComponent? restore = null;
            int returnIndex = -1;
            for (int index = _focusReturns.Count - 1; index >= 0; index--)
                if (_focusReturns[index].Scope == next)
                {
                    returnIndex = index;
                    break;
                }
            if (returnIndex >= 0)
            {
                var saved = _focusReturns[returnIndex];
                _focusReturns.RemoveRange(returnIndex, _focusReturns.Count - returnIndex);
                if (saved.Target is { } target && target.UITransform.ElementIdentity == saved.TargetIdentity &&
                    IsFocusTargetInScope(target, publication))
                    restore = target;
            }
            else
            {
                // Limit references retained by a long series of nested popups.
                if (_focusReturns.Count == 32)
                    _focusReturns.RemoveAt(0);
                UIInteractableComponent? current = FocusedComponent;
                _focusReturns.Add((previous, current, current?.UITransform.ElementIdentity ?? default));
            }
            if (restore is null)
            {
                int index = UIFocusNavigation.FindTabTarget(publication, 0, false, next);
                if (index >= 0 && publication.GetHitRecord(index).RenderInfo.Owner is UIInteractableComponent target &&
                    IsFocusTargetInScope(target, publication))
                    restore = target;
            }
            FocusMouseTarget(restore);
        }
        finally { _changingFocusScope = false; }
    }

    private bool IsFocusTargetInScope(UIInteractableComponent target, UIPublication publication)
        => _inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord record) &&
            IsCurrentHitRecord(record) && record.FocusScopeIdentity == publication.ActiveFocusScopeIdentity;
}
