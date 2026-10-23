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
    {
        var ownerWorld = node.World;
        EnqueueSceneEdit(() =>
        {
            if (node.IsDestroyed || !ReferenceEquals(node.World, ownerWorld) || component.IsDestroyed ||
                !ReferenceEquals(component.SceneNode, node) || !node.Components.Contains(component))
                return;
            node.DetachComponent(component);
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange($"Remove {component.GetType().Name}");
            Undo.RecordStructuralChange($"Remove {component.GetType().Name}",
                undoAction: () =>
                {
                    node.ReattachComponent(component);
                    MarkNativeComponentNodeDirty(node);
                },
                redoAction: () =>
                {
                    node.DetachComponent(component);
                    MarkNativeComponentNodeDirty(node);
                });
            MarkNativeComponentNodeDirty(node);
            completed?.Invoke();
        });
    }

    private static void MarkNativeComponentNodeDirty(SceneNode node)
    {
        RuntimeWorld? world = TryGetActiveWorldInstance();
        MarkSceneHierarchyDirty(node, FindSceneForNode(node, world), world);
    }
}
