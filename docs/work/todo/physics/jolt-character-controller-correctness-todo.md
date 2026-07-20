# Jolt Character Controller Correctness TODO

Last Updated: 2026-07-13
Owner: Physics / Character Movement
Status: In Progress - Phase 0 decisions and Phase 1-4 code complete
Target Branch: `physics-jolt-character-controller-correctness`

Implementation note (2026-07-13): the owner explicitly requested implementation
in the current worktree without creating the target branch and without baseline
trace validation. Those two Phase 0 tasks remain intentionally unchecked. A
redirected post-change build and focused regression suite were used only to
catch implementation errors.

## Phase 0 Decisions And Field Inventory

The implementation targets the repository-pinned JoltPhysicsSharp `2.20.1`
and MagicPhysX `1.0.0` APIs.

| Create field/concept | Classification | Mapping |
| --- | --- | --- |
| Position, up, radius, total height, slope, contact offset, step offset | Shared | Validated neutral values, converted to each native capsule representation |
| Motion input model | Shared | Tagged velocity/displacement stream resampled by duration at the backend boundary |
| Collision layer mask | Shared | Jolt object-layer mask and PhysX simulation/query shape filter data |
| Density | Shared authored concept | Native PhysX density; Jolt capsule volume times density produces character mass |
| Predictive contact distance, collision tolerance, floor stick, extra step down, max strength | Jolt capabilities | Independent Jolt settings; capability flags report their absence in PhysX |
| Material, invisible wall height, maximum jump height, scale coefficient, volume growth, constrained climbing | PhysX capabilities | Passed only to PhysX and exposed as unsupported capability differences in Jolt |
| Slide on steep slopes | Shared | PhysX non-walkable mode; Jolt steep-slope velocity cancellation toggle |

For v1, velocity/displacement input, arbitrary up, moving-ground inheritance,
dynamic-body interaction, filtering, and steep-slope policy are shared
requirements. Character-versus-character collision, query visibility, native
materials, invisible walls, and constrained climbing remain explicit backend
capabilities; they are not silently promised by Jolt. Optional inner-body and
full interaction parity remain Phase 5 work.

Serialized controller height is now unambiguously total capsule height. Since
v1 has not shipped, existing ambiguous `Height` values are reinterpreted under
the clean `TotalHeight` schema without a permanent compatibility heuristic.

## Goal

Make character movement a backend-neutral fixed-step simulation contract, then
implement that contract correctly for both Jolt `CharacterVirtual` and the
PhysX collide-and-slide controller.

The completed work must remove Update-rate-dependent velocity conversion,
advance Jolt characters on every physics step, give capsule dimensions and
ground state unambiguous meanings, support moving ground and arbitrary up
directions, and explicitly expose any backend capability that cannot be made
equivalent.

Exact trajectories do not need to be bit-identical between PhysX and Jolt.
They must satisfy the same authored movement contract within documented
tolerances.

## Why This Follow-up Exists

The completed physics parity ledgers claim broad character-controller parity,
but the current implementation and tests do not establish it. The existing
Jolt wrapper has a valid collide-and-slide core and uses
`CharacterVirtual.ExtendedUpdate`, but several surrounding contracts can
produce incorrect physics or backend-dependent behavior:

- Buffered Update-tick displacements are summed and divided by one fixed
  delta, while the recorded elapsed times are not used. Mismatched Update and
  physics rates can therefore change commanded velocity or create spikes.
- `ExtendedUpdate` is skipped when the accumulated movement is below
  `MinMoveDistance`, so zero-input characters do not refresh contacts, follow
  moving ground, stick to floors, or process stair/floor logic.
- `CharacterMovementComponent` can process input and produce movement commands
  either on the Update thread with other input/components or on the fixed
  PrePhysics thread with the controller. The cross-thread Update mode needs an
  explicit time-preserving handoff to fixed-step controller consumption.
- Moving-platform ground velocity is not part of a clearly defined
  world-relative versus ground-relative velocity contract.
- `CollisionTolerance`, `PredictiveContactDistance`, and
  `CharacterPadding` are all derived from `ContactOffset` despite representing
  different Jolt tolerances.
- Step height is also used as extra downward stair search distance, coupling
  two independent behaviors.
- Capsule `Height` is described and authored like total standing height, but
  both native capsule constructors interpret it as the cylinder segment
  between the hemispheres. The current default therefore creates a much taller
  collision shape than its stated height.
- Grounded state is inferred from contact normals and mixed into collision
  flags instead of being based on Jolt `GroundState` / support semantics.
- Jumping, air input, friction, speed clamping, and some ground logic assume
  world Y is up even though the controller exposes `UpDirection`.
- Several creation settings are ignored by the Jolt backend without a clear
  capability decision, including mass/density, material, constrained climbing,
  invisible walls, jump height, scale coefficient, and volume growth.
- Character-versus-character collision, dynamic-body pushing, query
  visibility, object-layer filtering, and the optional inner rigid body are
  not defined as shared features.
- Existing tests cover basic grounding, jumping, steps, slopes, resizing,
  contacts, and bounded buffering, but not the timing, moving-ground,
  interaction, dimension, and arbitrary-up failure modes above.

## Confirmed Good Foundations To Preserve

- Jolt `CharacterVirtual` is itself a collide-and-slide controller. Its
  `ExtendedUpdate` helper adds stair walking and floor sticking; the movement
  component does not need a second collide-and-slide pass.
- Calling character updates before `PhysicsSystem.Update` matches Jolt's
  upstream sample ordering.
- Converting an authored slope cosine to `acos` for Jolt's maximum slope angle
  is correct after clamping the cosine to `[-1, 1]`.
- The current supporting-volume plane is consistent with Jolt's capsule sample
  when evaluated in character-local space; retain it unless arbitrary-up tests
  disprove the shape rotation path.
- Reporting effective velocity from resolved displacement divided by fixed
  delta is useful. Keep it as a post-collision result, separate from the
  requested velocity.
- Gravity is currently integrated by character movement rather than
  automatically by `CharacterVirtual`. Preserve a single owner for gravity and
  document exactly how the gravity argument passed to `ExtendedUpdate` is
  used, avoiding double integration.

## Target Contracts

These are the intended default decisions. Change one only after recording the
replacement and updating its tests before implementation.

### Motion command input model

- Add a backend-neutral `CharacterMotionInputModel` toggle with at least
  `Velocity` and `Displacement` values. Default new controllers to `Velocity`,
  while keeping `Displacement` a fully supported model rather than a legacy
  compatibility path.
- In `Velocity` mode, gameplay supplies world-units-per-second for the current
  physics step. In `Displacement` mode, gameplay supplies the world-space
  distance requested for exactly one physics step.
- A displacement command is not an untagged Update-tick displacement. If an
  API accepts displacement sampled outside the physics tick, it must also
  carry elapsed duration and be resampled proportionally without losing an
  unconsumed remainder.
- Preserve `TickInputWithPhysics` as a processing-thread toggle:
  - `false` processes input and produces movement commands on the Update thread
    with the other inputs and component updates;
  - `true` processes input and produces movement commands on the fixed
    PrePhysics thread, the same thread/cadence as controller consumption.
- `Engine.Delta` in Update mode is the Update-loop duration, not a rendering or
  present delta. Input/movement code must not read a renderer-frame delta.
- Character acceleration, friction, gravity, jump consumption, and movement
  command generation run once per selected producer tick with that tick's
  explicit duration. Native collide-and-slide advancement still runs exactly
  once per physics step.
- In Update-thread mode, physics consumes a duration-tagged movement stream
  plus lossless edge events such as jump. A faster Update loop must not change
  distance, acceleration, gravity, or event count over equal elapsed time.
- PhysX passes displacement commands through and adapts velocity commands with
  `displacement = requestedVelocity * fixedDelta`.
- Jolt passes velocity commands through and adapts displacement commands with
  `requestedVelocity = requestedDisplacement / fixedDelta` before
  `ExtendedUpdate`.
- Perform conversion once at the native backend boundary. Do not convert to the
  other representation and back through shared gameplay layers.
- Apply runtime input-model changes on a physics-step boundary. Commands queued
  under the previous model retain their tagged meaning or are explicitly
  drained; they must never be reinterpreted under the new model.
- Raw requested command, normalized requested velocity, effective resolved
  velocity, and moving-ground velocity are distinct values with documented
  units, spaces, and lifetimes.

### Velocity relative to moving ground

- Define authored locomotion velocity as ground-relative while supported and
  world-relative while airborne.
- Add Jolt ground velocity exactly once. Preserve the intended inherited
  linear and angular platform velocity when stepping off or jumping.
- Do not hide ground velocity inside `LinearVelocity` without naming whether
  that property is requested, relative, or effective velocity.

### Capsule dimensions

- Keep the editor-facing standing/crouched height as total capsule height.
- Convert to native cylinder-segment height with
  `max(0, totalHeight - 2 * radius)` for both PhysX and Jolt.
- Name native-facing values `CylinderHeight` or `HalfCylinderHeight`; do not
  continue using an ambiguous `Height` across layers.
- Define how serialized legacy values are interpreted. Because v1 has not
  shipped, prefer a clean schema correction over a permanent compatibility
  heuristic.

### Contact and grounding state

- `IsGrounded` / support state comes from the backend's support model, not from
  a generic collision flag.
- `CollidingDown`, `CollidingUp`, and `CollidingSides` report contact location
  only. A steep wall-like contact is not grounded merely because its normal has
  a positive up component.
- Expose the ground normal, ground point velocity, and ground body identity
  required by locomotion without leaking native Jolt or PhysX types.

## Phase 0 - Branch, Baseline, And Decisions

- [ ] Create and switch to the dedicated branch
  `physics-jolt-character-controller-correctness`, preserving unrelated local
  changes without folding them into this work.
- [x] Create a bounded validation root under
  `Build/_AgentValidation/<timestamp>-jolt-character-controller/` for traces,
  reports, and temporary repro data.
- [ ] Capture current Jolt and PhysX traces for level walking, acceleration,
  stopping, falling, jumping, a walkable slope, a steep slope, and a step.
  Record requested velocity, effective velocity, position, support state,
  contact flags, ground normal, and ground velocity per physics step.
- [ ] Run the same traces at 30, 60, 120, and 144 Hz Update rates with a
  60 Hz physics step and record drift or velocity spikes.
- [x] Lock the motion-command, moving-ground, capsule-dimension, and ground
  state contracts above in a short design note or in API documentation beside
  the neutral contracts.
- [x] Inventory every field in `PhysicsCharacterControllerCreateInfo` and
  classify it as shared, PhysX-only, Jolt-only, replaced by a cleaner shared
  concept, or removed.
- [x] Decide which interaction features are required for v1: character versus
  character, pushing dynamic bodies, receiving pushes, query visibility, and
  collision-layer participation. Record unsupported choices as explicit
  capabilities rather than silent omissions.
- [x] Confirm whether existing serialized `Height` values may be reinterpreted
  as total height or require a one-time asset migration; document the decision.

Acceptance criteria:

- [ ] Baseline traces reproduce the Update/fixed-rate issue and zero-input
  update gap, or explain with evidence why either suspected issue is not
  observable.
- [x] All contract decisions are recorded in this work item and beside the neutral APIs.
- [x] No PhysX-specific setting remains presented as a backend-neutral
  guarantee without a mapped behavior or capability result.

## Phase 1 - Fixed-Step Movement Contract

- [x] Add `CharacterMotionInputModel` to the backend-neutral controller and
  character-movement authoring contract, with `Velocity` and `Displacement`
  modes and `Velocity` as the default for new controllers.
- [x] Represent each submitted motion command with its input model and value so
  queued work cannot be reinterpreted after the toggle changes. Include the
  producer-tick duration on every Update-thread command so both velocity and
  displacement streams can be resampled over the fixed interval.
- [x] Expose the toggle through the component/editor and serialization path,
  and allow changing it at runtime on a physics-step boundary without
  recreating the native controller unless a backend proves recreation is
  required.
- [x] Preserve `TickInputWithPhysics` as the input/movement-command processing
  thread selector. Document `false` as Update-thread production and `true` as
  fixed PrePhysics production on the controller thread.
- [x] Make the producer/consumer boundary explicit. Update-thread production
  uses `Engine.Delta` and a thread-safe duration-tagged handoff; fixed-thread
  production uses `Engine.FixedDelta` and may take a same-thread fast path with
  identical observable semantics.
- [x] Do not relabel Update-thread processing as rendering. Verify no movement
  input path reads timing from rendering, presentation, or interpolation.
- [x] Process acceleration, braking/friction, gravity, air control, jump
  application, and state transitions exactly once per selected producer tick,
  preserving elapsed time when their output crosses into fixed consumption.
- [x] Queue discrete input edges such as jump without allocation and consume
  them exactly once. Define deterministic overflow behavior.
- [x] Make continuous movement input a stable snapshot or timestamped sample
  whose resampling does not depend on the number of Update ticks.
- [x] Define velocity resampling across the fixed interval: duration-weight
  Update-thread velocity segments, consume only their overlap with the current
  physics step, and retain any unconsumed segment duration deterministically.
- [x] Adapt PhysX at its native boundary: pass `Displacement` through unchanged
  and convert `Velocity` using the current fixed delta immediately before its
  collide-and-slide move call.
- [x] Adapt Jolt at its native boundary: pass `Velocity` through unchanged and
  convert a one-step `Displacement` using the same fixed delta immediately
  before `ExtendedUpdate`.
- [x] For displacement samples spanning a different duration than one physics
  step, consume displacement proportionally by duration and retain the exact
  unconsumed displacement/time remainder. Never sum Update-tick displacement
  and divide the whole sum by one fixed delta.
- [x] Define zero/invalid fixed-delta handling in both adapters without
  division, native movement, or silently dropping a valid queued remainder.
- [x] Publish normalized requested velocity for diagnostics in both modes while
  retaining the raw tagged command for debugging and deterministic replay.
- [x] Keep controller hot paths allocation-free and safe for the existing
  update/physics thread relationship. Avoid captured delegates, LINQ, boxing,
  and per-step temporary collections.

Acceptance criteria:

- [ ] Equal input over equal wall-clock time produces equivalent trajectories
  at all Update rates in the validation matrix for both input models and both
  `TickInputWithPhysics` modes.
- [x] PhysX and Jolt accept both input models and perform exactly one conversion
  at their native boundary when the selected model differs from the native API.
- [x] Switching models at runtime neither reinterprets queued commands nor
  creates a one-step distance or velocity spike.
- [x] One physics step performs one and only one native controller advancement;
  movement-command production occurs only on the thread selected by
  `TickInputWithPhysics`.
- [x] No buffered-input coalescing can produce a velocity proportional to the
  number of Update ticks accumulated.
- [x] Raw command/model, normalized requested velocity, and effective resolved
  velocity can be inspected independently.

## Phase 2 - Correct Jolt Update Lifecycle

- [x] Call `CharacterVirtual.ExtendedUpdate` once on every registered fixed
  step, including zero commanded movement and movement below
  `MinMoveDistance`.
- [x] Treat `MinMoveDistance` as an output/noise or command threshold only; do
  not let it suppress contact refresh, floor sticking, stair logic, or moving
  ground.
- [x] Call `UpdateGroundVelocity` at the correct point each fixed tick before
  composing supported movement, following the current Jolt API/sample
  contract.
- [x] Define the order for: refreshing support, reading ground velocity,
  consuming jump, composing requested velocity, `ExtendedUpdate`, updating
  the physics system, and publishing resolved state.
- [x] Verify whether teleport, resize, up-direction change, and shape change
  require `RefreshContacts` and invoke it only at those discontinuities.
- [x] Pass gravity to `ExtendedUpdate` according to its documented role while
  retaining a single integration owner in character movement.
- [x] Preserve effective velocity as resolved position delta divided by the
  exact physics delta; guard non-positive delta explicitly.
- [x] Keep contact callbacks and temporary contact storage bounded and free of
  per-step allocations.

Acceptance criteria:

- [x] An idle supported character follows translating and rotating ground.
- [x] An idle character continues to report correct support/contact state and
  does not miss a floor transition because no move command was issued.
- [x] Floor sticking remains active and stair/floor state is refreshed during
  idle steps instead of being skipped with the movement command.
- [x] Gravity is neither skipped nor applied twice.

## Phase 3 - Jolt Settings, Filtering, And Shape Correctness

- [x] Separate the following authored Jolt settings and give each a documented
  default/range:
  - `CharacterPadding` (skin/separation margin),
  - `PredictiveContactDistance`,
  - `CollisionTolerance`,
  - floor-stick distance,
  - upward step distance,
  - extra downward stair search distance.
- [x] Stop assigning `ContactOffset` to all three Jolt contact tolerances. Start
  from Jolt's documented defaults and tune with scale-specific tests.
- [x] Stop using `StepOffset` as extra downward stair distance unless a named
  authoring setting explicitly requests it.
- [x] Clamp and validate radius, total height, cylinder height, slope cosine,
  up direction, tolerances, and step distances before native creation.
- [x] Apply the total-height-to-cylinder-height conversion consistently in
  Jolt, PhysX, resize/crouch code, foot-position preservation, debug drawing,
  serialization, and editor labels/tooltips.
- [x] Verify the supporting volume and native shape rotation for non-Y up
  directions instead of changing the currently valid local-space plane
  speculatively.
- [x] Route controller creation and update through explicit object-layer,
  broad-phase-layer, body, and shape filters consistent with engine collision
  masks.
- [x] Map material, mass/density, strength, and other meaningful properties to
  their closest Jolt concepts only when semantics match. Rename the shared
  concept when they do not.
- [x] Report unsupported creation settings through backend capability data and
  editor diagnostics. Remove dead settings from the neutral contract when no
  backend-neutral behavior is intended.

Acceptance criteria:

- [x] A configured 1.8 m total-height, 0.3 m radius capsule measures 1.8 m from
  bottom to top in both backends.
- [x] Contact padding, collision tolerance, and prediction distance can be
  varied independently and have focused tests.
- [x] Step-up height and extra step-down search can be varied independently.
- [x] Controller collision filtering matches the shared collision matrix and
  query-filter policy.
- [x] Invalid geometry/settings fail with actionable diagnostics instead of
  reaching native code with undefined values.

## Phase 4 - Ground State, Slopes, Platforms, And Arbitrary Up

- [x] Add a backend-neutral support-state model capable of representing at
  least unsupported/in-air, supported, sliding/too-steep, and invalid/stale
  states without exposing native enums.
- [x] Populate `IsGrounded` from Jolt `GroundState` and support semantics.
  Retain contact flags as independent collision-location observations.
- [x] Derive `CollidingUp`, `CollidingDown`, and `CollidingSides` relative to
  `UpDirection`, with documented normal thresholds and stable aggregation over
  all active contacts.
- [x] Expose the selected supporting contact's normal, point velocity, and
  body identity through neutral types.
- [x] Use the actual ground normal for locomotion projection and slope response
  rather than a global-up fallback.
- [x] Replace all world-Y and XZ-plane character math with vector projection
  along and perpendicular to the normalized `UpDirection`, including jump,
  gravity, air control, landing friction, horizontal speed clamping, and slope
  calculations.
- [x] Define steep-slope behavior explicitly: sliding, blocking uphill
  velocity, constrained climbing, and optional invisible-wall behavior. Map
  comparable PhysX/Jolt options or expose a capability difference.
- [x] Compose moving-ground velocity once and preserve the intended platform
  momentum when the character walks off or jumps.
- [x] Test accelerating platforms and rotating platforms at multiple radii so
  point velocity, not only body linear velocity, is validated.

Acceptance criteria:

- [x] Grounding is stable on valid slopes and false on walls/ceilings and
  too-steep surfaces according to the authored policy.
- [x] Collision flags remain useful even when the character is not supported.
- [x] Translating and rotating platform motion is inherited without double
  application or one-frame lag.
- [x] Equivalent scenes work with Y-up, Z-up, and a non-axis-aligned up vector.
- [ ] Jump height and airtime remain within documented tolerance across up
  directions and backends.

## Phase 5 - Character And Rigid-Body Interaction Capabilities

- [ ] If character-versus-character collision is required, add and own a
  `CharacterVsCharacterCollision` registry, register/unregister controllers
  deterministically, and cover teardown/reload.
- [ ] If rigid bodies and scene queries must detect the character, configure
  and lifecycle-own Jolt's optional inner rigid body with the intended layer,
  shape, mass, and user data.
- [ ] Define whether characters push dynamic bodies, receive impulses from
  them, or both. Map mass and maximum strength consistently with that policy.
- [ ] Ensure inner-body transforms cannot drift from the virtual character
  during movement, teleport, resize, up changes, deactivation, or scene reset.
- [ ] Define whether controller query hits come from the virtual-character
  registry, the inner body, or both, and prevent duplicate hits.
- [ ] Expose unsupported combinations visibly in the editor and backend
  capability API.
- [ ] Add lifecycle diagnostics proving all registries, inner bodies,
  listeners, and native controller objects return to zero after repeated
  initialize/create/step/destroy cycles.

Acceptance criteria:

- [ ] Required interaction modes behave consistently or return an explicit,
  tested unsupported result.
- [ ] Two characters cannot silently pass through each other when the shared
  authoring contract says they collide.
- [ ] Dynamic-body pushing conserves the intended direction and remains
  bounded by the configured strength/mass policy.
- [ ] Scene queries see exactly the configured character representation.
- [ ] No interaction object or callback survives controller teardown.

## Phase 6 - Regression And Parity Test Matrix

- [ ] Expand `JoltControllerParityTests` or split focused fixtures by purpose
  so failures identify timing, geometry, support, platform, interaction, or
  lifecycle regressions.
- [ ] Add Update/fixed cadence tests for 30/60/120/144 Hz Update ticks versus
  60 Hz physics, including uneven and jittered Update deltas. Run the matrix in
  both `Velocity` and `Displacement` modes, both `TickInputWithPhysics` modes,
  and both backends; rendering cadence is outside this test variable.
- [ ] Add cross-model equivalence tests showing that velocity `v` and
  displacement `v * fixedDelta` produce the same requested native motion and
  trajectory within tolerance for a single fixed step and a long sequence.
- [ ] Add duration-aware displacement resampling tests for samples shorter and
  longer than the physics step, including exact remainder consumption and no
  final distance loss.
- [ ] Add runtime toggle tests with queued commands in flight. Assert commands
  retain their tagged meaning, no command is applied twice, and no transition
  spike occurs in either direction.
- [ ] Add zero-input fixed-step tests for support refresh, floor sticking,
  moving ground, and contact transitions.
- [ ] Add translating, rotating, and accelerating platform tests, including
  walking off and jumping from each.
- [ ] Add exact capsule-dimension and foot-preservation tests for creation,
  crouch, resize, radius change, up change, and serialization round-trip.
- [ ] Add independent padding, predictive-distance, collision-tolerance,
  step-up, and step-down tests.
- [ ] Add walkable/too-steep/wall/ceiling contact fixtures that assert support
  state separately from collision flags.
- [ ] Add Y-up, Z-up, and non-axis-aligned-up locomotion, gravity, jump, slope,
  friction, and air-control tests.
- [ ] Add character-versus-character, dynamic pushing, query visibility,
  filtering, and inner-body tests for every enabled capability.
- [ ] Add requested-versus-effective velocity assertions for free movement,
  wall sliding, corner collision, slopes, steps, and moving ground.
- [ ] Add backend scenario traces that compare PhysX and Jolt against shared
  behavioral tolerances rather than native implementation details or bitwise
  equality.
- [ ] Retain the existing 10,000-command bounded-allocation coverage and add a
  fixed-step allocation assertion for idle and moving Jolt updates.
- [ ] Add teardown/reload tests covering controller registries, contact
  listeners, optional inner bodies, and backend diagnostic counts.

Required scenario outputs:

- selected input model and raw requested command,
- requested locomotion velocity,
- gravity/jump velocity,
- ground point velocity,
- effective resolved velocity,
- position and foot position,
- support state and ground normal,
- collision flags and contact count,
- supporting body identity,
- allocation and native-object counts where applicable.

Acceptance criteria:

- [ ] All shared scenarios have documented numeric tolerances and pass on both
  backends where the capability is supported.
- [ ] Capability-specific tests assert explicit support/unsupported results.
- [ ] Timing tests fail against the pre-fix displacement buffering behavior.
- [ ] Equivalent tagged velocity/displacement commands converge on the same
  backend request without hiding conversion errors in broad position
  tolerances.
- [ ] Tests are deterministic and do not require a visible editor or GPU.

## Phase 7 - Documentation, Validation, And Closeout

- [ ] Update API/editor documentation for the motion input-model toggle and its
  units, total capsule height, requested and effective velocity, support state,
  moving-ground behavior, up direction, and backend capabilities.
- [ ] Update serialization schema/tooltips if controller setting names or
  meanings change.
- [ ] Update the completed physics parity ledger with a link to this corrective
  work and replace any broader claim that is no longer accurate.
- [ ] Run the focused controller tests for Jolt and PhysX.
- [ ] Run the full physics test namespace after the focused suite is green.
- [ ] Build `XREngine.csproj`, `XREngine.UnitTests.csproj`, and the editor with
  zero new warnings. If a running editor locks build output, use a redirected
  output/intermediate path under the task validation root rather than killing
  an unrelated user process.
- [ ] Capture final scenario traces using the same cadence and movement matrix
  as the baseline and include a concise before/after report.
- [ ] Verify fixed-step controller updates introduce no hot-path heap
  allocations and no unbounded queue or registry growth.
- [ ] Exercise repeated scene initialize/create/step/destroy and confirm all
  Jolt controller, listener, character-collision, and optional inner-body
  counts return to zero.
- [ ] Review the diff for accidental changes to unrelated physics/rendering
  work and for direct backing-field assignments on `XRBase` types.
- [ ] Merge `physics-jolt-character-controller-correctness` back into `main`
  only after all acceptance criteria pass and validation evidence is recorded.

Final acceptance criteria:

- [ ] Character distance, acceleration, braking, fall speed, and jump behavior
  do not change with Update cadence in either input model or either
  `TickInputWithPhysics` mode; renderer cadence is not an input to movement.
- [ ] Both backends support velocity and displacement commands, and switching
  models at runtime is deterministic and spike-free.
- [ ] Jolt advances controller contacts, support, floor sticking, stairs, and
  moving-ground behavior every fixed step, including idle steps.
- [ ] Capsule dimensions and all controller settings have unambiguous units and
  backend mappings.
- [ ] Grounding and collision-location flags are separate, stable concepts.
- [ ] Moving and rotating platforms behave without double velocity or stale
  support.
- [ ] Locomotion is correct for arbitrary normalized up directions.
- [ ] Required character/rigid-body/query interactions are implemented;
  unsupported features are explicit and tested.
- [ ] Targeted and full physics tests pass with no new warnings, hot-path
  allocations, lifecycle leaks, or native diagnostic residue.

## Primary Implementation Areas

- `XRENGINE/Scene/Components/Movement/CharacterMovementComponent.cs`
- `XREngine.Runtime.Core/Scene/Physics/PhysicsBackendService.cs`
- `XREngine.Runtime.Core/Scene/Physics/PhysicsContracts.cs`
- `XREngine.Runtime.Core/Scene/Physics/Jolt/JoltCharacterVirtualController.cs`
- `XREngine.Runtime.Core/Scene/Physics/Jolt/JoltBackendService.cs`
- `XREngine.Runtime.Core/Scene/Physics/Jolt/JoltScene.cs`
- `XRENGINE/Scene/Physics/Physx/PhysxBackendService.cs`
- `XREngine.UnitTests/Physics/JoltControllerParityTests.cs`

Locate the concrete PhysX controller adapter and nearby serialization/editor
files during Phase 0 rather than assuming the current file layout remains
fixed throughout the contract cleanup.

## Research References

- [Jolt `CharacterVirtual` API](https://jrouwe.github.io/JoltPhysics/class_character_virtual.html)
- [Jolt character-controller overview](https://jrouwe.github.io/JoltPhysicsDocs/5.0.0/index.html)
- [Jolt `CharacterVirtual` sample](https://github.com/jrouwe/JoltPhysics/blob/master/Samples/Tests/Character/CharacterVirtualTest.cpp)
- [Jolt `CharacterVirtualSettings` source](https://raw.githubusercontent.com/jrouwe/JoltPhysics/master/Jolt/Physics/Character/CharacterVirtual.h)
- [Jolt `ExtendedUpdateSettings`](https://jrouwe.github.io/JoltPhysicsDocs/5.2.0/struct_character_virtual_1_1_extended_update_settings.html)
- [Jolt capsule shape API](https://jrouwe.github.io/JoltPhysicsDocs/5.1.0/class_capsule_shape.html)
- [PhysX character-controller guide](https://nvidia-omniverse.github.io/PhysX/physx/5.3.0/docs/CharacterControllers.html)
- [PhysX capsule-controller dimensions](https://nvidia-omniverse.github.io/PhysX/physx/5.1.0/_build/physx/latest/class_px_capsule_controller.html)

When implementation starts, verify these references against the exact Jolt and
PhysX package revisions pinned by the repository. If a pinned API differs,
record that version-specific behavior in the contract/tests instead of coding
against the latest documentation by assumption.
