# Physics Backend Integration Gap Analysis

> Merged document — replaces the former per-backend files
> `physx-xrcomponents-integration-gap-analysis.md` and
> `jolt-xrcomponents-integration-gap-analysis.md`.

## Scope

This document tracks what remains for **full physics integration surfaced through XRComponents** across both supported backends (**PhysX** and **Jolt**):

- rigid bodies
- joints / constraints
- character controllers
- scene queries
- collision filtering / layer mapping
- material / geometry authoring
- gameplay-facing component workflows
- serialization / networking / test hardening

---

## Status Summary (2026-02-25)

| Area | PhysX | Jolt |
|---|---|---|
| Rigid bodies | Done | Done (property parity partial) |
| Scene queries | Done | Done (layer filtering validated) |
| Joint adapters | Done — PhysX wrappers + XRComponents | Done — Jolt adapters wired through joint factory |
| Joint XRComponent authoring | Done (Phases 1-3) | Done — same components use abstract factory |
| Joint lifecycle tests | Done (121 tests) | Done (create → step → release) |
| Geometry / shape support | Done (native cooking) | Fallback paths added; full fidelity TBD |
| Layer / filter mapping | Done | Baseline implemented + tested; matrix parity TBD |
| Character controller | Functional (PhysX capsule) | Prototype functional; parity TBD |
| API de-PhysXing | In progress | Blocked on PhysX de-coupling first |
| Material / compound collider authoring | PhysX-first; abstraction TBD | Deferred until PhysX abstraction exists |
| Serialization / networking | Partial; joint round-trip tested | Not yet validated |
| Editor / debug visualization | PhysX has basic support | Not yet implemented |

---

## Current State (implemented)

### PhysX

1. **Rigid bodies** — `DynamicRigidBodyComponent` / `StaticRigidBodyComponent` auto-create PhysX actors, bind to `RigidBodyTransform`, and synchronize broad body settings (damping, mass, flags, collision metadata).
2. **Joints** — Engine wrappers exist for Fixed, Distance, D6, Revolute, Prismatic, Spherical, Gear, Rack-and-Pinion, and Contact joints. `PhysxScene` exposes native constraint counting/access and callback hookup.
3. **Character controller** — `CharacterMovement3DComponent` has a PhysX capsule-controller adapter via `ControllerManager`, wired into `RigidBodyTransform` for transform sync.
4. **Scene queries** — Raycast, sweep, overlap APIs are implemented in `PhysxScene` and consumed by world/view logic.
5. **Joint XRComponents (Phases 1-4):**
   - Phase 1: joint XRComponents exist (`PhysicsJointComponent` + Fixed/Distance/Hinge/Prismatic/Spherical/D6) with lifecycle ownership and rebind.
   - Phase 2: backend-neutral joint contracts (`IAbstractJoint` family) are in active use.
   - Phase 3: joint inspector tooling, in-editor gizmos, and PhysX break-callback routing are implemented.
   - Phase 4 (initial): comprehensive joint component tests (defaults, round-trip, lifecycle safety, VR grab workflow).

### Jolt

1. **Rigid bodies** — `JoltScene` implements `AbstractPhysicsScene`; `DynamicRigidBodyComponent` / `StaticRigidBodyComponent` can create Jolt bodies when the world scene is `JoltScene`. Velocity paths are wired through `JoltDynamicRigidBody`.
2. **Rigid-body property parity (baseline)** — gravity toggle, damping, mass scaling, lock axes, motion-quality/CCD mapping, object-layer updates, and sleep semantics are implemented.
3. **Scene queries** — Raycast/sweep/overlap methods map hits to owning components with layer-mask filtering applied in all query variants.
4. **Joint adapters** — Jolt adapters for Fixed, Distance, Hinge, Prismatic, Spherical, and D6 constraints are implemented. `JoltScene` joint factory creates/removes native Jolt constraints with managed lifetime tracking.
5. **Geometry fallbacks** — All `IPhysicsGeometry.AsJoltShape()` paths return safe fallback shapes with diagnostics instead of throwing `NotImplementedException`.
6. **Character controller prototype** — `JoltCharacterVirtualController` is plugged into `CharacterMovement3DComponent` with input buffering and per-step consume flow.
7. **Focused tests** — Deterministic tests for layer mapping, raycast layer filtering, and joint lifecycle (create → step → release) are passing.

---

## Remaining Gaps

### A) Joint / constraint integration

#### PhysX — completed
- [x] Joint component family is editor-authorable (Fixed, Distance, Hinge, Prismatic, Spherical, D6).
- [x] Component lifecycle owns native joint creation/destruction and rebind.
- [x] Joint settings mirrored as component properties and pushed to native joints.
- [x] Editor integration (inspectors, gizmos, break callbacks).
- [ ] Migrate remaining ad hoc gameplay constraint creation paths to componentized workflows.

#### Jolt — core done, lifecycle TBD
- [x] Jolt-side wrappers for fixed, hinge, slider/prismatic, distance/spring, and D6-equivalent constraints.
- [x] Abstract constraint interfaces usable by both backends.
- [x] XRComponent joint components instantiate Jolt constraints through scene joint factory.
- [x] Managed wrappers with lifetime ownership tracking.
- [ ] Playmode lifecycle + scene serialization coverage for Jolt joints.

**Definition of done:** common joint authoring works identically in both PhysX and Jolt scenes.

---

### B) PhysX-specific API leakage in gameplay components

**Problem:** Key gameplay components still expose PhysX concrete types (`PhysxCapsuleController`, `PhysxDynamicRigidBody`, `PhysxShape`, `Px...` enums/flags). Shared abstractions leak PhysX concepts (query filters, material namespace, etc.).

- [ ] Backend-neutral façade interfaces for components that should run against either backend.
- [ ] Convert public gameplay component API from PhysX concrete types to abstract contracts.
- [ ] Move generic material abstraction out of PhysX namespace.
- [ ] Create backend-agnostic query filter representation used by all scenes.
- [ ] Ensure PhysX-only component properties have explicit metadata/UI grouping.

**Definition of done:** XRComponent APIs clearly separate cross-backend guarantees from PhysX extensions; shared gameplay systems compile without direct dependency on `PhysxJoint_*` types.

---

### C) Geometry / shape support

#### PhysX — done
Shape cooking, compound bodies, and convex decomposition are functional through native PhysX pipelines.

#### Jolt — fallbacks in place, full fidelity TBD
- [x] ConvexMesh, TriangleMesh, HeightField, TetrahedronMesh, ParticleSystem fallback paths added.
- [ ] Shape scale/rotation behavior fully parity-validated against PhysX-authored content.
- [ ] True mesh-based Jolt shapes where native data transfer is feasible.

**Definition of done:** geometry types used by existing XRComponents instantiate in Jolt without exceptions, and scale/rotation behavior matches PhysX for shared authored content.

---

### D) Rigid-body property parity

#### PhysX — done
Broad property synchronization (damping, mass, flags, collision metadata) is present.

#### Jolt — baseline done, full mapping TBD
- [x] Gravity toggle, damping, mass scaling, lock axes, motion-quality/CCD flag, object-layer updates.
- [x] Sleep semantics use Jolt active-state checks.
- [ ] Solver-iteration equivalent tuning and complete feature mapping matrix.
- [ ] Inspector-level unsupported-field UX (explicit non-silent policy).

**Definition of done:** core rigid-body gameplay tuning behaves comparably between PhysX and Jolt in shared components.

---

### E) Collision filtering / layer mapping

#### PhysX — done
Layer-based filtering is production-ready.

#### Jolt — baseline done, matrix parity TBD
- [x] Coherent mask-based object-layer mapping.
- [x] Layer-mask filtering applied in Jolt query handling.
- [x] Deterministic tests for layer mapping and raycast layer filtering.
- [ ] Collision-matrix parity validation versus PhysX behavior.
- [ ] Full query parity suite for ordering/normals/face-id semantics across Any/Single/Multiple.
- [ ] Replace PhysX-filter compatibility hacks with backend-neutral query filter data.

**Definition of done:** picking/gameplay queries produce equivalent behavior across PhysX and Jolt for shared features.

---

### F) Material / collider authoring

**Problem:** Material class and many shape/cooking flows are PhysX-centric. No backend-neutral material abstraction or first-class compound collider authoring exists.

- [ ] Engine-level material abstraction exposed by components.
- [ ] Shape-authoring data contracts independent from backend cooking artifacts.
- [ ] Multi-shape collider support per rigid body (compound body setup) as first-class authoring.
- [ ] Stable shape mutation/rebuild flows (editor + runtime) without brittle actor recreation.
- [ ] Convex decomposition output as reusable authored collider assets.

**Definition of done:** collider authoring supports simple and compound setups entirely through components/assets, backend-neutrally.

---

### G) Character controller parity

**Problem (both backends):** Controller integration is tightly coupled to PhysX; Jolt prototype exists but is not feature-parity safe. Movement component exposes PhysX-only properties and logic branches.

- [ ] Introduce reusable `CharacterControllerComponent` base contract (capsule first, box optional).
- [ ] Split movement behavior from controller instantiation to allow alternative controller-driving components.
- [ ] Expose controller collision callbacks/events in a backend-neutral way.
- [ ] Remove PhysX-only public API assumptions from gameplay-facing movement component.
- [ ] Ensure update/grounding/jump/gravity paths rely on abstract `ActiveController`.
- [ ] Validate slope/step/contact behavior against movement module expectations for both backends.
- [ ] Harden resize/crouch/up-direction transitions.
- [ ] Benchmark and tune controller update under fixed timestep load.

**Definition of done:** character movement component behaves consistently and reliably on both PhysX and Jolt; controller creation is componentized and reusable.

---

### H) Transform sync and shape mutation (Jolt hardening)

**Problem:** Shape changes use remove/re-add simplification; transform sync is less mature than PhysX.

- [ ] Robust body/shape rebuild paths preserving component ownership, activation state, and stable identifiers.
- [ ] Active bodies/controllers synchronize transforms after simulation with same guarantees as `RigidBodyTransform`.
- [ ] Validate reset/teleport/reload flows for Jolt bodies and controllers.

**Definition of done:** runtime/editor shape edits and reset operations are predictable in Jolt scenes.

---

### I) Serialization / networking / test hardening

**Problem:** Low-level capability exists, but full production integration needs deterministic and network-safe behavior.

#### PhysX progress
- Joint component verification exists (defaults, mutation, lifecycle safety, VR grab workflow tests).
- Remaining gap: broader end-to-end serialization/networking/reload hardening.

#### Both backends
- [ ] Verify all physics component fields serialize/deserialize cleanly (joint references, limits) across full scene save/load.
- [ ] Define replication ownership rules for rigid bodies, controllers, and joints.
- [ ] XRComponent integration tests for joint lifecycle and serialization (deeper runtime/create-simulate-destroy coverage).
- [ ] Character controller behavior tests at component level.
- [ ] Cross-scene smoke tests verifying no native leaks after activate/deactivate cycles.
- [ ] Integration tests for: rigid body spawn/reset, joint rebinding on activation order changes, controller movement/contact state, scene save/load + playmode transitions.

**Definition of done:** scene reload and network handoff preserve physics setup without manual repair, on both backends.

---

### J) Editor / tooling / test coverage

#### PhysX
- Joint inspectors, gizmos, break callbacks are implemented.
- Debug visualization has basic support.

#### Jolt
- [x] Focused regression tests for layer mapping + joint lifecycle.
- [ ] Physics debug visualization parity (contacts, constraints, shapes).
- [ ] Inspector UX communicating supported/unsupported Jolt features.
- [ ] Broader Jolt scene smoke/regression coverage (movement, serialization, reload stress).
- [ ] Query parity tests (ray/sweep/overlap) versus known fixtures.
- [ ] Character movement regression tests (jumping, grounding, slopes, step offset).

**Definition of done:** Jolt backend can be used as a supported runtime option, not only an experimental sandbox.

---

## Implementation Plan

### Completed Phases

#### Phase 1 — Componentize constraints (PhysX) ✅
1. Added XRComponents for core joints (Fixed, Hinge/Revolute, Prismatic, Distance, D6).
2. Added serialized references for ConnectedBody / Anchor frames / auto-anchor options.
3. Added lifecycle ownership + robust rebind when bodies are recreated.

#### Phase 2 — Backend-neutral contracts ✅
1. Introduced `IAbstractJoint` and typed joint interfaces in common physics namespace.
2. Moved component APIs to abstract types; PhysX extensions kept internal.
3. Added adapter layer mapping abstract contracts to PhysX wrappers.

#### Phase 3 — Editor + diagnostics (PhysX) ✅
1. Added component editors for joint limits/drives.
2. Added in-editor gizmos for anchors/axes/limits.
3. Routed PhysX constraint-break callbacks to component events/logging UI.

#### Phase 4 — Verification (initial pass) ✅
1. Comprehensive joint component tests (defaults, round-trip, lifecycle, VR grab workflow).
2. Runtime integration tests (create → simulate → destroy).

#### Jolt Phase 1 — Rigid-body scene viability ✅
1. [x] Completed `AsJoltShape()` for all `IPhysicsGeometry` types (safe fallback paths).
2. [x] Baseline layer/object/broadphase mapping with layer-filter tests.
3. [x] Baseline rigid-body property mapping (gravity, damping, mass, lock flags, CCD, sleep).

#### Jolt Phase 2 — Joint architecture ✅
1. [x] Implemented Jolt joint adapters for all core constraint types.
2. [x] Wired `JoltScene` joint factory for create/remove.
3. [x] XRComponent joint components instantiate through abstract factory on both backends.
4. [ ] Playmode lifecycle + scene serialization for Jolt joints.

---

### Current / Upcoming Phases

#### Phase 5 — API de-PhysXing and cross-backend parity
1. Define backend-neutral API boundaries and audit remaining PhysX concrete type leaks.
2. Refactor public component APIs to abstractions; isolate PhysX-specific tuning behind adapter extensions.
3. Move physics material and collider authoring contracts to backend-agnostic namespaces.
4. Implement first-class compound collider authoring and robust runtime shape rebuild flows.

#### Phase 6 — Controller parity and gameplay decoupling
1. Introduce reusable `CharacterControllerComponent` contracts; decouple movement from controller ownership.
2. Add backend-agnostic controller collision/contact event contracts for gameplay scripting.
3. Remove PhysX-only public exposure from character movement API.
4. Add parity tests for movement behavior across PhysX and Jolt.

#### Phase 7 — Production hardening
1. Define replication ownership/authority rules for rigid bodies, controllers, and joints.
2. Expand integration tests for scene reload, activation-order rebind, native leak detection.
3. Add controller integration behavior tests.
4. Add Jolt debug visualization and telemetry hooks comparable to PhysX workflows.
5. Add stress tests for rapid scene reload and component rebind.

---

## Prioritized Execution Plan (P0 / P1 / P2)

### P0 — unblock backend-neutral gameplay API surface (do first)
- Define neutral physics API boundaries.
- Audit PhysX type leaks.
- Refactor public components to abstractions.
- Add PhysX extension adapter layer.
- Document API guarantees and PhysX-only flags.
- Complete Jolt rigid-body property mapping matrix and unsupported-field policy.
- Validate collision-matrix parity between PhysX and Jolt.

**Exit criteria:** gameplay-facing physics components compile and run without direct public dependency on PhysX concrete types; Jolt produces equivalent filtering/query behavior for shared features.

### P1 — component parity for controllers and collider/material authoring
- Create `CharacterControllerComponent` base component.
- Split movement from controller ownership.
- Add controller contact event contracts.
- Design backend-agnostic physics materials.
- Implement compound collider authoring.
- Harden runtime shape rebuild flows (both backends).

**Exit criteria:** controllers and collider/material workflows are authorable via reusable backend-neutral XRComponents with stable runtime rebuild behavior.

### P2 — hardening for production / runtime safety
- Define physics replication ownership rules.
- Add scene reload leak tests.
- Add activation-order rebind tests.
- Add controller integration behavior tests.
- Add Jolt debug visualization parity.
- Broader Jolt regression suite for movement, serialization, reload stress.

**Exit criteria:** reload/network handoff paths are deterministic and validated by automated integration coverage on both backends.

---

## Definition of Done

Physics is "fully integrated with XRComponents" when:
- rigid bodies, joints/constraints, and controllers are all authorable through reusable XRComponents,
- gameplay APIs no longer require public PhysX concrete types,
- both PhysX and Jolt backends produce equivalent behavior for shared features,
- character controller behavior is backend-neutral and parity-tested,
- activate/deactivate/serialize workflows are validated by automated tests,
- and all joint/constraint features used by gameplay are exposed in editor and runtime component events.
