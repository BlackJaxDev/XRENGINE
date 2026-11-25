using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene;

public sealed partial class SceneNode
{
    private void DestroyCurrentChildren()
    {
        if (_transform is not { } sourceTransform)
            return;
        if (sourceTransform.OwnedRetirementChildren is not { } sourceList)
            return;
        ObjectConstructionIdentity ownerIdentity = ObjectConstructionIdentity.Capture(this);
        ObjectConstructionIdentity sourceIdentity = ObjectConstructionIdentity.Capture(sourceTransform);
        ObjectConstructionIdentity listIdentity = ObjectConstructionIdentity.Capture(sourceList);
        List<(SceneNode Node, TransformBase Transform, ObjectConstructionIdentity NodeIdentity,
            ObjectConstructionIdentity TransformIdentity)> children = [];
        HashSet<SceneNode> seen = new(ReferenceEqualityComparer.Instance);
        foreach (TransformBase childTransform in sourceList)
        {
            if (childTransform?.OwnedRetirementNode is not { } child || !seen.Add(child))
                continue;
            children.Add((child, childTransform, ObjectConstructionIdentity.Capture(child),
                ObjectConstructionIdentity.Capture(childTransform)));
        }

        foreach (var child in children)
        {
            if (!ownerIdentity.IsCurrent || !sourceIdentity.IsCurrent || !listIdentity.IsCurrent ||
                !ReferenceEquals(_transform, sourceTransform) ||
                !ReferenceEquals(sourceTransform.OwnedRetirementNode, this) ||
                !ReferenceEquals(sourceTransform.OwnedRetirementChildren, sourceList) ||
                sourceTransform.IsDestroyed || sourceList.IsDestroyed)
                break;
            if (!child.NodeIdentity.IsCurrent || !child.TransformIdentity.IsCurrent ||
                child.Node.IsDestroyed || child.Transform.IsDestroyed ||
                !ReferenceEquals(child.Node.OwnedRetirementTransform, child.Transform) ||
                !ReferenceEquals(child.Transform.OwnedRetirementNode, child.Node) ||
                !ReferenceEquals(child.Transform.OwnedRetirementParent, sourceTransform))
                continue;
            HierarchyListMembership[] memberships = child.Transform.CaptureHierarchyListMemberships();
            if (memberships.Length != 1 || memberships[0].Count <= 0 || !memberships[0].IsCurrent ||
                !ReferenceEquals(memberships[0].Owner, sourceTransform) ||
                !ReferenceEquals(memberships[0].Collection, sourceList))
                continue;
            child.Node.Destroy(true);
        }
    }
}
