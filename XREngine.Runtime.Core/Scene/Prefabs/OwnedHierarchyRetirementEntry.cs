using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Stores exact raw graph state and the progress of its permitted removals.</summary>
internal sealed class OwnedHierarchyRetirementEntry
{
    internal OwnedHierarchyRetirementEntry(XRWorldObjectBase value, ObjectConstructionIdentity identity)
    {
        if (!ReferenceEquals(identity.Target, value) || !identity.IsCurrent)
            throw new InvalidOperationException("The owned lifetime no longer matches its construction receipt.");
        if (value.IsDestroyed)
            throw new InvalidOperationException("A previously destroyed allocation has no canonical cleanup proof in this receipt.");
        Value = value;
        Id = identity.Id;
        LifetimeRevision = identity.LifetimeRevision;
        MembershipRevision = value.HierarchyMembershipRevision;
        ExpectedWorld = value.World;
        switch (value)
        {
            case SceneNode node:
                Transform = node.OwnedRetirementTransform;
                ComponentList = node.OwnedRetirementComponents;
                if (ComponentList is null)
                    throw new InvalidOperationException("A partially constructed node has no component storage.");
                Components = [.. ComponentList];
                ComponentListDestroyed = ComponentList.IsDestroyed;
                break;
            case TransformBase transform:
                ExpectedStore = transform.HierarchyStore;
                ExpectedHandle = transform.HierarchyHandle;
                Parent = transform.OwnedRetirementParent;
                Node = transform.OwnedRetirementNode;
                ChildList = transform.OwnedRetirementChildren;
                if (ChildList is null)
                    throw new InvalidOperationException("A partially constructed transform has no child storage.");
                Children = [.. ChildList];
                ChildListDestroyed = ChildList.IsDestroyed;
                break;
            case XRComponent component:
                Node = component.OwnedRetirementNode;
                break;
        }
    }

    internal XRWorldObjectBase Value { get; }
    internal Guid Id { get; }
    internal long LifetimeRevision { get; set; }
    internal long MembershipRevision { get; set; }
    internal IRuntimeWorldContext? ExpectedWorld { get; set; }
    internal TransformHierarchyStore? ExpectedStore { get; set; }
    internal TransformHandle ExpectedHandle { get; set; }
    internal EOwnedHierarchyStepState DeactivationState { get; set; }
    internal EOwnedHierarchyStepState EndPlayState { get; set; }
    internal EOwnedHierarchyStepState WorldRemovalState { get; set; }
    internal bool Destroyed { get; set; }
    internal bool CleanupComplete { get; set; }
    internal TransformBase? Parent { get; set; }
    internal SceneNode? Node { get; set; }
    internal TransformBase? Transform { get; }
    internal EventList<TransformBase>? ChildList { get; }
    internal List<TransformBase>? Children { get; }
    internal EventList<XRComponent>? ComponentList { get; }
    internal List<XRComponent>? Components { get; }
    internal XRWorldObjectBase? ListOwner { get; set; }
    internal object? ListSlot { get; set; }
    internal bool ChildAuthorityRetired { get; set; }
    internal bool ComponentAuthorityRetired { get; set; }
    private bool ChildListDestroyed { get; }
    private bool ComponentListDestroyed { get; }

    internal void Validate()
    {
        ObjectConstructionIdentity expected = new(Value, Id, LifetimeRevision);
        if (!expected.IsCurrent || Value.IsDestroyed != Destroyed || Value.IsDestroyQueued ||
            Value.HierarchyMembershipRevision != MembershipRevision ||
            !ReferenceEquals(Value.World, ExpectedWorld))
            throw new InvalidOperationException("An owned structural object's identity or lifetime changed during cleanup.");
        HierarchyListMembership[] memberships = Value.CaptureHierarchyListMemberships();
        if (ListSlot is null ? memberships.Length != 0 : memberships.Length != 1 ||
            !ReferenceEquals(memberships[0].Collection, ListSlot) ||
            !ReferenceEquals(memberships[0].Owner, ListOwner) || memberships[0].Count != 1 || !memberships[0].IsCurrent)
            throw new InvalidOperationException("An owned structural object has an unexpected list owner.");
        switch (Value)
        {
            case SceneNode node:
                if (!ReferenceEquals(node.OwnedRetirementTransform, Transform) ||
                    !ReferenceEquals(node.OwnedRetirementComponents, ComponentList) ||
                    node.HasSceneRootOwner || node.CaptureRuntimeRootMemberships().Length != 0)
                    throw new InvalidOperationException("An owned node acquired another structural or scene owner.");
                ValidateList(ComponentList!, Components!, ComponentListDestroyed, ComponentAuthorityRetired);
                break;
            case TransformBase transform:
                if (!ReferenceEquals(transform.OwnedRetirementParent, Parent) ||
                    !ReferenceEquals(transform.OwnedRetirementNode, Node) ||
                    !ReferenceEquals(transform.OwnedRetirementChildren, ChildList) ||
                    !ReferenceEquals(transform.HierarchyStore, ExpectedStore) ||
                    transform.HierarchyHandle != ExpectedHandle)
                    throw new InvalidOperationException("An owned transform changed its exact structural owner.");
                ValidateList(ChildList!, Children!, ChildListDestroyed, ChildAuthorityRetired);
                break;
            case XRComponent component:
                if (!ReferenceEquals(component.OwnedRetirementNode, Node))
                    throw new InvalidOperationException("An owned component changed its exact node.");
                break;
        }
    }

    private static void ValidateList<T>(EventList<T> list, IReadOnlyList<T> expected, bool destroyed, bool retired)
        where T : XRWorldObjectBase
    {
        if (list.IsDestroyed != destroyed || list.IsDestroyQueued || list.Count != expected.Count ||
            list.GetMutationAuthority() is not HierarchyListAuthority<T> authority || authority.IsCurrent == retired)
            throw new InvalidOperationException("An owned structural list changed its identity or lifetime.");
        for (int index = 0; index < expected.Count; index++)
            if (!ReferenceEquals(list[index], expected[index]))
                throw new InvalidOperationException("An owned structural list changed its exact occurrences.");
    }
}
