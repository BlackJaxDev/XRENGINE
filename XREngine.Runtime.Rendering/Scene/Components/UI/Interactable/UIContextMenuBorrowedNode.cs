using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI;

/// <summary>
/// Records one node that custom menu content borrowed from another owner, and the parent
/// that the node returns to when the menu retires its content.
/// </summary>
internal readonly record struct UIContextMenuBorrowedNode(
    SceneNode Node,
    ObjectConstructionIdentity NodeIdentity,
    TransformBase? Home,
    ObjectConstructionIdentity? HomeIdentity)
{
    /// <summary>True while the borrowed node is the same live node that the menu captured.</summary>
    public bool IsNodeLive => NodeIdentity.IsCurrent && !Node.IsDestroyed && !Node.IsDestroyQueued;

    /// <summary>Gets the original parent while it is the same live transform, or null.</summary>
    public TransformBase? LiveHome => Home is { } home && HomeIdentity is { } identity &&
        identity.IsCurrent && !home.IsDestroyed && !home.IsDestroyQueued ? home : null;
}
