using XREngine.Scene;

namespace XREngine.Rendering.UI;

public partial class UIContextMenuComponent
{
    private bool BuildItemSubmenu(SceneNode host, UIContextMenuItemComponent row,
        ulong showRevision, ContextMenuItem item, bool focusFirstItem)
    {
        InvalidOperationException? cancellation = null;
        void RequireOwner()
        {
            if (!IsCurrentItem(row, showRevision) || !ReferenceEquals(row.Item, item) ||
                !ReferenceEquals(SceneNode.Parent, host) || HasPendingSubmenuTransition())
                throw cancellation ??= new InvalidOperationException("The submenu opening was superseded.");
        }

        RequireOwner();
        UIContextMenuLeafConstruction construction = new(host, RequireOwner, name: "ContextSubmenu");
        UIContextMenuComponent? submenu = null;
        bool published = false;
        ulong expectedShowRevision = 0;
        try
        {
            submenu = construction.Add(() => new UIContextMenuComponent());
            construction.Write(() => submenu.PreferredWidth = PreferredWidth);
            // Keep the candidate private until attachment callbacks finish. A root
            // Show or Hide can then retire the old row without destroying this factory.
            submenu._transitioning = true;
            Exception? attachmentFailure = null;
            try { construction.Attach(); }
            catch (Exception failure)
            {
                attachmentFailure = failure;
                throw;
            }
            finally
            {
                // A callback can request an independent show on the candidate. Keep
                // its captured request and execute it after the attachment mutation.
                try { submenu.DrainTransitions(); }
                catch (Exception drainFailure) when (attachmentFailure is not null)
                {
                    throw new AggregateException("Submenu attachment and queued transition failed.",
                        attachmentFailure, drainFailure);
                }
            }
            RequireOwner();
            if (submenu._showRevision != 0)
                throw new InvalidOperationException("The submenu candidate started an independent show.");
            _submenu = submenu;
            _submenuConstruction = construction;
            _submenuShowRevision = expectedShowRevision = submenu._showRevision + 1;
            _submenuRow = row;
            published = true;
            submenu._ownerMenu = this;
            submenu._ownerShowRevision = showRevision;
            ContextMenuItem[] items = new ContextMenuItem[item.SubmenuItems.Count];
            for (int index = 0; index < items.Length; index++)
                items[index] = item.SubmenuItems[index];
            submenu.ShowCore(ResolveSubmenuPosition(item, submenu.PreferredWidth), items,
                null, subscribeForDismiss: false);
            if (!IsCurrentItem(row, showRevision) || HasPendingSubmenuTransition() ||
                !ReferenceEquals(_submenu, submenu) || !construction.OwnsAttachedNode() ||
                !submenu._isOpen || !ReferenceEquals(submenu._ownerMenu, this))
            {
                CloseConstructedSubmenu(construction, submenu, expectedShowRevision);
                return false;
            }
            if (focusFirstItem)
                submenu.RequestFirstItemFocus(row);
            if (IsCurrentItem(row, showRevision) && !HasPendingSubmenuTransition() &&
                ReferenceEquals(_submenu, submenu) && construction.OwnsAttachedNode() &&
                ReferenceEquals(submenu._ownerMenu, this) && submenu.IsCurrentShow(expectedShowRevision))
                return true;
            CloseConstructedSubmenu(construction, submenu, expectedShowRevision);
            return false;
        }
        catch (Exception failure)
        {
            try
            {
                if (published)
                {
                    CloseConstructedSubmenu(construction, submenu!, expectedShowRevision);
                }
                else if (construction.OwnsAttachedNode())
                {
                    // Attachment has finished. This is ordinary exact-owned cleanup,
                    // not detached-leaf retirement of an adopted subtree.
                    if (submenu is { _showRevision: 0 })
                        construction.Node.Destroy(true);
                    else
                        _failedConstructionLeaves.Add(construction.Retirement);
                }
                else if (!construction.RetireFailedLeaf())
                {
                    _failedConstructionLeaves.Add(construction.Retirement);
                    if (ReferenceEquals(failure, cancellation))
                        throw new InvalidOperationException("The cancelled submenu could not be retired.",
                            construction.Retirement.Failure);
                }
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Submenu construction and cleanup failed.", failure, cleanupFailure);
            }
            if (ReferenceEquals(failure, cancellation))
                return false;
            throw;
        }
    }

    private void CloseConstructedSubmenu(UIContextMenuLeafConstruction construction,
        UIContextMenuComponent submenu, ulong expectedShowRevision)
    {
        if (ReferenceEquals(_submenuConstruction, construction))
            CloseSubmenu(refocusRow: false);
        // An owner Hide can queue the child's Hide while its Show is active. The
        // child has now drained that request; release only the still-owned closed node.
        if (construction.OwnsAttachedNode() && !submenu.IsOpen && submenu._ownerMenu is null &&
            submenu._showRevision == expectedShowRevision)
            construction.Node.Destroy(true);
    }
}
