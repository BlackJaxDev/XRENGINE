# Physics API

[Back to developer guides](../README.md)

This guide covers code-facing physics usage. For backend architecture and lifecycle details, see [Physics Architecture](../../architecture/physics/overview.md).

## Scene Access

World instances own the active physics scene. Components should usually interact through engine components rather than reaching directly into backend objects. Direct backend access is useful for integration code, custom queries, and solver-specific tooling.

## Actors And Components

Attach physics actors through components so activation, world binding, and teardown stay synchronized with the scene graph.

```csharp
var body = node.GetOrAddComponent<DynamicRigidBodyComponent>();
body.RigidBody = CreateDynamicBody();
```

When a component activates, it registers its actor with the active `AbstractPhysicsScene`. When it deactivates or is removed, it unregisters the actor.

## Queries

Use `AbstractPhysicsScene` query methods for raycast, sweep, and overlap work. Engine queries use shared data types such as `Segment`, `LayerMask`, `RaycastHit`, `SweepHit`, and `OverlapHit`.

```csharp
var segment = new Segment(origin, origin + direction * maxDistance);
var hits = physicsScene.RaycastMultiple(segment, layerMask);
```

Results resolve to owning `XRComponent` instances where possible, which keeps gameplay and editor tools backend-neutral.

## Backend Notes

PhysX is the production backend. Jolt and Jitter2 are experimental and should be treated as parity targets until their actor, query, and transform-propagation coverage matches PhysX.

If you add a backend:

1. Implement `AbstractPhysicsScene`.
2. Provide actor, shape, material, and joint wrappers.
3. Preserve layer-mask and query semantics.
4. Resolve backend results back to engine components.

## Physics Chains

Use `PhysicsChainComponent` for lightweight rope, cloth, hair, and tail simulation that does not require a full rigid-body solver. Chain colliders are regular components and can be shared across chains.

Performance details live in [Physics Chain Performance](../rendering/physics-chain-performance.md).

## Related Docs

- [Physics Architecture](../../architecture/physics/overview.md)
- [Component API](../components/component-api.md)
- [Physics User Guide](../../user-guide/physics.md)
