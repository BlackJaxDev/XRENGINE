# PhysX + XRComponents Integration Gap Analysis

## Scope
This document evaluates what still remains to achieve a **full PhysX integration surfaced through XRComponents** for:
- rigid bodies
- joints/constraints
- character controllers
- scene queries
- gameplay-facing component workflows

## Current State (implemented)

### 1) Rigid bodies are integrated through XRComponents
- `DynamicRigidBodyComponent` and `StaticRigidBodyComponent` can auto-create PhysX actors, register them with the current scene, and bind to `RigidBodyTransform`.
- Property synchronization for broad body settings (damping, mass, flags, collision metadata, etc.) is present for PhysX actors.

### 2) PhysX joints exist at engine-wrapper layer
- Engine wrappers are present for Fixed, Distance, D6, Revolute, Prismatic, Spherical, Gear, Rack-and-Pinion, and Contact joints.
- `PhysxScene` exposes native constraint counting / access and callback hookup.

### 3) PhysX character controller path exists
- `CharacterMovement3DComponent` has a PhysX capsule-controller adapter and creates controller instances via `ControllerManager`.
- Controller proxy path is wired into `RigidBodyTransform` for transform sync after simulation.

### 4) Scene query coverage is broad in PhysX
- Raycast, sweep, overlap APIs are implemented in `PhysxScene` and consumed by world/view logic.

### What is *not* fully integrated yet
The largest gaps are not low-level PhysX wrappers—they are **XRComponent-first workflows** and backend-neutral gameplay contracts.

---

## Remaining Gaps for "Full Integration"

## A) Missing XRComponent-level joint/constraint authoring model (highest impact)
**Problem:** Joints are currently created ad hoc in gameplay/editor code (e.g., VR grabbing, transform tool) rather than as reusable scene components. There is no unified XRComponent authoring layer for constraints comparable to rigid body components.

### What is missing
- A component family such as:
  - `PhysicsJointComponent` (base)
  - `DistanceJointComponent`, `FixedJointComponent`, `HingeJointComponent`, `PrismaticJointComponent`, `SphericalJointComponent`, `D6JointComponent`
- Support assignment by scene references (`ActorA`, `ActorB`, local frames / anchor frames) rather than immediate native pointers.
- Mirror key runtime properties in component fields and push changes live.
- Runtime creation/destruction ownership in component lifecycle (`OnComponentActivated` / deactivated):
  - create native joint on activation,
  - rebind when body references change,
  - release safely on deactivation/destroy,
  - survive scene unload/reload.
- Inspector/editor tooling for joint drives, limits, break thresholds, projection, and per-joint visualization.
- Constraint event routing (break notifications) up into component events.

### Why this blocks "full integration"
- Current gameplay usage (e.g. grab constraints) directly instantiates PhysX joints in gameplay component code.
- This creates backend lock-in, duplication, and no reusable XRComponent workflow.

### Definition of done
- Joint setup is authorable entirely in scene data; no gameplay code needs to manually call `PhysxScene.New*Joint(...)` for common use cases.

---

## B) PhysX-specific API leakage in gameplay components
**Problem:** Key gameplay components still expose PhysX concrete types (`PhysxCapsuleController`, `PhysxDynamicRigidBody`, `PhysxShape`, `Px...` enums/flags). Shared abstractions leak PhysX concepts (query filters, material namespace, etc.).

### What is missing
- Backend-neutral façade interfaces for components that should run against either engine.
- Conversion of public gameplay component API from PhysX concrete types to abstract contracts.
- Move generic material abstraction out of PhysX namespace.
- Create backend-agnostic query filter representation used by all scenes.
- Ensure component properties that are currently PhysX-only have either:
  - backend-neutral semantics, or
  - explicit "PhysX-only" metadata/UI grouping.

### Why this matters
- Prevents clean backend swapping and keeps higher-level systems PhysX-coupled.

### Definition of done
- XRComponent APIs clearly separate cross-backend guarantees from PhysX extensions.
- Shared gameplay systems can run without direct compile-time dependency on `PhysxJoint_*` types.

---

## C) No unified constraint abstraction shared with other backends
**Problem:** Even where small interfaces exist (`IHingeJoint`, `IPrismaticJoint`), they are empty and PhysX-namespaced.

### What is missing
- A scene-level abstract constraint model (`IAbstractJoint`, `IAbstractConstraint`, typed limits/drives) in backend-agnostic namespaces.
- Joint creation APIs on abstract physics scene or a separate factory/service.
- PhysX-specific extension points for advanced tuning isolated in optional adapter layers.

### Definition of done
- Gameplay systems can create and configure joints through backend-neutral interfaces.

---

## D) Material/geometry authoring still PhysX-first
**Problem:** Material class and many shape/cooking flows are PhysX-centric. Materials and shape composition are still mostly managed via code paths and single geometry slots.

### What is missing
- Engine-level material abstraction exposed by components.
- Shape-authoring data contracts independent from backend cooking artifacts.
- Support multi-shape colliders per rigid body as first-class authoring (compound body setup).
- Support stable shape mutation/rebuild flows (editor + runtime) without brittle actor recreation behavior.
- Ensure convex decomposition output can be turned into reusable authored collider assets.

### Definition of done
- Collider authoring supports simple and compound setups entirely through components/assets.

---

## E) Character controller parity at XRComponent/API level
**Problem:** Movement component works, but controller integration is tightly coupled and not yet a generally reusable controller component family.

### Remaining tasks
- Introduce a reusable `CharacterControllerComponent` base contract (capsule first, box optional).
- Split movement behavior from controller instantiation enough to allow alternative controller-driving components.
- Expose controller collision callbacks/events in a backend-neutral way for gameplay scripting.
- Ensure crouch/resize/up-direction changes are robust across spawn/teleport/world reset.

### Definition of done
- Controller creation/ownership is componentized and reusable outside current movement component.

---

## F) Serialization/networking/test hardening for physics components
**Problem:** Low-level capability exists, but full production integration needs deterministic and network-safe component behavior.

### What is missing
- Verification that all physics component fields serialize/deserialize cleanly (including joint references and limits).
- Replication ownership rules for rigid bodies, controllers, and future joints.
- XRComponent integration tests for joint lifecycle and serialization.
- Character controller behavior tests at component level (not only wrapper-level assumptions).
- Cross-scene smoke tests verifying no native leaks after activate/deactivate cycles.
- Integration tests for:
  - rigid body spawn/reset,
  - joint creation/rebinding on activation order changes,
  - controller movement and contact state,
  - scene save/load + playmode transitions.

### Definition of done
- Scene reload and network handoff preserve physics setup without manual repair.

---

## Recommended Implementation Plan

### Phase 1 — Componentize constraints
1. Add XRComponents for core joints (Fixed, Hinge/Revolute, Prismatic, Distance, D6).
2. Add serialized references for ConnectedBody / Anchor frames / auto-anchor options.
3. Add lifecycle ownership + robust rebind when bodies are recreated.

### Phase 2 — Backend-neutral contracts
1. Introduce `IAbstractJoint` and typed joint interfaces in common physics namespace.
2. Move component APIs to abstract types; keep PhysX extensions internal.
3. Add adapter layer mapping abstract contracts to PhysX wrappers.

### Phase 3 — Editor + diagnostics
1. Add component editors for joint limits/drives.
2. Add in-editor gizmos for anchors/axes/limits.
3. Route PhysX constraint-break callbacks to component events/logging UI.

### Phase 4 — Verification
1. Add serialization/load tests for each joint component.
2. Add runtime integration tests (create -> simulate -> destroy) for memory/resource hygiene.
3. Add VR grab workflow tests using componentized distance joint instead of direct PhysX calls.

This sequence gives immediate authoring value while improving long-term multi-backend maintainability.

---

## Definition of Done for PhysX Integration
PhysX is "fully integrated with XRComponents" when:
- rigid bodies, joints/constraints, and controllers are all authorable through reusable XRComponents,
- gameplay APIs no longer require public PhysX concrete types,
- activate/deactivate/serialize workflows are validated by automated tests,
- and all joint/constraint features used by gameplay are exposed in editor and runtime component events.
