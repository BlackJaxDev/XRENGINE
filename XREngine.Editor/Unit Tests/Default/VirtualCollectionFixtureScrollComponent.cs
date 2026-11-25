using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Scrolls the virtual collection fixture through its production list offset.</summary>
internal sealed class VirtualCollectionFixtureScrollComponent : UIComponent, IUIScrollReceiver
{
    public UIListTransform? List { get; set; }
    public float ViewportHeight { get; set; } = 450;

    public bool HandleMouseScroll(float diff)
    {
        UIListTransform? list = List;
        if (list is null || !float.IsFinite(diff))
            return false;
        float max = MathF.Max(0, list.VirtualContentExtent - ViewportHeight);
        list.ContentScrollOffset = Math.Clamp(list.ContentScrollOffset - diff * 48, 0, max);
        return true;
    }
}
