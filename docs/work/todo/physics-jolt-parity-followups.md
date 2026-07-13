# Jolt Physics Parity Follow-ups

This document is the current follow-up list after completing the P0/P1/P2 physics-finalization API surface work. It tracks what remains to make Jolt behave like the PhysX backend while keeping user-facing APIs backend-neutral.

## Wrapper separation follow-ups

- Move backend-neutral types (`PhysicsMaterialDefinition`, `PhysicsColliderShape`, `PhysicsReplicationAuthority`, `IAbstractCharacterController`, query filters, and joint interfaces) into a generic physics namespace/project boundary that does not reference `XREngine.Rendering.Physics.Physx`.
- Keep PhysX-only native callback types (`PxQueryHit`, `PxFilterData`, `PhysxShape`, `PhysxRigidActor`, `PhysxCapsuleController`) behind explicit extension adapters and editor groups.
- Add analyzer/source-contract coverage that prevents `XREngine.Components.*` gameplay APIs from exposing `Physx*`, `Jolt*`, or `MagicPhysX.Px*` types except in files or members explicitly named as backend extensions.
- Split backend factories (`CreatePhysx*`, `CreateJolt*`) behind small backend service interfaces so components request abstract actors/controllers/colliders without switch statements on concrete scene types.

## Jolt feature parity remaining

- Implement true Jolt compound shape construction from `PhysicsColliderShape` lists instead of consuming only the first enabled shape.
- Replace mesh fallback shapes with native Jolt mesh/convex/height-field data transfer where feasible.
- Validate shape scale and local rotation parity against PhysX-authored fixtures.
- Complete query parity for ray/sweep/overlap ordering, normals, face IDs, UVs, and Any/Single/Multiple semantics.
- Add full controller parity tests for grounding, jumping, slopes, step offset, crouch/resize, up-direction changes, and contact events.
- Add scene save/load and playmode reload tests that assert `JoltPhysicsDiagnostics` actor/controller/joint counts return to zero after teardown.
- Add activation-order tests for joint rebinding when connected bodies activate before/after joint components.
- Implement Jolt debug draw extraction for shapes, contacts, and constraints; current diagnostics only expose counts/log hooks.
- Define network replication handoff behavior for Jolt bodies/controllers/joints and validate authority transfer with `PhysicsReplicationAuthority` metadata.
