using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Keep the original leaves and their guarded component claims until detail publication ends.</summary>
internal sealed class InspectorDetailConstructionAttempt
{
    internal InspectorDetailConstructionAttempt(SceneNode host, DetachedLeafRetirementReceipt retirement)
    {
        Host = host;
        HostRetirement = retirement;
        HostTransform = host.Transform;
        HostIdentity = ObjectConstructionIdentity.Capture(host);
        HostTransformIdentity = ObjectConstructionIdentity.Capture(HostTransform);
        HostComponentsIdentity = ObjectConstructionIdentity.Capture(host.OwnedRetirementComponents!);
        HostChildrenIdentity = ObjectConstructionIdentity.Capture(HostTransform.OwnedRetirementChildren!);
        HostMembershipRevision = host.HierarchyMembershipRevision;
        HostTransformMembershipRevision = HostTransform.HierarchyMembershipRevision;
    }

    internal SceneNode Host { get; }
    internal TransformBase HostTransform { get; }
    internal ObjectConstructionIdentity HostIdentity { get; }
    internal ObjectConstructionIdentity HostTransformIdentity { get; }
    internal ObjectConstructionIdentity HostComponentsIdentity { get; }
    internal ObjectConstructionIdentity HostChildrenIdentity { get; }
    internal DetachedLeafRetirementReceipt HostRetirement { get; }
    internal DetachedLeafAttachmentReceipt? HostAttachment { get; set; }
    internal DetachedLeafAttachmentReceipt? TextAttachment { get; set; }
    internal AdoptedLeafRetirementReceipt? AdoptedHostRetirement { get; set; }
    internal DetachedLeafRetirementReceipt? TextRetirement { get; private set; }
    internal AdoptedLeafRetirementReceipt? AdoptedTextRetirement { get; set; }
    internal SceneNode? TextNode { get; private set; }
    internal TransformBase? TextTransform { get; private set; }
    internal ObjectConstructionIdentity? TextNodeIdentity { get; private set; }
    internal ObjectConstructionIdentity? TextTransformIdentity { get; private set; }
    internal ObjectConstructionIdentity? TextComponentsIdentity { get; private set; }
    internal ObjectConstructionIdentity? TextChildrenIdentity { get; private set; }
    internal TransformBase? ExpectedParent { get; set; }
    internal ObjectConstructionIdentity? ExpectedParentIdentity { get; set; }
    internal ObjectConstructionIdentity? ExpectedParentNodeIdentity { get; set; }
    internal ObjectConstructionIdentity? ExpectedParentNodeTransformIdentity { get; set; }
    internal ObjectConstructionIdentity? ExpectedParentComponentsIdentity { get; set; }
    internal ObjectConstructionIdentity? ExpectedParentChildrenIdentity { get; set; }
    internal TransformBase? ExpectedParentParent { get; set; }
    internal ObjectConstructionIdentity? ExpectedParentParentIdentity { get; set; }
    internal long ExpectedParentMembershipRevision { get; set; }
    internal IRuntimeWorldContext? ExpectedWorld { get; set; }
    internal TransformHierarchyStore? ExpectedParentStore { get; set; }
    internal TransformHandle ExpectedParentHandle { get; set; }
    internal UITextComponent? Text { get; set; }
    internal InspectorStringDetailSizeComponent? Size { get; set; }
    internal long HostMembershipRevision { get; }
    internal long HostTransformMembershipRevision { get; }
    internal long TextMembershipRevision { get; private set; }
    internal long TextTransformMembershipRevision { get; private set; }
    internal List<RuntimeComponentCreationReceipt> HostComponents { get; } = [];
    internal List<RuntimeComponentCreationReceipt> TextComponents { get; } = [];
    internal List<long> HostComponentMembershipRevisions { get; } = [];
    internal List<long> TextComponentMembershipRevisions { get; } = [];

    internal bool OwnsExpectedParent =>
        ExpectedParent is { IsDestroyed: false, IsDestroyQueued: false } parent &&
        parent.SceneNode is { IsDestroyed: false, IsDestroyQueued: false } node &&
        ExpectedParentIdentity is { IsCurrent: true } &&
        ExpectedParentNodeIdentity is { IsCurrent: true } &&
        ExpectedParentNodeTransformIdentity is { IsCurrent: true } &&
        ExpectedParentComponentsIdentity is { IsCurrent: true } &&
        ExpectedParentChildrenIdentity is { IsCurrent: true } &&
        (ExpectedParentParentIdentity is null || ExpectedParentParentIdentity.Value.IsCurrent) &&
        ReferenceEquals(node.Transform, parent) &&
        ReferenceEquals(node.OwnedRetirementComponents,
            ExpectedParentComponentsIdentity.Value.Target) &&
        ReferenceEquals(parent.OwnedRetirementChildren, ExpectedParentChildrenIdentity.Value.Target) &&
        ReferenceEquals(parent.Parent, ExpectedParentParent) &&
        parent.HasUsableHierarchyMembershipRevision &&
        parent.HierarchyMembershipRevision == ExpectedParentMembershipRevision &&
        ReferenceEquals(parent.World, ExpectedWorld) &&
        ReferenceEquals(parent.HierarchyStore, ExpectedParentStore) &&
        parent.HierarchyHandle == ExpectedParentHandle;

    internal bool OwnsOriginalDetachedHost =>
        OriginalDetachedLeaf(Host, HostTransform, HostIdentity, HostTransformIdentity,
            HostComponentsIdentity, HostChildrenIdentity, HostRetirement,
            HostMembershipRevision, HostTransformMembershipRevision);

    internal bool OwnsOriginalDetachedHostBoundary =>
        OriginalDetachedLeaf(Host, HostTransform, HostIdentity, HostTransformIdentity,
            HostComponentsIdentity, HostChildrenIdentity, HostRetirement,
            HostMembershipRevision, HostTransformMembershipRevision, requireEmptyChildren: false);

    internal bool OwnsOriginalDetachedText =>
        TextNode is { } node && TextTransform is { } transform &&
        TextNodeIdentity is { } nodeIdentity && TextTransformIdentity is { } transformIdentity &&
        TextComponentsIdentity is { } componentsIdentity && TextChildrenIdentity is { } childrenIdentity &&
        TextRetirement is { } retirement &&
        OriginalDetachedLeaf(node, transform, nodeIdentity, transformIdentity,
            componentsIdentity, childrenIdentity, retirement,
            TextMembershipRevision, TextTransformMembershipRevision);

    internal void CaptureTextNode(SceneNode textNode, DetachedLeafRetirementReceipt retirement)
    {
        if (TextNode is not null)
            throw new InvalidOperationException("The text leaf is already registered.");
        TextNode = textNode;
        TextTransform = textNode.Transform;
        TextRetirement = retirement;
        TextNodeIdentity = ObjectConstructionIdentity.Capture(textNode);
        TextTransformIdentity = ObjectConstructionIdentity.Capture(TextTransform);
        TextComponentsIdentity = ObjectConstructionIdentity.Capture(textNode.OwnedRetirementComponents!);
        TextChildrenIdentity = ObjectConstructionIdentity.Capture(TextTransform.OwnedRetirementChildren!);
        TextMembershipRevision = textNode.HierarchyMembershipRevision;
        TextTransformMembershipRevision = TextTransform.HierarchyMembershipRevision;
    }

    internal bool OwnsComponents(SceneNode node, List<RuntimeComponentCreationReceipt> receipts,
        List<long> revisions)
    {
        if (receipts.Count != revisions.Count || node.Components.Count != receipts.Count)
            return false;
        for (int index = 0; index < receipts.Count; index++)
        {
            RuntimeComponentCreationReceipt receipt = receipts[index];
            if (receipt.ClaimedIdentity is not { IsCurrent: true } || !receipt.IsInserted ||
                receipt.Component is not { IsDestroyed: false, IsDestroyQueued: false } component ||
                !ReferenceEquals(component.SceneNode, node) ||
                !ReferenceEquals(component.World, node.World) ||
                component.HierarchyMembershipRevision != revisions[index] ||
                !node.Components.Contains(component))
                return false;
        }
        return true;
    }

    private static bool OriginalDetachedLeaf(SceneNode node, TransformBase transform,
        ObjectConstructionIdentity nodeIdentity, ObjectConstructionIdentity transformIdentity,
        ObjectConstructionIdentity componentsIdentity, ObjectConstructionIdentity childrenIdentity,
        DetachedLeafRetirementReceipt retirement, long nodeRevision, long transformRevision,
        bool requireEmptyChildren = true)
        => nodeIdentity.IsCurrent && transformIdentity.IsCurrent && componentsIdentity.IsCurrent &&
           childrenIdentity.IsCurrent && !node.IsDestroyed && !node.IsDestroyQueued &&
           !transform.IsDestroyed && !transform.IsDestroyQueued &&
           ReferenceEquals(node.Transform, transform) && transform.Parent is null &&
           ReferenceEquals(node.OwnedRetirementComponents, componentsIdentity.Target) &&
           ReferenceEquals(transform.OwnedRetirementChildren, childrenIdentity.Target) &&
           (!requireEmptyChildren || transform.OwnedRetirementChildren!.Count == 0) &&
           !node.HasEnteredRootCollection && !node.HasEnteredStructuralList &&
           !transform.HasEnteredStructuralList && !node.HasSceneRootOwner &&
           node.HasUsableHierarchyMembershipRevision &&
           transform.HasUsableHierarchyMembershipRevision &&
           node.HierarchyMembershipRevision == nodeRevision &&
           transform.HierarchyMembershipRevision == transformRevision &&
           ReferenceEquals(node.World, retirement.SourceWorld) &&
           ReferenceEquals(transform.World, retirement.SourceWorld) &&
           ReferenceEquals(transform.HierarchyStore, retirement.SourceStore) &&
           transform.HierarchyHandle == retirement.SourceHandle;
}
