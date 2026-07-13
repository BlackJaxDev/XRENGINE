# Physics Backend Integration Finalization - Completed

Completed and archived on 2026-07-13.

This ledger closes the XRComponent physics-integration work for the shared PhysX and Jolt feature set. Backend-specific capabilities remain available only through explicitly named extension surfaces; they are not presented as cross-backend guarantees.

## Completion summary

| Area | PhysX | Jolt |
|---|---|---|
| Rigid bodies and transform synchronization | Complete | Complete |
| Scene queries and collision filtering | Complete | Complete |
| Joint adapters and XRComponent authoring | Complete | Complete |
| Geometry, compound colliders, and shape mutation | Complete | Complete |
| Character controllers | Complete | Complete |
| Serialization and replication policy | Complete | Complete |
| Editor support and debug visualization | Complete | Complete |
| Focused regression coverage | Complete | Complete |

## Closed checklist

### A. Joint and constraint integration

- [x] Make Fixed, Distance, Hinge, Prismatic, Spherical, and D6 joints editor-authorable XRComponents.
- [x] Give joint components ownership of native creation, destruction, and rebinding.
- [x] Mirror joint settings as serialized component properties and push them to active native joints.
- [x] Provide inspectors, gizmos, and break-callback routing.
- [x] Migrate ad hoc gameplay constraint creation to lifecycle-owned component/helper workflows.
- [x] Implement Jolt wrappers for the shared fixed, hinge, prismatic, distance/spring, spherical, and D6-equivalent constraints.
- [x] Use backend-neutral joint contracts from both backends.
- [x] Instantiate joints through the abstract scene factory.
- [x] Track and release managed/native joint ownership deterministically.
- [x] Cover Jolt playmode lifecycle, activation-order rebinding, teardown, and scene serialization.

Runtime-only grab constraints now use `RuntimeDistanceConstraintOwner`; authored constraints continue to use the joint component family.

### B. Backend-neutral gameplay API

- [x] Introduce backend-neutral actor, rigid-body, controller, query, material, geometry, authoring, and replication contracts.
- [x] Convert shared gameplay component APIs from PhysX concrete types to abstract contracts.
- [x] Move generic material definitions out of the PhysX namespace.
- [x] Replace PhysX query-filter compatibility data with `PhysicsQueryFilter` and shared actor/hit-detail flags.
- [x] Put native-only members behind explicitly named backend extension APIs and editor groups.
- [x] Route actor, controller, and collider creation through `IPhysicsBackendService` instead of concrete-scene factory switches.
- [x] Add reflection/source boundary tests that reject accidental backend types in shared gameplay APIs.

### C. Geometry and collider authoring

- [x] Keep primitive collision geometry in the neutral `IPhysicsGeometry` contract.
- [x] Add CPU-authored neutral convex-hull, indexed-triangle-mesh, and height-field geometry.
- [x] Transfer supported mesh data to true native Jolt convex, mesh, and height-field shapes.
- [x] Validate scale, scale rotation, collider-local rotation, and compound-child transforms.
- [x] Build true Jolt compound shapes from all enabled authored collider shapes.
- [x] Make compound collider authoring first-class through `PhysicsColliderShape` lists.
- [x] Add reusable `PhysicsColliderAsset` authoring and convex-decomposition output conversion.
- [x] Isolate PhysX-native geometry and cooking behind PhysX adapters/extensions.
- [x] Reject unsupported backend-native geometry explicitly instead of silently substituting a different collision shape.

### D. Rigid-body property parity

- [x] Map gravity, damping, mass, lock axes, motion quality/CCD, object layers, sleep state, and velocity operations for Jolt.
- [x] Apply shared solver iteration overrides to Jolt body creation/rebuild.
- [x] Preserve explicit behavior for maximum velocity and other shared creation settings.
- [x] Document and display live, creation-time, and unsupported backend feature behavior in the rigid-body inspector.

### E. Collision filtering and scene queries

- [x] Use coherent collision-group bit and layer-mask conversion for Jolt object layers.
- [x] Apply layer masks and static/dynamic actor filters before selecting query results.
- [x] Validate collision-matrix behavior against shared fixtures.
- [x] Validate ray, sweep, and overlap Any/Single/Multiple semantics and deterministic ordering.
- [x] Return world-space positions/normals, stable source face IDs, and requested-field semantics.
- [x] Compute PhysX-compatible triangle barycentric UV values for authored Jolt triangle meshes and return zero UV for non-mesh shapes.
- [x] Use the same backend-neutral query filter representation in gameplay and both scene implementations.

### F. Materials, compounds, assets, and shape mutation

- [x] Expose an engine-level `PhysicsMaterialDefinition` through components and collider shapes.
- [x] Keep shape-authoring data independent from backend cooking artifacts.
- [x] Support multiple enabled shapes per rigid body as a first-class compound setup.
- [x] Replace active Jolt shapes in place while preserving the native body identifier and component ownership.
- [x] Replace active PhysX shapes in place while preserving the native actor pointer and correctly releasing shape/material references.
- [x] Recompute PhysX mass/inertia after runtime compound replacement.
- [x] Store convex-decomposition results as reusable neutral collider assets.

### G. Character-controller parity

- [x] Introduce reusable backend-neutral `CharacterControllerComponent` and `IAbstractCharacterController` contracts.
- [x] Separate controller ownership/creation from character movement behavior.
- [x] Expose backend-neutral controller contact-state events.
- [x] Keep raw PhysX controller state only in explicitly named PhysX extension members.
- [x] Drive update, grounding, jump, and gravity paths through the active abstract controller.
- [x] Validate grounding, jumping, slopes, step offset, and contact transitions with Jolt runtime fixtures.
- [x] Preserve the authored foot position across resize/crouch and up-direction changes.
- [x] Load-test the fixed-timestep input path and keep 10,000 buffered moves allocation-bounded.

### H. Transform synchronization and rebuild hardening

- [x] Preserve component ownership, activation state, and stable native identifiers during Jolt body/shape rebuilds.
- [x] Synchronize active body and controller transforms after simulation through neutral contracts.
- [x] Validate remove/reactivate, reset, teleport, shape replacement, and repeated reload behavior.

### I. Serialization, replication, and lifecycle safety

- [x] Round-trip rigid bodies, compound collider shapes, material data, controller settings, joint references, limits, drives, and replication metadata through full scene YAML serialization.
- [x] Define replication authority and owner metadata for rigid bodies, controllers, and joints.
- [x] Implement lease application, authority handoff, server fallback, and local-simulation policy.
- [x] Exercise joint create/simulate/destroy and activation-order rebinding paths.
- [x] Exercise component-level controller movement and contact behavior.
- [x] Verify actors, controllers, and joints return to zero across repeated Jolt initialize/create/step/destroy cycles.
- [x] Validate spawn/reset, teleport, reload, and scene teardown paths.

### J. Editor, diagnostics, and regression coverage

- [x] Retain PhysX joint inspectors, gizmos, callbacks, and debug support.
- [x] Add native Jolt debug extraction for shapes, constraints, and contacts.
- [x] Surface Jolt live/creation-time/unsupported feature support in the inspector.
- [x] Add Jolt movement, serialization, reload, and teardown regression coverage.
- [x] Add ray/sweep/overlap query parity fixtures.
- [x] Add character movement regression fixtures for grounding, jumping, slopes, steps, resize, up-direction, and contacts.

## Implementation evidence

- Shared contracts and services: `XRENGINE/Scene/Physics/PhysicsContracts.cs`, `PhysicsBackendService.cs`, `PhysicsAuthoring.cs`, `PhysicsMaterial.cs`, and `PhysicsReplicationPolicy.cs`.
- Shared geometry and assets: `XRENGINE/Scene/Physics/IPhysicsGeometry.cs` and `PhysicsMeshGeometry.cs`.
- Jolt implementation: `XRENGINE/Scene/Physics/Jolt/JoltShapeFactory.cs`, `JoltScene.cs`, `JoltCharacterVirtualController.cs`, and `JoltEngineDebugRenderer.cs`.
- PhysX adapters and stable shape replacement: `XRENGINE/Scene/Physics/Physx/Geometry/`, `PhysxBackendService.cs`, `PhysxRigidActor.cs`, and `PhysxShape.cs`.
- Runtime constraint ownership: `XRENGINE/Scene/Components/Physics/Joints/RuntimeDistanceConstraintOwner.cs`.
- Editor capability presentation: `XREngine.Editor/ComponentEditors/RigidBodyComponentEditors.cs`.

## Validation

- [x] `XREngine.csproj` builds with zero warnings and zero errors.
- [x] `XREngine.UnitTests.csproj` builds with zero warnings and zero errors.
- [x] Consolidated physics finalization/parity selection: 184 passed, 0 failed, 0 skipped.
- [x] Full physics namespace after mesh/PhysicsChain follow-up repairs: 254 passed, 0 failed.
- [x] Boundary tests cover shared API exposure, backend service separation, and native geometry adapter isolation.
- [x] Runtime tests cover Jolt queries, geometry, controllers, diagnostics, teardown, serialization, replication, joint rebinding, and PhysX/Jolt stable shape mutation.

Post-closeout validation also repaired the shared `XRMesh` vertex/index-space contract, made unattached PhysicsChain setup safe, and removed the dirty-range upload allocation. The broader physics namespace is now fully green; environment-dependent GPU/shader tests remain skipped when their required hardware or shader assets are unavailable.

## Definition of done

- [x] Rigid bodies, joints, constraints, and controllers are authorable through reusable XRComponents.
- [x] Shared gameplay APIs do not require public backend concrete types.
- [x] Shared PhysX and Jolt features have equivalent contracts and parity fixtures.
- [x] Controller behavior is backend-neutral and runtime-tested.
- [x] Activation, teardown, serialization, reload, and authority handoff are automated-test covered.
- [x] Backend-specific capabilities are explicit extensions with non-silent editor diagnostics.
