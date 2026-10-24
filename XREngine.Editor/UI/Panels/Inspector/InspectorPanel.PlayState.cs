using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class InspectorPanel
{
    private RuntimeWorld? _playWorld;
    private Guid? _playWorldId;
    private Guid? _playOwnerWorldId;
    private InspectorPlayTarget[]? _playInspectedTargets;
    private bool _playTransitionsSubscribed;

    private void SubscribePlayModeTransitions()
    {
        if (_playTransitionsSubscribed)
            return;
        Engine.PlayMode.PreEnterPlay += CapturePlayModeState;
        Engine.PlayMode.PostExitPlay += RestorePlayModeState;
        _playTransitionsSubscribed = true;
    }

    private void UnsubscribePlayModeTransitions()
    {
        if (!_playTransitionsSubscribed)
            return;
        Engine.PlayMode.PreEnterPlay -= CapturePlayModeState;
        Engine.PlayMode.PostExitPlay -= RestorePlayModeState;
        _playTransitionsSubscribed = false;
    }

    private void CapturePlayModeState()
    {
        if (IsDestroyed || IsDestroyQueued || _playInspectedTargets is not null ||
            InspectedObjects is not { Length: > 0 } targets)
            return;
        // A locked panel may inspect another world than the global selection. Only
        // rebind this panel's own targets; global selection has one canonical owner.
        RuntimeWorld? world = null;
        List<InspectorPlayTarget> identities = [];
        foreach (object? target in targets)
        {
            RuntimeWorld? targetWorld;
            InspectorPlayTarget identity;
            switch (target)
            {
                case SceneNode node when !node.IsDestroyed:
                    targetWorld = node.World as RuntimeWorld;
                    identity = new InspectorPlayTarget(node.ID, null, false);
                    break;
                case XRComponent { SceneNode: { } owner } component when
                    !component.IsDestroyed && !owner.IsDestroyed:
                    targetWorld = component.World as RuntimeWorld;
                    identity = new InspectorPlayTarget(component.ID, owner.ID, true);
                    break;
                default:
                    return;
            }
            if (targetWorld is null || world is not null && !ReferenceEquals(world, targetWorld))
                return;
            world = targetWorld;
            identities.Add(identity);
        }
        if (world?.TargetWorld is not { } sourceWorld)
            return;
        if (_virtualPropertyList is not null)
            CaptureViewState();
        _playWorld = world;
        _playWorldId = sourceWorld.ID;
        _playOwnerWorldId = (Window?.TargetWorldInstance?.WorldContext as RuntimeWorld)?.TargetWorld?.ID;
        _playInspectedTargets = identities.ToArray();
    }

    private void RestorePlayModeState()
    {
        InspectorPlayTarget[]? targetIds = _playInspectedTargets;
        if (targetIds is null || IsDestroyed || IsDestroyQueued)
            return;
        // A hidden panel keeps its snapshot until it can rebuild its view.
        if (!IsActiveInHierarchy &&
            Engine.PlayMode.Configuration.StateRestorationMode != EStateRestorationMode.PersistChanges)
            return;
        RuntimeWorld? world = _playWorld;
        Guid? worldId = _playWorldId;
        Guid? ownerWorldId = _playOwnerWorldId;
        _playInspectedTargets = null;
        _playWorld = null;
        _playWorldId = null;
        _playOwnerWorldId = null;
        if (worldId is null ||
            Engine.PlayMode.Configuration.StateRestorationMode == EStateRestorationMode.PersistChanges)
            return;
        if (ownerWorldId is { } expectedOwnerWorld &&
            (Window?.TargetWorldInstance?.WorldContext as RuntimeWorld)?.TargetWorld?.ID != expectedOwnerWorld)
        {
            _componentTargets = [];
            InspectedObjects = null;
            return;
        }
        if (world is null || world.IsDisposing || world.TargetWorld?.ID != worldId)
            world = RuntimeWorldRegistryServices.Current?.Snapshot().Values
                .FirstOrDefault(candidate => !candidate.IsDisposing && candidate.TargetWorld?.ID == worldId);
        if (world is null)
        {
            _componentTargets = [];
            InspectedObjects = null;
            return;
        }

        Dictionary<Guid, SceneNode> nodes = [];
        Stack<SceneNode> pending = new(world.RootNodes);
        while (pending.TryPop(out SceneNode? node))
        {
            if (node.IsDestroyed || !ReferenceEquals(node.World, world))
                continue;
            nodes[node.ID] = node;
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is { } childNode)
                    pending.Push(childNode);
        }
        object?[] targets = targetIds.Select(target =>
        {
            if (!nodes.TryGetValue(target.OwnerId ?? target.Id, out SceneNode? node))
                return null;
            return target.IsComponent
                ? (object?)node.Components.OfType<XRComponent>().FirstOrDefault(component => !component.IsDestroyed && component.ID == target.Id)
                : node;
        }).ToArray();
        if (targets.Any(target => target is null))
        {
            _componentTargets = [];
            InspectedObjects = null;
            return;
        }

        _componentTargets = targets.All(target => target is XRComponent)
            ? targets.Cast<XRComponent>().Select(component => component.SceneNode)
                .Distinct<SceneNode>(ReferenceEqualityComparer.Instance).ToArray()
            : [];
        _savedViewTargets = targets.ToArray();
        InspectedObjects = targets;
        if (IsActiveInHierarchy && _propertyScroll is not null)
            RestoreViewState();
    }
}
