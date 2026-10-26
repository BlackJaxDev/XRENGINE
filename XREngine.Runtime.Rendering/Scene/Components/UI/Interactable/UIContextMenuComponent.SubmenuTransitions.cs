namespace XREngine.Rendering.UI;

public partial class UIContextMenuComponent
{
    private List<UIContextMenuComponent> HoldSubmenuTransitions()
    {
        // This allocation belongs to an explicit open gesture, not a frame loop.
        List<UIContextMenuComponent> held = [];
        for (UIContextMenuComponent? menu = this; menu is not null; menu = menu._ownerMenu)
        {
            if (menu._transitioning)
                continue;
            held.Add(menu);
            menu._transitioning = true;
        }
        return held;
    }

    private bool HasPendingSubmenuTransition()
    {
        for (UIContextMenuComponent? menu = this; menu is not null; menu = menu._ownerMenu)
            if (menu._pendingTransition is not null)
                return true;
        return false;
    }

    private static void ReleaseSubmenuTransitions(List<UIContextMenuComponent> held)
    {
        // Drain from the child toward the root. A later parent Hide can then finish
        // the child's Hide immediately. Never recapture a queued request's authority.
        try
        {
            foreach (UIContextMenuComponent menu in held)
                menu.DrainTransitions();
        }
        finally
        {
            // A failed drain must not leave an ancestor blocked or replay a request
            // after its descendant transition failed.
            foreach (UIContextMenuComponent menu in held)
            {
                menu._pendingTransition = null;
                menu._transitioning = false;
            }
        }
    }
}
