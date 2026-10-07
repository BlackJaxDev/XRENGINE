# Box3D Backend Integration TODO

Last Updated: 2026-10-06
Status: Blocked: upstream Box3D is alpha and the dependency is not approved.
Architecture: [Physics Architecture](../../../architecture/physics/overview.md)  Design: [Box3D Backend Integration Design](../../design/physics/box3d-backend-integration-design.md)
Validation: [Physics Validation](../../testing/physics/physics-validation.md#box3d-backend)

## Current State

No Box3D code exists. `EPhysicsLibrary` has no `Box3D` value. Backends register through `IPhysicsBackendModule` and `PhysicsBackendCatalog` (`BuiltInPhysicsBackendModules.RegisterDesktop`), so Box3D goes in a new leaf project `XREngine.Runtime.Physics.Box3D` with its own module. The design doc holds the upstream capability baseline, the geometry and joint mapping contracts, the capability policy, and the risk register.

## Open Code Items

### Native build and interop

- [ ] Add the pinned source under `Build/Submodules/box3d` after approval. Done when: the submodule points at the approved tag and commit.
- [ ] Add `Tools/Dependencies/Build-Box3D.ps1` with a fixed source, configuration, architecture, and output check (`BUILD_SHARED_LIBS=ON`, samples, benchmarks, docs, and unit tests off, `BOX3D_DOUBLE_PRECISION=OFF`). Done when: the script builds a `win-x64` DLL into `XREngine.Runtime.Physics.Box3D/runtimes/win-x64/native/` and fails on a wrong architecture.
- [ ] Create `XREngine.Runtime.Physics.Box3D` with the file map from the design doc. Done when: the project builds and is in `XRENGINE.slnx`.
- [ ] Bind the public `include/box3d` C API with `LibraryImport`, the C calling convention, one-byte `bool` marshalling, and blittable structs in `Box3DNative` and `Box3DInteropTypes`. Done when: the bindings compile with no runtime marshalling.
- [ ] Represent `b3WorldId`, `b3BodyId`, `b3ShapeId`, and `b3JointId` as distinct managed value types. Done when: no API takes a raw integer ID.
- [ ] Bind the upstream default-definition functions and check their sentinel values. Done when: no code creates a zero-initialized definition struct.
- [ ] Root query, task, assertion, logging, and debug callbacks for their native lifetime. Route assertions and logs to bounded engine diagnostics. Done when: no callback can throw across the native boundary.
- [ ] Check `b3GetVersion()` and `b3IsDoublePrecision()` before world creation in `Box3DNativeLibrary`. Done when: absent DLL, wrong architecture, missing export, version mismatch, and precision mismatch each return a named error.
- [ ] Add ABI unit tests for `sizeof`, alignment, field offsets, enum values, `bool`, quaternion, transform, and returned structs. Done when: the tests exist in `XREngine.UnitTests/Physics/Box3D/`.
- [ ] Run `pwsh Tools/Reports/Generate-Dependencies.ps1` after the dependency lands. Done when: `docs/DEPENDENCIES.md` and `docs/licenses/` list Box3D.

### Scene lifecycle and fixed step

- [ ] Implement `Box3DScene : AbstractPhysicsScene` with `Initialize`, `Destroy`, `Gravity`, `StepSimulation`, and `OnEnterPlayMode`. Map gravity, sleep, CCD, contact tuning, hit threshold, capacity, and worker count from explicit engine defaults. Done when: the scene creates a world with one worker through `b3DefaultWorldDef`.
- [ ] Call `b3World_Step` with the engine fixed delta and an explicit substep setting. Block reads and writes during the step through the engine physics mutation queue. Done when: no wrapper API touches the world while a step runs.
- [ ] Consume movement, contact, sensor, and joint events before the next step. Update only moved body owners, then call `NotifySimulationStepped()`. Done when: the step has no all-body scan.
- [ ] Make a failed `Initialize` leave no registered world. Make `Destroy` idempotent and release controllers, joints, bodies, cooked geometry, callbacks, and the world in that order. Done when: unit tests for repeated create, step, and destroy and for failed initialize pass.

### Bodies

- [ ] Implement `Box3DBackendService`, `Box3DActor`, `Box3DRigidActor`, `Box3DStaticRigidBody`, and `Box3DDynamicRigidBody` with opaque IDs and scene ownership. Store a stable registry key in native user data. Done when: no movable managed pointer goes to native code.
- [ ] Map static, kinematic, and dynamic types, transform, velocities, sleep, gravity, kinematic target, wake, teleport, motion locks, CCD, damping, velocity limits, mass, inertia, and solver settings. Done when: each `PhysicsRigidBodyCreateInfo` member maps or returns an explicit capability result.
- [ ] Track scene generation plus native ID. Done when: `Destroy`, `RemoveActor`, and world teardown cannot destroy an ID twice or send an event to a recycled wrapper.
- [ ] Implement `TryReplaceCollisionShapes` as a transaction. Done when: a failed replacement keeps the old shapes, pose, and velocities.

### Geometry, materials, and filtering

- [ ] Implement the geometry mapping table in `Box3DShapeFactory`. Validate dimensions, finite values, rotations, scale, winding, indices, and degeneracy before native calls. Done when: invalid geometry cannot create a partial body.
- [ ] Reject infinite planes and dynamic or kinematic concave shapes with `UnsupportedGeometryForBodyType`. Done when: unit tests assert the typed result.
- [ ] Own cooked hull, mesh, height-field, and compound memory in `Box3DShapeOwner` with reference counts. Done when: native cooked data is freed only after the last shape that uses it.
- [ ] Keep negative mesh scale and winding only when the pinned API supports them. Keep source triangle and material identity for queries and contacts. Done when: unsupported scale returns a named reason.
- [ ] Map `LayerMask` to Box3D category and mask bits in `Box3DMaterialAdapter` or a filter helper. Done when: the code defines behavior for layer widths larger than the native filter width.
- [ ] Map friction, restitution, and damping to their real native owners. Done when: the capability report states the loss of separate static friction and any combine-mode difference.
- [ ] Add optional baked static compounds after multi-shape bodies work. Key any persisted cache by Box3D version and `B3_COMPOUND_VERSION`. Done when: an incompatible cache entry is rejected.

### Queries

- [ ] Implement `Box3DQueryContext` with reusable unmanaged callback contexts. Done when: hits cause no per-hit managed allocation.
- [ ] Map `PhysicsQueryActorTypes`, `LayerMask`, and custom filters to `b3QueryFilter` plus callback filtering. Implement `RaycastAny`, single, and multiple ray casts, sphere, box, and capsule sweeps through `b3World_CastShape`, and overlaps through `b3World_OverlapShape`. Done when: each `AbstractPhysicsScene` query method has a Box3D implementation.
- [ ] Define initial-overlap and sweep inflation behavior to match the neutral contract. Convert hit fractions to world distance. Resolve hits to the owning `XRComponent`. Done when: results carry position, normal, distance, and owner.
- [ ] Return an explicit unsupported state for face index and UV when the public API cannot give them. Done when: no query returns zero as a valid face without native evidence.
- [ ] Sort results outside the native callback in bounded reusable storage. Done when: result order matches the existing contract.
- [ ] Allow concurrent read-only queries outside `b3World_Step` only after the registry and filter contexts are thread-safe. Done when: a concurrency unit test passes.

### Joints

- [ ] Add `Box3DFixedJoint`, `Box3DDistanceJoint`, `Box3DHingeJoint`, `Box3DPrismaticJoint`, and `Box3DSphericalJoint`. Convert anchors, axes, frames, angle units, limits, springs, damping, motors, force and torque limits, and collision enablement. Done when: each neutral joint type creates the mapped native joint.
- [ ] Register joint ownership for break and lifecycle callbacks. If Box3D break semantics differ from `NotifyConstraintBroken`, report the limitation. Done when: no code polls every joint each frame.
- [ ] Return a typed unsupported result for D6 creation. Done when: a unit test asserts it.

### Events, debug frames, and diagnostics

- [ ] Enable only the contact, sensor, hit, and pre-solve events that components request. Copy them to bounded neutral buffers before the next step. Define the delivery order against transform publication and `OnSimulationStep`. Done when: event buffers are bounded and generation-safe.
- [ ] Implement `Box3DDebugFrameAdapter` from `b3World_Draw` to `PhysicsDebugFrameWriter`. Respect `IncludeDebugRenderViewBounds`, debug budgets, truncation telemetry, and depth modes. Done when: a debug-off unit test proves no callback or geometry cost.
- [ ] Expose `b3Profile`, `b3Counters`, byte count, awake bodies, worker count, substeps, event counts, debug truncation, and unsupported-feature counters. Rate-limit native warnings. Done when: the values appear in backend diagnostics.

### Character mover

- [ ] Implement `Box3DCharacterController` with `b3World_CastMover`, `b3World_CollideMover`, `b3SolvePlanes`, and `b3ClipVector`, with bounded plane storage. Keep `CharacterControllerCapabilities` at `None` until it is complete. Done when: a move causes no allocation.
- [ ] Map total capsule height and radius. Keep the neutral velocity and displacement timing contract. Derive support state from contact planes with documented slope tolerances. Use `b3Body_CollideMover` for moving platforms. Done when: the controller advertises only the capabilities its tests prove.
- [ ] Add controller unit tests that reuse the timing, moving-ground, arbitrary-up, slope, step, teleport, resize, and interaction cases from `JoltControllerParityTests`. Done when: the tests exist and assert support or unsupported results.

### Selection, settings, and editor

- [ ] Add `Box3D` to the end of `EPhysicsLibrary`. Add `Box3DBackendModule` and register it in the desktop catalog. Done when: existing serialized enum values do not change and a missing native runtime gives a typed failure.
- [ ] Update the unit-testing world settings, descriptions, and bootstrap parsing. Run `Tools/Generate-UnitTestingWorldSettings.ps1`. Done when: the generated schema lists Box3D.
- [ ] Add Box3D to the ImGui backend selector with an experimental label. Show native version and commit, precision, worker count, substeps, capabilities, unsupported authored fields, and counters. Done when: the editor keeps the requested setting and explains a failed creation.
- [ ] Add a Box3D worker-count setting with `1` as the default. Done when: the setting reaches `b3WorldDef`.

### Tests

- [ ] Add unit tests for backend selection and missing runtime, scene lifecycle and reload, body properties, sleep and wake, kinematic, CCD, locks, mass, shape replacement, and stale IDs. Done when: the tests exist in `XREngine.UnitTests/Physics/Box3D/`.
- [ ] Add unit tests for geometry, filters, materials, query results, query details, query concurrency, required joints, events, and debug budgets. Done when: the tests exist and cover the design doc mapping tables.

### Documentation

- [ ] Update `docs/architecture/physics/overview.md` and `docs/user-guide/physics.md` with the Box3D support level, limits, setup, and troubleshooting. Done when: the docs match the code.

## Decisions Needed

- [ ] Approve or reject Box3D as a dependency and submodule. Record the tag, commit, source URL, and license hash. Owner: maintainer.
- [ ] Decide whether a small native ABI probe or shim is necessary for struct layout, returned structs, inline helpers, or callbacks. Owner: physics runtime.
- [ ] Decide whether to evaluate an external engine scheduler for Box3D tasks. Do this only when profiles show a problem with the internal scheduler. Owner: physics runtime.
- [ ] Decide when to re-evaluate Box3D after it leaves alpha, including its Emscripten build. Owner: physics runtime.

## Out Of Scope

- See the non-goals in the design doc.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/physics/box3d-backend-integration-todo.md`

- [ ] `BOX3D_UNIT_TESTS=OFF` for the shipping artifact;
- [ ] An empty and populated Box3D world step deterministically without leaks.
- [ ] Audit CCD/bullet behavior, damping, max velocities, density/mass/inertia,
  solver iterations, and body flags.
- [ ] Audit whether Box3D joint events expose break thresholds compatible with
  `NotifyConstraintBroken`.
- [ ] cast desired translation with `b3World_CastMover`;
- [ ] gather planes with `b3World_CollideMover`;
- [ ] solve penetration with `b3SolvePlanes`; and
- [ ] Decide and test dynamic-body pushing; the geometric mover is not itself a
  simulated body.
- [ ] Box3D can be selected, inspected, and diagnosed without source knowledge.
