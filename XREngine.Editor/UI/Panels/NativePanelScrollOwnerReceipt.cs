using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Exact Rows list, node, parent, and panel ancestry for a captured view.</summary>
internal readonly record struct NativePanelScrollOwnerReceipt(
    UIListTransform List,
    ObjectConstructionIdentity ListIdentity,
    SceneNode Node,
    ObjectConstructionIdentity NodeIdentity,
    TransformBase Parent,
    ObjectConstructionIdentity ParentIdentity,
    TransformBase PanelTransform,
    ObjectConstructionIdentity PanelTransformIdentity)
{
    internal static NativePanelScrollOwnerReceipt? Capture(
        UIListTransform list, TransformBase panelTransform)
    {
        if (list.SceneNode is not { } node || list.Parent is not { } parent)
            return null;
        NativePanelScrollOwnerReceipt receipt = new(list,
            ObjectConstructionIdentity.Capture(list), node,
            ObjectConstructionIdentity.Capture(node), parent,
            ObjectConstructionIdentity.Capture(parent), panelTransform,
            ObjectConstructionIdentity.Capture(panelTransform));
        return receipt.IsCurrent(list, panelTransform) ? receipt : null;
    }

    internal bool IsCurrent(UIListTransform list, TransformBase panelTransform)
    {
        if (!ReferenceEquals(List, list) || !ReferenceEquals(PanelTransform, panelTransform) ||
            !ListIdentity.IsCurrent || !NodeIdentity.IsCurrent ||
            !ParentIdentity.IsCurrent || !PanelTransformIdentity.IsCurrent ||
            List.IsDestroyed || List.IsDestroyQueued ||
            Node.IsDestroyed || Node.IsDestroyQueued ||
            Parent.IsDestroyed || Parent.IsDestroyQueued ||
            PanelTransform.IsDestroyed || PanelTransform.IsDestroyQueued ||
            !ReferenceEquals(List.SceneNode, Node) ||
            !ReferenceEquals(Node.Transform, List) ||
            !ReferenceEquals(List.Parent, Parent))
            return false;
        for (TransformBase? ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, PanelTransform))
                return true;
        return false;
    }
}
