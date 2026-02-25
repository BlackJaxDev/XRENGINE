# Jolt + XRComponents Integration Gap Analysis

## Scope
This document identifies what remains for **full Jolt integration surfaced through XRComponents** for:
- rigid bodies
- joints/constraints
- character controllers
- scene queries
- layer/filtering parity
- gameplay and editor workflows

## Current State (implemented)

### 1) Basic Jolt scene + rigid body creation exists
- `JoltScene` implements the `AbstractPhysicsScene` contract and can initialize/update/destroy a Jolt physics world.
- `DynamicRigidBodyComponent` and `StaticRigidBodyComponent` can create Jolt rigid bodies when `World.PhysicsScene` is `JoltScene`.
- Linear/angular velocity paths are partially wired through `JoltDynamicRigidBody`.

### 2) Basic query implementations exist
- `JoltScene` contains raycast/sweep/overlap methods that map hits back to owning components and return engine hit dictionaries.

### 3) Jolt character controller prototype exists
- `JoltCharacterVirtualController` exists and is plugged into `CharacterMovement3DComponent` through an internal adapter.
- Input buffering and per-step consume flow are implemented.

### What remains incomplete
Jolt is functional but still missing major parity areas and robust XRComponent-level integration.

---

## Remaining Gaps for "Full Integration"

## A) Joints and constraints are effectively missing for Jolt (highest impact, critical)
**Problem:** There are no Jolt joint wrappers parallel to the PhysX joint set, and no XRComponent joint authoring path targeting Jolt.

### What is missing
- Jolt-side wrappers/adapters for fixed, hinge, slider/prismatic, distance/spring, and configurable (D6-equivalent) constraints.
- Abstract constraint interfaces usable by both backends.
- XRComponent joint components that instantiate Jolt constraints when running in `JoltScene`.
- Managed wrappers with lifetime ownership tracking.
- Integration into playmode lifecycle and scene serialization.

### Impact
- Jolt cannot yet support grab constraints, articulated setups, or any gameplay relying on runtime joints.

### Definition of done
- Common joint authoring works in Jolt scenes, not only PhysX scenes.

---

## B) Geometry support for Jolt is incomplete (critical)
**Problem:** Several `IPhysicsGeometry.AsJoltShape()` implementations still throw `NotImplementedException`.

### Missing shape conversions
- ConvexMesh
- TriangleMesh
- HeightField
- TetrahedronMesh (if required by gameplay)
- ParticleSystem geometry (or explicitly de-scope)

### Remaining tasks
- Ensure shape scale/rotation parity with PhysX behavior and existing authored content.
- Add fallback diagnostics when conversion cannot be represented in Jolt.

### Impact
- Many existing PhysX collision authoring patterns cannot run under Jolt.

### Definition of done
- Geometry types used by existing XRComponents can instantiate in Jolt without exceptions.

---

## C) Rigid-body property parity is partial
**Problem:** Many rigid-body component properties are PhysX-only in practice; Jolt paths currently apply a smaller subset.

### What is missing
- Mapping for mass, damping, sleep thresholds, lock axes, mass/inertia overrides, gravity toggles, CCD-like behavior, solver iteration equivalents, etc.
- Explicit fallback semantics when Jolt has no 1:1 feature.
- Clearly mark unsupported fields in inspector and avoid silent no-op behavior.
- Fix/validate sleep state semantics (`IsSleeping`) and motion-state handling for dynamic bodies.

### Definition of done
- Core rigid body gameplay tuning behaves comparably between PhysX and Jolt in shared components.

---

## D) Collision filtering / layer mapping is not production-ready
**Problem:** `LayerMask.AsJoltObjectLayer()` currently maps raw mask bits directly to `ObjectLayer`, while `JoltScene` initializes a simplistic 2-layer broadphase/filter table.

### What is missing
- A coherent mapping from engine layer mask semantics to Jolt object layers and broadphase layers.
- Validation of collision matrix parity against PhysX layer behavior.
- Apply `LayerMask` filtering consistently in all Jolt query variants.
- Replace PhysX-filter compatibility hacks with backend-neutral query filter data.
- Ensure ray/sweep/overlap normals, face/sub-shape IDs, and distances match expected semantics.
- Add tests for Any/Single/Multiple ordering and filtering behavior.

### Impact
- Risk of incorrect collisions/non-collisions and inconsistent gameplay behavior.

### Definition of done
- Picking/gameplay queries produce equivalent behavior across PhysX and Jolt for shared features.

---

## E) Transform sync and shape mutation flows need hardening
**Problem:** Shape changes currently use remove/re-add simplification and transform sync pathways are still less mature than PhysX.

### Remaining tasks
- Implement robust body/shape rebuild paths that preserve component ownership, activation state, and stable identifiers.
- Ensure active bodies/controllers synchronize transforms after simulation with same guarantees expected by `RigidBodyTransform`.
- Validate reset/teleport/reload flows for Jolt bodies and controllers.

### Definition of done
- Runtime/editor shape edits and reset operations are predictable in Jolt scenes.

---

## F) Character movement contains PhysX-gated behavior
**Problem:** `CharacterMovement3DComponent` still has PhysX-only exposed properties and logic branches that gate movement updates on the PhysX controller property.

### What is missing
- Remove PhysX-only public API assumptions from gameplay-facing movement component.
- Ensure update/grounding/jump/gravity paths rely on abstract `ActiveController`, not PhysX `Controller` property checks.
- Add consistent state-query abstraction so gameplay/UI can inspect either backend.
- Validate slope/step/contact behavior against movement module expectations.
- Add/verify collision callbacks and interaction with dynamic rigid bodies.
- Harden resize/crouch/up-direction transitions.
- Benchmark and tune controller update behavior under fixed timestep load.

### Impact
- Jolt controller integration exists but is not yet feature-parity or behavior-parity safe.

### Definition of done
- Character movement component behaves consistently and reliably on Jolt.

---

## G) Editor/tooling/test coverage
**Problem:** Jolt backend lacks the same depth of tooling and regression coverage as PhysX.

### What is missing
- Physics debug visualization parity (contacts, constraints, shapes where feasible).
- Inspector UX that clearly communicates supported/unsupported Jolt features.
- Jolt scene smoke tests.
- XRComponent lifecycle tests under Jolt.
- Query parity tests (ray/sweep/overlap) versus known fixtures.
- Character movement regression tests for jumping, grounding, slopes, and step offset.
- Integration tests for future joint components and scene serialization/playmode transitions.

### Definition of done
- Jolt backend can be used as a supported runtime option, not only an experimental sandbox.

---

## Recommended Implementation Plan

### Phase 1 — Make Jolt viable for current rigid-body scenes
1. Complete `AsJoltShape()` implementations for all shapes in `IPhysicsGeometry`.
2. Implement robust layer/object/broadphase mapping and matrix tests.
3. Expand rigid-body property mapping with explicit unsupported-feature policy.

### Phase 2 — Introduce backend-neutral joint architecture
1. Add abstract joint/constraint interfaces in common physics namespace.
2. Implement Jolt joint adapters for core constraint types.
3. Add XRComponent joint components that instantiate either PhysX or Jolt adapters.
4. Integrate into playmode lifecycle and scene serialization.

### Phase 3 — Controller parity and gameplay decoupling
1. Remove PhysX-only public exposure from character movement API.
2. Ensure all movement and grounding logic uses backend-neutral controller interface.
3. Add parity tests for movement behavior across PhysX and Jolt.

### Phase 4 — Hardening
1. Add activation/deactivation leak checks for Jolt actors/controllers/joints.
2. Harden transform sync and shape mutation flows.
3. Add stress tests for rapid scene reload and component rebind.
4. Add debug visualization and telemetry hooks comparable to PhysX workflows.

This order minimizes user-facing breakage while moving Jolt from experimental to production-capable.

---

## Definition of Done for Jolt Integration
Jolt is "fully integrated with XRComponents" when:
- all core shapes, rigid body settings, and scene queries used by gameplay are supported,
- joints/constraints are authorable through the same XRComponent model as PhysX,
- character controller behavior is backend-neutral and parity-tested,
- and automated tests validate lifecycle correctness, filtering correctness, and gameplay-level parity.
