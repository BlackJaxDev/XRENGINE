using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.UI;

/// <summary>Owns a canvas-level popup for an inspector control and releases it with the row.</summary>
internal sealed class NativePropertyPopupHost : UIComponent
{
    private UIContextMenuComponent? _popup;
    private SceneNode? _popupNode;
    private SceneNode? _popupCanvas;
    private UIBoundableTransform? _popupBounds;
    private ObjectConstructionIdentity? _popupIdentity;
    private ObjectConstructionIdentity? _popupNodeIdentity;
    private ObjectConstructionIdentity? _popupCanvasIdentity;
    private ObjectConstructionIdentity? _popupCanvasTransformIdentity;
    private ObjectConstructionIdentity? _popupBoundsIdentity;
    private readonly List<RuntimeComponentCreationReceipt> _failedPopupCreations = [];

    public UIContextMenuComponent CreatePopup()
    {
        if (_popup is { } existingPopup)
        {
            if (OwnsPopup(existingPopup)) return existingPopup;
            throw new InvalidOperationException("The inspector popup is no longer owned by this host.");
        }
        if (!TryCreatePopup(static () => true, out UIContextMenuComponent? popup) || popup is null)
            throw new InvalidOperationException("The inspector popup could not retain its canvas owner.");
        return popup;
    }

    internal bool TryCreatePopup(Func<bool> isCurrent, out UIContextMenuComponent? popup)
    {
        popup = null;
        if (!isCurrent()) return false;
        if (_popup is { } currentPopup)
        {
            popup = currentPopup;
            return isCurrent() && OwnsPopup(currentPopup);
        }
        SceneNode canvas = UITransform.GetCanvasTransform()?.SceneNode
            ?? throw new InvalidOperationException("An inspector popup requires an attached canvas.");
        TransformBase canvasTransform = canvas.Transform;
        ObjectConstructionIdentity canvasIdentity = ObjectConstructionIdentity.Capture(canvas);
        ObjectConstructionIdentity canvasTransformIdentity = ObjectConstructionIdentity.Capture(canvasTransform);
        SceneNode node = new(canvas) { Name = "Property popup" };
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
        UIBoundableTransform? bounds = null;
        ObjectConstructionIdentity? boundsIdentity = null;
        UIContextMenuComponent? created = null;
        ObjectConstructionIdentity? createdIdentity = null;
        bool NodeOwned() => canvasIdentity.IsCurrent && canvasTransformIdentity.IsCurrent &&
            nodeIdentity.IsCurrent && !canvas.IsDestroyed && !canvas.IsDestroyQueued &&
            !node.IsDestroyed && !node.IsDestroyQueued && ReferenceEquals(canvas.Transform, canvasTransform) &&
            ReferenceEquals(node.Transform.Parent, canvasTransform);
        bool PopupOwned() => NodeOwned() &&
            (bounds is null || boundsIdentity is { } capturedBounds && capturedBounds.IsCurrent &&
                !bounds.IsDestroyed && !bounds.IsDestroyQueued && ReferenceEquals(node.Transform, bounds)) &&
            (created is null || createdIdentity is { } capturedPopup && capturedPopup.IsCurrent &&
                !created.IsDestroyed && !created.IsDestroyQueued && ReferenceEquals(created.SceneNode, node) &&
                node.Components.Contains(created));
        void RetireIfOwned()
        {
            if (!PopupOwned()) return;
            try { node.Destroy(true); }
            catch (Exception error) { Debug.LogException(error, "Could not retire an incomplete inspector popup."); }
        }
        if (!isCurrent() || !PopupOwned())
        {
            RetireIfOwned();
            return false;
        }
        bounds = node.SetTransform<UIBoundableTransform>();
        boundsIdentity = ObjectConstructionIdentity.Capture(bounds);
        bool Current() => isCurrent() && PopupOwned();
        if (!Current())
        {
            RetireIfOwned();
            return false;
        }
        bounds.Visibility = EVisibility.Collapsed;
        if (!Current())
        {
            RetireIfOwned();
            return false;
        }
        RuntimeComponentCreationReceipt receipt = new(node, Current);
        try
        {
            created = node.AddComponentGuarded(static () => new UIContextMenuComponent(), receipt);
            if (created is not null) createdIdentity = ObjectConstructionIdentity.Capture(created);
        }
        catch (Exception failure)
        {
            bool ownerWasCurrent = Current();
            receipt.RecordFailure(failure);
            bool aborted = receipt.Component is null || receipt.TryAbortUnchangedComponent();
            if (!aborted)
            {
                _failedPopupCreations.Add(receipt);
                Debug.LogException(receipt.CleanupFailure is { } cleanup
                    ? new AggregateException(failure, cleanup) : failure,
                    "Could not abort a failed inspector popup creation.");
            }
            RetireIfOwned();
            if (failure is InvalidOperationException && !ownerWasCurrent) return false;
            throw;
        }
        if (!Current() || created is null || created.IsDestroyed || created.IsDestroyQueued ||
            !ReferenceEquals(created.SceneNode, node))
        {
            if (receipt.Component is not null)
            {
                InvalidOperationException failure = new("The inspector popup owner changed after component creation.");
                receipt.RecordFailure(failure);
                if (!receipt.TryAbortUnchangedComponent())
                {
                    _failedPopupCreations.Add(receipt);
                    Debug.LogException(receipt.CleanupFailure is { } cleanup
                        ? new AggregateException(failure, cleanup) : failure,
                        "Could not abort an inspector popup after its owner changed.");
                }
            }
            RetireIfOwned();
            return false;
        }
        _popup = created;
        _popupNode = node;
        _popupCanvas = canvas;
        _popupBounds = bounds;
        _popupIdentity = createdIdentity;
        _popupNodeIdentity = nodeIdentity;
        _popupCanvasIdentity = canvasIdentity;
        _popupCanvasTransformIdentity = canvasTransformIdentity;
        _popupBoundsIdentity = boundsIdentity;
        if (!OwnsPopup(created))
        {
            _popup = null;
            _popupNode = null;
            _popupCanvas = null;
            _popupBounds = null;
            _popupIdentity = null;
            _popupNodeIdentity = null;
            _popupCanvasIdentity = null;
            _popupCanvasTransformIdentity = null;
            _popupBoundsIdentity = null;
            RetireIfOwned();
            return false;
        }
        popup = created;
        return true;
    }

    internal bool OwnsPopup(UIContextMenuComponent popup)
    {
        SceneNode? node = _popupNode;
        SceneNode? canvas = _popupCanvas;
        UIBoundableTransform? bounds = _popupBounds;
        return ReferenceEquals(_popup, popup) && node is not null && canvas is not null && bounds is not null &&
            _popupIdentity is { } popupIdentity && popupIdentity.IsCurrent &&
            _popupNodeIdentity is { } nodeIdentity && nodeIdentity.IsCurrent &&
            _popupCanvasIdentity is { } canvasIdentity && canvasIdentity.IsCurrent &&
            _popupCanvasTransformIdentity is { } canvasTransformIdentity && canvasTransformIdentity.IsCurrent &&
            _popupBoundsIdentity is { } boundsIdentity && boundsIdentity.IsCurrent &&
            !popup.IsDestroyed && !popup.IsDestroyQueued && !node.IsDestroyed && !node.IsDestroyQueued &&
            !canvas.IsDestroyed && !canvas.IsDestroyQueued && !bounds.IsDestroyed && !bounds.IsDestroyQueued &&
            ReferenceEquals(popup.SceneNode, node) && node.Components.Contains(popup) &&
            ReferenceEquals(node.Transform, bounds) && ReferenceEquals(node.Transform.Parent, canvas.Transform) &&
            ReferenceEquals(canvas.Transform, canvasTransformIdentity.Target);
    }

    public Vector2 GetPosition()
        => UITransform is UIBoundableTransform bounds
            ? bounds.LocalToCanvas(new Vector2(0, bounds.ActualHeight)) : Vector2.Zero;

    protected override void OnComponentDeactivated()
    {
        if (_popup is { } popup && OwnsPopup(popup)) popup.Hide();
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        SceneNode? popupNode = _popup is { } popup && OwnsPopup(popup) ? _popupNode : null;
        _popup = null;
        _popupNode = null;
        _popupCanvas = null;
        _popupBounds = null;
        _popupIdentity = null;
        _popupNodeIdentity = null;
        _popupCanvasIdentity = null;
        _popupCanvasTransformIdentity = null;
        _popupBoundsIdentity = null;
        try { popupNode?.Destroy(true); }
        finally { base.OnDestroying(); }
    }
}
