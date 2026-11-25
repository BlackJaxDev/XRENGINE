using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    internal static NativeComponentType[] GetNativeComponentTypes(string? search)
        => GetFilteredComponentTypes(search)
            .Select(descriptor => new NativeComponentType(descriptor.Type, descriptor.DisplayName, descriptor.Namespace))
            .ToArray();

    internal static void QueueNativeComponentAdd(IReadOnlyList<SceneNode> nodes, Type componentType,
        Action<string?>? completed = null)
    {
        var targets = nodes.Where(node => node is not null).Distinct()
            .Select(node => (Node: node, World: node.World)).ToArray();
        EnqueueSceneEdit(() =>
        {
            List<(SceneNode Node, XRComponent Component)> added = [];
            List<HumanoidComponent> deferredHumanoids = [];
            List<string> failures = [];
            foreach (var (node, ownerWorld) in targets)
            {
                if (node.IsDestroyed || !ReferenceEquals(node.World, ownerWorld))
                {
                    failures.Add(node.Name ?? SceneNode.DefaultName);
                    continue;
                }
                HashSet<XRComponent> before = new(node.Components, ReferenceEqualityComparer.Instance);
                bool success = false;
                XRComponent? component = null;
                try
                {
                    if (typeof(HumanoidComponent).IsAssignableFrom(componentType))
                    {
                        using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
                            success = node.TryAddComponent(componentType, out component);
                    }
                    else
                        success = node.TryAddComponent(componentType, out component);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, $"Failed to add component '{componentType.Name}' to '{node.Name}'.");
                }
                XRComponent[] newlyAdded = node.Components.Where(candidate => !before.Contains(candidate)).ToArray();
                if (!success || component is null || !newlyAdded.Contains(component))
                {
                    // RequireComponents may have attached prerequisites before the requested
                    // component failed. Restore the node before recording any successful peers.
                    for (int index = newlyAdded.Length - 1; index >= 0; index--)
                    {
                        XRComponent created = newlyAdded[index];
                        node.DetachComponent(created);
                        created.Destroy();
                    }
                    failures.Add(node.Name ?? SceneNode.DefaultName);
                    continue;
                }
                foreach (XRComponent created in newlyAdded)
                {
                    added.Add((node, created));
                    if (created is HumanoidComponent humanoid)
                        deferredHumanoids.Add(humanoid);
                }
            }
            if (deferredHumanoids.Count > 0)
                QueueDeferredHumanoidInitialization(deferredHumanoids);
            if (added.Count > 0)
            {
                var captured = added.ToArray();
                string description = $"Add {componentType.Name}";
                using var interaction = Undo.BeginUserInteraction();
                using var scope = Undo.BeginChange(description);
                foreach (var (_, component) in captured)
                    Undo.Track(component);
                Undo.RecordStructuralChange(description,
                    undoAction: () =>
                    {
                        for (int i = captured.Length - 1; i >= 0; i--)
                        {
                            var (node, component) = captured[i];
                            node.DetachComponent(component);
                            MarkNativeComponentNodeDirty(node);
                        }
                    },
                    redoAction: () =>
                    {
                        foreach (var (node, component) in captured)
                        {
                            node.ReattachComponent(component);
                            MarkNativeComponentNodeDirty(node);
                        }
                    });
                foreach (var (node, _) in captured)
                    MarkNativeComponentNodeDirty(node);
            }
            completed?.Invoke(failures.Count == 0 ? null :
                $"Unable to add {componentType.Name} to: {string.Join(", ", failures)}.");
        });
    }

    internal static void QueueNativeComponentRemove(SceneNode node, XRComponent component,
        Action? completed = null)
        => QueueNativeComponentRemoveMany([(node, component)], completed);

    internal static void QueueNativeComponentRemoveMany(
        IReadOnlyList<(SceneNode Node, XRComponent Component)> components, Action? completed = null)
    {
        var targets = components.Select(entry =>
            (entry.Node, entry.Component, World: entry.Node.World)).ToArray();
        EnqueueSceneEdit(() =>
        {
            if (targets.Length == 0 || targets.Select(entry => entry.Component)
                .Distinct(ReferenceEqualityComparer.Instance).Count() != targets.Length)
                return;
            string description = $"Remove {targets[0].Component.GetType().Name}";
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange(description);
            // Establish the inverse before firing detach callbacks. If rollback
            // cannot fully restore membership, retain this step for recovery.
            Undo.RecordStructuralChange(description,
                undoAction: () => ApplyNativeComponentBatch(targets, attach: true, repairExisting: true),
                redoAction: () => ApplyNativeComponentBatch(targets, attach: false, repairExisting: true));
            ComponentBatchResult result = ApplyNativeComponentBatch(targets, attach: false);
            if (result != ComponentBatchResult.Success)
            {
                if (result == ComponentBatchResult.Restored)
                    scope.Cancel();
                else
                    Debug.UI("[NativeInspector] Component removal was partially applied; its Undo step was retained for recovery.");
                return;
            }
            completed?.Invoke();
        });
    }

    private enum ComponentBatchResult { Success, Restored, Partial }

    private static ComponentBatchResult ApplyNativeComponentBatch(
        (SceneNode Node, XRComponent Component, IRuntimeWorldContext? World)[] targets,
        bool attach, bool repairExisting = false)
    {
        static bool HasOwner((SceneNode Node, XRComponent Component, IRuntimeWorldContext? World) entry)
            => !entry.Node.IsDestroyed && !entry.Component.IsDestroyed &&
               ReferenceEquals(entry.Node.World, entry.World) &&
               ReferenceEquals(entry.Component.SceneNode, entry.Node);

        // Validate ownership before touching any target. Undo/Redo can repair a
        // mixed membership state left by a failed callback or rollback.
        if (targets.Any(entry => !HasOwner(entry)))
        {
            if (repairExisting)
                Debug.UI("[NativeInspector] Undo/Redo could not restore a component whose owner or lifetime changed.");
            return repairExisting ? ComponentBatchResult.Partial : ComponentBatchResult.Restored;
        }
        if (!repairExisting && targets.Any(entry =>
            entry.Node.Components.Contains(entry.Component) == attach))
            return ComponentBatchResult.Restored;
        bool[] originalAttached = targets.Select(entry =>
            entry.Node.Components.Contains(entry.Component)).ToArray();

        try
        {
            foreach (var entry in targets)
            {
                if (!HasOwner(entry))
                    throw new InvalidOperationException("A component target changed owner during the batch.");
                var (node, component, _) = entry;
                if (node.Components.Contains(component) == attach)
                    continue;
                if (attach)
                    node.ReattachComponent(component);
                else if (!node.DetachComponent(component))
                    throw new InvalidOperationException("The component was no longer attached.");
            }
            if (targets.Any(entry => !HasOwner(entry) ||
                entry.Node.Components.Contains(entry.Component) != attach))
                throw new InvalidOperationException("A component callback changed batch membership or ownership.");
            foreach (var (node, _, _) in targets)
                MarkNativeComponentNodeDirty(node);
            if (targets.Any(entry => !HasOwner(entry) ||
                entry.Node.Components.Contains(entry.Component) != attach))
                throw new InvalidOperationException("Dirty-state callbacks changed component batch ownership or membership.");
            return ComponentBatchResult.Success;
        }
        catch (Exception exception)
        {
            // Check every captured component, including ones mutated by callbacks on
            // a different target, and restore each target's original membership.
            bool restored = true;
            for (int index = targets.Length - 1; index >= 0; index--)
            {
                var entry = targets[index];
                var (node, component, _) = entry;
                try
                {
                    if (!HasOwner(entry))
                    {
                        restored = false;
                        continue;
                    }
                    bool isAttached = node.Components.Contains(component);
                    if (originalAttached[index] && !isAttached)
                        node.ReattachComponent(component);
                    else if (!originalAttached[index] && isAttached)
                        node.DetachComponent(component);
                    MarkNativeComponentNodeDirty(node);
                    if (node.Components.Contains(component) != originalAttached[index])
                        restored = false;
                }
                catch (Exception rollbackException)
                {
                    restored = false;
                    Debug.LogException(rollbackException,
                        $"Failed to roll back component change on '{node.Name}'.");
                }
            }
            // Rollback callbacks can mutate an earlier target after it was checked.
            // Only discard the inverse if the whole captured batch is restored now.
            if (targets.Where((entry, index) => !HasOwner(entry) ||
                entry.Node.Components.Contains(entry.Component) != originalAttached[index]).Any())
                restored = false;
            Debug.LogException(exception, restored
                ? "Failed to apply native component batch; original membership restored."
                : "Failed to apply native component batch; rollback left partial membership.");
            return restored ? ComponentBatchResult.Restored : ComponentBatchResult.Partial;
        }
    }

    private static void MarkNativeComponentNodeDirty(SceneNode node)
    {
        RuntimeWorld? world = TryGetActiveWorldInstance();
        MarkSceneHierarchyDirty(node, FindSceneForNode(node, world), world);
    }
}
