using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

internal sealed partial class HierarchyCloneDocument
{
    internal HierarchyCloneDocumentHandoff PrepareHandoff(ObjectConstructionOwnership ownership)
    {
        RequireCustody(ownership);
        lock (SceneRootCollection.MutationGate)
        {
            HashSet<XRWorldObjectBase> endpoints = new(ReferenceEqualityComparer.Instance);
            foreach (XRObjectBase value in ownership.Objects)
                if (value is XRWorldObjectBase structural)
                    CaptureEndpoints(structural, endpoints);
            using HierarchyStructuralReservation all = HierarchyStructuralReservation.TryAcquire([.. endpoints]) ??
                throw new InvalidOperationException("The clone changed before document handoff admission.");
            RequireAuthoredDocument();
            HashSet<XRObjectBase> privateObjects = new(ReferenceEqualityComparer.Instance);
            HashSet<XRObjectBase> installedLists = new(ReferenceEqualityComparer.Instance);
            foreach (ObjectConstructionIdentity identity in ownership.Identities)
            {
                if (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued)
                    throw new InvalidOperationException("An allocation changed lifetime before document handoff.");
                switch (identity.Target)
                {
                    case SceneNode node:
                        RequireInstalledList(node.OwnedRetirementComponents, node, ownership);
                        installedLists.Add(node.OwnedRetirementComponents!);
                        break;
                    case TransformBase transform:
                        RequireInstalledList(transform.OwnedRetirementChildren, transform, ownership);
                        installedLists.Add(transform.OwnedRetirementChildren!);
                        if (IsRetainedPrivateTransform(transform, ownership))
                        {
                            if (transform.GetType() != typeof(Transform))
                                throw new InvalidOperationException("Document handoff cannot retire a custom private transform.");
                            privateObjects.Add(transform);
                            privateObjects.Add(transform.OwnedRetirementChildren!);
                        }
                        break;
                    case XRComponent:
                        break;
                    default:
                        // Only exact structural containers belong to this profile.
                        // Assets and other helpers retain their complete construction receipt.
                        if (!IsStructuralContainer(identity.Target))
                            throw new InvalidOperationException("Document handoff retains an unsupported allocation.");
                        break;
                }
            }
            foreach (XRObjectBase value in ownership.Objects)
                if (value is not XRWorldObjectBase && !installedLists.Contains(value) && !IsTemporaryContainer(value))
                    throw new InvalidOperationException("A structural container has another lifetime owner.");

            ObjectConstructionOwnership privateOwnership = ownership.CreateSubset(privateObjects);
            ObjectConstructionOwnership adopted = ownership.CreateSubset(ownership.Objects.Where(value => !privateObjects.Contains(value)));
            Dictionary<XRScene, (ObjectConstructionIdentity Identity, SceneRootCollection Roots)> scenes =
                new(ReferenceEqualityComparer.Instance);
            RequireAdopted(adopted, scenes, captureScenes: true);

            // Replace the all-allocation reservation under the same gate. Private
            // transforms need their own strict retirement admission. No callback runs here.
            endpoints.Clear();
            foreach (XRObjectBase value in adopted.Objects)
                if (value is XRWorldObjectBase structural)
                    CaptureEndpoints(structural, endpoints);
            all.Dispose();
            HierarchyStructuralReservation adoptedReservation = HierarchyStructuralReservation.TryAcquire([.. endpoints]) ??
                throw new InvalidOperationException("The adopted hierarchy changed before document handoff.");
            try
            {
                void RequireCurrent()
                {
                    lock (SceneRootCollection.MutationGate)
                        RequireAdopted(adopted, scenes, captureScenes: false);
                }
                RequireCurrent();
                return new(privateOwnership, adoptedReservation, RequireCurrent);
            }
            catch
            {
                adoptedReservation.Dispose();
                throw;
            }
        }
    }

    private void RequireAuthoredDocument()
    {
        if (!IsCurrent() || _asset is null || _assetIdentity?.IsCurrent != true || _scenes is null)
            throw new InvalidOperationException("Document handoff requires the captured live authored world.");
    }

    private void RequireAdopted(ObjectConstructionOwnership ownership,
        Dictionary<XRScene, (ObjectConstructionIdentity Identity, SceneRootCollection Roots)> scenes, bool captureScenes)
    {
        RequireAuthoredDocument();
        HashSet<XRObjectBase> installedLists = new(ReferenceEqualityComparer.Instance);
        foreach (XRObjectBase value in ownership.Objects)
        {
            if (value is SceneNode node && node.OwnedRetirementComponents is { } components)
                installedLists.Add(components);
            else if (value is TransformBase transform && transform.OwnedRetirementChildren is { } children)
                installedLists.Add(children);
        }
        foreach (ObjectConstructionIdentity identity in ownership.Identities)
        {
            if (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued)
                throw new InvalidOperationException("An adopted allocation changed lifetime during document handoff.");
            if (identity.Target is not XRWorldObjectBase structural)
            {
                if (!installedLists.Contains(identity.Target) && !IsTemporaryContainer(identity.Target))
                    throw new InvalidOperationException("A temporary structural container gained unowned content during document handoff.");
                continue;
            }
            TransformBase transform = GetTransform(structural);
            if (structural is SceneNode node)
            {
                if (!ReferenceEquals(transform.OwnedRetirementNode, node))
                    throw new InvalidOperationException("An adopted node lost its exact transform.");
                RequireInstalledList(node.OwnedRetirementComponents, node, ownership);
            }
            if (structural is TransformBase ownedTransform)
                RequireInstalledList(ownedTransform.OwnedRetirementChildren, ownedTransform, ownership);
            if (structural is XRComponent component)
                RequireComponent(component);
            RequireDocumentPath(transform);
            for (TransformBase current = transform; ; current = current.OwnedRetirementParent!)
            {
                SceneRootMembership[] memberships = current.OwnedRetirementNode?.CaptureSceneRootMemberships() ?? [];
                foreach (SceneRootMembership membership in memberships)
                {
                    XRScene scene = membership.Scene;
                    if (membership.Count != 1 || !membership.Collection.IsCurrent ||
                        !ReferenceEquals(scene.SceneRoots, membership.Collection) ||
                        scene.IsDestroyed || scene.IsDestroyQueued || !ContainsAuthoredScene(scene))
                        throw new InvalidOperationException("An adopted root has no exact authored scene owner.");
                    if (captureScenes && !scenes.ContainsKey(scene))
                        scenes.Add(scene, (ObjectConstructionIdentity.Capture(scene), membership.Collection));
                    if (!scenes.TryGetValue(scene, out var captured) || !captured.Identity.IsCurrent ||
                        !ReferenceEquals(captured.Roots, membership.Collection))
                        throw new InvalidOperationException("An authored scene changed lifetime during document handoff.");
                }
                if (current.OwnedRetirementParent is not null)
                    continue;
                if (memberships.Length == 0)
                    throw new InvalidOperationException("Runtime-only roots cannot receive authored document custody.");
                break;
            }
        }
        // A scene captured before private cleanup must remain the same owner even
        // when cleanup callbacks change the world's asset list or scene lifetime.
        foreach (var pair in scenes)
            if (!pair.Value.Identity.IsCurrent || pair.Key.IsDestroyed || pair.Key.IsDestroyQueued ||
                !ContainsAuthoredScene(pair.Key) || !ReferenceEquals(pair.Key.SceneRoots, pair.Value.Roots) || !pair.Value.Roots.IsCurrent)
                throw new InvalidOperationException("A captured authored scene no longer owns the handoff.");
        RequireAuthoredDocument();
    }

    private bool ContainsAuthoredScene(XRScene scene)
    {
        if (_scenes is not null)
            foreach (XRScene candidate in _scenes)
                if (ReferenceEquals(candidate, scene))
                    return true;
        return false;
    }

    private static void RequireInstalledList<T>(EventList<T>? list, XRWorldObjectBase owner,
        ObjectConstructionOwnership ownership) where T : XRWorldObjectBase
    {
        if (list is null || list.GetType() != typeof(EventList<T>) || !ownership.Contains(list) ||
            list.IsDestroyed || list.IsDestroyQueued || list.GetMutationAuthority() is not HierarchyListAuthority<T> authority ||
            !authority.IsCurrent || !ReferenceEquals(authority.Owner, owner) || !ReferenceEquals(authority.Collection, list))
            throw new InvalidOperationException("An adopted structural list has no exact construction owner.");
    }

    private static bool IsStructuralContainer(XRObjectBase value)
        => value.GetType() == typeof(EventList<SceneNode>) ||
           value.GetType() == typeof(EventList<TransformBase>) || value.GetType() == typeof(EventList<XRComponent>);

    private static bool IsTemporaryContainer(XRObjectBase value)
        => value switch
        {
            EventList<SceneNode> nodes when nodes.GetType() == typeof(EventList<SceneNode>) => nodes.GetMutationAuthority() is null && nodes.Count == 0,
            EventList<TransformBase> transforms when transforms.GetType() == typeof(EventList<TransformBase>) => transforms.GetMutationAuthority() is null && transforms.Count == 0,
            EventList<XRComponent> components when components.GetType() == typeof(EventList<XRComponent>) => components.GetMutationAuthority() is null && components.Count == 0,
            _ => false
        };
}
