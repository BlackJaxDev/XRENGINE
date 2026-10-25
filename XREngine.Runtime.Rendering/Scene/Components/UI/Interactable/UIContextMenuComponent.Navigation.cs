using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;

namespace XREngine.Rendering.UI;

public partial class UIContextMenuComponent
{
    private const int MaximumNavigationFocusTicks = 120;
    private readonly WorldTick _navigationFocusTick;
    private UIContextMenuItemComponent? _navigationSource;
    private UIContextMenuItemComponent? _navigationTarget;
    private UICanvasComponent? _navigationCanvas;
    private IUICanvasInputSource? _navigationInput;
    private UIScrollableTransform? _navigationScroll;
    private ObjectConstructionIdentity _navigationCanvasIdentity;
    private ObjectConstructionIdentity _navigationCanvasNodeIdentity;
    private ObjectConstructionIdentity _navigationCanvasTransformIdentity;
    private ObjectConstructionIdentity? _navigationInputIdentity;
    private ObjectConstructionIdentity _navigationScrollIdentity;
    private ObjectConstructionIdentity _navigationScrollNodeIdentity;
    private ulong _navigationShowRevision;
    private ulong _navigationIntentRevision;
    private ulong _navigationSerial;
    private int _navigationTicks;
    private bool _navigationTickRegistered;

    public UIContextMenuComponent() => _navigationFocusTick = NavigationFocusTick;

    private bool RequestItemFocus(UIContextMenuItemComponent source, UIContextMenuItemComponent target,
        ulong showRevision, UICanvasComponent canvas, IUICanvasInputSource input)
    {
        CancelNavigationFocus();
        if (!IsCurrentItem(source, showRevision) || !IsCurrentItem(target, showRevision) ||
            target.Transform.Parent?.Parent is not UIScrollableTransform scroll ||
            !ReferenceEquals(scroll.Parent, Transform) || input.HasPendingFocusRequest)
            return false;
        _navigationSource = source;
        _navigationTarget = target;
        _navigationCanvas = canvas;
        _navigationInput = input;
        _navigationScroll = scroll;
        _navigationCanvasIdentity = ObjectConstructionIdentity.Capture(canvas);
        _navigationCanvasNodeIdentity = ObjectConstructionIdentity.Capture(canvas.SceneNode);
        _navigationCanvasTransformIdentity = ObjectConstructionIdentity.Capture(canvas.Transform);
        _navigationInputIdentity = input is XRObjectBase inputObject
            ? ObjectConstructionIdentity.Capture(inputObject) : null;
        _navigationScrollIdentity = ObjectConstructionIdentity.Capture(scroll);
        _navigationScrollNodeIdentity = ObjectConstructionIdentity.Capture(scroll.SceneNode!);
        _navigationShowRevision = showRevision;
        _navigationIntentRevision = input.FocusIntentRevision;
        return TryNavigationFocus(_navigationSerial);
    }

    private bool IsCurrentNavigationContext(ulong serial, bool checkIntent = true)
    {
        return serial == _navigationSerial &&
            _navigationSource is { } source && _navigationTarget is { } target &&
            _navigationCanvas is { } canvas && _navigationInput is { } input &&
            _navigationScroll is { } scroll &&
            IsCurrentItem(source, _navigationShowRevision) && IsCurrentItem(target, _navigationShowRevision) &&
            source.CanInteract && target.CanInteract &&
            IsLiveNavigationIdentity(_navigationCanvasIdentity) &&
            IsLiveNavigationIdentity(_navigationCanvasNodeIdentity) &&
            IsLiveNavigationIdentity(_navigationCanvasTransformIdentity) &&
            (_navigationInputIdentity is not { } inputIdentity || IsLiveNavigationIdentity(inputIdentity)) &&
            IsLiveNavigationIdentity(_navigationScrollIdentity) &&
            IsLiveNavigationIdentity(_navigationScrollNodeIdentity) &&
            ReferenceEquals(canvas.SceneNode, _navigationCanvasNodeIdentity.Target) &&
            ReferenceEquals(canvas.Transform, _navigationCanvasTransformIdentity.Target) &&
            ReferenceEquals(UserInterfaceCanvas, canvas) && ReferenceEquals(canvas.GetInputComponent(), input) &&
            ReferenceEquals(scroll.SceneNode, _navigationScrollNodeIdentity.Target) &&
            ReferenceEquals(scroll.SceneNode?.Transform, scroll) && ReferenceEquals(scroll.Parent, Transform) &&
            ReferenceEquals(target.Transform.Parent?.Parent, scroll) &&
            (!checkIntent || input.FocusIntentRevision == _navigationIntentRevision) && !input.HasPendingFocusRequest;
    }

    private static bool IsLiveNavigationIdentity(ObjectConstructionIdentity identity)
        => identity.Target is { IsDestroyed: false, IsDestroyQueued: false } && identity.IsCurrent;

    private bool CanWaitForNavigationFocus()
        => _navigationIntentRevision != 0 && _navigationSource is { IsFocused: true } source &&
            ReferenceEquals(_navigationInput?.FocusedComponent, source);

    private bool TryNavigationFocus(ulong serial)
    {
        if (!IsCurrentNavigationContext(serial))
        {
            if (serial == _navigationSerial)
                CancelNavigationFocus();
            return false;
        }
        UIContextMenuItemComponent target = _navigationTarget!;
        IUICanvasInputSource input = _navigationInput!;
        _navigationScroll!.ScrollIntoView(new Vector2(0, target.ContentTop), new Vector2(0, target.ContentBottom));
        // A scroll callback can replace this request or move its controls.
        if (!IsCurrentNavigationContext(serial))
        {
            if (serial == _navigationSerial)
                CancelNavigationFocus();
            return false;
        }
        bool accepted = input.TryFocusPresented(target);
        if (serial != _navigationSerial)
            return false;
        if (accepted)
        {
            // Accepted focus changes the intent revision. Check the resulting owner instead.
            bool current = IsCurrentNavigationContext(serial, checkIntent: false) &&
                ReferenceEquals(input.FocusedComponent, target) && target.IsFocused;
            CancelNavigationFocus();
            return current;
        }
        if (!IsCurrentNavigationContext(serial) || !CanWaitForNavigationFocus())
        {
            CancelNavigationFocus();
            return false;
        }
        if (!_navigationTickRegistered)
        {
            _navigationTickRegistered = true;
            try { RegisterTick(ETickGroup.Late, ETickOrder.Scene, _navigationFocusTick); }
            catch
            {
                if (serial == _navigationSerial)
                    CancelNavigationFocus();
                throw;
            }
        }
        return true;
    }

    private void NavigationFocusTick()
    {
        if (_navigationTarget is null)
            return;
        if (++_navigationTicks > MaximumNavigationFocusTicks || !CanWaitForNavigationFocus())
        {
            CancelNavigationFocus();
            return;
        }
        TryNavigationFocus(_navigationSerial);
    }

    private void CancelNavigationFocus()
    {
        _navigationSerial++;
        _navigationSource = null;
        _navigationTarget = null;
        _navigationCanvas = null;
        _navigationInput = null;
        _navigationScroll = null;
        _navigationCanvasIdentity = default;
        _navigationCanvasNodeIdentity = default;
        _navigationCanvasTransformIdentity = default;
        _navigationInputIdentity = null;
        _navigationScrollIdentity = default;
        _navigationScrollNodeIdentity = default;
        _navigationShowRevision = 0;
        _navigationIntentRevision = 0;
        _navigationTicks = 0;
        if (!_navigationTickRegistered)
            return;
        _navigationTickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, _navigationFocusTick);
    }
}
