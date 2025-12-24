using XREngine.Rendering.UI;

namespace XREngine.Editor.UI.Toolbar;

public class ToolbarSeparator : ToolbarItemBase
{
    public ToolbarSeparator()
    {

    }
    public override void OnInteracted(UIInteractableComponent component)
    {
        // No action on interaction
    }
    public override void OnCancelInteraction(UIInteractableComponent component)
    {
        // No action on cancel interaction
    }
}
