using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI;

public partial class UIContextMenuComponent
{
    private readonly List<UIContextMenuBorrowedNode> _borrowedContent = [];
    private SceneNode? _borrowWatchedNode;

    /// <summary>
    /// Moves a node that another owner keeps into the custom content of the current show.
    /// Call it only from the builder of <see cref="ShowContent"/> or <see cref="ShowPassiveContent"/>.
    /// </summary>
    /// <remarks>
    /// Custom content has two kinds of nodes. A node that the builder creates under the content
    /// root belongs to the menu, and the menu destroys it when it clears the content. A borrowed
    /// node belongs to its original owner. When the menu clears the content, or when the menu is
    /// destroyed, a borrowed node that is still under the content root returns to its original
    /// parent. If that parent no longer exists, the node is detached and is not destroyed. A
    /// borrowed node that a callback moved out of the content root stays where it is.
    /// </remarks>
    /// <param name="contentParent">The content root or a node under it.</param>
    /// <param name="borrowed">The node to borrow. It must not contain this menu.</param>
    public void BorrowContent(SceneNode contentParent, SceneNode borrowed)
    {
        ArgumentNullException.ThrowIfNull(contentParent);
        ArgumentNullException.ThrowIfNull(borrowed);
        if (_contentBuilder is null || !_isOpen || _contentRoot is not { } root || !OwnsContentRoot(root) ||
            !IsUnder(contentParent, root) || contentParent.IsDestroyed || contentParent.IsDestroyQueued)
            throw new InvalidOperationException("Borrowed menu content needs the custom content of the current show.");
        if (borrowed.IsDestroyed || borrowed.IsDestroyQueued)
            throw new InvalidOperationException("A destroyed node cannot be borrowed into menu content.");
        if (IsUnder(SceneNode, borrowed) || IsUnder(borrowed, root))
            throw new InvalidOperationException("The borrowed node already belongs to this menu.");
        for (int index = 0; index < _borrowedContent.Count; index++)
            if (ReferenceEquals(_borrowedContent[index].Node, borrowed))
                throw new InvalidOperationException("The node is already borrowed by this menu.");

        TransformBase? home = borrowed.Transform.Parent;
        UIContextMenuBorrowedNode record = new(borrowed, ObjectConstructionIdentity.Capture(borrowed),
            home, home is null ? null : ObjectConstructionIdentity.Capture(home));
        // Record first, so a failed move cannot leave an unrecorded node under the content root.
        _borrowedContent.Add(record);
        WatchMenuNodeDestruction();
        borrowed.Transform.SetParent(contentParent.Transform, false, EParentAssignmentMode.Immediate);
        if (!ReferenceEquals(borrowed.Transform.Parent, contentParent.Transform))
            throw new InvalidOperationException("The borrowed node changed ownership during attachment.");
    }

    /// <summary>
    /// Returns each borrowed node that is still under the content root to its original parent.
    /// The root is the content root that this menu still owns.
    /// </summary>
    private void ReturnBorrowedContent(SceneNode root)
    {
        // Return in reverse order, so a node borrowed into another borrowed node returns first.
        // A failed return keeps its exception; the records are released either way.
        try
        {
            for (int index = _borrowedContent.Count - 1; index >= 0; index--)
            {
                UIContextMenuBorrowedNode borrowed = _borrowedContent[index];
                if (borrowed.IsNodeLive && IsUnder(borrowed.Node, root))
                    borrowed.Node.Transform.SetParent(borrowed.LiveHome, false, EParentAssignmentMode.Immediate);
            }
        }
        finally { ClearBorrowedRecords(); }
    }

    /// <summary>
    /// Watches the menu node while it holds borrowed content. The node destroys its children
    /// before it destroys this component, so the borrowed nodes must leave before that.
    /// </summary>
    private void WatchMenuNodeDestruction()
    {
        if (_borrowWatchedNode is not null) return;
        SceneNode node = SceneNode;
        node.Destroying += OnMenuNodeDestroying;
        _borrowWatchedNode = node;
    }

    private bool OnMenuNodeDestroying(XRObjectBase destroying)
    {
        SceneNode? root = _contentRoot;
        ReleaseBorrowedContent(root, root is not null && OwnsContentRoot(root));
        return true;
    }

    private void ClearBorrowedRecords()
    {
        _borrowedContent.Clear();
        if (_borrowWatchedNode is not { } node) return;
        _borrowWatchedNode = null;
        node.Destroying -= OnMenuNodeDestroying;
    }

    /// <summary>
    /// Returns borrowed content before the owned content root is destroyed. A content root that
    /// this menu no longer owns keeps its nodes, so the records are only released.
    /// </summary>
    private void ReleaseBorrowedContent(SceneNode? root, bool owned)
    {
        if (_borrowedContent.Count == 0 && _borrowWatchedNode is null) return;
        if (owned && root is not null)
            ReturnBorrowedContent(root);
        else
            ClearBorrowedRecords();
    }

    private static bool IsUnder(SceneNode candidate, SceneNode ancestor)
    {
        for (SceneNode? node = candidate; node is not null; node = node.Parent)
            if (ReferenceEquals(node, ancestor))
                return true;
        return false;
    }
}
