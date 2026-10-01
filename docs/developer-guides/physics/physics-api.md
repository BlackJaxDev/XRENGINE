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

When a component activates, it registers its actor with the active `AbstractPhysicsScene`. When it deactivates or is removed, it unregisters the actor. Component and scene contracts live in `XREngine.Runtime.Core`; gameplay code should use `IAbstractPhysicsActor`, `IAbstractDynamicRigidBody`, `IAbstractCharacterController`, and the typed neutral joint interfaces rather than concrete solver wrappers.

### Kinematic Motion And Immediate Pose Changes

Use `IAbstractDynamicRigidBody.KinematicTarget` for contact-producing movement and `SetTransform` for an immediate pose change, such as a reset. A non-null target selects kinematic motion; clearing it with `null` restores dynamic motion. Set targets during `PrePhysics` when they must affect that fixed step.

Jolt stores the latest target in a value mailbox and consumes it before character-controller moving-ground queries and native simulation. It uses `BodyInterface.MoveKinematic` to derive linear and angular velocity from the same finite, positive fixed delta passed to `PhysicsSystem.Update`; it does not teleport to the target. Multiple writes before consumption coalesce to the last value. Without another command, target-generated velocity is stopped before the following simulation step so a one-shot target does not keep moving the body. The getter retains the last requested target until cleared or replaced.

Jolt's immediate `SetTransform` cancels an unconsumed target without changing the body's motion type. It does not erase separately authored velocity. As with other immediate native body operations, callers must synchronize pose changes with simulation; only target publication uses the cross-thread mailbox. Non-finite positions, invalid rotations, and non-positive or non-finite simulation deltas fail explicitly. These implementation semantics do not establish contact/friction equivalence with PhysX; live solver comparison remains required.

## Queries

Use `AbstractPhysicsScene` query methods for raycast, sweep, and overlap work. Engine queries use shared data types such as `Segment`, `LayerMask`, `RaycastHit`, `SweepHit`, and `OverlapHit`.

```csharp
var segment = new Segment(origin, origin + direction * maxDistance);
var hits = physicsScene.RaycastMultiple(segment, layerMask);
```

Results resolve to owning `XRComponent` instances where possible, which keeps gameplay and editor tools backend-neutral.

## Backend Notes

PhysX remains the selected production default. Desktop composition registers the PhysX and Jolt modules in `PhysicsBackendCatalog`; Jolt is available for parity work but is not promoted until its full desktop and browser gates pass. Jitter2 is a separate experimental module that is absent from default composition. A known saved backend selection without an installed module fails with a named diagnostic.

The implementation projects are `XREngine.Runtime.Physics.PhysX`, `XREngine.Runtime.Physics.Jolt`, and `XREngine.Runtime.Physics.Jitter`. General live body settings use `IPhysicsRuntimeBodyProperties` and `IPhysicsDynamicBodySettings`; PhysX-specific component properties are labeled as extensions in editor metadata. `PhysicsQueryFilter` is the shared actor-type/layer query contract; native PhysX callback filters remain solver-specific.

Generated convex colliders use the optional `IPhysicsColliderAuthoringService`. The editor/cook host installs `XREngine.Runtime.Physics.Authoring`, which owns CoACD execution and its disk cache; the PhysX backend implements `IPhysicsConvexHullInstaller` for the resulting hulls. A server or client without authoring must load cooked collider data. Image-backed PhysX height fields require an installed `IPhysicsHeightFieldImageSource` rather than direct imaging-package access.

If you add a backend:

1. Implement `AbstractPhysicsScene` and the neutral actor, controller, joint, and query contracts in the new leaf.
2. Provide a module with a stable `EPhysicsLibrary` ID, capability report, and scene factory; register it explicitly in the application catalog.
3. Preserve layer-mask, actor-type filtering, ownership, and query-result semantics across backends.
4. Keep native wrappers and packages in the leaf, and resolve backend results back to engine components.

## Physics Chains

Use `PhysicsChainComponent` for lightweight rope, cloth, hair, and tail simulation that does not require a full rigid-body solver. Chain colliders are regular components and can be shared across chains.

Performance details live in [Physics Chain Performance](../rendering/physics-chain-performance.md).

## Related Docs

- [Physics Architecture](../../architecture/physics/overview.md)
- [Component API](../components/component-api.md)
- [Physics User Guide](../../user-guide/physics.md)
