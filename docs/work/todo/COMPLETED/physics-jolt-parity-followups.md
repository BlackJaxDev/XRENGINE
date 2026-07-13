# Jolt Physics Parity Follow-ups - Completed

Completed and archived on 2026-07-13 after the backend-neutral physics finalization work.

## Wrapper separation follow-ups

- [x] Move `PhysicsMaterialDefinition`, `PhysicsColliderShape`, `PhysicsReplicationAuthority`, `IAbstractCharacterController`, query filters, geometry, and joint interfaces into the generic physics namespace without PhysX/Jolt native references.
- [x] Keep `PxQueryHit`, `PxFilterData`, `PhysxShape`, `PhysxRigidActor`, and `PhysxCapsuleController` behind explicitly named PhysX adapters/extensions and editor groups.
- [x] Add reflection and source-contract coverage that prevents shared gameplay APIs from exposing `Physx*`, `Jolt*`, or `MagicPhysX.Px*` types outside explicitly named backend extensions.
- [x] Put backend actor/controller/collider factories behind `IPhysicsBackendService`, allowing components to request abstract objects without concrete-scene creation switches.

Evidence: `PhysicsBackendBoundaryTests`, `PhysicsGameplayApiBoundaryTests`, `PhysicsGeometryAdapterBoundaryTests`, and `PhysicsP0ApiContractTests` enforce these boundaries.

## Jolt feature parity follow-ups

- [x] Construct true Jolt compound shapes from every enabled `PhysicsColliderShape`, including local position and rotation.
- [x] Transfer neutral convex-hull, triangle-mesh, and height-field data to native Jolt shapes instead of fallback primitives.
- [x] Validate authored scale, scale rotation, collider-local rotation, compound children, and source-face metadata against fixtures.
- [x] Complete ray/sweep/overlap parity for ordering, world-space normals, face IDs, triangle barycentric UVs, requested hit fields, and Any/Single/Multiple semantics.
- [x] Cover grounding, jumping, slopes, step offset, crouch/resize, up-direction changes, contact events, and allocation-bounded fixed-timestep buffering.
- [x] Cover scene serialization and repeated playmode-style initialize/create/step/destroy cycles, asserting all Jolt diagnostic counts return to zero.
- [x] Cover joint rebinding when connected bodies activate before or after their joint components.
- [x] Extract native Jolt debug shapes and constraints and collect contact diagnostics for engine visualization.
- [x] Define and validate replication handoff for bodies, controllers, and joints using `PhysicsReplicationAuthority`, network identity, lease-owner metadata, and server fallback.

## Additional closeout fixes

- [x] Correct Jolt collision-group bitmask conversion and collision-matrix behavior.
- [x] Preserve Jolt body identifiers and PhysX actor pointers during live shape replacement.
- [x] Preserve authored triangle source-face IDs through compound/decorated Jolt shapes.
- [x] Compute PhysX-compatible triangle barycentric query UVs and return zero UV for non-mesh shapes.
- [x] Migrate transient gameplay distance constraints to `RuntimeDistanceConstraintOwner` so creation/removal is idempotent and lifecycle-owned.
- [x] Route character-movement fallback construction through the backend service rather than concrete scene switches.
- [x] Present Jolt live, creation-time, and unsupported rigid-body fields explicitly in the editor.

## Validation

- [x] Engine build: zero warnings, zero errors.
- [x] Unit-test project build: zero warnings, zero errors.
- [x] Consolidated finalization/parity suite: 184 passed, 0 failed, 0 skipped.
- [x] Query and geometry parity subset: 18 passed, 0 failed.
- [x] Stable PhysX compound shape mutation test: passed.
- [x] Runtime constraint ownership tests: passed.

Primary regression fixtures are `JoltQueryParityTests`, `JoltGeometryParityTests`, `JoltControllerParityTests`, `JoltProductionHardeningTests`, `PhysicsSceneSerializationTests`, `PhysicsReplicationPolicyTests`, `RuntimeDistanceConstraintOwnerTests`, and `PhysxShapeMutationTests`.
