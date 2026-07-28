# Box3D Physics Backend Integration TODO

Status: proposed - upstream alpha; blocked on dependency approval

Last Updated: 2026-07-27

Owner: Physics Runtime / Native Integration

Related docs:

- [Physics architecture](../../../architecture/physics/overview.md)
- [Physics user guide](../../../user-guide/physics.md)
- [Jolt character-controller correctness](jolt-character-controller-correctness-todo.md)
- [Dependency policy](../../../DEPENDENCIES.md)

## Goal

Integrate Erin Catto's Box3D as an optional `AbstractPhysicsScene` backend
alongside PhysX, Jolt, and the existing experimental Jitter2 backend.

The integration must use XREngine's backend-neutral rigid-body, collider,
query, joint, character, replication, and debug-frame contracts. Gameplay and
editor code must not branch on Box3D types.

PhysX remains the default during this work. Box3D must be selected explicitly,
and an unavailable or incomplete Box3D backend must fail visibly rather than
falling back silently to PhysX or Jolt.

## Upstream Maturity And Capability Baseline

Box3D was announced on 2026-06-30 and the initial documented release is
`v0.1.0`. Its author explicitly describes it as alpha software while its
documentation and testing approach v1.0. The implementation must pin an exact
tag/commit and re-run the capability audit before every upgrade.

Confirmed `v0.1.0` characteristics:

| Area | Upstream capability | XREngine consequence |
| --- | --- | --- |
| API/runtime | Portable C17 C API, opaque generational IDs, shared-library exports, MIT license, no core dependency beyond the C runtime (`libm` on Unix). | Prefer a thin source-generated P/Invoke layer over an unvetted third-party managed wrapper. |
| Simulation | Fixed-step rigid bodies, sub-stepping, CCD, sleep, SIMD, multithreading, determinism, movement/contact/sensor/joint events. | Strong fit for `AbstractPhysicsScene`, but thread and callback ownership must be explicit. |
| Bodies/shapes | Static, kinematic, dynamic; multiple shapes per body; sphere, capsule, convex hull, triangle mesh, height field. | Primitive and compound authoring can map, with static-only restrictions for concave geometry. |
| Queries | Ray casts, shape casts, overlaps, filters, closest/all callback forms. | Can cover XREngine ray/sweep/overlap APIs after result and face-index fidelity are audited. |
| Joints | Revolute, prismatic, distance, spherical, weld, wheel, motor, parallel, and filter joints. | Fixed/distance/hinge/prismatic/spherical map naturally; D6 needs an explicit unsupported or composition decision. |
| Character movement | Experimental geometric capsule mover outside rigid-body simulation. | Implement only after rigid-body/query parity; expose its limits rather than claiming Jolt/PhysX controller parity. |
| Concave geometry | Triangle meshes and height fields create contacts only on static bodies. Baked compounds are immutable and static-only. | Reject dynamic concave/baked-compound authoring; do not silently convexify or substitute. |
| Precision | Single precision by default; optional double-precision build changes the ABI. | First integration uses single precision to match `System.Numerics`; large-world/double support is a separate ABI and engine-coordinate project. |
| Units | Tuned for meters/kilograms/seconds and typical moving sizes around 0.1-10 m. | Preserve engine MKS assumptions and add scale validation/diagnostics. |

Primary upstream references:

- [Box3D announcement](https://box2d.org/posts/2026/06/announcing-box3d/)
- [Box3D repository](https://github.com/erincatto/box3d)
- [Box3D v0.1 documentation](https://box2d.org/documentation3d/)
- [Simulation and shapes](https://box2d.org/documentation3d/md_simulation.html)
- [Collision and concave geometry](https://box2d.org/documentation3d/md_collision.html)
- [Character mover](https://box2d.org/documentation3d/md_character.html)
- [Multithreading](https://box2d.org/documentation3d/md_foundation.html)
- [Compound shapes](https://box2d.org/documentation3d/md_compound.html)

## Decisions Locked For The First Implementation

- [ ] Pin `v0.1.0` or the exact owner-approved successor available when Phase 0
  begins. Do not track `main`.
- [ ] Prefer an approved `Build/Submodules/box3d` source dependency and a
  repository-owned CMake build over a community C# wrapper.
- [ ] Build a `win-x64` shared library with samples, docs, benchmarks, and
  upstream unit tests disabled for the shipping artifact; run upstream tests in
  the dependency validation lane separately.
- [ ] Use single precision and verify `b3IsDoublePrecision() == false` before
  creating a world.
- [ ] Start correctness work with `workerCount = 1`. Add Box3D's internal
  scheduler after single-thread parity; evaluate an engine-job-system adapter
  only after profiling shows a reason.
- [ ] Use upstream default-definition functions such as
  `b3DefaultWorldDef`, `b3DefaultBodyDef`, and `b3DefaultShapeDef`. Do not
  assume zero-initialized interop structs are valid.
- [ ] Use body movement events after each step to update engine transforms
  instead of scanning every body.
- [ ] Keep native IDs and Box3D types inside
  `XREngine.Scene.Physics.Box3D`.
- [ ] Reject unsupported geometry, material semantics, joint types, query
  details, and character capabilities explicitly.
- [ ] Keep Box3D recording/replay as an opt-in diagnostics phase, not gameplay
  save-state serialization.

## Current Engine Seam

The main integration points already exist:

- `EPhysicsLibrary` selects PhysX, Jolt, or Jitter2.
- `Engine.RuntimeRenderingHostServices.CreatePhysicsScene()` constructs the
  selected `AbstractPhysicsScene`.
- `IPhysicsBackendService` creates neutral static bodies, dynamic bodies, and
  character controllers.
- `PhysicsRigidBodyCreateInfo` carries collider shapes, material, pose,
  density, layers, limits, flags, and solver settings.
- `IPhysicsGeometry` plus `PhysicsConvexHullGeometry`,
  `PhysicsTriangleMeshGeometry`, and `PhysicsHeightFieldGeometry` provide
  backend-neutral geometry.
- `AbstractPhysicsScene` defines fixed stepping, queries, actor lifecycle,
  joint factories, and debug-frame publication.
- `IAbstractStaticRigidBody`, `IAbstractDynamicRigidBody`, and
  `IAbstractCharacterController` isolate gameplay from native types.

The Box3D implementation should follow the Jolt ownership shape while using
Box3D's ID/event model rather than copying JoltPhysicsSharp patterns blindly.

## Target Type And File Map

Keep every type in its own file.

```text
XREngine.Runtime.Core/Scene/Physics/Box3D/
  Box3DNative.cs
  Box3DNativeLibrary.cs
  Box3DInteropTypes.cs
  Box3DScene.cs
  Box3DBackendService.cs
  Box3DActor.cs
  Box3DRigidActor.cs
  Box3DStaticRigidBody.cs
  Box3DDynamicRigidBody.cs
  Box3DShapeFactory.cs
  Box3DShapeOwner.cs
  Box3DMaterialAdapter.cs
  Box3DQueryContext.cs
  Box3DDebugFrameAdapter.cs
  Box3DCharacterController.cs
  Joints/
    Box3DJoint.cs
    Box3DFixedJoint.cs
    Box3DDistanceJoint.cs
    Box3DHingeJoint.cs
    Box3DPrismaticJoint.cs
    Box3DSphericalJoint.cs
```

If the interop surface becomes large, split it by upstream module
(`World`, `Body`, `Shape`, `Query`, `Joint`, `Debug`) rather than creating one
monolithic native file.

## Geometry Mapping Contract

| XREngine geometry | Box3D representation | Required policy |
| --- | --- | --- |
| `IPhysicsGeometry.Sphere` | `b3Sphere` shape | Validate finite positive radius and local center. |
| `IPhysicsGeometry.Box` | `b3MakeBoxHull` / hull shape | Validate half extents; apply local pose once. |
| `IPhysicsGeometry.Capsule` | `b3Capsule` shape | Convert XREngine half-height semantics to the two sphere centers and radius. |
| `IPhysicsGeometry.Plane` | No infinite collision-plane shape in the v0.1 public shape set. | Unsupported. Add an explicitly bounded plane/slab authoring type later if needed; never invent a huge box silently. |
| `PhysicsConvexHullGeometry` | Cooked `b3HullData` / hull shape | Transform scale/rotation, validate degeneracy and Box3D hull limits, own cooked memory through shape destruction. |
| `PhysicsTriangleMeshGeometry` | Cooked `b3MeshData` / mesh shape | Static bodies only; preserve scale, winding, materials, source identity, and mesh lifetime. |
| `PhysicsHeightFieldGeometry` | `b3HeightFieldData` / height-field shape | Static bodies only; preserve row/column orientation, scale, holes, bounds, and lifetime. |
| Multiple authored shapes | Multiple native shapes attached to one body | Supported for primitives/convex shapes on all valid body types. Apply each local pose to its geometry. |
| Baked static compound | `b3CompoundData` / compound shape | Optional later optimization for large immutable static sets; version and cache it separately. |

Dynamic or kinematic triangle meshes, height fields, and baked compounds must
produce a named `UnsupportedGeometryForBodyType` result. They must not be
silently dropped, converted, or attached as invalid shapes.

## Joint Mapping Contract

| XREngine joint | Box3D mapping | Initial disposition |
| --- | --- | --- |
| Fixed | Weld joint | Required |
| Distance | Distance joint | Required |
| Hinge | Revolute joint | Required |
| Prismatic | Prismatic joint | Required |
| Spherical | Spherical joint | Required |
| D6 | No direct v0.1 equivalent | Explicitly unsupported until a tested constraint composition is designed |

Motor, wheel, parallel, and filter joints are Box3D extension opportunities;
they are not substitutes for the neutral D6 contract.

## Capability Policy

Before Box3D is user-selectable outside developer settings, add or extend a
backend capability report covering at least:

- primitive, convex, mesh, height-field, multi-shape, and baked-compound
  support by body type;
- static versus dynamic friction semantics and material combine modes;
- CCD, kinematic targets, gravity scaling, sleep, locks, mass/inertia override,
  per-body velocity limits, and solver overrides;
- ray/sweep/overlap result detail, filters, face index, UV, and initial overlap;
- each neutral joint type, limit/motor/spring/break behavior;
- character input, arbitrary up, moving ground, dynamic interaction, query
  visibility, slope/step/floor behavior, and character-to-character collision;
- collision/contact/sensor/joint events;
- debug-draw categories;
- worker scheduling, determinism, and recording/replay; and
- single versus double precision.

An inspector must be able to show an authored field as unsupported for Box3D.
Ignoring a field during native creation is not acceptable.

## Success Criteria

- [ ] `EPhysicsLibrary.Box3D` creates a real `Box3DScene`.
- [ ] Missing/wrong-architecture/wrong-version native libraries fail with a
  targeted Box3D diagnostic and never select another solver silently.
- [ ] Static, dynamic, and kinematic bodies synchronize poses and velocities
  through neutral interfaces.
- [ ] Sphere, box, capsule, convex hull, static triangle mesh, static height
  field, and multiple-shape bodies pass focused parity tests.
- [ ] Layer masks and static/dynamic query filters match PhysX/Jolt inclusion
  behavior.
- [ ] Ray, sweep, and overlap APIs return correct ordering, position, normal,
  distance, owning component, and explicitly supported detail fields.
- [ ] Fixed, distance, hinge, prismatic, and spherical joints pass lifecycle
  and basic behavior tests; D6 reports unsupported.
- [ ] Contact/movement events and debug geometry are copied into
  backend-neutral bounded storage before Box3D's transient data expires.
- [ ] Fixed stepping performs no per-body full scan and no steady-state managed
  allocation.
- [ ] The geometric character controller satisfies its declared capability
  subset or remains visibly unavailable; it never claims Jolt/PhysX parity by
  type name alone.
- [ ] Single-thread correctness is established before multithreading, and the
  accepted worker configuration has deterministic/stress evidence.
- [ ] Existing PhysX, Jolt, and Jitter2 paths continue to build and pass their
  targeted tests.
- [ ] Dependency/license reports, architecture docs, user docs, generated
  settings schema, and packaging are updated.

## Phase 0 - Dependency Approval And Capability Spike

- [ ] Request approval to add Box3D as a dependency/submodule.
- [ ] Verify the pinned release's MIT license and third-party contents under the
  repository commercial-use policy.
- [ ] Record the exact upstream tag, commit SHA, source archive/submodule URL,
  and expected license hash.
- [ ] Build the pinned source on Windows x64 with:
  - [ ] `BUILD_SHARED_LIBS=ON`;
  - [ ] `BOX3D_SAMPLES=OFF`;
  - [ ] `BOX3D_BENCHMARKS=OFF`;
  - [ ] `BOX3D_DOCS=OFF`;
  - [ ] `BOX3D_UNIT_TESTS=OFF` for the shipping artifact;
  - [ ] `BOX3D_DOUBLE_PRECISION=OFF`; and
  - [ ] explicit Release/Debug validation settings.
- [ ] Run the upstream unit tests in a separate validation build before
  disabling them in the runtime artifact.
- [ ] Confirm exported C symbols, calling convention, CRT choice, native
  filename, and transitive DLL dependencies.
- [ ] Build a disposable interop spike that creates a world, ground box,
  falling dynamic box, steps 120 frames, reads movement events, and destroys
  everything.
- [ ] Exercise one callback query and one debug-draw callback from managed code.
- [ ] Audit Box3D against every member of `PhysicsRigidBodyCreateInfo`,
  `IAbstractDynamicRigidBody`, `AbstractPhysicsScene`, the neutral joint
  interfaces, and `IAbstractCharacterController`.
- [ ] Fill a durable supported/approximated/unsupported mapping table before
  production implementation.
- [ ] Decide whether a tiny native ABI probe/shim is required for struct layout,
  returned-by-value structures, inline-only helpers, or callback portability.

### Exit Criteria

- [ ] Dependency approval and license review are recorded.
- [ ] The pinned DLL passes upstream tests and the managed hello-world spike.
- [ ] Capability gaps are named before backend selection is exposed.

## Phase 1 - Native Build, Packaging, And Interop

- [ ] Add the approved pinned dependency under `Build/Submodules/box3d` or the
  owner-approved equivalent.
- [ ] Add `Tools/Dependencies/Build-Box3D.ps1` with deterministic source,
  configuration, architecture, and output validation.
- [ ] Copy the native DLL into
  `XREngine.Runtime.Core/runtimes/win-x64/native/` using the existing runtime
  dependency convention.
- [ ] Make ordinary restore/build use the pinned artifact or explicit setup
  command; do not download mutable `main` during MSBuild.
- [ ] Bind only public `include/box3d` C APIs.
- [ ] Use source-generated `LibraryImport` where supported, explicit C calling
  convention, explicit one-byte native `bool` marshalling, and blittable
  structs.
- [ ] Represent `b3WorldId`, `b3BodyId`, `b3ShapeId`, and `b3JointId` as
  distinct managed value types; never interchange raw integer fields.
- [ ] Add native/managed `sizeof`, alignment, field-offset, enum-value, bool,
  quaternion, matrix/transform, and returned-struct ABI tests.
- [ ] Bind upstream default-definition functions and validate their internal
  sentinel values.
- [ ] Root query, task, assertion, logging, and debug callbacks for their native
  lifetimes.
- [ ] Route native assertions/logging through bounded engine diagnostics; never
  throw across an unmanaged callback.
- [ ] Verify `b3GetVersion()` and `b3IsDoublePrecision()` before world creation.
- [ ] Add clear errors for absent DLL, wrong architecture, missing export,
  version mismatch, and precision mismatch.
- [ ] Run `pwsh Tools/Generate-Dependencies.ps1`; review
  `docs/DEPENDENCIES.md` and `docs/licenses/`.

### Exit Criteria

- [ ] The interop assembly loads only the approved ABI and version.
- [ ] Native lifecycle and callback tests pass repeatedly under Debug and
  Release x64.

## Phase 2 - Scene Lifecycle And Fixed Step

- [ ] Implement `Box3DScene : AbstractPhysicsScene`.
- [ ] Create `b3WorldDef` through `b3DefaultWorldDef`.
- [ ] Map gravity, sleep, CCD, contact tuning, hit threshold, capacity, and
  worker settings from explicit engine defaults.
- [ ] Create the world with one worker for the correctness phases.
- [ ] Implement `Initialize`, `Destroy`, `Gravity`, `StepSimulation`, and
  `OnEnterPlayMode`.
- [ ] Call `b3World_Step` with the engine fixed delta and an explicit Box3D
  substep setting.
- [ ] Prevent reads or writes while `b3World_Step` is active.
- [ ] Reuse the engine physics mutation queue so bodies, shapes, and joints are
  not changed concurrently with simulation.
- [ ] Consume transient movement/contact/sensor/joint events before the next
  native step.
- [ ] Update only moved body owners and then call `NotifySimulationStepped()`.
- [ ] Make initialization failure leave no partially registered world.
- [ ] Make `Destroy` idempotent and release controllers, joints, bodies, cooked
  geometry, callbacks, and the world in ownership order.
- [ ] Add repeated create/step/destroy and failed-initialize tests.

### Exit Criteria

- [ ] An empty and populated Box3D world step deterministically without leaks.
- [ ] Transform publication uses movement events, not an all-body scan.

## Phase 3 - Actor And Rigid-Body Wrappers

- [ ] Implement `Box3DBackendService`.
- [ ] Implement actor, rigid actor, static body, and dynamic body wrappers with
  opaque IDs and scene ownership.
- [ ] Store a stable managed registry key in native body/shape user data
  without exposing movable managed pointers.
- [ ] Validate every native ID before use in diagnostics/debug builds.
- [ ] Map static, kinematic, and dynamic body types.
- [ ] Implement transform, linear/angular velocity, sleeping state, gravity
  enablement, kinematic target, wake, teleport, and destruction semantics.
- [ ] Preserve `OwningComponent` and transform synchronization through neutral
  interfaces.
- [ ] Map motion locks exactly.
- [ ] Audit CCD/bullet behavior, damping, max velocities, density/mass/inertia,
  solver iterations, and body flags.
- [ ] Add explicit capability results for settings Box3D cannot represent.
- [ ] Ensure `Destroy`, `RemoveActor`, and world teardown cannot double-destroy
  an ID or deliver a stale event to a recycled wrapper.
- [ ] Implement `TryReplaceCollisionShapes` transactionally: build and validate
  replacements before removing old shapes, preserve pose/velocities, and roll
  back on failure.

### Exit Criteria

- [ ] Neutral rigid-body component tests pass with Box3D.
- [ ] Unsupported authored properties are visible and tested.

## Phase 4 - Geometry, Materials, And Collision Filtering

- [ ] Implement the geometry mapping table above in `Box3DShapeFactory`.
- [ ] Validate all dimensions, finite values, normalized rotations, scale
  ranges, winding, indices, and degeneracy before native calls.
- [ ] Apply local collider poses once and verify them against PhysX/Jolt test
  fixtures.
- [ ] Keep cooked hull, mesh, height-field, and compound memory alive for every
  referencing shape.
- [ ] Destroy native cooked data only after the last referencing shape.
- [ ] Preserve negative mesh scale and winding only if the pinned API supports
  it correctly; otherwise reject it with a named reason.
- [ ] Preserve source triangle/material identity needed by query and contact
  results.
- [ ] Map `LayerMask` to Box3D category/mask bits and define the behavior when
  engine layer width exceeds native filter width.
- [ ] Map dynamic friction to Box3D's single Coulomb friction value and report
  the loss of separate static-friction semantics unless a tested policy is
  approved.
- [ ] Audit friction/restitution combine modes against Box3D's global mixing
  callbacks. Do not silently apply a different per-material rule.
- [ ] Map restitution and damping to their actual native owners; do not put
  body damping into a surface material by name alone.
- [ ] Reject infinite planes and dynamic concave shapes explicitly.
- [ ] Add optional baked static-compound work only after ordinary multiple-shape
  bodies are correct.
- [ ] If baked compounds are persisted, key the cache by Box3D version and
  `B3_COMPOUND_VERSION`; never deserialize incompatible bytes.

### Exit Criteria

- [ ] Primitive, convex, mesh, height-field, multi-shape, filtering, and
  lifetime tests pass for their declared body types.
- [ ] Invalid/unsupported geometry cannot create a partial body.

## Phase 5 - Ray, Sweep, And Overlap Queries

- [ ] Implement reusable unmanaged callback contexts with no per-hit managed
  allocation.
- [ ] Map `PhysicsQueryActorTypes`, `LayerMask`, and any custom filter to
  `b3QueryFilter` plus callback filtering.
- [ ] Implement `RaycastAny`, async/single, and multiple semantics.
- [ ] Implement sphere, box/hull, and capsule sweeps through
  `b3World_CastShape`.
- [ ] Implement overlaps through `b3World_OverlapShape`.
- [ ] Define initial-overlap behavior and sweep inflation consistently with
  the neutral contract.
- [ ] Resolve hit shape/body user data to the correct `XRComponent`.
- [ ] Convert hit fractions into world distance and preserve world position and
  normal.
- [ ] Audit whether the public Box3D query result can recover exact source face
  index and UV without abusing material identity.
- [ ] If face index or UV is unavailable, return an explicit unsupported detail
  state/sentinel and capability flag; never fabricate zero as a valid face.
- [ ] Sort results according to the existing XREngine contract outside the
  native callback using bounded reusable storage.
- [ ] Support concurrent read-only queries outside `b3World_Step` only after
  registry/filter contexts are proven thread-safe.
- [ ] Add parity tests for static-only, dynamic-only, all, masks, nearest/all,
  misses, initial overlap, rotated/scaled shapes, mesh triangles, and
  concurrent reads.

### Exit Criteria

- [ ] Query behavior matches the declared neutral contract and capability
  report.
- [ ] Callbacks remain bounded and cannot outlive their context.

## Phase 6 - Joints

- [ ] Add one focused wrapper file per neutral joint type.
- [ ] Map fixed to weld, hinge to revolute, distance to distance, prismatic to
  prismatic, and spherical to spherical.
- [ ] Convert local anchors, axes, frames, angular units, limits, springs,
  damping, motors, force/torque limits, and collision enablement explicitly.
- [ ] Use a world-anchor native body only if Box3D requires two valid bodies;
  otherwise use the native world-attachment convention.
- [ ] Register neutral joint ownership for break/lifecycle callbacks.
- [ ] Audit whether Box3D joint events expose break thresholds compatible with
  `NotifyConstraintBroken`.
- [ ] If break semantics differ, expose the limitation rather than polling
  every joint per frame.
- [ ] Make D6 creation throw/return a typed unsupported result until a
  separately designed composition passes constraint and performance tests.
- [ ] Keep Box3D-only motor/wheel/parallel/filter joints as optional extension
  work.
- [ ] Add creation, behavior, body destruction, explicit removal, world
  teardown, limits, motors/springs, and stale-ID tests.

### Exit Criteria

- [ ] Required neutral joints work and release safely.
- [ ] D6 and break capabilities are represented honestly.

## Phase 7 - Events, Debug Frames, And Diagnostics

- [ ] Enable only the contact/sensor/hit/pre-solve events requested by engine
  components; avoid unconditional native event cost.
- [ ] Copy transient native events into bounded backend-neutral buffers before
  the next step.
- [ ] Resolve begin/end/hit events through generation-safe shape/body owners.
- [ ] Define delivery order relative to transform publication and
  `OnSimulationStep`.
- [ ] Adapt `b3World_Draw` callbacks to `PhysicsDebugFrameWriter`.
- [ ] Support bounded points, lines, triangles/hulls, AABBs, joints, contacts,
  normals, forces, centers of mass, and names where the neutral debug contract
  permits them.
- [ ] Respect `IncludeDebugRenderViewBounds`, debug budgets, truncation
  telemetry, and depth modes.
- [ ] Never render directly from a native callback or retain transient native
  pointers.
- [ ] Expose `b3Profile`, `b3Counters`, byte count, awake bodies, worker count,
  substeps, event counts, debug truncation, and unsupported-feature counters.
- [ ] Rate-limit native warnings and preserve the first actionable context.
- [ ] Add debug-off tests proving no callback or geometry collection cost is
  paid when disabled.

### Exit Criteria

- [ ] Box3D debug output uses the same renderer-independent frame pipeline as
  PhysX/Jolt.
- [ ] Event and diagnostic buffers are bounded and generation-safe.

## Phase 8 - Experimental Character Mover

- [ ] Keep `CharacterControllerCapabilities` at `None` until a complete
  fixed-step mover wrapper exists.
- [ ] Implement the upstream capsule workflow:
  - [ ] cast desired translation with `b3World_CastMover`;
  - [ ] gather planes with `b3World_CollideMover`;
  - [ ] solve penetration with `b3SolvePlanes`; and
  - [ ] clip blocked velocity with `b3ClipVector`.
- [ ] Reuse bounded plane storage; do not allocate per move.
- [ ] Map total capsule height/radius unambiguously.
- [ ] Preserve velocity/displacement input timing from the neutral controller
  contract.
- [ ] Support arbitrary up only if the capsule/origin/plane logic passes
  non-Y-up tests.
- [ ] Implement grounded/support state from contact planes with documented
  normal/slope tolerances.
- [ ] Use `b3Body_CollideMover` and body movement data for moving platforms.
- [ ] Decide and test dynamic-body pushing; the geometric mover is not itself a
  simulated body.
- [ ] Keep character-to-character collision, rapid rotation, query visibility,
  materials, max strength, floor stick, stair stepping, and soft push limits
  separately capability-gated.
- [ ] Reuse the timing, moving-ground, arbitrary-up, slope, step, teleport,
  resize, and interaction cases from the Jolt correctness tracker.
- [ ] Do not promote Box3D for normal player movement until the required v1
  shared controller capabilities pass.

### Exit Criteria

- [ ] The controller advertises only demonstrated capabilities.
- [ ] Zero-input, moving-ground, arbitrary-up, and timing tests establish
  behavior, or controller creation remains visibly unsupported.

## Phase 9 - Selection, Settings, Editor, And Documentation

- [ ] Append `Box3D` to `EPhysicsLibrary` without changing existing serialized
  enum values.
- [ ] Add `Box3DScene` to
  `Engine.RuntimeRenderingHostServices.CreatePhysicsScene()`.
- [ ] Update unit-testing world settings, descriptions, JSONC schema, and
  bootstrap parsing.
- [ ] Run `Tools/Generate-UnitTestingWorldSettings.ps1` after the settings type
  change and review the tracked schema.
- [ ] Add Box3D to the ImGui backend selector with alpha/experimental labeling.
- [ ] Show native version/commit, precision, worker count, substeps,
  capabilities, unsupported authored fields, and runtime counters.
- [ ] Prevent selection when the native runtime is unavailable, but preserve
  the requested setting and explain why creation failed.
- [ ] Update physics architecture and user guides with support level,
  limitations, setup, and troubleshooting.
- [ ] Update launch/task documentation only if a new setup/build command is
  required.
- [ ] Keep PhysX as the documented primary backend until a separate production
  decision changes that posture.

### Exit Criteria

- [ ] Box3D can be selected, inspected, and diagnosed without source knowledge.
- [ ] Generated settings and docs match runtime behavior.

## Phase 10 - Multithreading, Determinism, Recording, And Performance

- [ ] Capture a single-worker correctness and performance baseline first.
- [ ] Add a Box3D worker-count setting with `1` as the initial safe default.
- [ ] Evaluate Box3D's internal scheduler using physical performance cores,
  excluding efficiency cores/hyperthreads unless measurements justify them.
- [ ] Avoid oversubscription with engine rendering, job, audio, and physics
  threads.
- [ ] Verify no world read/write occurs during `b3World_Step`.
- [ ] Evaluate external engine scheduler callbacks only if the internal
  scheduler creates measurable coordination problems.
- [ ] If external scheduling is adopted, preallocate task wrappers, ensure the
  stepping thread participates, and make `finishTask` help progress rather than
  parking blindly.
- [ ] Compare deterministic state hashes across repeated runs and accepted
  worker counts on the same platform/build.
- [ ] Use upstream recording/replay for bug captures only after memory limits,
  versioning, file placement, and teardown are bounded.
- [ ] Store `.b3rec` captures under the current
  `Build/_AgentValidation/<run>/` evidence root, not as required project data.
- [ ] Benchmark matched Box3D/Jolt/PhysX scenes:
  - [ ] falling stacks and large piles;
  - [ ] sleeping/waking bodies;
  - [ ] fast CCD bodies;
  - [ ] many static meshes/height fields;
  - [ ] ray/sweep/overlap batches;
  - [ ] joint chains/ragdolls; and
  - [ ] character movers if promoted.
- [ ] Record fixed-step p50/p95/p99/max, bodies/shapes/contacts/islands,
  allocations, native bytes, worker utilization, and transform/event
  publication cost.
- [ ] Verify steady-state fixed stepping has zero managed allocations.

### Exit Criteria

- [ ] The accepted worker policy improves a named workload without correctness
  or frame-tail regressions.
- [ ] Performance claims are backed by matched solver profiles, not sample-app
  impressions.

## Phase 11 - Validation And Closeout

- [ ] Add interop ABI and native lifecycle tests.
- [ ] Add backend selection and missing-runtime failure tests.
- [ ] Add scene initialize/step/destroy/reload tests.
- [ ] Add rigid-body property, transform, sleep/wake, kinematic, CCD, locks,
  mass, shape replacement, and stale-ID tests.
- [ ] Add every geometry/filter/material test from Phases 3-4.
- [ ] Add query result/filter/detail/concurrency tests from Phase 5.
- [ ] Add required joint tests from Phase 6.
- [ ] Add event/debug-budget tests from Phase 7.
- [ ] Add character tests only after the mover is functionally complete.
- [ ] Run focused `XREngine.UnitTests/Physics` tests for Box3D and the existing
  backend-neutral boundary suites.
- [ ] Run targeted PhysX and Jolt regression suites after shared contract
  changes.
- [ ] Build `XREngine.Runtime.Core`, `XREngine.Editor`, and the narrowest
  runtime/bootstrap projects affected by native packaging.
- [ ] Launch an isolated unit-testing editor session with Box3D, capture
  multiple physics-debug views, inspect the images, and review the named
  session logs.
- [ ] Run the accepted single/multithread performance matrix on named hardware.
- [ ] Regenerate dependency/license and settings documentation.
- [ ] Record accepted unsupported features and give each required follow-up an
  owner/tracker.
- [ ] Mark this TODO complete only when selecting Box3D produces an honest,
  usable backend rather than a partial demo scene.

### Final Acceptance Criteria

- [ ] Box3D is integrated through neutral runtime contracts with visible
  capabilities and failure behavior.
- [ ] Required rigid-body, geometry, query, joint, event, debug, settings,
  packaging, and validation work is complete.
- [ ] Character support is either validated for its advertised subset or
  explicitly unavailable.
- [ ] Existing physics backends remain intact.

## Validation Matrix

| Area | Minimum cases |
| --- | --- |
| Lifecycle | empty/populated world, failed init, repeated reload, teardown with live bodies/joints |
| Bodies | static, kinematic, dynamic, sleep/wake, gravity off, locks, CCD, teleport, velocity |
| Geometry | sphere, box, capsule, convex, multi-shape, static mesh, static height field, invalid plane/dynamic concave |
| Filtering | each layer, masks, static-only, dynamic-only, all, runtime filter change |
| Queries | any/single/multiple ray, sphere/box/capsule sweep, overlap, initial overlap, mesh hit |
| Joints | fixed, distance, hinge, prismatic, spherical, world attachment, limits, motor/spring, removal |
| Character | timing, slopes, steps, floor, moving platform, arbitrary up, dynamic interaction, teleport/resize |
| Events/debug | begin/end/hit, movement, sensors, joint event, debug on/off, budget overflow |
| Scale | small scene, 1k/10k bodies as appropriate, large static set, batched queries |
| Failure | DLL missing, wrong architecture/version/precision, invalid ID, unsupported field/type, callback fault |

## Risk Register

| Risk | Required mitigation |
| --- | --- |
| Box3D is new alpha software and its API may change quickly. | Pin an exact release/commit, isolate interop, version-check at load, and upgrade only through a separate audited change. |
| C struct/callback ABI is bound incorrectly. | Add native ABI probes, explicit bool/calling-convention/layout tests, and returned-struct coverage. |
| Native generational IDs are reused after managed wrappers survive. | Track scene generation plus native ID; invalidate wrappers before native destruction and reject stale events. |
| Concave or plane authoring is silently changed. | Reject infinite planes and dynamic concave shapes with typed diagnostics; require explicit bounded/convex authoring. |
| Face index/UV cannot be recovered from public query results. | Audit before claiming support; expose unsupported detail instead of fabricated values. |
| Material semantics differ from PhysX/Jolt. | Capability-audit single friction, combine callbacks, damping ownership, and per-triangle material identity. |
| Geometric character mover is mistaken for a simulated controller. | Gate each capability, test moving/dynamic interaction, and keep it experimental until shared requirements pass. |
| Multithreading races with engine mutations/queries. | Establish single-thread correctness, forbid access during step, reuse mutation queues, then add measured scheduling. |
| Internal scheduler oversubscribes hybrid CPUs. | Default to one worker, select physical performance cores conservatively, and profile tail latency. |
| Cooked hull/mesh/height-field/compound memory is freed early. | Use explicit reference-counted native geometry owners and destruction-order tests. |
| Box3D selection falls through to PhysX when unavailable. | Add an explicit selector arm and typed initialization failure; never use the default switch arm for a known backend. |
| Native DLL is missing from editor/game publish output. | Add runtime asset tests and inspect both build and publish layouts. |

## Non-Goals

- Replacing PhysX as the default backend in this work.
- Matching PhysX GPU dynamics, CUDA, or vehicle features.
- Tracking Box3D `main` or upgrading it opportunistically.
- Enabling Box3D double precision before XREngine has a compatible
  double-precision world-coordinate contract.
- Supporting infinite planes through an undocumented giant-box approximation.
- Supporting dynamic triangle meshes, height fields, or baked compounds.
- Claiming D6 or character parity through untested constraint/controller
  approximations.
- Exposing native Box3D IDs or structs to gameplay, components, serialization,
  or editor tools.
- Treating Box3D recording as a saved-game format.
- Hiding missing native support or unsupported fields behind PhysX/Jolt
  fallback behavior.

