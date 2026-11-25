namespace XREngine.Rendering.UI;

/// <summary>Routes popup wheel input into a bounded virtual list.</summary>
public class UIVirtualPopupScrollComponent : UIComponent, IUIScrollReceiver
{
    public UIListTransform? List { get; set; }

    public bool HandleMouseScroll(float diff)
    {
        UIListTransform? list = List;
        if (list is null || !IsActiveInHierarchy || !float.IsFinite(diff) || diff == 0)
            return false;
        float previous = list.ContentScrollOffset;
        float maximum = MathF.Max(0, list.VirtualContentExtent - list.ActualHeight);
        list.ContentScrollOffset = Math.Clamp(previous - diff * UIContextMenuComponent.ItemHeight * 3, 0, maximum);
        return list.ContentScrollOffset != previous;
    }
}
