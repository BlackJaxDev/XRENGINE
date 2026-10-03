using System.Numerics;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Networking;

/// <summary>
/// Replicates opted-in scene graphs through explicit factories. The simulation dispatcher owns all calls.
/// Wire IDs live in this session map, never in the process-wide object cache.
/// </summary>
public sealed class SceneReplicatedEntityWorld : IReplicatedEntityWorld
{
    private readonly RuntimeWorld world;
    private const int MaximumEntities = 4096;
    private readonly Dictionary<NetworkEntityId, SceneNode> _entities = [];
    private readonly Dictionary<NetworkEntityId, ReplicatedEntityState> _states = [];
    private readonly HashSet<NetworkEntityId> _created = [];
    private readonly Dictionary<(NetworkEntityId Entity, Guid Component), XRComponent> _components = [];
    private Guid _sessionId;
    private long _tick;
    private readonly Dictionary<string, SceneNode> _packageNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<SceneNode, string> _packagePaths = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SceneNode, ReplicatedNodeRestoreState> _restoreNodes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRComponent, (NetworkComponentSchema Schema, ReplicatedComponentState State)> _restoreComponents = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<XRComponent> _addedPackageComponents = new(ReferenceEqualityComparer.Instance);

    public SceneReplicatedEntityWorld(RuntimeWorld runtimeWorld)
    {
        world = runtimeWorld;
        if (world.TargetWorld is not { } asset)
            return;
        for (int sceneIndex = 0; sceneIndex < asset.Scenes.Count; sceneIndex++)
        {
            XRScene scene = asset.Scenes[sceneIndex];
            for (int rootIndex = 0; rootIndex < scene.RootNodes.Count; rootIndex++)
                IndexPackageNode(scene.RootNodes[rootIndex], $"{sceneIndex}/{rootIndex}");
        }
    }

    private void IndexPackageNode(SceneNode node, string path)
    {
        _packageNodes.Add(path, node);
        _packagePaths.Add(node, path);
        for (int index = 0; index < node.Transform.Children.Count; index++)
            if (node.Transform.Children[index].SceneNode is { } child)
                IndexPackageNode(child, $"{path}/{index}");
    }

    public ReplicatedWorldSnapshot CaptureSnapshot(Guid sessionId, long tickId)
    {
        NetworkReplicationSchemaRegistry.Freeze();
        var selected = new Dictionary<NetworkEntityId, SceneNode>();
        var stack = new Stack<SceneNode>();
        for (int i = 0; i < world.RootNodes.Count; i++)
            stack.Push(world.RootNodes[i]);
        while (stack.TryPop(out SceneNode? node))
        {
            for (int i = 0; i < node.Transform.Children.Count; i++)
                if (node.Transform.Children[i].SceneNode is { } child)
                    stack.Push(child);
            if (node.GetComponent<NetworkReplicatedComponent>() is null)
                continue;
            for (SceneNode? ancestor = node; ancestor is not null; ancestor = ancestor.Parent)
            {
                selected.TryAdd(new NetworkEntityId(ancestor.ID), ancestor);
                if (selected.Count > MaximumEntities)
                    throw new InvalidOperationException("Replicated world exceeds its entity limit.");
            }
        }

        // This bounded snapshot intentionally owns immutable copies, and runs at replication cadence rather than per frame.
        ReplicatedEntityState[] entities = selected.OrderBy(static pair => pair.Key.Value)
            .Select(pair => CaptureEntity(pair.Value)).ToArray();
        return new ReplicatedWorldSnapshot
        {
            SessionId = sessionId,
            TickId = tickId,
            Entities = entities,
            RequiredScenes = world.TargetWorld?.Scenes.Select(static scene => scene.Name ?? string.Empty).Order(StringComparer.Ordinal).ToArray() ?? [],
            RequiredSceneIds = world.TargetWorld?.Scenes.Select(static scene => scene.ID).Order().ToArray() ?? [],
            RequiredSchemas = entities.SelectMany(static entity => entity.Components)
                .Select(static component => (component.SchemaId, component.SchemaVersion)).Distinct()
                .Select(static schema => new ReplicationSchemaRequirement { SchemaId = schema.SchemaId, SchemaVersion = schema.SchemaVersion }).ToArray(),
        };
    }

    private ReplicatedEntityState CaptureEntity(SceneNode node)
    {
        if (node.Transform is not Transform transform)
            throw new InvalidOperationException($"Replicated node '{node.Name}' requires a supported TRS transform.");
        NetworkReplicatedComponent? marker = node.GetComponent<NetworkReplicatedComponent>();
        var components = new List<ReplicatedComponentState>();
        foreach (string schemaId in marker?.ComponentSchemas ?? [])
        {
            if (!NetworkReplicationSchemaRegistry.TryGetComponent(schemaId, out NetworkComponentSchema? schema) || schema is null)
                throw new InvalidOperationException($"Unregistered replicated component schema '{schemaId}'.");
            for (int index = 0; index < node.Components.Count; index++)
            {
                XRComponent component = node.Components[index];
                if (component.IsDestroyed || component.IsDestroyQueued || component.GetType() != schema.ComponentType)
                    continue;
                ReplicatedComponentState state = schema.Capture(component);
                state.ComponentId = component.ID;
                state.SchemaId = schema.Id;
                state.SchemaVersion = schema.Version;
                state.Active = component.IsActive;
                if (!schema.Validate(state))
                    throw new InvalidOperationException($"Invalid server state for '{schemaId}'.");
                components.Add(state);
            }
        }
        return new ReplicatedEntityState
        {
            EntityId = new NetworkEntityId(node.ID),
            SourcePath = _packagePaths.GetValueOrDefault(node),
            ParentId = node.Parent is null ? null : new NetworkEntityId(node.Parent.ID),
            FactoryId = marker?.FactoryId ?? "scene-node-v1",
            Name = node.Name ?? string.Empty,
            Active = node.IsActiveSelf,
            Translation = transform.Translation,
            Rotation = transform.Rotation,
            Scale = transform.Scale,
            Components = components.OrderBy(static component => component.ComponentId).ToArray(),
            Relevance = marker?.Relevance is { } relevance ? new NetworkRelevanceHint
            {
                EntityId = relevance.EntityId,
                Center = relevance.Center,
                Radius = relevance.Radius,
                Tags = relevance.Tags.ToArray(),
            } : null,
        };
    }

    public bool ValidateSnapshot(ReplicatedWorldSnapshot snapshot, out string? error)
    {
        NetworkReplicationSchemaRegistry.Freeze();
        error = null;
        if (snapshot.SessionId == Guid.Empty || snapshot.TickId < 0 || snapshot.Entities is null || snapshot.RequiredScenes is null || snapshot.RequiredSchemas is null)
            return Fail("Invalid world snapshot identity.", out error);
        string[] loaded = world.TargetWorld?.Scenes.Select(static scene => scene.Name ?? string.Empty).Order(StringComparer.Ordinal).ToArray() ?? [];
        if (!snapshot.RequiredScenes.Order(StringComparer.Ordinal).SequenceEqual(loaded, StringComparer.Ordinal))
            return Fail("Required scenes do not match the loaded verified world.", out error);
        if (snapshot.RequiredSceneIds is null || !snapshot.RequiredSceneIds.Order().SequenceEqual(world.TargetWorld?.Scenes.Select(static scene => scene.ID).Order() ?? Enumerable.Empty<Guid>()))
            return Fail("Required scene identities do not match the loaded verified world.", out error);
        foreach (ReplicationSchemaRequirement requirement in snapshot.RequiredSchemas)
            if (requirement is null || string.IsNullOrWhiteSpace(requirement.SchemaId) || !NetworkReplicationSchemaRegistry.TryGetComponent(requirement.SchemaId, requirement.SchemaVersion, out _))
                return Fail("Required game component schema is unavailable.", out error);
        return ValidateGraph(snapshot.Entities, out error);
    }

    public bool ValidateDelta(ReplicatedWorldDelta delta, out string? error)
    {
        if (delta.SessionId != _sessionId || delta.BaseTickId != _tick || delta.TickId <= delta.BaseTickId || delta.Upserts is null || delta.DestroyedEntityIds is null)
            return Fail("Delta does not extend the applied world baseline.", out error);
        var next = new Dictionary<NetworkEntityId, ReplicatedEntityState>(_states);
        var seen = new HashSet<NetworkEntityId>();
        foreach (NetworkEntityId id in delta.DestroyedEntityIds)
            if (!seen.Add(id) || !next.Remove(id))
                return Fail("Delta contains a duplicate or unknown removal.", out error);
        foreach (ReplicatedEntityState state in delta.Upserts)
        {
            if (state is null || !seen.Add(state.EntityId))
                return Fail("Delta contains conflicting entity operations.", out error);
            next[state.EntityId] = state;
        }
        return ValidateGraph(next.Values, out error);
    }

    private bool ValidateGraph(IEnumerable<ReplicatedEntityState> input, out string? error)
    {
        try { return ValidateGraphCore(input, out error); }
        catch (Exception ex) { return Fail($"Replicated game schema validation failed: {ex.Message}", out error); }
    }

    private bool ValidateGraphCore(IEnumerable<ReplicatedEntityState> input, out string? error)
    {
        error = null;
        var states = new Dictionary<NetworkEntityId, ReplicatedEntityState>();
        var componentIds = new HashSet<Guid>();
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReplicatedEntityState state in input)
        {
            if (state is null || state.EntityId.IsEmpty || !states.TryAdd(state.EntityId, state) || states.Count > MaximumEntities
                || state.Name is null || state.Name.Length > 256 || state.Components is null || state.Components.Length > 64
                || string.IsNullOrWhiteSpace(state.FactoryId) || !NetworkReplicationSchemaRegistry.SupportsFactory(state.FactoryId, state.SchemaVersion)
                || !Finite(state.Translation) || !Finite(state.Scale) || !Finite(state.Rotation)
                || state.Rotation.LengthSquared() < 0.5f || state.Rotation.LengthSquared() > 1.5f)
                return Fail("Unsupported or invalid replicated entity.", out error);
            if (state.SourcePath is { } source && (!sourcePaths.Add(source) || !_packageNodes.TryGetValue(source, out SceneNode? packageNode)
                || packageNode.Transform is not Transform))
                return Fail("Replicated package node does not exist in the loaded content.", out error);
            if (_states.TryGetValue(state.EntityId, out ReplicatedEntityState? prior) && (prior.FactoryId != state.FactoryId || prior.SchemaVersion != state.SchemaVersion))
                return Fail("An existing entity cannot change factory identity.", out error);
            if (prior is not null && prior.SourcePath != state.SourcePath)
                return Fail("An existing entity cannot change package identity.", out error);
            foreach (ReplicatedComponentState component in state.Components)
            {
                if (component is null || component.ComponentId == Guid.Empty || !componentIds.Add(component.ComponentId)
                    || component.Payload is null || component.Payload.Length > NetworkReplicationSchemaRegistry.MaximumComponentBytes
                    || component.EntityReferences is null || component.EntityReferences.Length > 64
                    || component.AssetReferences is null || component.AssetReferences.Length > 64
                    || string.IsNullOrWhiteSpace(component.SchemaId) || !NetworkReplicationSchemaRegistry.TryGetComponent(component.SchemaId, component.SchemaVersion, out NetworkComponentSchema? schema)
                    || schema is null || !schema.Validate(component))
                    return Fail("Unsupported or invalid replicated component.", out error);
                ReplicatedComponentState? previousComponent = prior?.Components.FirstOrDefault(previous => previous.ComponentId == component.ComponentId);
                if (previousComponent is not null && (previousComponent.SchemaId != component.SchemaId || previousComponent.SchemaVersion != component.SchemaVersion))
                    return Fail("An existing component cannot change schema identity.", out error);
                foreach (string asset in component.AssetReferences)
                    if (!WorldAssetIdentityProvider.IsVerifiedAssetPath(world.TargetWorld, asset))
                        return Fail("A component references content outside the verified package.", out error);
            }
        }
        foreach (ReplicatedEntityState state in states.Values)
        {
            var ancestors = new HashSet<NetworkEntityId> { state.EntityId };
            NetworkEntityId? parent = state.ParentId;
            while (parent is { } id)
            {
                if (!states.TryGetValue(id, out ReplicatedEntityState? ancestor) || !ancestors.Add(id) || ancestors.Count > 128)
                    return Fail("Replicated graph has a missing parent, cycle or excessive depth.", out error);
                parent = ancestor.ParentId;
            }
            foreach (ReplicatedComponentState component in state.Components)
                foreach (NetworkEntityId reference in component.EntityReferences)
                    if (!states.ContainsKey(reference))
                        return Fail("Replicated component references a missing entity.", out error);
        }
        return true;
    }

    public bool ApplySnapshot(ReplicatedWorldSnapshot snapshot, out string? error)
    {
        if (!ValidateSnapshot(snapshot, out error))
            return false;
        if (_sessionId != Guid.Empty && snapshot.SessionId != _sessionId)
            Reset();
        return ApplyGraph(snapshot.SessionId, snapshot.TickId, snapshot.Entities, out error);
    }

    public bool ApplyDelta(ReplicatedWorldDelta delta, out string? error)
    {
        if (!ValidateDelta(delta, out error))
            return false;
        var next = new Dictionary<NetworkEntityId, ReplicatedEntityState>(_states);
        foreach (NetworkEntityId id in delta.DestroyedEntityIds)
            next.Remove(id);
        foreach (ReplicatedEntityState state in delta.Upserts)
            next[state.EntityId] = state;
        return ApplyGraph(delta.SessionId, delta.TickId, next.Values.ToArray(), out error, delta.Upserts.Select(static state => state.EntityId).ToHashSet());
    }

    private bool ApplyGraph(Guid sessionId, long tick, ReplicatedEntityState[] states, out string? error, HashSet<NetworkEntityId>? changed = null)
    {
        error = null;
        try
        {
            var incoming = states.ToDictionary(static state => state.EntityId);
            // Side-channel-only deltas must not deactivate or reapply untouched game objects.
            ReplicatedEntityState[] updates = changed is null ? states : states.Where(state => changed.Contains(state.EntityId)).ToArray();
            // Create every entity before resolving parents and component references.
            foreach (ReplicatedEntityState state in updates)
                if (!_entities.ContainsKey(state.EntityId))
                {
                    SceneNode node;
                    if (state.SourcePath is { } source)
                    {
                        node = _packageNodes[source];
                        if (!_restoreNodes.ContainsKey(node))
                        {
                            var original = (Transform)node.Transform;
                            _restoreNodes.Add(node, new ReplicatedNodeRestoreState(node, node.Parent, node.Name, node.IsActiveSelf,
                                original.Translation, original.Rotation, original.Scale));
                        }
                    }
                    else
                    {
                        node = NetworkReplicationSchemaRegistry.CreateEntity(state.FactoryId, state.SchemaVersion);
                        node.IsActiveSelf = false;
                        _created.Add(state.EntityId);
                    }
                    _entities.Add(state.EntityId, node);
                }
            foreach (ReplicatedEntityState state in updates.OrderBy(state => Depth(state, incoming)))
            {
                SceneNode node = _entities[state.EntityId];
                node.IsActiveSelf = false;
                if (state.ParentId is { } parentId)
                {
                    if (node.Parent is null)
                        world.RootNodes.Remove(node);
                    node.Parent = _entities[parentId];
                }
                else if (node.Parent is not null)
                {
                    node.Parent = null;
                    world.RootNodes.Add(node);
                }
                node.Name = state.Name;
                if (node.Transform is not Transform transform)
                    throw new InvalidOperationException("Entity factory did not create the declared TRS transform.");
                transform.Translation = state.Translation;
                transform.Rotation = Quaternion.Normalize(state.Rotation);
                transform.Scale = state.Scale;
                var wanted = state.Components.Select(static component => component.ComponentId).ToHashSet();
                foreach (var key in _components.Keys.Where(key => key.Entity == state.EntityId && !wanted.Contains(key.Component)).ToArray())
                {
                    XRComponent removed = _components[key];
                    if (_restoreComponents.ContainsKey(removed))
                        removed.IsActive = false;
                    else
                        removed.Destroy();
                    _components.Remove(key);
                }
                foreach (ReplicatedComponentState component in state.Components)
                {
                    NetworkReplicationSchemaRegistry.TryGetComponent(component.SchemaId, component.SchemaVersion, out NetworkComponentSchema? schema);
                    var key = (state.EntityId, component.ComponentId);
                    if (!_components.TryGetValue(key, out XRComponent? instance))
                    {
                        if (!_created.Contains(state.EntityId))
                        {
                            for (int index = 0; index < node.Components.Count; index++)
                            {
                                XRComponent candidate = node.Components[index];
                                if (candidate.GetType() == schema!.ComponentType && !_components.ContainsValue(candidate))
                                {
                                    instance = candidate;
                                    ReplicatedComponentState original = schema.Capture(candidate);
                                    original.Active = candidate.IsActive;
                                    _restoreComponents.TryAdd(candidate, (schema, original));
                                    break;
                                }
                            }
                        }
                        if (instance is null)
                        {
                            instance = schema!.Create(node);
                            if (!_created.Contains(state.EntityId))
                                _addedPackageComponents.Add(instance);
                        }
                        _components.Add(key, instance);
                    }
                    if (instance.GetType() != schema!.ComponentType)
                        throw new InvalidOperationException("An existing component cannot change schema identity.");
                }
            }
            // All component factories have finished before any codec resolves references.
            foreach (ReplicatedEntityState state in updates)
                foreach (ReplicatedComponentState component in state.Components)
                {
                    NetworkReplicationSchemaRegistry.TryGetComponent(component.SchemaId, component.SchemaVersion, out NetworkComponentSchema? schema);
                    schema!.Apply(_components[(state.EntityId, component.ComponentId)], component, _entities);
                    _components[(state.EntityId, component.ComponentId)].IsActive = component.Active;
                }
            foreach (ReplicatedEntityState state in updates)
            {
                SceneNode node = _entities[state.EntityId];
                if (node.Parent is null && _created.Contains(state.EntityId) && !world.RootNodes.Contains(node))
                    world.RootNodes.Add(node);
                node.IsActiveSelf = state.Active;
            }
            // Reparent survivors first, then destroy removed roots so no surviving child is destroyed with its old parent.
            foreach (NetworkEntityId id in _entities.Keys.Where(id => !incoming.ContainsKey(id)).ToArray())
                RemoveEntity(id);
            _states.Clear();
            foreach (ReplicatedEntityState state in states)
                _states.Add(state.EntityId, state);
            _sessionId = sessionId;
            _tick = tick;
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            return Fail($"World replication application failed: {ex.Message}", out error);
        }
    }

    public void Reset()
    {
        foreach (var pair in _restoreComponents)
        {
            pair.Value.Schema.Apply(pair.Key, pair.Value.State, _entities);
            pair.Key.IsActive = pair.Value.State.Active;
        }
        _restoreComponents.Clear();
        foreach (XRComponent component in _addedPackageComponents)
            component.Destroy();
        _addedPackageComponents.Clear();
        foreach (ReplicatedNodeRestoreState original in _restoreNodes.Values)
        {
            SceneNode node = original.Node;
            if (node.Parent is null && original.Parent is not null)
                world.RootNodes.Remove(node);
            bool addRoot = node.Parent is not null && original.Parent is null;
            node.Parent = original.Parent;
            if (addRoot)
                world.RootNodes.Add(node);
            node.Name = original.Name;
            node.IsActiveSelf = original.Active;
            var transform = (Transform)node.Transform;
            transform.Translation = original.Translation;
            transform.Rotation = original.Rotation;
            transform.Scale = original.Scale;
        }
        foreach (NetworkEntityId id in _entities.Keys.ToArray())
            if (_created.Contains(id))
                RemoveEntity(id);
        _entities.Clear();
        _restoreNodes.Clear();
        _states.Clear();
        _components.Clear();
        _sessionId = Guid.Empty;
        _tick = 0;
    }

    private void RemoveEntity(NetworkEntityId id)
    {
        if (!_entities.Remove(id, out SceneNode? node))
            return;
        foreach (var key in _components.Keys.Where(key => key.Entity == id).ToArray())
            _components.Remove(key);
        if (_created.Remove(id))
        {
            node.Parent = null;
            world.RootNodes.Remove(node);
            node.Destroy();
        }
        else
            node.IsActiveSelf = false;
    }

    private static int Depth(ReplicatedEntityState state, IReadOnlyDictionary<NetworkEntityId, ReplicatedEntityState> states)
    {
        int depth = 0;
        while (state.ParentId is { } parent)
        {
            state = states[parent];
            depth++;
        }
        return depth;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static bool Fail(string message, out string? error) { error = message; return false; }
}
