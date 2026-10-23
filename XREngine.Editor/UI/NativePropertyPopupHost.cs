using System.Numerics;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Owns a canvas-level popup for an inspector control and releases it with the row.</summary>
internal sealed class NativePropertyPopupHost : UIComponent
{
    private UIContextMenuComponent? _popup;

    public UIContextMenuComponent CreatePopup()
    {
        if (_popup is not null)
            return _popup;
        SceneNode canvas = UITransform.GetCanvasTransform()?.SceneNode
            ?? throw new InvalidOperationException("An inspector popup requires an attached canvas.");
        SceneNode node = new(canvas) { Name = "Property popup" };
        node.SetTransform<UIBoundableTransform>().Visibility = EVisibility.Collapsed;
        _popup = node.AddComponent<UIContextMenuComponent>()!;
        return _popup;
    }

    public Vector2 GetPosition()
        => UITransform is UIBoundableTransform bounds
            ? bounds.LocalToCanvas(new Vector2(0, bounds.ActualHeight)) : Vector2.Zero;

    protected override void OnComponentDeactivated()
    {
        _popup?.Hide();
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        SceneNode? popupNode = _popup?.SceneNode;
        _popup = null;
        try { popupNode?.Destroy(true); }
        finally { base.OnDestroying(); }
    }
}
